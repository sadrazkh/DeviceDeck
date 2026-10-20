// The mock battery history: a year or so of snapshots per sample phone (one per connect), plus an old phone
// that is no longer connected, so the screen can show history for a phone that isn't here.

import type { BatteryDevice, BatterySnapshot } from '../cleanupTypes';
import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { BatteryInfo, DeviceCapability, DeviceSummary } from '../types';
import { ANDROID_ID, IPHONE_ID } from './mock-data';
import { createRandom } from './random';

export type BatteryMethodName = Extract<MethodName, `battery.${string}`>;

export interface MockBatteryContext {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  battery(deviceId: string): BatteryInfo | undefined;
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  now(): number;
  badParams(message: string): Error;
}

export const OLD_PHONE_ID = 'HT7A21AB1234';

interface HistorySpec {
  deviceId: string;
  name: string;
  platform: 'ios' | 'android';
  model: string;
  os: string[];
  from: string;
  to: string;
  count: number;
  health: [number, number];
  cycles: [number, number];
  design: number;
  seed: number;
}

const SPECS: HistorySpec[] = [
  { deviceId: IPHONE_ID, name: 'Nima’s iPhone', platform: 'ios', model: 'iPhone 17 Pro', os: ['26.0', '26.0.1', '26.1'], from: '2025-09-26T18:00:00Z', to: '2026-10-18T19:10:00Z', count: 34, health: [100, 87], cycles: [3, 412], design: 4252, seed: 41 },
  { deviceId: ANDROID_ID, name: 'Galaxy S25 Ultra', platform: 'android', model: 'Galaxy S25 Ultra', os: ['15', '16'], from: '2025-02-14T12:00:00Z', to: '2026-10-17T08:30:00Z', count: 28, health: [100, 92], cycles: [1, 268], design: 5000, seed: 43 },
  { deviceId: OLD_PHONE_ID, name: 'Pixel 6a', platform: 'android', model: 'Pixel 6a', os: ['13', '14'], from: '2023-03-02T09:00:00Z', to: '2025-01-20T21:00:00Z', count: 19, health: [97, 78], cycles: [180, 710], design: 4410, seed: 47 },
];

function generate(spec: HistorySpec, firstId: number): BatterySnapshot[] {
  const rnd = createRandom(spec.seed);
  const from = Date.parse(spec.from);
  const to = Date.parse(spec.to);
  const list: BatterySnapshot[] = [];
  for (let i = 0; i < spec.count; i++) {
    const t = i / (spec.count - 1);
    // Irregular gaps (a phone connects when it connects), health falling a little faster at first.
    const at = from + (to - from) * Math.min(1, Math.max(0, t + (i > 0 && i < spec.count - 1 ? (rnd.next() - 0.5) * 0.02 : 0)));
    const health = Math.round(spec.health[0] - (spec.health[0] - spec.health[1]) * Math.pow(t, 0.8) + (i > 0 && i < spec.count - 1 ? rnd.between(-0.6, 0.6) : 0));
    list.push({
      id: firstId + i,
      deviceId: spec.deviceId,
      deviceName: spec.name,
      platform: spec.platform,
      model: spec.model,
      osVersion: spec.os[Math.min(spec.os.length - 1, Math.floor(t * spec.os.length))]!,
      at: new Date(at).toISOString(),
      levelPercent: Math.round(rnd.between(18, 96)),
      isCharging: rnd.chance(0.55),
      healthPercent: health,
      healthEstimated: spec.platform === 'ios',
      cycleCount: Math.round(spec.cycles[0] + (spec.cycles[1] - spec.cycles[0]) * t),
      designCapacityMah: spec.design,
      fullChargeCapacityMah: Math.round((spec.design * health) / 100),
      temperatureCelsius: Math.round(rnd.between(23, 35) * 10) / 10,
    });
  }
  return list.sort((a, b) => a.at.localeCompare(b.at));
}

export function batteryHandlers(ctx: MockBatteryContext): { [M in BatteryMethodName]: (params: MethodParams<M>) => MethodResult<M> } {
  const history = new Map<string, BatterySnapshot[]>();
  let nextId = 1;
  for (const spec of SPECS) {
    const list = generate(spec, nextId);
    nextId += list.length;
    history.set(spec.deviceId, list);
  }

  const summary = (deviceId: string, list: BatterySnapshot[]): BatteryDevice => {
    const latest = list[list.length - 1]!;
    return {
      deviceId,
      name: latest.deviceName,
      platform: latest.platform,
      model: latest.model,
      count: list.length,
      firstAt: list[0]!.at,
      lastAt: latest.at,
      latest,
    };
  };

  return {
    'battery.devices': () =>
      [...history.entries()]
        .filter(([, list]) => list.length > 0)
        .map(([id, list]) => summary(id, list))
        .sort((a, b) => b.lastAt.localeCompare(a.lastAt)),

    'battery.history': ({ deviceId }) => structuredClone(history.get(deviceId) ?? []),

    'battery.record': ({ deviceId }) => {
      const device = ctx.requireReady(deviceId, 'battery');
      const info = ctx.battery(deviceId);
      if (!info) throw ctx.badParams('This phone has no battery figures.');
      const snapshot: BatterySnapshot = {
        id: nextId++,
        deviceId,
        deviceName: device.name,
        platform: device.platform,
        model: device.marketingName ?? device.model,
        osVersion: device.osVersion,
        at: new Date(ctx.now()).toISOString(),
        ...info,
        healthEstimated: device.platform === 'ios',
      };
      history.set(deviceId, [...(history.get(deviceId) ?? []), snapshot]);
      ctx.emit('battery.recorded', { ...snapshot });
      return { ...snapshot };
    },

    'battery.export': ({ deviceId, destination }) => {
      const list = history.get(deviceId);
      if (!list || list.length === 0) throw ctx.badParams('There is no battery history for this phone yet.');
      if (!/^[a-zA-Z]:\\/.test(destination)) throw ctx.badParams('destination must be a full folder path on this PC.');
      const day = new Date(ctx.now()).toISOString().slice(0, 10);
      return { path: `${destination.replace(/\\$/, '')}\\Battery report - ${list[list.length - 1]!.deviceName} - ${day}.html` };
    },
  };
}
