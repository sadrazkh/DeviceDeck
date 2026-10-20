// The mock backend's toolbox: a live log that keeps talking, sample crash reports, a device report, HEIC jobs,
// Wi-Fi pairing (code 123456 works) with a QR flow that "scans" after a few seconds, and ringtones.
// Mirroring needs the app: mirror.start answers, but no video comes.

import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { DeviceCapability, DeviceDetails, DeviceSummary } from '../types';
import { createRandom, hashString } from './random';
import type { CrashReport, LogLevel, LogLine, ToolboxJob } from '../toolbox';

export type ToolboxMethod =
  | 'toolbox.features'
  | 'log.start'
  | 'log.stop'
  | 'log.clear'
  | 'log.export'
  | 'crash.list'
  | 'crash.read'
  | 'crash.export'
  | 'crash.delete'
  | 'report.preview'
  | 'report.save'
  | 'heic.status'
  | 'heic.convert'
  | 'toolbox.cancelJob'
  | 'mirror.open'
  | 'wifi.status'
  | 'wifi.pair'
  | 'wifi.connect'
  | 'wifi.disconnect'
  | 'wifi.qrStart'
  | 'wifi.qrCancel'
  | 'wifi.iphoneSync'
  | 'wifi.setIphoneSync'
  | 'ringtone.create'
  | 'mirror.start'
  | 'mirror.input'
  | 'mirror.stop'
  | 'mirror.screenshot';

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

