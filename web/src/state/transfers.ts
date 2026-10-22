// Pure helpers over the transfer queue (grouping for the drawer, the pill's overall percentage).

import type { TransferJob, TransferState } from '../bridge/types';

export type TransferGroup = 'active' | 'queued' | 'done' | 'failed';

export const GROUP_ORDER: TransferGroup[] = ['active', 'queued', 'failed', 'done'];

export function groupOf(state: TransferState): TransferGroup {
  switch (state) {
    case 'running':
    case 'paused':
    case 'waitingForDevice':
    case 'needsDecision':
      return 'active';
    case 'queued':
      return 'queued';
    case 'failed':
      return 'failed';
    case 'done':
    case 'cancelled':
      return 'done';
  }
}

export function groupTransfers(jobs: readonly TransferJob[]): Record<TransferGroup, TransferJob[]> {
  const groups: Record<TransferGroup, TransferJob[]> = { active: [], queued: [], done: [], failed: [] };
  for (const job of jobs) groups[groupOf(job.state)].push(job);
  groups.active.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  groups.queued.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  const finished = (j: TransferJob) => j.finishedAt ?? j.createdAt;
  groups.done.sort((a, b) => finished(b).localeCompare(finished(a)));
  groups.failed.sort((a, b) => finished(b).localeCompare(finished(a)));
  return groups;
}

export function jobFraction(job: TransferJob): number {
  if (job.state === 'done') return 1;
  // v0.4.1: a job in stages (a delete) shows its stage's progress.
  if (job.stage) return job.stage.total > 0 ? Math.min(1, job.stage.done / job.stage.total) : 0;
  if (job.totalBytes > 0) return Math.min(1, job.doneBytes / job.totalBytes);
  if (job.totalItems > 0) return Math.min(1, job.doneItems / job.totalItems);
  return 0;
}

/** What the title-bar pill shows: overall progress of unfinished work, by bytes. */
export function overallProgress(jobs: readonly TransferJob[]): { active: number; fraction: number | null; failed: number } {
  let total = 0;
  let done = 0;
  let active = 0;
  let failed = 0;
  for (const job of jobs) {
    const group = groupOf(job.state);
    if (group === 'failed') failed++;
    if (group !== 'active' && group !== 'queued') continue;
    active++;
    // A delete (v0.4.1) moves in stages, not bytes: it counts as active without a share of the bytes.
    if (job.kind === 'photosDelete') continue;
    total += job.totalBytes;
    done += job.doneBytes;
  }
  return { active, fraction: active === 0 ? null : total > 0 ? done / total : active > 0 && jobs.some(isByteJob) ? 0 : null, failed };
}

function isByteJob(job: TransferJob): boolean {
  const group = groupOf(job.state);
  return job.kind !== 'photosDelete' && (group === 'active' || group === 'queued');
}

export function isFinished(state: TransferState): boolean {
  return state === 'done' || state === 'failed' || state === 'cancelled';
}
