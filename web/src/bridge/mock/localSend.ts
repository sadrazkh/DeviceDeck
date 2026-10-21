// The mock of LocalSend on the phone (round 3): finding it over the cable or Wi-Fi, the Accept tap, a PIN, a decline,
// a busy phone. The scenario comes from the page URL (?localsend=closed and so on, see MockLocalSendScenario).

import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { DeviceCapability, DeviceSummary } from '../types';
import type {
  LocalSendDiscovery,
  LocalSendFileResult,
  LocalSendJobStatus,
  LocalSendPhase,
  LocalSendReceiver,
  LocalSendUsbState,
} from '../localSend';
import type { RpcFault } from './backend';
import type { JobItem, TransferEngine } from './transfers';

/**
 * ready: found over the cable, Accept tapped after a moment. quickSave: no Accept needed. closed: LocalSend isn't open.
 * opening: closed for the first two looks, then open. background: open but suspended. wifi: not over the cable, found
 * on Wi-Fi. pin: asks for the PIN 123456. reject: Decline is tapped. busy: busy with another transfer the first time.
 */
export type MockLocalSendScenario = 'ready' | 'quickSave' | 'closed' | 'opening' | 'background' | 'wifi' | 'pin' | 'reject' | 'busy';

export const MOCK_LOCALSEND_SCENARIOS: readonly MockLocalSendScenario[] = [
  'ready',
  'quickSave',
  'closed',
  'opening',
  'background',
  'wifi',
  'pin',
  'reject',
  'busy',
];

export const MOCK_LOCALSEND_PIN = '123456';

/** Reaching the phone, and how long the person takes to tap Accept. */
const CONNECT_MS = 400;
const ACCEPT_MS = 1500;
const WIFI_SEARCH_MS = 1500;

export interface LocalSendHost {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  engine(): TransferEngine;
  sizeOfLocal(path: string): number;
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  after(ms: number, run: () => void): void;
  fault(code: string, message?: string): RpcFault;
  badParams(message: string): RpcFault;
}

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

interface Send {
  jobId: string;
  deviceId: string;
  phase: LocalSendPhase;
  receiverAlias: string | null;
  pin: string | null;
  files: JobItem[];
}

interface PhoneState {
  looks: number;
  wifiSearching: boolean;
  wifiSearchedAt: string | null;
}

const FINGERPRINT = 'F3A1C0DE5E1D2B4A9C8E7F60112233445566778899AABBCCDDEEFF0011223344';

export class MockLocalSend {
  scenario: MockLocalSendScenario = 'ready';
  private readonly sends = new Map<string, Send>();
  private readonly phones = new Map<string, PhoneState>();
  private busyOnce = false;

  constructor(private readonly host: LocalSendHost) {}

  private phone(deviceId: string): PhoneState {
    let state = this.phones.get(deviceId);
    if (!state) {
      state = { looks: 0, wifiSearching: false, wifiSearchedAt: null };
      this.phones.set(deviceId, state);
    }
    return state;
  }

  private usbState(device: DeviceSummary, looks: number): LocalSendUsbState {
    if (device.platform !== 'ios') return 'unavailable';
    switch (this.scenario) {
      case 'closed':
        return 'notListening';
      case 'opening':
        return looks <= 2 ? 'notListening' : 'found';
      case 'background':
      case 'wifi':
        return 'notAnswering';
      default:
        return 'found';
    }
  }

  private receivers(device: DeviceSummary): LocalSendReceiver[] {
    const others: LocalSendReceiver[] = [
      {
        id: 'wifi:office',
        route: 'wifi',
        alias: 'Office PC',
        deviceModel: 'Windows',
        deviceType: 'desktop',
        address: '192.168.1.10',
        port: 53317,
        protocol: 'https',
        fingerprint: 'B'.repeat(64),
        likely: false,
      },
    ];
    // LocalSend in the background or closed isn't on the network either.
    if (this.scenario === 'closed' || this.scenario === 'background' || this.scenario === 'opening') return others;
    return [
      {
        id: `wifi:${FINGERPRINT}`,
        route: 'wifi',
        alias: device.name,
        deviceModel: 'iPhone',
        deviceType: 'mobile',
        address: '192.168.1.23',
        port: 53317,
        protocol: 'https',
        fingerprint: FINGERPRINT,
        likely: true,
      },
      ...others,
    ];
  }

