// The mock backend's backups (round 2): a few backups of the sample phones and an old iPhone in iTunes' folder,
// backup / restore / encryption jobs that run on simulated time (and wait when the phone is unplugged), browsing,
// contacts, conversations and exports. Same methods and shapes as docs/bridge-contract.md.

import type { BridgeEvents, MethodParams, MethodResult } from '../methods';
import type {
  BackupDomain,
  BackupDomainKind,
  BackupEntry,
  BackupInfo,
  BackupJob,
  BackupJobKind,
  BackupPhase,
  Contact,
  Conversation,
  DeviceCapability,
  DeviceSummary,
  Message,
  PrunePlan,
  Settings,
} from '../types';
import { ANDROID_ID, GB, IPHONE_ID, KB, MB } from './mock-data';
import { createRandom, hashString } from './random';

/** The password of the sample encrypted backup (and of the iPhone once encryption is turned on in the mock). */
export const MOCK_BACKUP_PASSWORD = '1234';

const OLD_IPHONE_ID = '00008030-000A1B2C3D4E5F60';
const DAY = 86_400_000;

export type BackupMethodName =
  | 'backup.list'
  | 'backup.device'
  | 'backup.start'
  | 'backup.restore'
  | 'backup.setEncryption'
  | 'backup.jobs'
  | 'backup.cancel'
  | 'backup.prunePlan'
  | 'backup.deletePlan'
  | 'backup.delete'
  | 'backup.unlock'
  | 'backup.lock'
  | 'backup.domains'
  | 'backup.files'
  | 'backup.extract'
  | 'contacts.list'
  | 'contacts.export'
  | 'messages.conversations'
  | 'messages.list'
  | 'messages.export';

type Handler<M extends BackupMethodName> = (params: MethodParams<M>) => MethodResult<M>;

/** What the backups need from the rest of the mock. */
export interface MockBackupContext {
  emit(event: 'backup.updated', params: BridgeEvents['backup.updated']): void;
  now(): number;
  device(id: string): DeviceSummary | undefined;
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  settings(): Settings;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
  /** The phone restarts after a restore: gone, then back. */
  restart(deviceId: string): void;
}

interface Stage {
  phase: BackupPhase;
  ms: number;
}

interface RunningJob {
  job: BackupJob;
  stages: Stage[];
  stage: number;
  elapsed: number;
  /** Bytes the whole run moves, for progress. */
  plannedBytes: number;
  plannedFiles: number;
  finish(job: BackupJob): void;
}

interface StoredBackup {
  info: BackupInfo;
  /** iPhone backups made by the mock share content by device. */
  seed: number;
}

export class MockBackups {
  private readonly backups: StoredBackup[] = [];
  private readonly jobs: BackupJob[] = [];
  private readonly running: RunningJob[] = [];
  private readonly unlocked = new Set<string>();
  /** deviceId → backup password, when the phone encrypts its backups. */
  private readonly encryption = new Map<string, string>();
  private nextJob = 1;
  private seeded = false;

  /** The context's functions are called lazily: the backend creates this before its own constructor body runs. */
  constructor(private readonly ctx: MockBackupContext) {}

  /** The sample backups, made on first use so their dates follow the mock's clock. */
  private seed(): void {
    if (this.seeded) return;
    this.seeded = true;
    const ctx = this.ctx;
    const now = ctx.now();
    const iphone = (daysAgo: number, size: number, minutesAt: number): StoredBackup => {
      const created = new Date(now - daysAgo * DAY);
      created.setHours(Math.floor(minutesAt / 60), minutesAt % 60, 0, 0);
      return {
        seed: 1,
        info: {
          id: `dd:${IPHONE_ID}-${stamp(created)}`,
          deviceId: IPHONE_ID,
          deviceName: 'Nima’s iPhone',
          platform: 'ios',
          model: 'iPhone18,1',
          marketingName: 'iPhone 17 Pro',
          osVersion: '26.1',
          createdAt: created.toISOString(),
          sizeBytes: size,
          encrypted: false,
          state: 'complete',
          source: 'deviceDeck',
          path: `C:\\Users\\Nima\\DeviceDeck\\Backups\\${IPHONE_ID}-${stamp(created)}`,
          apps: 86,
          locked: false,
        },
      };
    };
    this.backups.push(iphone(2, Math.round(118.4 * GB), 21 * 60 + 14), iphone(9, Math.round(117.1 * GB), 20 * 60 + 2), iphone(16, Math.round(115.8 * GB), 22 * 60 + 41));

    const galaxy = new Date(now - 5 * DAY);
    galaxy.setHours(18, 30, 0, 0);
    const galaxyUnfinished = new Date(now - DAY);
    galaxyUnfinished.setHours(9, 12, 0, 0);
    const android = (created: Date, state: BackupInfo['state'], size: number): StoredBackup => ({
      seed: 2,
      info: {
        id: `dd:${ANDROID_ID}-${stamp(created)}`,
        deviceId: ANDROID_ID,
        deviceName: 'Galaxy S25 Ultra',
        platform: 'android',
        model: 'SM-S938B',
        marketingName: 'Galaxy S25 Ultra',
        osVersion: '16',
        createdAt: created.toISOString(),
        sizeBytes: size,
        encrypted: false,
        state,
        source: 'deviceDeck',
        path: `C:\\Users\\Nima\\DeviceDeck\\Backups\\${ANDROID_ID}-${stamp(created)}`,
        apps: 64,
        locked: false,
      },
    });
    this.backups.push(android(galaxy, 'complete', Math.round(38.2 * GB)), android(galaxyUnfinished, 'incomplete', Math.round(12.9 * GB)));

    const old = new Date('2024-03-17T19:05:00');
    this.backups.push({
      seed: 3,
      info: {
        id: `apple0:${OLD_IPHONE_ID}`,
        deviceId: OLD_IPHONE_ID,
        deviceName: 'iPhone 12 (old)',
        platform: 'ios',
        model: 'iPhone13,2',
        marketingName: 'iPhone 12',
        osVersion: '17.4',
        createdAt: old.toISOString(),
        sizeBytes: Math.round(54.6 * GB),
        encrypted: true,
        state: 'complete',
        source: 'apple',
        path: `C:\\Users\\Nima\\Apple\\MobileSync\\Backup\\${OLD_IPHONE_ID}`,
        apps: 52,
        locked: true,
      },
    });
  }

