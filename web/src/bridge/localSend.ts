// Photos into Photos with LocalSend (round 3): docs/bridge-contract.md, "Photos into Photos with LocalSend".

export type LocalSendRoute = 'usb' | 'wifi';

/** Who to send to: the phone's cable, or a receiver's address on the network. The fingerprint is pinned. */
export interface LocalSendTarget {
  route: LocalSendRoute;
  port: number;
  protocol: 'https' | 'http';
  fingerprint: string | null;
  /** Wi-Fi: the receiver's IP address. */
  address?: string | null;
  alias?: string | null;
  deviceModel?: string | null;
}

export interface LocalSendReceiver {
  id: string;
  route: LocalSendRoute;
  alias: string;
  deviceModel: string | null;
  deviceType: string | null;
  address: string | null;
  port: number;
  protocol: 'https' | 'http';
  fingerprint: string;
  /** Over USB always; over Wi-Fi when it looks like this phone. */
  likely: boolean;
}

/** found: LocalSend answers over the cable; notListening: it isn't open; notAnswering: open but in the background. */
export type LocalSendUsbState = 'found' | 'notListening' | 'notAnswering' | 'unavailable';

export interface LocalSendDiscovery {
  deviceId: string;
  usb: LocalSendUsbState;
  usbDetail: string | null;
  usbReceiver: LocalSendReceiver | null;
  wifi: LocalSendReceiver[];
  wifiSearching: boolean;
  wifiSearchedAt: string | null;
}

export type LocalSendPhase = 'connecting' | 'waitingForAccept' | 'sending' | 'done' | 'failed' | 'cancelled';

export interface LocalSendFileResult {
  index: number;
  name: string;
  size: number;
  state: 'queued' | 'sending' | 'done' | 'failed' | 'skipped';
  error: { code: string; message: string } | null;
}

export interface LocalSendJobStatus {
  jobId: string;
  deviceId: string;
  phase: LocalSendPhase;
  receiverAlias: string | null;
  pinGiven: boolean;
  error: { code: string; message: string } | null;
  /** localsend.job only; null in localsend.updated. */
  files: LocalSendFileResult[] | null;
}

/** photos.importToApp's answer (round 3): where the files will be, to save them all at once in the Files app. */
export interface PhotosToAppStarted {
  jobId: string;
  /** The dated folder in the app's Documents: "DeviceDeck 2026-10-21". */
  folder: string;
  /** The folder itself, or its parts of 200: "DeviceDeck 2026-10-21/201-400". */
  parts: { path: string; count: number }[];
}

/** The codes a send can fail with (TransferJob.error.code and each file's error). */
export const LOCALSEND_ERRORS = [
  'localsend_not_listening',
  'localsend_connection_lost',
  'localsend_pin_required',
  'localsend_pin_wrong',
  'localsend_rejected',
  'localsend_busy',
  'localsend_checksum',
  'localsend_session_lost',
  'localsend_fingerprint',
  'localsend_failed',
] as const;
export type LocalSendErrorCode = (typeof LOCALSEND_ERRORS)[number];

export function receiverToTarget(receiver: LocalSendReceiver): LocalSendTarget {
  return {
    route: receiver.route,
    port: receiver.port,
    protocol: receiver.protocol,
    fingerprint: receiver.fingerprint,
    address: receiver.address,
    alias: receiver.alias,
    deviceModel: receiver.deviceModel,
  };
}
