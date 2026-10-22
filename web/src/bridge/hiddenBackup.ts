// Hidden photos in the photo backup (round 4, hiddenbackup): the host's PhotoBackupHiddenRules, for the page (the
// folder name is checked as it is typed) and the mock. The host checks again; this only says why sooner.

import type { PhotoBackupHidden, PhotoBackupHiddenLayout } from './types';

export const HIDDEN_FOLDER_DEFAULT = 'Hidden';
/** The name suggested in Persian. */
export const HIDDEN_FOLDER_FA = 'پنهان';
export const HIDDEN_FOLDER_MAX = 64;

export const HIDDEN_MODES: readonly PhotoBackupHidden[] = ['exclude', 'include', 'only'];
export const HIDDEN_LAYOUTS: readonly PhotoBackupHiddenLayout[] = ['separate', 'together'];

export type HiddenFolderProblem = 'empty' | 'tooLong' | 'separator' | 'characters' | 'dot' | 'device' | 'date';

// Control characters, Windows' reserved ones, and the marks that reorder text (LRM, RLM, embeddings, isolates). ZWNJ is fine.
const BAD_CHARS = /[\u0000-\u001f<>:"|?*\u200e\u200f\u202a-\u202e\u2066-\u2069]/;
const DEVICE = /^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])$/i;
const DATE_LIKE = /^[0-9][0-9 ._-]*$/;

/** Why a name can't be the hidden photos' folder (one folder name Windows takes, not a date folder's), or null. */
export function hiddenFolderProblem(name: string): HiddenFolderProblem | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) return 'empty';
  if (trimmed.length > HIDDEN_FOLDER_MAX) return 'tooLong';
  if (/[\\/]/.test(trimmed)) return 'separator';
  if (BAD_CHARS.test(trimmed)) return 'characters';
  if (trimmed === '.' || trimmed === '..' || trimmed.endsWith('.')) return 'dot';
  if (DEVICE.test(trimmed.split('.')[0]!.trimEnd())) return 'device';
  if (trimmed.toLowerCase() === 'undated' || DATE_LIKE.test(trimmed)) return 'date';
  return null;
}

/** True when a photo (hidden on the phone or not) is one the options back up. */
export function wantsPhoto(mode: PhotoBackupHidden, hidden: boolean): boolean {
  return mode === 'include' || (mode === 'only' ? hidden : !hidden);
}

/** The folder hidden photos go under, or null when they go with the others (or aren't backed up). */
export function hiddenRoot(destination: string, mode: PhotoBackupHidden, layout: PhotoBackupHiddenLayout, folder: string): string | null {
  if (mode === 'exclude' || layout !== 'separate') return null;
  return `${destination.replace(/[\\/]+$/, '')}\\${folder.trim() || HIDDEN_FOLDER_DEFAULT}`;
}