  // ---------- simulated time ----------

  tick(ms: number): void {
    this.seed();
    for (const run of [...this.running]) {
      const job = run.job;
      if (job.state === 'cancelled') {
        this.running.splice(this.running.indexOf(run), 1);
        continue;
      }
      if (job.deviceId && job.kind !== 'extract' && job.kind !== 'export') {
        const ready = this.ctx.device(job.deviceId)?.state === 'ready';
        if (!ready && job.state === 'running') {
          if (job.kind === 'backup') {
            this.update(job, { state: 'waitingForDevice', phase: null, bytesPerSecond: null });
          } else {
            this.fail(run, 'device_disconnected', `${job.deviceName} was disconnected. Connect it and start again.`);
          }
          continue;
        }
        if (ready && job.state === 'waitingForDevice') this.update(job, { state: 'running' });
        if (!ready) continue;
      }

      run.elapsed += ms;
      let stage = run.stages[run.stage]!;
      while (run.elapsed >= stage.ms && run.stage < run.stages.length - 1) {
        run.elapsed -= stage.ms;
        run.stage++;
        stage = run.stages[run.stage]!;
      }
      if (run.stage === run.stages.length - 1 && run.elapsed >= stage.ms) {
        this.running.splice(this.running.indexOf(run), 1);
        run.finish(job);
        continue;
      }
      const transferring = stage.phase === 'transferring';
      const fraction = transferring ? Math.min(1, run.elapsed / stage.ms) : stage.phase === 'finishing' ? 1 : 0;
      const totalMs = stage.ms;
      this.update(job, {
        phase: stage.phase,
        percent: Math.round((transferring ? 3 + 95 * fraction : stage.phase === 'finishing' ? 99 : job.percent ?? 1) * 10) / 10,
        bytes: Math.round(run.plannedBytes * fraction),
        files: Math.round(run.plannedFiles * fraction),
        bytesPerSecond: transferring && totalMs > 0 ? Math.round(run.plannedBytes / (totalMs / 1000)) : null,
      });
    }
  }

  // ---------- handlers ----------

