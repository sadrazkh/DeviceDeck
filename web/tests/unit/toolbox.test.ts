import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { BridgeError } from '../../src/bridge/errors';
import type { BridgeEvents, EventName } from '../../src/bridge/methods';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import type { LogLine, ToolboxJob } from '../../src/bridge/toolbox';
import { combine } from '../../src/screens/toolbox/HeicTool';
import { toolboxMessages } from '../../src/screens/toolbox/i18n';
import { LogBuffer, logTime, matches } from '../../src/screens/toolbox/logBuffer';
import { codecFromConfig, FrameAssembler, fromBase64, splitNalUnits } from '../../src/screens/toolbox/mirror/h264';
import { fitSize, KEY, keyAction, toVideoPoint, wheelToScroll } from '../../src/screens/toolbox/mirror/input';
import { clampSelection, computePeaks, formatSeconds, initialSelection } from '../../src/screens/toolbox/waveform';

const line = (seq: number, level: LogLine['level'], message: string, process = 'proc', tag: string | null = null): LogLine => ({
  seq,
  time: '2026-10-20T16:44:01.123+03:30',
  level,
  process,
  pid: 42,
  tag,
  message,
});

describe('live log buffer', () => {
  it('filters by level and by text in message, process or tag', () => {
    expect(matches(line(1, 'debug', 'x'), { minLevel: 'info', search: '' })).toBe(false);
    expect(matches(line(1, 'error', 'x'), { minLevel: 'warning', search: '' })).toBe(true);
    expect(matches(line(1, 'info', 'Disk full'), { minLevel: 'verbose', search: 'disk' })).toBe(true);
    expect(matches(line(1, 'info', 'x', 'SpringBoard'), { minLevel: 'verbose', search: 'spring' })).toBe(true);
    expect(matches(line(1, 'info', 'x', 'p', 'ActivityManager'), { minLevel: 'verbose', search: 'activity' })).toBe(true);
    expect(matches(line(1, 'info', 'x'), { minLevel: 'verbose', search: 'nope' })).toBe(false);
  });

  it('keeps the filtered view in step as lines arrive and the filter changes', () => {
    const buffer = new LogBuffer(1000);
    buffer.push([line(1, 'info', 'a'), line(2, 'error', 'b')]);
    buffer.setFilter({ minLevel: 'error', search: '' });
    expect(buffer.view.map((l) => l.seq)).toEqual([2]);
    buffer.push([line(3, 'debug', 'c'), line(4, 'fault', 'd')]);
    expect(buffer.view.map((l) => l.seq)).toEqual([2, 4]);
    const before = buffer.view;
    buffer.push([line(5, 'error', 'e')]);
    expect(buffer.view).not.toBe(before); // a new array, so React sees the change
    buffer.setFilter({ minLevel: 'verbose', search: '' });
    expect(buffer.view).toHaveLength(5);
    buffer.clear();
    expect(buffer.total).toBe(0);
    expect(buffer.view).toHaveLength(0);
  });

  it('drops the oldest lines past its cap, from the view too', () => {
    const buffer = new LogBuffer(3);
    buffer.push([1, 2, 3, 4, 5].map((n) => line(n, 'info', `m${n}`)));
    expect(buffer.total).toBe(3);
    expect(buffer.view.map((l) => l.seq)).toEqual([3, 4, 5]);
    buffer.setFilter({ minLevel: 'verbose', search: 'm1' });
    expect(buffer.view).toHaveLength(0);
  });

  it('shows times with milliseconds', () => {
    expect(logTime(null)).toBe('');
    expect(logTime('2026-10-20T16:44:01.123Z')).toMatch(/^\d\d:\d\d:01\.123$/);
  });
});