/** What the toolbox needs from the backend it lives in. */
export interface MockToolboxHost {
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  ready(deviceId: string, capability: DeviceCapability): DeviceSummary;
  after(ms: number, run: () => void): void;
  now(): number;
  details(deviceId: string): DeviceDetails;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

const DOCS = 'C:\\Users\\Nima\\Documents\\DeviceDeck';

type Sample = [LogLevel, string, number | null, string | null, string];

const IPHONE_LOG: Sample[] = [
  ['notice', 'SpringBoard', 61, 'FrontBoard', 'Scene update for com.apple.mobileslideshow: foreground'],
  ['info', 'backboardd', 72, null, 'Display brightness changed to 0.{n}'],
  ['notice', 'kernel', 0, null, 'AppleBCMWLAN: link quality {n}'],
  ['debug', 'locationd', 96, 'CoreLocation', 'Location update, accuracy {n} m'],
  ['error', 'mDNSResponder', 118, null, 'Query timed out for _apple-mobdev2._tcp.local'],
  ['warning', 'Photos', 1402, 'PhotoLibraryServices', 'Thumbnail cache miss for asset {n}'],
  ['notice', 'lockdownd', 54, null, 'Service com.apple.syslog_relay started for DeviceDeck'],
  ['fault', 'MobileMail', 2210, 'Message', 'Background fetch took {n} ms'],
  ['info', 'wifid', 88, null, 'Associated with network, RSSI -{n}'],
];

const ANDROID_LOG: Sample[] = [
  ['info', 'system_server', 1290, 'ActivityManager', 'Start proc {n}:com.sec.android.gallery3d/u0a201 for activity'],
  ['debug', 'system_server', 1290, 'WindowManager', 'Focus moved to Window{{n} u0 com.android.launcher}'],
  ['verbose', 'com.android.systemui', 2104, 'StatusBar', 'Battery level {n}'],
  ['warning', 'com.google.android.gms', 3388, 'GmsClient', 'Connection timed out after {n} ms'],
  ['error', 'com.example.notes', 5120, 'AndroidRuntime', 'FATAL EXCEPTION: main (sample)'],
  ['info', 'adbd', 801, 'adbd', 'host-{n}: connected'],
  ['debug', 'surfaceflinger', 640, 'SurfaceFlinger', 'Frame {n} presented'],
  ['fault', 'init', 1, 'init', "Service 'vendor.sample' exited with status {n}"],
];

interface LogSession {
  deviceId: string;
  seq: number;
  lines: LogLine[];
  running: boolean;
}

function sampleCrashes(platform: 'ios' | 'android', now: number): (CrashReport & { text: string })[] {
  const at = (hours: number) => new Date(now - hours * 3_600_000).toISOString();
  const make = (id: string, fileName: string, process: string, kind: CrashReport['kind'], hours: number, body: string, readable = true) => {
    const text = `Process: ${process}\nDate/Time: ${at(hours)}\n\n${body}\n`;
    return { id, fileName, process, kind, time: at(hours), size: text.length * 37, readable, text };
  };
  return platform === 'ios'
    ? [
        make('Instagram-2026-10-19-201102.ips', 'Instagram-2026-10-19-201102.ips', 'Instagram', 'crash', 1.1,
          'Exception Type: EXC_BAD_ACCESS (SIGSEGV)\nTriggered by Thread: 0\n\nThread 0 Crashed:\n0   libobjc.A.dylib  objc_msgSend + 32\n1   Instagram        0x104a2c000 + 1188772'),
        make('JetsamEvent-2026-10-19-180544.ips', 'JetsamEvent-2026-10-19-180544.ips', 'JetsamEvent', 'memory', 3.2,
          '{"bug_type":"298","largestProcess":"Safari","memoryStatus":{"pageSize":16384}}'),
        make('Retired/Telegram-2026-10-17-093012.ips', 'Telegram-2026-10-17-093012.ips', 'Telegram', 'hang', 59.7,
          'Exception Type: EXC_CRASH (SIGKILL)\nTermination Reason: FRONTBOARD 2343432205 <RBSTerminateContext| scene-update watchdog>'),
        make('ExcResource_Maps-2026-10-16-111530.ips', 'ExcResource_Maps-2026-10-16-111530.ips', 'Maps', 'diagnostic', 82,
          'Event: cpu usage\nAction taken: none\nCPU: 90 seconds cpu time over 104 seconds'),
        make('panic-full-2026-10-12-042201.ips', 'panic-full-2026-10-12-042201.ips', 'kernel', 'panic', 185,
          'panic(cpu 4 caller 0xfffffe0027a1c2e0): watchdog timeout: no checkins from watchdogd in 93 seconds'),
      ]
    : [
        make('dropbox/data_app_crash/2026-10-19 20:38:00', 'data_app_crash-2026-10-19-203800.txt', 'com.example.notes', 'crash', 0.6,
          'Package: com.example.notes v42 (4.2)\n\njava.lang.NullPointerException: sample\n\tat com.example.notes.MainActivity.onCreate(MainActivity.kt:57)'),
        make('dropbox/data_app_anr/2026-10-18 23:20:00', 'data_app_anr-2026-10-18-232000.txt', 'com.whatsapp', 'hang', 21.8,
          'Subject: Input dispatching timed out (5001ms)\n\n----- pid 7781 at 2026-10-19 00:13:20 -----'),
        make('dropbox/SYSTEM_TOMBSTONE/2026-10-17 14:00:00', 'SYSTEM_TOMBSTONE-2026-10-17-140000.txt', 'surfaceflinger', 'tombstone', 55.1,
          "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***\nBuild fingerprint: 'samsung/sample/16'\nsignal 6 (SIGABRT), code -1 (SI_QUEUE)"),
        make('dropbox/SYSTEM_TOMBSTONE_PROTO/2026-10-17 14:00:00', 'SYSTEM_TOMBSTONE_PROTO-2026-10-17-140000.txt', 'SYSTEM_TOMBSTONE_PROTO', 'tombstone', 55.1, '', false),
        make('dropbox/system_server_crash/2026-10-15 06:50:00', 'system_server_crash-2026-10-15-065000.txt', 'system_server', 'panic', 110.6,
          'java.lang.IllegalStateException: sample watchdog'),
      ];
}

const escapeHtml = (text: string) => text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);

export class MockToolbox {
  private readonly logs = new Map<string, LogSession>();
  private readonly crashes = new Map<string, (CrashReport & { text: string })[]>();
  private readonly jobs = new Map<string, ToolboxJob>();
  private readonly qr = new Set<string>();
  private wifiSync = false;
  private nextId = 1;

  constructor(private readonly host: MockToolboxHost) {}

  private id(prefix: string) {
    return `${prefix}-${this.nextId++}`;
  }

  private crashList(device: DeviceSummary) {
    let list = this.crashes.get(device.id);
    if (!list) {
      list = sampleCrashes(device.platform, this.host.now());
      this.crashes.set(device.id, list);
    }
    return list;
  }