  readonly handlers: { [M in BackupMethodName]: Handler<M> } = {
    'backup.list': ({ deviceId }) => this.visible().filter((b) => !deviceId || b.deviceId === deviceId),
    'backup.device': ({ deviceId }) => {
      const device = this.ctx.device(deviceId);
      if (!device) throw this.ctx.fault('device_not_found');
      const settings = this.ctx.settings();
      const ios = device.platform === 'ios';
      return {
        deviceId,
        platform: device.platform,
        folder: settings.backupFolder,
        keep: settings.backupKeep,
        keepsHistory: true,
        freeBytes: Math.round(412.7 * GB),
        encrypted: ios ? this.encryption.has(deviceId) : null,
        cloudBackupEnabled: ios ? false : null,
        needsHelperApp: device.platform === 'android',
        lastBackup: this.visible().find((b) => b.deviceId === deviceId && b.state === 'complete') ?? null,
      };
    },
    'backup.start': ({ deviceId, full }) => {
      const device = this.ctx.requireReady(deviceId, 'backup');
      this.requireIdle(device);
      const ios = device.platform === 'ios';
      const last = this.visible().find((b) => b.deviceId === deviceId && b.state === 'complete');
      const plannedBytes = ios ? (full || !last ? Math.round(118.9 * GB) : Math.round(2.4 * GB)) : Math.round(38.6 * GB);
      const plannedFiles = ios ? (full || !last ? 184_220 : 2_318) : 6_240;
      const job = this.newJob('backup', device, `Backing up ${device.name}`);
      if (!ios) {
        job.totalBytes = plannedBytes;
        job.totalFiles = plannedFiles;
      }
      this.start(job, plannedBytes, plannedFiles, [
        { phase: 'starting', ms: 600 },
        { phase: 'preparing', ms: ios ? 1500 : 800 },
        { phase: 'transferring', ms: 12_000 },
        { phase: 'finishing', ms: 800 },
      ], (j) => this.finishBackup(j, device, plannedBytes));
      return { ...job };
    },
    'backup.restore': ({ deviceId, backupId, confirmName, password }) => {
      const device = this.ctx.requireReady(deviceId, 'restore');
      if (confirmName.trim().toLocaleLowerCase() !== device.name.trim().toLocaleLowerCase()) {
        throw this.ctx.badParams('Invalid params: To confirm the restore, type the phone’s name exactly as it is shown.');
      }
      const backup = this.find(backupId);
      if (backup.platform !== 'ios') throw this.ctx.fault('not_supported', 'Only iPhone backups can be restored onto an iPhone.');
      if (backup.state !== 'complete') throw this.ctx.fault('backup_corrupt', 'This backup isn’t complete, so it can’t be restored.');
      if (backup.encrypted && !password) throw this.ctx.fault('password_required', 'This backup is encrypted. Enter its password to restore it.');
      if (backup.encrypted && password !== MOCK_BACKUP_PASSWORD) throw this.ctx.fault('wrong_password', 'That isn’t the password of this backup.');
      this.requireIdle(device);
      const job = this.newJob('restore', device, `Restoring ${device.name} from the backup of ${backup.deviceName}`);
      job.backupId = backupId;
      this.start(job, backup.sizeBytes, 160_000, [
        { phase: 'starting', ms: 600 },
        { phase: 'preparing', ms: 1200 },
        { phase: 'transferring', ms: 10_000 },
        { phase: 'finishing', ms: 800 },
      ], (j) => {
        this.complete(j);
        this.ctx.restart(device.id);
      });
      return { ...job };
    },
    'backup.setEncryption': ({ deviceId, enable, password, oldPassword }) => {
      const device = this.ctx.requireReady(deviceId, 'backup');
      if (device.platform !== 'ios') throw this.ctx.fault('not_supported');
      if (enable && !password) throw this.ctx.badParams('Invalid params: Choose a password for the backups.');
      if (!enable && !oldPassword) throw this.ctx.badParams('Invalid params: Enter the current backup password to turn encryption off.');
      this.requireIdle(device);
      const current = this.encryption.get(deviceId);
      const title = !enable ? `Turning off backup encryption on ${device.name}` : oldPassword ? `Changing the backup password of ${device.name}` : `Turning on backup encryption on ${device.name}`;
      const job = this.newJob('encryption', device, title);
      this.start(job, 0, 0, [
        { phase: 'starting', ms: 300 },
        { phase: 'waitingForPhone', ms: 3000 },
      ], (j) => {
        if (current !== undefined && oldPassword !== current) {
          this.update(j, { state: 'failed', phase: null, error: { code: 'wrong_password', message: 'The backup password isn’t right. The iPhone said: Incorrect password (error 207).' }, finishedAt: this.iso() });
          return;
        }
        if (enable) this.encryption.set(deviceId, password!);
        else this.encryption.delete(deviceId);
        this.complete(j);
      });
      return { ...job };
    },
    'backup.jobs': () => this.jobs.map((j) => ({ ...j })),
    'backup.cancel': ({ jobId }) => {
      const job = this.jobs.find((j) => j.id === jobId);
      if (!job) throw this.ctx.badParams('Invalid params: No backup job has that id.');
      if (['queued', 'running', 'waitingForDevice'].includes(job.state)) this.update(job, { state: 'cancelled', phase: null, finishedAt: this.iso() });
      return {};
    },
    'backup.prunePlan': ({ deviceId }) => this.prunePlan(deviceId) ?? { deviceId, keep: this.ctx.settings().backupKeep, backups: [], freedBytes: 0 },
    'backup.deletePlan': ({ backupIds }) => {
      const doomed = this.doomed(backupIds);
      return { deviceId: doomed[0]?.deviceId ?? '', keep: this.ctx.settings().backupKeep, backups: doomed, freedBytes: this.freed(doomed) };
    },
    'backup.delete': ({ backupIds }) => {
      const doomed = this.doomed(backupIds);
      const freedBytes = this.freed(doomed);
      for (const backup of doomed) this.backups.splice(this.backups.findIndex((b) => b.info.id === backup.id), 1);
      return { deleted: doomed.length, freedBytes };
    },
    'backup.unlock': ({ backupId, password }) => {
      const backup = this.find(backupId);
      if (!backup.encrypted) return {};
      if (password !== MOCK_BACKUP_PASSWORD) throw this.ctx.fault('wrong_password', 'That isn’t the password of this backup.');
      this.unlocked.add(backupId);
      return {};
    },
    'backup.lock': ({ backupId }) => {
      this.unlocked.delete(backupId);
      return {};
    },
    'backup.domains': ({ backupId }) => domainsOf(this.open(backupId)),
    'backup.files': ({ backupId, domain, path }) => {
      const backup = this.open(backupId);
      const d = domainsOf(backup).find((x) => x.domain === domain);
      if (!d) throw this.ctx.fault('path_not_found', 'That domain isn’t in this backup.');
      return filesOf(d, path);
    },
    'backup.extract': ({ backupId, items, destination }) => {
      const backup = this.open(backupId);
      if (!items.length) throw this.ctx.badParams('Invalid params: items is empty.');
      const leaf = items[0]!.path.split('/').filter(Boolean).pop() ?? items[0]!.domain;
      const job = this.newJob('extract', null, items.length === 1 ? `Extracting ${leaf} from the backup of ${backup.deviceName}` : `Extracting ${items.length} items from the backup of ${backup.deviceName}`, backup.deviceName);
      job.backupId = backupId;
      job.destination = destination;
      job.totalFiles = 12 * items.length;
      job.totalBytes = 48 * MB * items.length;
      this.start(job, job.totalBytes, job.totalFiles, [{ phase: 'transferring', ms: 2500 }], (j) => this.complete(j));
      return { ...job };
    },
    'contacts.list': ({ backupId }) => contactsOf(this.openWithContacts(backupId)),
    'contacts.export': ({ backupId, format, destination, contactIds }) => {
      const backup = this.openWithContacts(backupId);
      const count = contactIds?.length || contactsOf(backup).length;
      const date = (backup.createdAt ?? this.iso()).slice(0, 10);
      const job = this.newJob('export', null, `Saving contacts from the backup of ${backup.deviceName}`, backup.deviceName);
      job.backupId = backupId;
      job.destination = `${destination}\\Contacts - ${backup.deviceName} - ${date}.${format === 'vCard' ? 'vcf' : 'csv'}`;
      job.totalFiles = count;
      this.start(job, count * 2 * KB, count, [{ phase: 'transferring', ms: 900 }], (j) => this.complete(j));
      return { ...job };
    },
    'messages.conversations': ({ backupId }) => conversationsOf(this.openWithContacts(backupId)).map((c) => c.conversation),
    'messages.list': ({ backupId, conversationId, offset, limit }) => {
      if (offset < 0 || limit < 1 || limit > 1000) throw this.ctx.badParams('Invalid params: offset must be 0 or more and limit between 1 and 1000.');
      const found = conversationsOf(this.openWithContacts(backupId)).find((c) => c.conversation.id === conversationId);
      if (!found) throw this.ctx.fault('path_not_found', 'That conversation isn’t in this backup.');
      return { items: found.messages.slice(offset, offset + limit), total: found.messages.length };
    },
    'messages.export': ({ backupId, conversationIds, format, destination }) => {
      const backup = this.openWithContacts(backupId);
      if (!conversationIds.length) throw this.ctx.badParams('Invalid params: conversationIds is empty.');
      const job = this.newJob(
        'export',
        null,
        conversationIds.length === 1 ? `Saving a conversation from the backup of ${backup.deviceName}` : `Saving ${conversationIds.length} conversations from the backup of ${backup.deviceName}`,
        backup.deviceName,
      );
      job.backupId = backupId;
      job.destination = destination;
      job.totalFiles = conversationIds.length * 6;
      this.start(job, conversationIds.length * 30 * MB, job.totalFiles, [{ phase: 'transferring', ms: format === 'pdf' ? 3000 : 1800 }], (j) => this.complete(j));
      return { ...job };
    },
  };