describe('mirror window: H.264 and input', () => {
  const sps = [0x67, 0x64, 0x00, 0x28, 0xac, 0xd9];
  const pps = [0x68, 0xee, 0x3c, 0x80];
  const config = new Uint8Array([0, 0, 0, 1, ...sps, 0, 0, 0, 1, ...pps]);
  const frame = (key: boolean, pts: number) => ({ kind: 'frame' as const, pts, key, width: 0, height: 0, data: new Uint8Array([0, 0, 0, 1, key ? 0x65 : 0x41, 1, 2, 3]) });

  it('splits Annex B into NAL units and names the codec from the SPS', () => {
    expect(splitNalUnits(config).map((u) => u[0])).toEqual([0x67, 0x68]);
    expect(codecFromConfig(config)).toBe('avc1.640028');
    expect(codecFromConfig(new Uint8Array([0, 0, 1, ...pps]))).toBeNull();
  });

  it('waits for a key frame, configures with the stored config in front, then passes frames through', () => {
    const a = new FrameAssembler();
    expect(a.push({ kind: 'session', pts: 0, key: false, width: 1080, height: 2340, data: new Uint8Array() })).toEqual({ type: 'session', width: 1080, height: 2340 });
    expect(a.push({ kind: 'config', pts: 0, key: false, width: 1080, height: 2340, data: config })).toBeNull();
    expect(a.push(frame(false, 10))).toBeNull(); // no key frame yet
    const first = a.push(frame(true, 20));
    expect(first?.type).toBe('configure');
    if (first?.type !== 'configure') throw new Error();
    expect(first.codec).toBe('avc1.640028');
    expect(first.chunk.data.length).toBe(config.length + 8);
    expect(a.push(frame(false, 30))).toEqual({ type: 'chunk', chunk: { key: false, timestamp: 30, data: frame(false, 30).data } });
  });

  it('after rotation (a new session) or a backlog, skips deltas until the next key frame', () => {
    const a = new FrameAssembler();
    a.push({ kind: 'config', pts: 0, key: false, width: 0, height: 0, data: config });
    a.push(frame(true, 1));
    a.skipToKeyFrame();
    expect(a.push(frame(false, 2))).toBeNull();
    expect(a.push(frame(true, 3))?.type).toBe('chunk');
    a.push({ kind: 'session', pts: 0, key: false, width: 2340, height: 1080, data: new Uint8Array() });
    a.push({ kind: 'config', pts: 0, key: false, width: 0, height: 0, data: config });
    expect(a.push(frame(false, 4))).toBeNull();
    expect(a.push(frame(true, 5))?.type).toBe('configure');
  });

  it('decodes base64 like atob does', () => {
    const bytes = new Uint8Array(Array.from({ length: 1000 }, (_, i) => (i * 37) % 256));
    for (const n of [0, 1, 2, 3, 4, 999, 1000]) {
      const slice = bytes.subarray(0, n);
      const b64 = btoa(String.fromCharCode(...slice));
      expect(Array.from(fromBase64(b64))).toEqual(Array.from(slice));
    }
  });

  it('maps a click on the shown video to the phone’s pixels, and ignores the letterbox', () => {
    const shown = { left: 100, top: 50, width: 200, height: 400 };
    expect(toVideoPoint(200, 250, shown, { width: 1080, height: 2160 })).toEqual({ x: 540, y: 1080 });
    expect(toVideoPoint(100, 50, shown, { width: 1080, height: 2160 })).toEqual({ x: 0, y: 0 });
    expect(toVideoPoint(300, 450, shown, { width: 1080, height: 2160 })).toEqual({ x: 1079, y: 2159 });
    expect(toVideoPoint(90, 250, shown, { width: 1080, height: 2160 })).toBeNull();
    expect(fitSize({ width: 400, height: 400 }, { width: 1080, height: 2160 })).toEqual({ width: 200, height: 400 });
    expect(fitSize({ width: 400, height: 400 }, { width: 0, height: 0 })).toEqual({ width: 0, height: 0 });
  });

  it('sends editing keys as key codes and characters as text', () => {
    const plain = { ctrlKey: false, altKey: false, metaKey: false };
    expect(keyAction({ key: 'Enter', ...plain })).toEqual({ kind: 'key', keyCode: KEY.enter });
    expect(keyAction({ key: 'Backspace', ...plain })).toEqual({ kind: 'key', keyCode: KEY.del });
    expect(keyAction({ key: 'Escape', ...plain })).toEqual({ kind: 'key', keyCode: KEY.back });
    expect(keyAction({ key: 'a', ...plain })).toEqual({ kind: 'text', text: 'a' });
    expect(keyAction({ key: 'س', ...plain })).toEqual({ kind: 'text', text: 'س' });
    expect(keyAction({ key: 'Shift', ...plain })).toBeNull();
    expect(keyAction({ key: 'c', ...plain, ctrlKey: true })).toBeNull();
  });

  it('turns wheel deltas into scroll notches, up positive', () => {
    expect(wheelToScroll(0, 100, 0)).toEqual({ hScroll: -0, vScroll: -1 });
    expect(wheelToScroll(0, -3, 1)).toEqual({ hScroll: -0, vScroll: 1 });
    expect(wheelToScroll(0, 100_000, 0).vScroll).toBe(-16);
  });
});