  private status(send: Send, withFiles: boolean): LocalSendJobStatus {
    const job = this.host.engine().get(send.jobId);
    const phase: LocalSendPhase =
      job?.state === 'done' ? 'done' : job?.state === 'failed' ? 'failed' : job?.state === 'cancelled' ? 'cancelled' : send.phase;
    const error = job?.state === 'failed' ? job.error : null;
    let files: LocalSendFileResult[] | null = null;
    if (withFiles) {
      const done = job?.doneItems ?? 0;
      files = send.files.map((f, index) => ({
        index,
        name: f.name,
        size: f.size,
        state: index < done ? 'done' : job?.state === 'failed' ? 'failed' : job?.state === 'running' && index === done ? 'sending' : 'queued',
        error: index >= done && job?.state === 'failed' ? job.error : null,
      }));
    }
    return { jobId: send.jobId, deviceId: send.deviceId, phase, receiverAlias: send.receiverAlias, pinGiven: send.pin !== null, error, files };
  }

  private setPhase(send: Send, phase: LocalSendPhase): void {
    send.phase = phase;
    this.host.emit('localsend.updated', this.status(send, false));
  }

  private finished(send: Send): boolean {
    const state = this.host.engine().get(send.jobId)?.state;
    return state === 'done' || state === 'failed' || state === 'cancelled';
  }

  /** What happens on the phone from here: the PIN check, busy, the Accept question, then the bytes. */
  private outcome(send: Send): { fail?: { code: string; message: string }; acceptMs: number } {
    if (this.scenario === 'pin' && send.pin !== MOCK_LOCALSEND_PIN) {
      return send.pin === null
        ? { fail: { code: 'localsend_pin_required', message: 'LocalSend on the iPhone asks for a PIN.' }, acceptMs: 0 }
        : { fail: { code: 'localsend_pin_wrong', message: 'That PIN isn’t the one LocalSend shows. Check it and try again.' }, acceptMs: 0 };
    }
    if (this.scenario === 'busy' && !this.busyOnce) {
      this.busyOnce = true;
      return { fail: { code: 'localsend_busy', message: 'LocalSend on the iPhone is busy with another transfer.' }, acceptMs: 0 };
    }
    if (this.scenario === 'reject') return { fail: { code: 'localsend_rejected', message: 'The iPhone declined the photos.' }, acceptMs: ACCEPT_MS };
    if (this.scenario === 'closed' || this.scenario === 'background') {
      return { fail: { code: 'localsend_not_listening', message: 'LocalSend isn’t open on the iPhone. Open it and keep it open.' }, acceptMs: 0 };
    }
    return { acceptMs: this.scenario === 'quickSave' ? 0 : ACCEPT_MS };
  }

  /** The job's hold (seconds) and the steps of one run; the engine starts the bytes when the hold ends. */
  private run(send: Send): number {
    const { fail, acceptMs } = this.outcome(send);
    this.setPhase(send, 'connecting');
    this.host.after(CONNECT_MS, () => {
      if (this.finished(send)) return;
      if (fail && acceptMs === 0) {
        this.host.engine().fail(send.jobId, fail);
        this.setPhase(send, 'failed');
        return;
      }
      this.setPhase(send, 'waitingForAccept');
      this.host.after(acceptMs, () => {
        if (this.finished(send)) return;
        if (fail) {
          this.host.engine().fail(send.jobId, fail);
          this.setPhase(send, 'failed');
          return;
        }
        this.setPhase(send, 'sending');
      });
    });
    // Failing runs are held until they fail; the others start sending right after Accept.
    return (CONNECT_MS + acceptMs + (fail ? 60_000 : 0)) / 1000;
  }

