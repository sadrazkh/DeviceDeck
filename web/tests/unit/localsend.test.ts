import { afterEach, describe, expect, it } from 'vitest';
import { mockOptionsFromUrl } from '../../src/bridge';
import { BridgeClient } from '../../src/bridge/client';
import type { LocalSendPhase } from '../../src/bridge/localSend';
import { receiverToTarget } from '../../src/bridge/localSend';
import type { MockBackend } from '../../src/bridge/mock/backend';
import type { MockLocalSendScenario } from '../../src/bridge/mock/localSend';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { partsFor, todaysFolder } from '../../src/screens/photos/IntoAppPanel';

let handle: MockHandle | null = null;

function setup(localSend: MockLocalSendScenario = 'ready') {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false, localSend });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const phases: LocalSendPhase[] = [];
  client.on('localsend.updated', (status) => phases.push(status.phase));
  return { client, backend: handle.backend, phases };
}

function run(backend: MockBackend, ms: number, step = 100) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

async function usbTarget(client: BridgeClient) {
  const found = await client.call('localsend.discover', { deviceId: IPHONE_ID });
  expect(found.usb).toBe('found');
  return receiverToTarget(found.usbReceiver!);
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('LocalSend (mock)', () => {
  it('finds LocalSend over the cable and sends: waiting for Accept, sending, done', async () => {
    const { client, backend, phases } = setup();
    const files = backend.registerFiles([new File([new Uint8Array(4096)], 'IMG_0001.HEIC'), new File([new Uint8Array(8192)], 'IMG_0001.MOV')]);
    const { jobId } = await client.call('localsend.send', { deviceId: IPHONE_ID, target: await usbTarget(client), files });

    run(backend, 600);
    expect((await client.call('localsend.job', { jobId })).phase).toBe('waitingForAccept');
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.doneBytes).toBe(0);
    run(backend, 4000);
    const status = await client.call('localsend.job', { jobId });
    expect(status.phase).toBe('done');
    expect(status.files!.map((f) => f.state)).toEqual(['done', 'done']);
    expect(phases).toEqual(['connecting', 'waitingForAccept', 'sending', 'done']);
    const job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.kind).toBe('localSend');
    expect(job.title).toBe('Sending 2 items to Nima’s iPhone with LocalSend');
  });

  it('a PIN: fails at once, a wrong PIN fails again, the right one sends', async () => {
    const { client, backend } = setup('pin');
    const files = backend.registerFiles([new File([new Uint8Array(1024)], 'a.jpg')]);
    const { jobId } = await client.call('localsend.send', { deviceId: IPHONE_ID, target: await usbTarget(client), files });
    run(backend, 600);
    let job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.state).toBe('failed');
    expect(job.error?.code).toBe('localsend_pin_required');

    await client.call('localsend.retry', { jobId, pin: '0000' });
    run(backend, 600);
    job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.error?.code).toBe('localsend_pin_wrong');
    expect((await client.call('localsend.job', { jobId })).pinGiven).toBe(true);

    await client.call('localsend.retry', { jobId, pin: '123456' });
    run(backend, 4000);
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.state).toBe('done');
  });

  it('declined, busy and closed end the send with their codes', async () => {
    for (const [scenario, code] of [
      ['reject', 'localsend_rejected'],
      ['busy', 'localsend_busy'],
    ] as const) {
      const { client, backend } = setup(scenario);
      const files = backend.registerFiles([new File([new Uint8Array(1024)], 'a.jpg')]);
      const { jobId } = await client.call('localsend.send', { deviceId: IPHONE_ID, target: await usbTarget(client), files });
      run(backend, 2500);
      const status = await client.call('localsend.job', { jobId });
      expect(status.phase, scenario).toBe('failed');
      expect(status.error?.code, scenario).toBe(code);
      expect(status.files![0]!.state).toBe('failed');
      handle?.dispose();
    }

    const { client } = setup('closed');
    expect((await client.call('localsend.discover', { deviceId: IPHONE_ID })).usb).toBe('notListening');
  });

  it('LocalSend opened after a moment is found on a later look', async () => {
    const { client } = setup('opening');
    expect((await client.call('localsend.discover', { deviceId: IPHONE_ID })).usb).toBe('notListening');
    expect((await client.call('localsend.discover', { deviceId: IPHONE_ID })).usb).toBe('notListening');
    expect((await client.call('localsend.discover', { deviceId: IPHONE_ID })).usb).toBe('found');
  });

  it('on Wi-Fi: a search, then this phone first and another device after it', async () => {
    const { client, backend } = setup('wifi');
    const first = await client.call('localsend.discover', { deviceId: IPHONE_ID, wifi: true });
    expect(first.usb).toBe('notAnswering');
    expect(first.wifiSearching).toBe(true);
    run(backend, 2000);
    const found = await client.call('localsend.discover', { deviceId: IPHONE_ID, wifi: true });
    expect(found.wifiSearching).toBe(false);
    expect(found.wifi.map((r) => [r.alias, r.likely])).toEqual([
      ['Nima’s iPhone', true],
      ['Office PC', false],
    ]);
    const files = backend.registerFiles([new File([new Uint8Array(1024)], 'a.jpg')]);
    const { jobId } = await client.call('localsend.send', { deviceId: IPHONE_ID, target: receiverToTarget(found.wifi[0]!), files });
    run(backend, 4000);
    expect((await client.call('localsend.job', { jobId })).phase).toBe('done');
  });

  it('an Android phone has no cable route; the URL picks the scenario', async () => {
    const { client } = setup();
    expect((await client.call('localsend.discover', { deviceId: ANDROID_ID })).usb).toBe('unavailable');
    expect(mockOptionsFromUrl('?localsend=pin').localSend).toBe('pin');
    expect(mockOptionsFromUrl('?localsend=nonsense').localSend).toBe('ready');
  });
});

describe('Into an app: folders', () => {
  it('names today’s folder and splits big sends into parts of 200, like the host', () => {
    expect(todaysFolder(new Date(2026, 9, 21))).toBe('DeviceDeck 2026-10-21');
    expect(partsFor('F', 200)).toEqual([{ path: 'F', count: 200 }]);
    expect(partsFor('F', 450)).toEqual([
      { path: 'F/1-200', count: 200 },
      { path: 'F/201-400', count: 200 },
      { path: 'F/401-450', count: 50 },
    ]);
  });
});