describe('ringtone maker helpers', () => {
  it('finds the loudest sample per slice', () => {
    const samples = new Float32Array([0, 0.5, -0.9, 0.1, 0.2, 0.2, -0.3, 0]);
    expect(computePeaks(samples, 4)).toEqual([0.5, 0.9, 0.2, 0.3].map((v) => Math.fround(v)));
    expect(computePeaks(new Float32Array(), 3)).toEqual([0, 0, 0]);
  });

  it('keeps the selection inside the song, at least a second and at most five minutes', () => {
    expect(clampSelection(-5, 10, 60)).toEqual({ start: 0, end: 10 });
    expect(clampSelection(20, 10, 60)).toEqual({ start: 10, end: 20 });
    expect(clampSelection(59.9, 70, 60)).toEqual({ start: 59, end: 60 });
    expect(clampSelection(0, 1000, 1000)).toEqual({ start: 0, end: 300 });
    expect(clampSelection(1.234, 5.678, 60)).toEqual({ start: 1.2, end: 5.7 });
    expect(initialSelection(12.5)).toEqual({ start: 0, end: 12.5 });
    expect(initialSelection(200)).toEqual({ start: 0, end: 30 });
    expect(formatSeconds(65.25)).toBe('1:05.3');
  });
});

describe('HEIC jobs in the page', () => {
  const job = (id: string, state: ToolboxJob['state'], done: number, total: number): ToolboxJob => ({
    id, kind: 'heic', state, total, done, failed: 0, current: null, outputs: [`C:\\out\\${id}.jpg`], errors: [],
  });
  it('shows two jobs (files and folders) as one', () => {
    expect(combine([])).toBeNull();
    const both = combine([job('a', 'done', 3, 3), job('b', 'running', 5, 24)])!;
    expect([both.state, both.done, both.total, both.outputs.length]).toEqual(['running', 8, 27, 2]);
    expect(combine([job('a', 'done', 3, 3), job('b', 'cancelled', 5, 24)])!.state).toBe('cancelled');
  });
});