  readonly handlers: { [M in 'localsend.discover' | 'localsend.send' | 'localsend.retry' | 'localsend.job']: Handler<M> } = {
    'localsend.discover': ({ deviceId, wifi, rescan }): LocalSendDiscovery => {
      const device = this.host.requireReady(deviceId, 'photosRead');
      const phone = this.phone(deviceId);
      phone.looks++;
      const usb = this.usbState(device, phone.looks);
      if (wifi && !phone.wifiSearching && (phone.wifiSearchedAt === null || rescan)) {
        phone.wifiSearching = true;
        this.host.after(WIFI_SEARCH_MS, () => {
          phone.wifiSearching = false;
          phone.wifiSearchedAt = new Date().toISOString();
        });
      }
      const usbReceiver: LocalSendReceiver | null =
        usb === 'found'
          ? {
              id: `usb:${FINGERPRINT}`,
              route: 'usb',
              alias: 'Clever Mango',
              deviceModel: 'iPhone',
              deviceType: 'mobile',
              address: null,
              port: 53317,
              protocol: 'https',
              fingerprint: FINGERPRINT,
              likely: true,
            }
          : null;
      return {
        deviceId,
        usb,
        usbDetail: usb === 'found' ? null : usb === 'notListening' ? 'Nothing listens on the iPhone’s port 53317.' : null,
        usbReceiver,
        wifi: phone.wifiSearchedAt !== null ? this.receivers(device) : [],
        wifiSearching: phone.wifiSearching,
        wifiSearchedAt: phone.wifiSearchedAt,
      };
    },
    'localsend.send': ({ deviceId, target, files }) => {
      const device = this.host.requireReady(deviceId, 'photosRead');
      if (!target || (target.route !== 'usb' && target.route !== 'wifi')) throw this.host.badParams('target is required.');
      if (!Array.isArray(files) || files.length === 0) throw this.host.badParams('Choose at least one photo.');
      if (target.route === 'usb' && device.platform !== 'ios') throw this.host.fault('not_supported', `${device.name} can’t be reached over its cable; use Wi-Fi.`);
      const items = files.map<JobItem>((path) => ({ name: path.split(/[\\/]/).pop() ?? path, size: this.host.sizeOfLocal(path) }));
      const what = items.length === 1 ? items[0]!.name : `${items.length} items`;
      const send: Send = { jobId: '', deviceId, phase: 'connecting', receiverAlias: target.alias ?? null, pin: null, files: items };
      const job = this.host.engine().add({
        deviceId,
        kind: 'localSend',
        title: `Sending ${what} to ${device.name} with LocalSend`,
        items,
        holdSeconds: 3600,
        onDone: () => this.setPhase(send, 'done'),
      });
      send.jobId = job.id;
      this.sends.set(job.id, send);
      this.hold(job.id, this.run(send));
      return { jobId: job.id };
    },
    'localsend.retry': ({ jobId, pin }) => {
      const send = this.sends.get(jobId);
      if (!send) throw this.host.badParams('No LocalSend transfer has that id.');
      if (pin !== undefined) send.pin = pin.trim() === '' ? null : pin.trim();
      if (this.host.engine().retry(jobId, 3600) === 'nothing') throw this.host.fault('internal', 'This transfer has nothing to try again.');
      this.hold(jobId, this.run(send));
      return {};
    },
    'localsend.job': ({ jobId }) => {
      const send = this.sends.get(jobId);
      if (!send) throw this.host.badParams('No LocalSend transfer has that id.');
      return this.status(send, true);
    },
  };

  /** Sets how long a queued job waits before its bytes start (the engine counts it down with the clock). */
  private hold(jobId: string, seconds: number): void {
    this.host.engine().setHold(jobId, seconds);
  }
}