  private pump(sessionId: string) {
    const session = this.logs.get(sessionId);
    if (!session?.running) return;
    const device = (() => {
      try {
        return this.host.ready(session.deviceId, 'syslog');
      } catch {
        return null;
      }
    })();
    if (!device) {
      session.running = false;
      this.host.emit('log.ended', { sessionId, error: { code: 'device_disconnected', message: 'The phone was disconnected.' } });
      return;
    }
    const random = createRandom(hashString(sessionId) + session.seq);
    const samples = device.platform === 'ios' ? IPHONE_LOG : ANDROID_LOG;
    const lines: LogLine[] = [];
    for (let i = random.int(1, 4); i > 0; i--) {
      const [level, process, pid, tag, message] = random.pick(samples);
      lines.push({
        seq: ++session.seq,
        time: new Date(this.host.now()).toISOString(),
        level,
        process,
        pid,
        tag,
        message: message.replace('{n}', String(random.int(1, 999))),
      });
    }
    session.lines.push(...lines);
    if (session.lines.length > 100_000) session.lines.splice(0, session.lines.length - 100_000);
    this.host.emit('log.lines', { sessionId, lines, dropped: 0 });
    this.host.after(250, () => this.pump(sessionId));
  }

  readonly handlers: { [M in ToolboxMethod]: Handler<M> } = {
    'toolbox.features': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'info');
      const has = (c: DeviceCapability) => device.capabilities.includes(c);
      return {
        log: has('syslog'),
        crashReports: has('crashReports'),
        mirror: has('screenMirror'),
        ringtone: device.platform === 'android',
        wifiSync: device.platform === 'ios',
        report: has('info'),
        wirelessDebugging: true,
      };
    },
    'log.start': ({ deviceId }) => {
      this.host.ready(deviceId, 'syslog');
      const sessionId = this.id('log');
      this.logs.set(sessionId, { deviceId, seq: 0, lines: [], running: true });
      this.host.after(100, () => this.pump(sessionId));
      return { sessionId };
    },
    'log.stop': ({ sessionId }) => {
      const session = this.logs.get(sessionId);
      if (session) session.running = false;
      this.logs.delete(sessionId);
      return {};
    },
    'log.clear': ({ sessionId }) => {
      const session = this.logs.get(sessionId);
      if (!session) throw this.host.fault('path_not_found', 'That log is no longer running.');
      session.lines = [];
      return {};
    },
    'log.export': ({ sessionId, minLevel, search, destination }) => {
      const session = this.logs.get(sessionId);
      if (!session) throw this.host.fault('path_not_found', 'That log is no longer running.');
      const order = ['verbose', 'debug', 'info', 'notice', 'warning', 'error', 'fault'];
      const min = order.indexOf(minLevel ?? 'verbose');
      const needle = (search ?? '').toLowerCase();
      const count = session.lines.filter(
        (l) => order.indexOf(l.level) >= min && (!needle || `${l.process} ${l.tag ?? ''} ${l.message}`.toLowerCase().includes(needle)),
      ).length;
      return { path: `${destination ?? `${DOCS}\\Logs`}\\Phone log ${new Date(this.host.now()).toISOString().slice(0, 10)}.txt`, count };
    },
    'crash.list': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'crashReports');
      return {
        reports: this.crashList(device).map(({ text: _text, ...report }) => report),
        canDelete: device.platform === 'ios',
        unreadable: device.platform === 'ios' ? [] : ['tombstones', 'anr'],
      };
    },
    'crash.read': ({ deviceId, id }) => {
      const device = this.host.ready(deviceId, 'crashReports');
      const report = this.crashList(device).find((r) => r.id === id);
      if (!report) throw this.host.fault('path_not_found', 'That crash report is no longer on the phone.');
      return { text: report.text, truncated: false, size: report.size };
    },
    'crash.export': ({ deviceId, ids, destination }) => {
      const device = this.host.ready(deviceId, 'crashReports');
      const list = this.crashList(device).filter((r) => ids.includes(r.id));
      if (list.length === 0) throw this.host.badParams('ids is empty.');
      const folder = destination ?? `${DOCS}\\Crash reports\\${device.name}`;
      return { folder, files: list.map((r) => `${folder}\\${r.fileName}`) };
    },
    'crash.delete': ({ deviceId, ids }) => {
      const device = this.host.ready(deviceId, 'crashReports');
      if (device.platform !== 'ios') throw this.host.fault('permission_denied', 'Android keeps these reports where only the system can delete them.');
      const list = this.crashList(device);
      const before = list.length;
      this.crashes.set(device.id, list.filter((r) => !ids.includes(r.id)));
      return { deleted: before - this.crashes.get(device.id)!.length };
    },
    'report.preview': ({ deviceId, language, revealSerials }) => {
      const device = this.host.ready(deviceId, 'info');
      const rows = this.host
        .details(deviceId)
        .rows.map((r) => `<tr><th>${escapeHtml(r.label)}</th><td>${escapeHtml(r.sensitive && !revealSerials ? r.value.replace(/[^\s:-]/g, '•') : r.value)}</td></tr>`)
        .join('');
      const dir = language === 'fa' ? 'rtl' : 'ltr';
      return {
        html:
          `<!doctype html><html lang="${language}" dir="${dir}"><head><meta charset="utf-8"><style>body{font-family:'Segoe UI',Tahoma,sans-serif;margin:24px;color:#1b1b1b;background:#fff}` +
          `th{text-align:start;padding:6px 8px;color:#424242}td{padding:6px 8px}h1{margin:0 0 4px}</style></head><body>` +
          `<h1>${escapeHtml(device.name)}</h1><p>${language === 'fa' ? 'گزارش دستگاه (نمونه)' : 'Device report (sample)'}</p><table>${rows}</table></body></html>`,
      };
    },
    'report.save': ({ deviceId, format, destination }) => {
      const device = this.host.ready(deviceId, 'info');
      const folder = destination ?? `${DOCS}\\Reports`;
      return { path: `${folder}\\DeviceDeck report – ${device.name}.${format}`, count: 1 };
    },
    'heic.status': () => ({ available: true }),
    'heic.convert': ({ files, quality, destination }) => {
      if (!Number.isInteger(quality) || quality < 1 || quality > 100) throw this.host.badParams('quality is 1 to 100.');
      if (files.length === 0) throw this.host.badParams('files must be full paths on this PC.');
      // A dropped folder counts as 24 photos, a file as one.
      const names = files.flatMap((f) =>
        /\.(heic|heif|hif)$/i.test(f) ? [f] : Array.from({ length: 24 }, (_, i) => `${f}\\IMG_${String(4100 + i).padStart(4, '0')}.HEIC`),
      );
      const job: ToolboxJob = { id: this.id('heic'), kind: 'heic', state: 'running', total: names.length, done: 0, failed: 0, current: null, outputs: [], errors: [] };
      this.jobs.set(job.id, job);
      const step = (index: number) => {
        const current = this.jobs.get(job.id);
        if (!current || current.state !== 'running') return;
        if (index >= names.length) {
          const finished = { ...current, current: null, state: 'done' as const };
          this.jobs.set(job.id, finished);
          this.host.emit('toolbox.job', finished);
          return;
        }
        const source = names[index]!;
        const output = (destination ? `${destination}\\${source.split('\\').pop()}` : source).replace(/\.(heic|heif|hif)$/i, '.jpg');
        const next = { ...current, current: source.split('\\').pop() ?? source, done: current.done + 1, outputs: [...current.outputs, output].slice(0, 20) };
        this.jobs.set(job.id, next);
        this.host.emit('toolbox.job', next);
        this.host.after(150, () => step(index + 1));
      };
      this.host.after(100, () => step(0));
      return { jobId: job.id, total: job.total };
    },
    'toolbox.cancelJob': ({ jobId }) => {
      const job = this.jobs.get(jobId);
      if (!job || job.state !== 'running') throw this.host.fault('path_not_found', 'That job has already finished.');
      const cancelled = { ...job, current: null, state: 'cancelled' as const };
      this.jobs.set(jobId, cancelled);
      this.host.emit('toolbox.job', cancelled);
      return {};
    },
    'mirror.open': ({ deviceId }) => {
      this.host.ready(deviceId, 'screenMirror');
      return {};
    },
    'wifi.status': () => ({ wirelessDebugging: true }),
    'wifi.pair': ({ host, port, code }) => {
      if (!host || !Number.isInteger(port)) throw this.host.badParams('host and port are required.');
      if (code !== '123456') throw this.host.fault('unauthorized', 'The pairing code is wrong or has expired. Get a new one on the phone.');
      return { serial: null, message: `Successfully paired to ${host}:${port} [guid=adb-R5CY10QWERT-mock]` };
    },
    'wifi.connect': ({ host, port }) => {
      if (!host || !Number.isInteger(port)) throw this.host.badParams('host and port are required.');
      return { serial: `${host}:${port}`, message: `connected to ${host}:${port}` };
    },
    'wifi.disconnect': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'info');
      if (device.connection !== 'wifi') throw this.host.fault('not_supported', 'That phone is connected by USB.');
      return {};
    },
    'wifi.qrStart': () => {
      const sessionId = this.id('qr');
      const name = `devicedeck-${(hashString(sessionId) % 1_000_000).toString(36)}`;
      const password = `mock${hashString(name).toString(36)}`;
      this.qr.add(sessionId);
      const stage = (ms: number, s: 'waiting' | 'pairing' | 'connecting' | 'done', serial: string | null = null) =>
        this.host.after(ms, () => {
          if (this.qr.has(sessionId)) this.host.emit('wifi.qr', { sessionId, stage: s, serial, message: null, error: null });
          if (s === 'done') this.qr.delete(sessionId);
        });
      stage(50, 'waiting');
      stage(6000, 'pairing');
      stage(7500, 'connecting');
      stage(8500, 'done', '192.168.1.42:41917');
      return { sessionId, name, password, payload: `WIFI:T:ADB;S:${name};P:${password};;` };
    },
    'wifi.qrCancel': ({ sessionId }) => {
      this.qr.delete(sessionId);
      return {};
    },
    'wifi.iphoneSync': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'info');
      if (device.platform !== 'ios') throw this.host.fault('not_supported', 'Only an iPhone has the Wi-Fi sync switch.');
      return { enabled: this.wifiSync };
    },
    'wifi.setIphoneSync': ({ deviceId, enabled }) => {
      const device = this.host.ready(deviceId, 'info');
      if (device.platform !== 'ios') throw this.host.fault('not_supported', 'Only an iPhone has the Wi-Fi sync switch.');
      this.wifiSync = enabled;
      return { enabled };
    },
    'ringtone.create': ({ deviceId, files, startSeconds, endSeconds, name, setAsDefault }) => {
      const device = this.host.ready(deviceId, 'info');
      if (device.platform === 'ios') {
        throw this.host.fault('not_supported', 'An iPhone takes ringtones only through a Finder or iTunes sync, not over this USB connection.');
      }
      if (files.length !== 1) throw this.host.badParams('files must hold one full path on this PC.');
      if (!(endSeconds > startSeconds) || startSeconds < 0 || endSeconds - startSeconds > 300) {
        throw this.host.badParams('A ringtone runs from startSeconds to endSeconds, at most five minutes.');
      }
      const source = files[0]!.split('\\').pop() ?? 'Ringtone.mp3';
      const extension = /\.(mp3|wav)$/i.test(source) ? source.slice(source.lastIndexOf('.')).toLowerCase() : '.m4a';
      const stem = (name?.trim() || source.replace(/\.[^.]+$/, '')).replace(/[\\/:*?"<>|]/g, '_');
      return { devicePath: `/sdcard/Ringtones/${stem}${extension}`, isDefault: setAsDefault, bytes: Math.round((endSeconds - startSeconds) * 24_000) };
    },
    'mirror.start': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'screenMirror');
      return { sessionId: this.id('mirror'), deviceName: device.marketingName ?? device.name, codec: 'h264' };
    },
    'mirror.input': () => ({}),
    'mirror.stop': () => ({}),
    'mirror.screenshot': ({ deviceId }) => {
      const device = this.host.ready(deviceId, 'screenshot');
      return { path: `C:\\Users\\Nima\\Pictures\\DeviceDeck\\Screenshot ${device.name}.png` };
    },
  };
}