  // ---------- jobs ----------

  private newJob(kind: BackupJobKind, device: DeviceSummary | null, title: string, deviceName?: string): BackupJob {
    return {
      id: `bk-${this.nextJob++}`,
      deviceId: device?.id ?? null,
      deviceName: device?.name ?? deviceName ?? '',
      kind,
      title,
      state: 'running',
      phase: null,
      percent: null,
      bytes: 0,
      files: 0,
      totalBytes: null,
      totalFiles: null,
      bytesPerSecond: null,
      backupId: null,
      destination: null,
      prune: null,
      error: null,
      createdAt: this.iso(),
      finishedAt: null,
    };
  }

  private start(job: BackupJob, plannedBytes: number, plannedFiles: number, stages: Stage[], finish: (job: BackupJob) => void): void {
    job.phase = stages[0]!.phase;
    this.jobs.push(job);
    this.running.push({ job, stages, stage: 0, elapsed: 0, plannedBytes, plannedFiles, finish });
    const finished = this.jobs.filter((j) => ['done', 'failed', 'cancelled'].includes(j.state));
    for (const old of finished.slice(0, Math.max(0, finished.length - 50))) this.jobs.splice(this.jobs.indexOf(old), 1);
    this.ctx.emit('backup.updated', { ...job });
  }

  private update(job: BackupJob, patch: Partial<BackupJob>): void {
    Object.assign(job, patch);
    this.ctx.emit('backup.updated', { ...job, prune: job.prune ? { ...job.prune } : null });
  }

