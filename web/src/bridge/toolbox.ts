// Shapes of the toolbox methods and events (docs/bridge-contract.md, "Toolbox").

export type LogLevel = 'verbose' | 'debug' | 'info' | 'notice' | 'warning' | 'error' | 'fault';

export const LOG_LEVELS: readonly LogLevel[] = ['verbose', 'debug', 'info', 'notice', 'warning', 'error', 'fault'];

export interface ToolboxFeatures {
  log: boolean;
  crashReports: boolean;
  mirror: boolean;
  ringtone: boolean;
  wifiSync: boolean;
  report: boolean;
  wirelessDebugging: boolean;
}

/** One live log line; `seq` counts from 1 per session. */
export interface LogLine {
  seq: number;
  time: string | null;
  level: LogLevel;
  process: string;
  pid: number | null;
  tag: string | null;
  message: string;
}

export interface ToolboxError {
  code: string;
  message: string;
}

export type CrashReportKind = 'crash' | 'hang' | 'memory' | 'panic' | 'tombstone' | 'diagnostic';

export interface CrashReport {
  id: string;
  fileName: string;
  process: string;
  kind: CrashReportKind;
  time: string | null;
  size: number;
  readable: boolean;
}

export interface CrashReportList {
  reports: CrashReport[];
  canDelete: boolean;
  /** Places that exist but need root on this phone: "tombstones", "anr". */
  unreadable: string[];
}

export type ReportFormat = 'html' | 'pdf';

export type ToolboxJobState = 'running' | 'done' | 'failed' | 'cancelled';

export interface ToolboxJob {
  id: string;
  kind: 'heic';
  state: ToolboxJobState;
  total: number;
  done: number;
  failed: number;
  current: string | null;
  outputs: string[];
  errors: string[];
}

export interface WirelessResult {
  serial: string | null;
  message: string;
}

export type QrStage = 'waiting' | 'pairing' | 'connecting' | 'done' | 'failed' | 'cancelled' | 'expired';

export interface WifiQrEvent {
  sessionId: string;
  stage: QrStage;
  serial: string | null;
  message: string | null;
  error: ToolboxError | null;
}

export type MirrorPacketKind = 'session' | 'config' | 'frame';

export interface MirrorVideoEvent {
  sessionId: string;
  kind: MirrorPacketKind;
  pts: number;
  key: boolean;
  width: number;
  height: number;
  /** Base64 bytes (H.264 Annex B); empty for session packets. */
  data: string;
}

export type MirrorTouchAction = 'down' | 'up' | 'move' | 'cancel';

export type MirrorCommand =
  | 'backOrScreenOn'
  | 'expandNotifications'
  | 'expandSettings'
  | 'collapsePanels'
  | 'rotate'
  | 'resetVideo'
  | 'screenOff'
  | 'screenOn';

export type MirrorInput =
  | { kind: 'touch'; action: MirrorTouchAction; pointerId: number; x: number; y: number; width: number; height: number; pressure: number }
  | { kind: 'key'; down: boolean; keyCode: number }
  | { kind: 'text'; text: string }
  | { kind: 'scroll'; x: number; y: number; width: number; height: number; hScroll: number; vScroll: number }
  | { kind: 'command'; command: MirrorCommand };

/** The mouse as one finger (scrcpy's generic finger pointer). */
export const MOUSE_POINTER_ID = -2;
