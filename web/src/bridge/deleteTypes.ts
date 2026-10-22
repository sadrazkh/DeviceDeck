// Shapes of docs/bridge-contract.md, "Every delete as a transfer (v0.6)", hand-written from
// src/DeviceDeck.Core/Deletes/DeleteModels.cs: files and folders, and apps, deleted by a job in the Transfers drawer
// the way photos are since v0.4.1.

import type { TransferJob, TransferKind } from './types';

/** A file or folder the user confirmed: its size as the page showed it (a folder: what it holds, or 0 when not counted). */
export interface FileDeleteItem {
  root: string;
  path: string;
  size: number;
  isDirectory?: boolean;
}

/** An app the user confirmed: its size (app and data) and name as the page showed them. */
export interface AppUninstallItem {
  id: string;
  size: number;
  name?: string | null;
}

/** What starting a delete answers: its job, at once. */
export interface DeleteStarted {
  jobId: string;
}

export type TransferItemState = 'queued' | 'running' | 'done' | 'failed' | 'skipped';

/** One item of a job afterwards (transfers.items): a delete's result per item. */
export interface TransferItemView {
  index: number;
  name: string;
  /** The item's key: a root and path, an app id, a photo id. */
  source: string;
  size: number;
  /** done: it went. skipped: left alone (it changed since the analysis). queued: not tried. */
  state: TransferItemState;
  code: string | null;
  message: string | null;
}

/** Every kind of delete job: each has its own row in the Transfers drawer, with stages instead of bytes. */
export const DELETE_KINDS = ['photosDelete', 'filesDelete', 'appsUninstall'] as const satisfies readonly TransferKind[];
export type DeleteKind = (typeof DELETE_KINDS)[number];

export function isDeleteKind(kind: TransferKind): kind is DeleteKind {
  return (DELETE_KINDS as readonly TransferKind[]).includes(kind);
}

export function isDeleteJob(job: TransferJob): job is TransferJob & { kind: DeleteKind } {
  return isDeleteKind(job.kind);
}
