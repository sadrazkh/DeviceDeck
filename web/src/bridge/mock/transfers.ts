// The mock transfer queue: jobs progress over time, pause when their phone goes away,
// resume when it is back, and stop for a decision on a name conflict.

import type { ConflictChoice, TransferConflict, TransferJob, TransferKind } from '../types';
import { MB } from './mock-data';

export interface JobItem {
  name: string;
  size: number;
  /** Set when this item already exists at the destination. */
  existingSize?: number;
}

export interface NewJob {
  deviceId: string;
  kind: TransferKind;
  title: string;
  items: JobItem[];
  /** Fails at the end with this error (an unsigned .ipa, for example). */
  failWith?: { code: string; message: string };
  /** Called once per finished item (to add a copied file to the mock file system, say). */
  onItemDone?: (item: JobItem, renamedTo?: string) => void;
  onDone?: () => void;
  /** Seconds the job waits, queued, before it may start (LocalSend: until the Accept tap). */
  holdSeconds?: number;
}

interface Internal {
  job: TransferJob;
  items: JobItem[];
  index: number;
  itemDoneBytes: number;
  baseSpeed: number;
  /** Seconds left before a queued job may start (the Snapseed install waits a little). */
  holdSeconds: number;
  rule: ConflictChoice | null;
  failWith?: { code: string; message: string };
  onItemDone?: (item: JobItem, renamedTo?: string) => void;
  onDone?: () => void;
  resumeState: 'running' | 'queued' | null;
  speedSamples: number[];
}

/** USB services are a single pipe: a few jobs at once per phone; the rest wait in line. */
export const MAX_RUNNING_PER_DEVICE = 3;

const SPEED: Record<TransferKind, number> = {
  photosExport: 38 * MB,
  photosImport: 30 * MB,
  copyToPc: 36 * MB,
  copyToDevice: 41 * MB,
  appInstall: 24 * MB,
  appExport: 45 * MB,
  dragOut: 36 * MB,
  photosBackup: 36 * MB,
  photosToApp: 32 * MB,
  localSend: 28 * MB,
  whatsAppMedia: 36 * MB, // round 4
};

export class TransferEngine {
  private readonly jobs = new Map<string, Internal>();
  private sequence = 1;
  private readonly offline = new Set<string>();

  constructor(
    private readonly emit: (job: TransferJob) => void,
    private readonly now: () => number = () => Date.now(),
    private conflictRule: () => 'ask' | ConflictChoice = () => 'ask',
  ) {}

  list(): TransferJob[] {
    return [...this.jobs.values()].map((j) => ({ ...j.job }));
  }

  get(jobId: string): TransferJob | undefined {
    const job = this.jobs.get(jobId)?.job;
    return job ? { ...job } : undefined;
  }

  /** Puts already-made jobs (the sample queue) in place, with items made up to match their totals. */
  seed(jobs: TransferJob[], options: { holdSeconds?: Record<string, number> } = {}): void {
    for (const job of jobs) {
      const perItem = job.totalItems > 0 ? job.totalBytes / job.totalItems : job.totalBytes;
      const items: JobItem[] = Array.from({ length: Math.max(1, job.totalItems) }, (_, i) => ({
        name: `item-${i + 1}`,
        size: Math.round(perItem),
      }));
      this.jobs.set(job.id, {
        job: { ...job },
        items,
        index: job.doneItems,
        itemDoneBytes: Math.max(0, job.doneBytes - Math.round(perItem * job.doneItems)),
        baseSpeed: job.bytesPerSecond ?? SPEED[job.kind],
        holdSeconds: options.holdSeconds?.[job.id] ?? 0,
        rule: null,
        resumeState: null,
        speedSamples: [],
      });
    }
  }

  add(spec: NewJob): TransferJob {
    const id = `job-${this.now().toString(36)}-${this.sequence++}`;
    const totalBytes = spec.items.reduce((n, i) => n + i.size, 0);
    const job: TransferJob = {
      id,
      deviceId: spec.deviceId,
      kind: spec.kind,
      title: spec.title,
      state: this.offline.has(spec.deviceId) ? 'waitingForDevice' : 'queued',
      totalItems: spec.items.length,
      doneItems: 0,
      failedItems: 0,
      totalBytes,
      doneBytes: 0,
      bytesPerSecond: null,
      etaSeconds: null,
      conflict: null,
      error: null,
      createdAt: new Date(this.now()).toISOString(),
      finishedAt: null,
    };
    const internal: Internal = {
      job,
      items: spec.items,
      index: 0,
      itemDoneBytes: 0,
      baseSpeed: SPEED[spec.kind],
      holdSeconds: spec.holdSeconds ?? 0,
      rule: null,
      failWith: spec.failWith,
      onItemDone: spec.onItemDone,
      onDone: spec.onDone,
      resumeState: job.state === 'waitingForDevice' ? 'queued' : null,
      speedSamples: [],
    };
    this.jobs.set(id, internal);
    this.emit({ ...job });
    this.schedule();
    return { ...job };
  }