describe('toolbox words', () => {
  it('has every key in Persian, with the same placeholders', () => {
    const placeholders = (text: string) => [...text.matchAll(/\{(\w+)/g)].map((m) => m[1]).sort();
    for (const [key, english] of Object.entries(toolboxMessages.en)) {
      const persian = toolboxMessages.fa[key as keyof typeof toolboxMessages.en];
      expect(persian, key).toBeTruthy();
      expect(placeholders(persian), key).toEqual(placeholders(english));
    }
  });
});

// ---------- the mock backend's toolbox ----------

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, seedQueue: false, indexing: false });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const events: { event: string; params: unknown }[] = [];
  const record = <E extends EventName>(event: E) => client.on(event, (params: BridgeEvents[E]) => events.push({ event, params }));
  (['log.lines', 'log.ended', 'toolbox.job', 'wifi.qr'] as const).forEach(record);
  const run = (ms: number) => {
    for (let t = 0; t < ms; t += 250) handle!.backend.tick(250);
  };
  return { client, backend: handle.backend, events, run };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock toolbox', () => {
  it('reports per-phone features', async () => {
    const { client } = setup();
    const iphone = await client.call('toolbox.features', { deviceId: IPHONE_ID });
    const android = await client.call('toolbox.features', { deviceId: ANDROID_ID });
    expect([iphone.mirror, iphone.ringtone, iphone.wifiSync, iphone.log, iphone.crashReports]).toEqual([false, false, true, true, true]);
    expect([android.mirror, android.ringtone, android.wifiSync]).toEqual([true, true, false]);
  });

  it('streams log lines in order until stopped', async () => {
    const { client, events, run } = setup();
    const { sessionId } = await client.call('log.start', { deviceId: ANDROID_ID });
    run(2000);
    await settle();
    const seqs = events.filter((e) => e.event === 'log.lines').flatMap((e) => (e.params as { lines: LogLine[] }).lines.map((l) => l.seq));
    expect(seqs.length).toBeGreaterThan(5);
    expect(seqs).toEqual([...seqs].sort((a, b) => a - b));
    expect(seqs[0]).toBe(1);
    const exported = await client.call('log.export', { sessionId, minLevel: 'error' });
    expect(exported.path).toMatch(/\.txt$/);
    await client.call('log.stop', { sessionId });
    const before = events.length;
    run(2000);
    await settle();
    expect(events.length).toBe(before);
  });

  it('ends the log when the phone is unplugged', async () => {
    const { client, backend, events, run } = setup();
    await client.call('log.start', { deviceId: IPHONE_ID });
    run(500);
    backend.unplug(IPHONE_ID);
    run(500);
    await settle();
    expect(events.find((e) => e.event === 'log.ended')?.params).toMatchObject({ error: { code: 'device_disconnected' } });
  });

  it('deletes crash reports on an iPhone only', async () => {
    const { client } = setup();
    const ios = await client.call('crash.list', { deviceId: IPHONE_ID });
    expect(ios.canDelete).toBe(true);
    const { deleted } = await client.call('crash.delete', { deviceId: IPHONE_ID, ids: [ios.reports[0]!.id] });
    expect(deleted).toBe(1);
    expect((await client.call('crash.list', { deviceId: IPHONE_ID })).reports).toHaveLength(ios.reports.length - 1);
    const android = await client.call('crash.list', { deviceId: ANDROID_ID });
    expect(android.unreadable).toEqual(['tombstones', 'anr']);
    await expect(client.call('crash.delete', { deviceId: ANDROID_ID, ids: [android.reports[0]!.id] })).rejects.toMatchObject({ code: 'permission_denied' });
  });

  it('runs a HEIC job to the end with progress', async () => {
    const { client, events, run } = setup();
    const { jobId, total } = await client.call('heic.convert', { files: ['C:\\Users\\Nima\\Pictures\\Trip'], quality: 90 });
    expect(total).toBe(24);
    run(10_000);
    await settle();
    const updates = events.filter((e) => e.event === 'toolbox.job').map((e) => e.params as ToolboxJob);
    expect(updates.every((u) => u.id === jobId)).toBe(true);
    expect(updates.at(-1)).toMatchObject({ state: 'done', done: 24 });
    await expect(client.call('heic.convert', { files: ['C:\\x.heic'], quality: 0 })).rejects.toBeInstanceOf(BridgeError);
  });

  it('pairs with the right code only, and walks the QR flow in order', async () => {
    const { client, events, run } = setup();
    await expect(client.call('wifi.pair', { host: '192.168.1.42', port: 37099, code: '000000' })).rejects.toMatchObject({ code: 'unauthorized' });
    const paired = await client.call('wifi.pair', { host: '192.168.1.42', port: 37099, code: '123456' });
    expect(paired.message).toMatch(/Successfully paired/);
    const qr = await client.call('wifi.qrStart');
    expect(qr.payload).toBe(`WIFI:T:ADB;S:${qr.name};P:${qr.password};;`);
    run(10_000);
    await settle();
    expect(events.filter((e) => e.event === 'wifi.qr').map((e) => (e.params as { stage: string }).stage)).toEqual(['waiting', 'pairing', 'connecting', 'done']);
  });

  it('refuses ringtones for an iPhone and makes them for Android', async () => {
    const { client } = setup();
    await expect(
      client.call('ringtone.create', { deviceId: IPHONE_ID, files: ['C:\\song.mp3'], startSeconds: 0, endSeconds: 20, setAsDefault: false }),
    ).rejects.toMatchObject({ code: 'not_supported' });
    const made = await client.call('ringtone.create', { deviceId: ANDROID_ID, files: ['C:\\song.mp3'], startSeconds: 5, endSeconds: 25, name: 'Morning', setAsDefault: true });
    expect(made).toMatchObject({ devicePath: '/sdcard/Ringtones/Morning.mp3', isDefault: true });
  });
});