  private complete(job: BackupJob): void {
    this.update(job, { state: 'done', phase: null, percent: 100, bytesPerSecond: null, finishedAt: this.iso() });
  }

  private fail(run: RunningJob, code: string, message: string): void {
    this.running.splice(this.running.indexOf(run), 1);
    this.update(run.job, { state: 'failed', phase: null, bytesPerSecond: null, error: { code, message }, finishedAt: this.iso() });
  }

  private finishBackup(job: BackupJob, device: DeviceSummary, plannedBytes: number): void {
    const created = new Date(this.ctx.now());
    const ios = device.platform === 'ios';
    const last = this.visible().find((b) => b.deviceId === device.id && b.state === 'complete');
    // An unfinished Android backup of the phone is the one that was just finished.
    const unfinished = this.backups.findIndex((b) => b.info.deviceId === device.id && b.info.state === 'incomplete');
    if (!ios && unfinished >= 0) this.backups.splice(unfinished, 1);
    const id = `dd:${device.id}-${stamp(created)}`;
    this.backups.unshift({
      seed: ios ? 1 : 2,
      info: {
        id,
        deviceId: device.id,
        deviceName: device.name,
        platform: device.platform,
        model: device.model,
        marketingName: device.marketingName,
        osVersion: device.osVersion,
        createdAt: created.toISOString(),
        sizeBytes: ios && last ? last.sizeBytes + Math.round(plannedBytes * 0.4) : plannedBytes,
        encrypted: ios && this.encryption.has(device.id),
        state: 'complete',
        source: 'deviceDeck',
        path: `${this.ctx.settings().backupFolder}\\${id.slice(3)}`,
        apps: ios ? 86 : 64,
        locked: false,
      },
    });
    job.backupId = id;
    job.prune = this.prunePlan(device.id);
    this.complete(job);
  }

  private requireIdle(device: DeviceSummary): void {
    const busy = this.jobs.some((j) => j.deviceId === device.id && ['backup', 'restore', 'encryption'].includes(j.kind) && ['queued', 'running', 'waitingForDevice'].includes(j.state));
    if (busy) throw this.ctx.fault('busy', `${device.name} is already busy with a backup, restore or encryption change.`);
  }

  // ---------- backups ----------

  private visible(): BackupInfo[] {
    this.seed();
    const writing = new Set(this.jobs.filter((j) => j.kind === 'backup' && ['running', 'waitingForDevice'].includes(j.state)).map((j) => j.deviceId));
    return this.backups
      .map((b) => ({
        ...b.info,
        locked: b.info.encrypted && !this.unlocked.has(b.info.id),
        state: b.info.state === 'incomplete' && writing.has(b.info.deviceId) ? ('inProgress' as const) : b.info.state,
      }))
      .sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''));
  }

  private find(backupId: string): BackupInfo {
    const backup = this.visible().find((b) => b.id === backupId);
    if (!backup) throw this.ctx.fault('path_not_found', 'That backup isn’t on this PC any more.');
    return backup;
  }

  private open(backupId: string): BackupInfo {
    const backup = this.find(backupId);
    if (backup.state !== 'complete') throw this.ctx.fault('backup_corrupt', 'This backup isn’t complete, so it can’t be opened. Back up again to finish it.');
    if (backup.locked) throw this.ctx.fault('password_required', 'This backup is encrypted. Enter its password to open it.');
    return backup;
  }

  private openWithContacts(backupId: string): BackupInfo {
    const backup = this.open(backupId);
    if (backup.platform === 'android') {
      throw this.ctx.fault('not_supported', 'Android backups don’t include contacts or messages: Android only lets an app on the phone read them, and DeviceDeck’s helper app isn’t available yet.');
    }
    return backup;
  }

  private prunePlan(deviceId: string): PrunePlan | null {
    const keep = this.ctx.settings().backupKeep;
    const copies = this.visible().filter((b) => b.deviceId === deviceId && b.source === 'deviceDeck' && b.state === 'complete');
    const doomed = copies.slice(keep);
    return doomed.length ? { deviceId, keep, backups: doomed, freedBytes: this.freed(doomed) } : null;
  }

  private doomed(backupIds: string[]): BackupInfo[] {
    if (!backupIds.length) throw this.ctx.badParams('Invalid params: backupIds is empty.');
    return [...new Set(backupIds)].map((id) => {
      const backup = this.find(id);
      if (backup.source !== 'deviceDeck') throw this.ctx.fault('permission_denied', 'Backups made by iTunes or Apple Devices are only read by DeviceDeck. Delete them there.');
      if (backup.state === 'inProgress') throw this.ctx.fault('busy', 'That backup is being written right now.');
      return backup;
    });
  }

  /** Dated copies share unchanged files: deleting one frees what changed since the one before. */
  private freed(doomed: BackupInfo[]): number {
    return doomed.reduce((n, b) => n + (b.platform === 'ios' && b.source === 'deviceDeck' ? Math.round(b.sizeBytes * 0.035) : b.sizeBytes), 0);
  }

  private iso(): string {
    return new Date(this.ctx.now()).toISOString();
  }

  // ---------- round 4 (whatsapp session) ----------

  /** Runs another feature's export of this backup as a backup job, like messages.export (./whatsApp.ts). */
  startExport(backup: BackupInfo, title: string, destination: string, files: number, bytes: number, ms: number): BackupJob {
    const job = this.newJob('export', null, title, backup.deviceName);
    job.backupId = backup.id;
    job.destination = destination;
    job.totalFiles = files;
    this.start(job, bytes, files, [{ phase: 'transferring', ms }], (j) => this.complete(j));
    return { ...job };
  }
}