  pause(jobId: string): boolean {
    const j = this.jobs.get(jobId);
    if (!j) return false;
    if (j.job.state === 'running' || j.job.state === 'queued') {
      this.update(j, { state: 'paused', bytesPerSecond: null, etaSeconds: null });
      this.schedule();
    }
    return true;
  }

  /** Resumes a paused job; on a failed job it retries the items that failed. */
  resume(jobId: string): boolean {
    const j = this.jobs.get(jobId);
    if (!j) return false;
    if (j.job.state === 'paused') {
      this.update(j, { state: this.offline.has(j.job.deviceId) ? 'waitingForDevice' : 'queued' });
    } else if (j.job.state === 'failed') {
      j.index = j.job.doneItems;
      j.itemDoneBytes = 0;
      j.failWith = undefined;
      this.update(j, {
        state: this.offline.has(j.job.deviceId) ? 'waitingForDevice' : 'queued',
        failedItems: 0,
        error: null,
        finishedAt: null,
      });
    }
    this.schedule();
    return true;
  }

  cancel(jobId: string): boolean {
    const j = this.jobs.get(jobId);
    if (!j) return false;
    if (!isFinished(j.job.state)) {
      this.update(j, { state: 'cancelled', bytesPerSecond: null, etaSeconds: null, conflict: null, finishedAt: this.iso() });
      this.schedule();
    }
    return true;
  }

  resolveConflict(jobId: string, choice: ConflictChoice, applyToAll: boolean): boolean {
    const j = this.jobs.get(jobId);
    if (!j || j.job.state !== 'needsDecision') return false;
    if (applyToAll) j.rule = choice;
    this.applyChoice(j, choice);
    this.update(j, { state: 'running', conflict: null });
    return true;
  }

  /** The phone went away: running and queued work waits for it. */
  deviceOffline(deviceId: string): void {
    this.offline.add(deviceId);
    for (const j of this.jobs.values()) {
      if (j.job.deviceId !== deviceId) continue;
      if (j.job.state === 'running' || j.job.state === 'queued' || j.job.state === 'needsDecision') {
        j.resumeState = j.job.state === 'queued' ? 'queued' : 'running';
        this.update(j, { state: 'waitingForDevice', bytesPerSecond: null, etaSeconds: null });
      }
    }
  }

  /** The same phone is back: its waiting jobs carry on from the item they were on. */
  deviceOnline(deviceId: string): void {
    this.offline.delete(deviceId);
    for (const j of this.jobs.values()) {
      if (j.job.deviceId !== deviceId || j.job.state !== 'waitingForDevice') continue;
      this.update(j, { state: 'queued', conflict: j.job.conflict });
      j.resumeState = null;
    }
    this.schedule();
  }

  /** Advances every running job by `ms` of simulated time. */
  tick(ms: number): void {
    const seconds = ms / 1000;
    for (const j of this.jobs.values()) {
      if (j.job.state === 'queued' && j.holdSeconds > 0) j.holdSeconds = Math.max(0, j.holdSeconds - seconds);
      if (j.job.state !== 'running') continue;
      this.advance(j, seconds);
    }
    this.schedule();
  }

  setConflictRule(rule: () => 'ask' | ConflictChoice): void {
    this.conflictRule = rule;
  }

  private advance(j: Internal, seconds: number): void {
    // Speed wanders a little around the job's base speed; averaged like the host (5 s window).
    const wobble = 0.86 + 0.28 * Math.abs(Math.sin(this.now() / 1700 + j.job.totalBytes));
    const speed = j.baseSpeed * wobble;
    let budget = speed * seconds;
    let moved = 0;
    while (budget > 0 && j.index < j.items.length) {
      const item = j.items[j.index]!;
      if (j.itemDoneBytes === 0 && item.existingSize !== undefined) {
        const rule = j.rule ?? nonAsk(this.conflictRule());
        if (!rule) {
          const conflict: TransferConflict = { itemName: item.name, existingSize: item.existingSize, newSize: item.size };
          this.update(j, { state: 'needsDecision', conflict, bytesPerSecond: null, etaSeconds: null });
          return;
        }
        this.applyChoice(j, rule);
        continue;
      }
      const left = item.size - j.itemDoneBytes;
      const step = Math.min(left, budget);
      j.itemDoneBytes += step;
      budget -= step;
      moved += step;
      if (j.itemDoneBytes >= item.size) {
        this.finishItem(j, item);
      }
    }
    j.speedSamples.push(moved / seconds);
    if (j.speedSamples.length > 20) j.speedSamples.shift();
    const average = j.speedSamples.reduce((a, b) => a + b, 0) / j.speedSamples.length;
    const doneBytes = Math.min(j.job.totalBytes, j.job.doneBytes + moved);
    const remaining = j.job.totalBytes - doneBytes;

    if (j.index >= j.items.length) {
      if (j.failWith) {
        this.update(j, {
          state: 'failed',
          doneBytes,
          failedItems: j.items.length - j.job.doneItems,
          doneItems: j.job.doneItems,
          error: j.failWith,
          bytesPerSecond: null,
          etaSeconds: null,
          finishedAt: this.iso(),
        });
      } else {
        this.update(j, { state: 'done', doneBytes: j.job.totalBytes, bytesPerSecond: null, etaSeconds: null, finishedAt: this.iso() });
        j.onDone?.();
      }
      return;
    }
    this.update(j, {
      doneBytes,
      bytesPerSecond: Math.round(average),
      etaSeconds: average > 0 ? Math.ceil(remaining / average) : null,
    });
  }

