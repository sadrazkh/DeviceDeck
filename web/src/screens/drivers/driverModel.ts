// What each Drivers card row shows (v0.6), worked out from the host's DriverStatus and Android's download, apart from
// React so the unit tests can check every state. Red only for real failures; "not started" and "starting" are grey.

import type { DriverDownloadStatus, DriverPhoneHint, DriverStatus } from '../../bridge';
import type { MessageKey } from '../../i18n';

/** ready = green · notStarted, starting, downloading = grey · action = amber · problem = red. */
export type DriverChip = 'ready' | 'notStarted' | 'starting' | 'downloading' | 'action' | 'problem';

export type DriverAction = 'getAppleDevices' | 'startAmds' | 'downloadAdb' | 'retryDownload' | 'startAdb' | 'restartAdb';

export interface DriverRowModel {
  providerId: string;
  name: MessageKey;
  chip: DriverChip;
  line: MessageKey;
  /** The technical reason under a red line (adb's own words). */
  detail: string | null;
  /** The one primary button, when there's something to do. */
  action: DriverAction | null;
  /** A download is running: the row shows its progress and Cancel. */
  download: DriverDownloadStatus | null;
}

export const CHIP_TONE: Record<DriverChip, 'success' | 'neutral' | 'warning' | 'danger'> = {
  ready: 'success',
  notStarted: 'neutral',
  starting: 'neutral',
  downloading: 'neutral',
  action: 'warning',
  problem: 'danger',
};

export const CHIP_LABEL: Record<DriverChip, MessageKey> = {
  ready: 'drivers.chip.ready',
  notStarted: 'drivers.chip.notStarted',
  starting: 'drivers.chip.starting',
  downloading: 'drivers.chip.downloading',
  action: 'drivers.chip.action',
  problem: 'drivers.chip.problem',
};

export const ACTION_LABEL: Record<DriverAction, MessageKey> = {
  getAppleDevices: 'drivers.ios.getAppleDevices',
  startAmds: 'drivers.ios.start',
  downloadAdb: 'drivers.android.download',
  retryDownload: 'drivers.download.retry',
  startAdb: 'drivers.android.start',
  restartAdb: 'drivers.android.restart',
};

/** A download running now (downloading, verifying or installing). */
export function isDownloading(download: DriverDownloadStatus | null | undefined): boolean {
  return download?.state === 'downloading' || download?.state === 'verifying' || download?.state === 'installing';
}

const checkFailed = (status: DriverStatus) => status.reason === 'checkFailed' || status.reason === 'startFailed';

function iosRow(status: DriverStatus): DriverRowModel {
  const base = { providerId: status.providerId, name: 'drivers.ios.name' as MessageKey, detail: null, action: null, download: null };
  switch (status.state) {
    case 'ready':
      return { ...base, chip: 'ready', line: 'drivers.ios.ready' };
    case 'missing':
      return { ...base, chip: 'action', line: 'drivers.ios.missing', action: 'getAppleDevices' };
    case 'stopped':
      return { ...base, chip: 'action', line: 'drivers.ios.stopped', action: 'startAmds' };
    // Not sent for the iPhone today; grey and nothing to do, like Android's.
    case 'starting':
      return { ...base, chip: 'starting', line: 'drivers.ios.stopped' };
    case 'idle':
      return { ...base, chip: 'notStarted', line: 'drivers.ios.stopped' };
    default:
      return { ...base, chip: 'problem', line: checkFailed(status) ? 'drivers.error.check' : 'drivers.ios.error', detail: status.detail };
  }
}

function androidRow(status: DriverStatus, download: DriverDownloadStatus | null): DriverRowModel {
  const base = { providerId: status.providerId, name: 'drivers.android.name' as MessageKey, detail: null, action: null, download: null };
  if (download && isDownloading(download)) {
    const line: MessageKey =
      download.state === 'verifying' ? 'drivers.download.verifying' : download.state === 'installing' ? 'drivers.download.installing' : 'drivers.download.label';
    return { ...base, chip: 'downloading', line, download };
  }
  switch (status.state) {
    case 'ready':
      return { ...base, chip: 'ready', line: 'drivers.android.ready' };
    case 'idle':
      return { ...base, chip: 'notStarted', line: 'drivers.android.idle' };
    case 'starting':
      return { ...base, chip: 'starting', line: 'drivers.android.starting' };
    case 'missing':
      if (download?.state === 'failed') {
        return {
          ...base,
          chip: 'problem',
          line: `drivers.download.failed.${download.errorCode ?? 'network'}` as MessageKey,
          detail: download.error,
          action: 'retryDownload',
        };
      }
      return { ...base, chip: 'action', line: 'drivers.android.missing', action: 'downloadAdb' };
    case 'stopped':
      return { ...base, chip: 'action', line: 'drivers.android.stopped', action: 'startAdb' };
    default:
      return { ...base, chip: 'problem', line: checkFailed(status) ? 'drivers.error.check' : 'drivers.android.failed', detail: status.detail, action: 'restartAdb' };
  }
}

/** Any other provider (the sample phones in a developer build): its state, nothing to do. */
function otherRow(status: DriverStatus): DriverRowModel {
  const chip: DriverChip =
    status.state === 'ready' ? 'ready' : status.state === 'idle' ? 'notStarted' : status.state === 'starting' ? 'starting' : status.state === 'error' ? 'problem' : 'action';
  return { providerId: status.providerId, name: 'drivers.other.name', chip, line: 'drivers.android.idle', detail: status.detail, action: null, download: null };
}

export function rowModel(status: DriverStatus, download: DriverDownloadStatus | null = null): DriverRowModel {
  if (status.providerId === 'ios') return iosRow(status);
  if (status.providerId === 'android') return androidRow(status, download);
  return otherRow(status);
}

/** The phones Android's row helps with, one per name and problem. */
export function phoneHints(status: DriverStatus | undefined): DriverPhoneHint[] {
  const seen = new Set<string>();
  return (status?.phones ?? []).filter((hint) => {
    const key = `${hint.name}\u0000${hint.problem}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** iPhone first, then Android, then anything else, the way the card lists them. */
export function orderDrivers(list: readonly DriverStatus[]): DriverStatus[] {
  const rank = (id: string) => (id === 'ios' ? 0 : id === 'android' ? 1 : 2);
  return [...list].filter((d) => d.providerId !== 'mtp').sort((a, b) => rank(a.providerId) - rank(b.providerId));
}