function stamp(date: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}

// ---------- sample content ----------

const IOS_APPS: [id: string, name: string, gb: number][] = [
  ['net.whatsapp.WhatsApp', 'WhatsApp', 3.2],
  ['ph.telegra.Telegraph', 'Telegram', 1.1],
  ['com.spotify.client', 'Spotify', 1.4],
  ['org.videolan.vlc-ios', 'VLC', 2.1],
  ['com.burbn.instagram', 'Instagram', 0.42],
  ['com.google.Maps', 'Google Maps', 0.21],
  ['cab.snapp.passenger', 'Snapp', 0.08],
  ['ir.divar.app', 'Divar', 0.06],
  ['com.apple.Pages', 'Pages', 0.04],
];

const IOS_SYSTEM: [domain: string, gb: number][] = [
  ['CameraRollDomain', 61.4],
  ['MediaDomain', 4.8],
  ['HomeDomain', 1.6],
  ['HealthDomain', 0.12],
  ['DatabaseDomain', 0.015],
  ['KeyboardDomain', 0.008],
  ['TonesDomain', 0.005],
  ['SystemPreferencesDomain', 0.003],
  ['KeychainDomain', 0.002],
  ['WirelessDomain', 0.001],
];

function domainsOf(backup: BackupInfo): BackupDomain[] {
  if (backup.platform === 'android') {
    return [
      { domain: 'apps', kind: 'system', appId: null, name: 'apps', files: 72, bytes: Math.round(6.4 * GB) },
      { domain: 'media', kind: 'system', appId: null, name: 'media', files: 6168, bytes: Math.round(31.8 * GB) },
    ];
  }
  const random = createRandom(hashString(backup.deviceId));
  const apps: BackupDomain[] = IOS_APPS.map(([id, name, gb]) => ({
    domain: `AppDomain-${id}`,
    kind: 'app' as BackupDomainKind,
    appId: id,
    name,
    files: random.int(40, 2400),
    bytes: Math.round(gb * GB),
  }));
  apps.push(
    { domain: 'AppDomainGroup-group.net.whatsapp.WhatsApp.shared', kind: 'appGroup', appId: 'net.whatsapp.WhatsApp', name: 'WhatsApp', files: 1840, bytes: Math.round(2.4 * GB) },
    { domain: 'AppDomainPlugin-net.whatsapp.WhatsApp.Intents', kind: 'appPlugin', appId: 'net.whatsapp.WhatsApp', name: 'WhatsApp', files: 6, bytes: 48 * KB },
  );
  apps.sort((a, b) => a.name.localeCompare(b.name) || a.kind.localeCompare(b.kind));
  const system: BackupDomain[] = IOS_SYSTEM.map(([domain, gb]) => ({ domain, kind: 'system', appId: null, name: domain, files: random.int(12, 9000), bytes: Math.round(gb * GB) }));
  return [...apps, ...system];
}

/** A small made-up tree per domain: folders at the top, files under them. */
function filesOf(domain: BackupDomain, path: string): BackupEntry[] {
  const trimmed = path.replace(/^\/+|\/+$/g, '');
  const tree = treeOf(domain);
  const children = tree.get(trimmed);
  if (!children) return [];
  return children
    .map((c) => ({ ...c, domain: domain.domain }))
    .sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name));
}

const trees = new Map<string, Map<string, BackupEntry[]>>();