  private applyChoice(j: Internal, choice: ConflictChoice): void {
    const item = j.items[j.index];
    if (!item) return;
    if (choice === 'skip') {
      // A skipped item counts as handled; its bytes leave the total.
      j.index++;
      j.itemDoneBytes = 0;
      this.update(j, { doneItems: j.job.doneItems + 1, totalBytes: j.job.totalBytes - item.size });
      return;
    }
    if (choice === 'keepBoth') {
      item.name = keepBothName(item.name);
    }
    delete item.existingSize;
  }

  private finishItem(j: Internal, item: JobItem): void {
    j.index++;
    j.itemDoneBytes = 0;
    if (j.failWith) return; // counted as failed when the job ends
    j.job.doneItems += 1;
    j.onItemDone?.(item);
  }

  /** Starts queued jobs while a phone has free slots. */
  private schedule(): void {
    const running = new Map<string, number>();
    for (const j of this.jobs.values()) {
      if (j.job.state === 'running' || j.job.state === 'needsDecision') {
        running.set(j.job.deviceId, (running.get(j.job.deviceId) ?? 0) + 1);
      }
    }
    const queued = [...this.jobs.values()]
      .filter((j) => j.job.state === 'queued')
      .sort((a, b) => a.job.createdAt.localeCompare(b.job.createdAt));
    for (const j of queued) {
      if (j.holdSeconds > 0 || this.offline.has(j.job.deviceId)) continue;
      const count = running.get(j.job.deviceId) ?? 0;
      if (count >= MAX_RUNNING_PER_DEVICE) continue;
      running.set(j.job.deviceId, count + 1);
      this.update(j, { state: j.job.conflict ? 'needsDecision' : 'running' });
    }
  }

  // ---------- Desktop (round 2): transfers.retry and transfers.clear ----------

  /** A failed or cancelled job (or one done with failures) runs again from the item it stopped at, after `holdSeconds`. */
  retry(jobId: string, holdSeconds = 0): 'ok' | 'missing' | 'nothing' {
    const j = this.jobs.get(jobId);
    if (!j) return 'missing';
    const s = j.job.state;
    if (!(s === 'failed' || s === 'cancelled' || (s === 'done' && j.job.failedItems > 0))) return 'nothing';
    j.index = j.job.doneItems;
    j.itemDoneBytes = 0;
    j.failWith = undefined;
    j.holdSeconds = holdSeconds;
    this.update(j, {
      state: this.offline.has(j.job.deviceId) ? 'waitingForDevice' : 'queued',
      failedItems: 0,
      error: null,
      finishedAt: null,
    });
    this.schedule();
    return 'ok';
  }

  /** Changes how long a queued job still waits before it may start. */
  setHold(jobId: string, seconds: number): void {
    const j = this.jobs.get(jobId);
    if (j) j.holdSeconds = seconds;
    this.schedule();
  }

  /** Ends a job at once: every item not done yet fails with this error (LocalSend: a PIN, a decline, a busy phone). */
  fail(jobId: string, error: { code: string; message: string }): boolean {
    const j = this.jobs.get(jobId);
    if (!j || isFinished(j.job.state)) return false;
    j.holdSeconds = 0;
    this.update(j, {
      state: 'failed',
      failedItems: j.items.length - j.job.doneItems,
      error,
      bytesPerSecond: null,
      etaSeconds: null,
      finishedAt: this.iso(),
    });
    this.schedule();
    return true;
  }

  /** Removes finished jobs (all, or those in jobIds); returns how many went. */
  clear(jobIds?: string[]): number {
    let cleared = 0;
    for (const [id, j] of this.jobs) {
      if (isFinished(j.job.state) && (!jobIds || jobIds.includes(id))) {
        this.jobs.delete(id);
        cleared++;
      }
    }
    return cleared;
  }

  private update(j: Internal, patch: Partial<TransferJob>): void {
    j.job = { ...j.job, ...patch };
    this.emit({ ...j.job });
  }

  private iso(): string {
    return new Date(this.now()).toISOString();
  }
}

function nonAsk(rule: 'ask' | ConflictChoice): ConflictChoice | null {
  return rule === 'ask' ? null : rule;
}

export function isFinished(state: TransferJob['state']): boolean {
  return state === 'done' || state === 'failed' || state === 'cancelled';
}

/** "Persepolis.mkv" → "Persepolis (2).mkv" */
export function keepBothName(name: string): string {
  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : '';
  const match = /^(.*) \((\d+)\)$/.exec(stem);
  if (match) return `${match[1]} (${Number(match[2]) + 1})${ext}`;
  return `${stem} (2)${ext}`;
}
