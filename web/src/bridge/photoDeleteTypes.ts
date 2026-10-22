// Shapes of docs/bridge-contract.md, "Deleting photos (round 4, photodelete)",
// hand-written from src/DeviceDeck.Core/Abstractions/IPhotoDeleter.cs and Core/PhotoDelete/PhotoDeleteHandler.cs.

import type { TransferStage, TransferState } from './types';

/** Why photos can't be deleted from the PC right now. */
export type PhotoDeleteBlock = 'none' | 'checking' | 'iCloudPhotos' | 'readOnly' | 'locked' | 'notCamera' | 'converted' | 'unsupported';

export type PhotoDeleteDestination = 'unknown' | 'recentlyDeleted' | 'permanent';

export interface PhotoDeleteSupport {
  canDelete: boolean;
  reason: PhotoDeleteBlock;
  /** One technical sentence of what the check saw, for a details fold. */
  detail: string | null;
  /** iPhone: "permanent" as users report, until a delete on this phone shows otherwise. */
  goesTo: PhotoDeleteDestination;
  /** goesTo comes from a delete on this phone in this session. */
  observed: boolean;
  checkedAt: string | null;
  /** iCloud Photos is on: a delete reaches iCloud and the other devices too. Null when unknown. */
  iCloudPhotos: boolean | null;
}

export interface PhotoDeleteItem {
  id: string;
  size: number;
}

export type PhotoDeleteOutcome = 'deleted' | 'mismatch' | 'failed' | 'notTried';

export interface PhotoDeleteResult {
  id: string;
  fileName: string;
  /** What it freed (a Live Photo's video included). */
  size: number;
  outcome: PhotoDeleteOutcome;
  code: string | null;
  /** Why not, or a note (its edit, its video). */
  message: string | null;
  /** Files that went with it ("IMG_0001.MOV"). */
  also: string[];
  wentTo: PhotoDeleteDestination;
}

// v0.4.1 (deletejob): a confirmed delete runs as a transfer job (kind "photosDelete") in the Transfers drawer.

/** What photos.deleteItems and cleanup.deleteMedia answer: the job, at once. */
export interface PhotoDeleteStarted {
  /** Null when nothing was left to delete (Clean-up: every item changed since the analysis). */
  jobId: string | null;
  /** Clean-up: items left out because they changed since the analysis. */
  skipped: number;
}

/**
 * v0.4.2: which items this PC can't delete because Windows lists them nowhere (photos moved over from another iPhone,
 * older imports): photos.deleteReach. Known only once Windows' view was read completely ("ready"); "checking" says how
 * far that got and marks nothing. Android: "ready" with nothing listed.
 */
export interface PhotoDeleteReach {
  deviceId: string;
  state: 'unsupported' | 'checking' | 'ready';
  /** Objects read of all while checking (0 of 0 before the folders are listed). */
  done: number;
  total: number;
  /** Ready: the asset ids to delete on the phone itself. */
  notListed: string[];
  checkedAt: string | null;
}

/** A delete job's results so far (all of them once it finished): photos.deleteJob. */
export interface PhotoDeleteJobStatus {
  jobId: string;
  deviceId: string;
  source: 'photos' | 'cleanup';
  state: TransferState;
  stage: TransferStage | null;
  /** One per confirmed item, in order; items not reached yet say notTried. */
  items: PhotoDeleteResult[];
  deleted: number;
  freedBytes: number;
  goesTo: PhotoDeleteDestination;
  copyJobIds: string[];
  copyIncomplete: boolean;
  skipped: number;
  error: { code: string; message: string } | null;
}