function treeOf(domain: BackupDomain): Map<string, BackupEntry[]> {
  const cached = trees.get(domain.domain);
  if (cached) return cached;
  const random = createRandom(hashString(domain.domain));
  const tree = new Map<string, BackupEntry[]>();
  const modified = (days: number) => new Date(Date.UTC(2026, 9, 15) - days * DAY).toISOString();
  const add = (path: string, size?: number) => {
    const parts = path.split('/');
    for (let i = 0; i < parts.length; i++) {
      const parent = parts.slice(0, i).join('/');
      const full = parts.slice(0, i + 1).join('/');
      const isDir = i < parts.length - 1 || size === undefined;
      const list = tree.get(parent) ?? [];
      if (!list.some((e) => e.path === full)) {
        list.push({ domain: domain.domain, path: full, name: parts[i]!, isDirectory: isDir, size: isDir ? 0 : size!, modified: isDir ? null : modified(random.int(0, 300)) });
      }
      tree.set(parent, list);
      if (isDir && !tree.has(full)) tree.set(full, []);
    }
  };
  switch (domain.domain) {
    case 'CameraRollDomain':
      for (let i = 0; i < 48; i++) add(`Media/DCIM/1${String(Math.floor(i / 24)).padStart(2, '0')}APPLE/IMG_${String(4100 + i).padStart(4, '0')}.HEIC`, random.int(1_400_000, 4_200_000));
      add('Media/PhotoData/Photos.sqlite', 186 * MB);
      break;
    case 'HomeDomain':
      add('Library/AddressBook/AddressBook.sqlitedb', 6 * MB);
      add('Library/AddressBook/AddressBookImages.sqlitedb', 22 * MB);
      add('Library/SMS/sms.db', 412 * MB);
      add('Library/Notes/notes.sqlite', 3 * MB);
      add('Library/Calendar/Calendar.sqlitedb', 9 * MB);
      add('Library/Safari/Bookmarks.db', 2 * MB);
      add('Library/Preferences/com.apple.springboard.plist', 12 * KB);
      add('Library/Preferences/com.apple.Preferences.plist', 4 * KB);
      break;
    case 'MediaDomain':
      for (let i = 0; i < 12; i++) add(`Library/SMS/Attachments/${(i * 7).toString(16).padStart(2, '0')}/0${i}/IMG_${2000 + i}.HEIC`, random.int(900_000, 3_100_000));
      add('Library/Recordings/Recording 1.m4a', 6 * MB);
      break;
    default:
      if (domain.kind === 'system') {
        add(`Library/Preferences/${domain.domain}.plist`, random.int(2_000, 90_000));
        break;
      }
      add('Documents/Exports/Report 2026.pdf', 2 * MB);
      add('Documents/notes.txt', 3 * KB);
      for (let i = 0; i < 6; i++) add(`Documents/Media/file-${i + 1}.dat`, random.int(200_000, 40_000_000));
      add(`Library/Preferences/${domain.appId}.plist`, 18 * KB);
      add('Library/Application Support/store.sqlite', random.int(1, 90) * MB);
      break;
  }
  trees.set(domain.domain, tree);
  return tree;
}

const PEOPLE: [first: string | null, last: string | null, org: string | null][] = [
  ['سارا', 'احمدی', null],
  ['علی', 'رضایی', 'دانشگاه تهران'],
  ['مریم', 'کریمی', null],
  ['رضا', 'محمدی', 'اسنپ'],
  ['نیلوفر', 'حسینی', null],
  ['امیر', 'جعفری', null],
  ['زهرا', 'موسوی', null],
  ['حسین', 'صادقی', 'بانک ملت'],
  ['فاطمه', 'نوری', null],
  ['محمد', 'کاظمی', null],
  ['مامان', null, null],
  ['بابا', null, null],
  ['Anna', 'Schmidt', 'Bosch'],
  ['John', 'Appleseed', 'Apple'],
  ['Emma', 'Wilson', null],
  ['Lucas', 'Martin', null],
  ['Sophie', 'Dubois', 'Airbus'],
  ['Daniel', 'Kim', null],
  ['Olivia', 'Brown', null],
  [null, null, 'Pizza Place'],
  [null, null, 'Dr. Rahimi Dental Clinic'],
];

function contactsOf(backup: BackupInfo): Contact[] {
  const random = createRandom(hashString('contacts' + backup.deviceId));
  return PEOPLE.map(([first, last, org], i): Contact => {
    const name = [first, last].filter(Boolean).join(' ');
    const persian = /[\u0600-\u06FF]/.test(name);
    const phone = persian || !first ? `+98 9${random.int(10, 39)} ${random.int(100, 999)} ${random.int(1000, 9999)}` : `+1 (${random.int(200, 989)}) 555-${random.int(1000, 9999)}`;
    const email = first && !persian ? `${first.toLowerCase()}.${(last ?? 'mail').toLowerCase()}@example.com` : i % 3 === 0 ? `user${i}@example.ir` : null;
    return {
      id: String(i + 1),
      displayName: name || org || phone,
      firstName: first,
      middleName: null,
      lastName: last,
      prefix: null,
      suffix: null,
      nickname: null,
      organization: org,
      department: null,
      jobTitle: org && first ? (persian ? 'کارشناس' : 'Engineer') : null,
      note: i === 1 ? 'کلاس‌های پنجشنبه' : null,
      birthday: i % 4 === 0 ? `19${random.int(70, 99)}-${String(random.int(1, 12)).padStart(2, '0')}-${String(random.int(1, 28)).padStart(2, '0')}` : i === 5 ? '--03-21' : null,
      phones: [{ label: i % 3 === 0 ? 'iphone' : i % 3 === 1 ? 'mobile' : org ? 'work' : 'home', value: phone }],
      emails: email ? [{ label: org ? 'work' : 'home', value: email }] : [],
      urls: i === 13 ? [{ label: 'homePage', value: 'https://www.apple.com' }] : [],
      addresses: i === 0 ? [{ label: 'home', street: 'خیابان ولیعصر، کوچه نوزدهم، پلاک ۱۲', city: 'تهران', region: null, postalCode: '1415', country: 'ایران' }] : [],
      hasPhoto: i % 5 === 0,
      modified: new Date(Date.UTC(2026, 6, 1 + i)).toISOString(),
    };
  }).sort((a, b) => (a.lastName ?? a.firstName ?? a.displayName).localeCompare(b.lastName ?? b.firstName ?? b.displayName));
}

const PERSIAN_LINES = [
  'سلام! خوبی؟',
  'فردا وقت داری؟',
  'ساعت ۵ کافه همیشگی؟',
  'قبول 👍',
  'عکس‌ها رو فرستادم',
  'چقدر قشنگه! ❤️',
  'رسیدی خبر بده',
  'باشه، تو راهم',
  'مرسی بابت امروز 🌷',
  'شب بخیر',
];

const ENGLISH_LINES = [
  'Hi! Are the slides ready for Monday?',
  'Almost — sending them tonight.',
  'Perfect, thanks!',
  'Running 10 minutes late, sorry',
  'No worries 🙂',
  'Can you call me when you’re free?',
  'Here’s the photo from yesterday',
  'Looks great!',
];

const conversationCache = new Map<string, { conversation: Conversation; messages: Message[] }[]>();

function conversationsOf(backup: BackupInfo): { conversation: Conversation; messages: Message[] }[] {
  const key = backup.deviceId;
  const cached = conversationCache.get(key);
  if (cached) return cached;
  const random = createRandom(hashString('messages' + key));
  const contacts = contactsOf(backup);
  const byName = (name: string) => contacts.find((c) => c.displayName === name)!;
  const base = Date.UTC(2026, 9, 14, 17, 0);
  const result: { conversation: Conversation; messages: Message[] }[] = [];

  const add = (id: string, title: string | null, members: Contact[], service: string, count: number, english: boolean, startDaysAgo: number) => {
    const messages: Message[] = [];
    let at = base - startDaysAgo * DAY;
    const group = members.length > 1;
    for (let i = 0; i < count; i++) {
      at += random.int(2, 600) * 60_000;
      const fromMe = random.chance(0.45);
      const sender = members[random.int(0, members.length - 1)]!;
      const photo = random.chance(0.08);
      const lines = english ? ENGLISH_LINES : PERSIAN_LINES;
      messages.push({
        id: `${id}-${i + 1}`,
        kind: 'text',
        date: new Date(at).toISOString(),
        fromMe,
        handle: fromMe ? null : sender.phones[0]!.value.replace(/[^+\d]/g, ''),
        senderName: fromMe ? null : sender.displayName,
        text: photo && random.chance(0.5) ? null : lines[random.int(0, lines.length - 1)]!,
        service,
        attachments: photo
          ? [{ id: `${id}-a${i}`, name: random.chance(0.8) ? `IMG_${2000 + i}.HEIC` : `clip-${i}.mov`, mimeType: 'image/heic', size: random.int(900_000, 3_400_000), inBackup: random.chance(0.85) }]
          : [],
      });
    }
    if (group) messages.unshift({ id: `${id}-0`, kind: 'event', date: new Date(base - startDaysAgo * DAY).toISOString(), fromMe: false, handle: null, senderName: null, text: title, service, attachments: [] });
    const last = messages.at(-1)!;
    result.push({
      conversation: {
        id,
        title: title ?? members.map((m) => m.displayName).join(', '),
        members: members.map((m) => ({ handle: m.phones[0]!.value.replace(/[^+\d]/g, ''), name: m.displayName })),
        isGroup: group,
        service,
        messageCount: messages.length,
        attachmentCount: messages.reduce((n, m) => n + m.attachments.length, 0),
        lastMessageAt: last.date,
        preview: last.text,
      },
      messages,
    });
  };

  add('1', null, [byName('سارا احمدی')], 'iMessage', 240, false, 120);
  add('2', 'خانواده', [byName('مامان'), byName('بابا'), byName('مریم کریمی')], 'iMessage', 420, false, 200);
  add('3', null, [byName('John Appleseed')], 'iMessage', 64, true, 60);
  add('4', 'Team DeviceDeck', [byName('Anna Schmidt'), byName('Lucas Martin'), byName('Emma Wilson')], 'iMessage', 180, true, 90);
  add('5', null, [byName('علی رضایی')], 'SMS', 38, false, 30);
  add('6', null, [byName('Pizza Place')], 'SMS', 6, true, 12);
  add('7', null, [byName('نیلوفر حسینی')], 'iMessage', 88, false, 45);
  add('8', null, [byName('Sophie Dubois')], 'iMessage', 22, true, 20);
  result.sort((a, b) => (b.conversation.lastMessageAt ?? '').localeCompare(a.conversation.lastMessageAt ?? ''));
  conversationCache.set(key, result);
  return result;
}
