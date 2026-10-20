// Shapes of docs/bridge-contract.md, "Backup, contacts and messages" (round 2), hand-written from the C# records
// in src/DeviceDeck.Core/Backups.

import type { DevicePlatform } from './types';

export type BackupState = 'complete' | 'incomplete' | 'inProgress';
export type BackupSource = 'deviceDeck' | 'apple';

export interface BackupInfo {
  /** Opaque: pass it back to the other backup methods. */
  id: string;
  deviceId: string;
  deviceName: string;
  platform: DevicePlatform;
  model: string | null;
  marketingName: string | null;
  osVersion: string | null;
  createdAt: string | null;
  /** What the folder holds; dated copies share unchanged files, so they take less disk than the sum. */
  sizeBytes: number;
  encrypted: boolean;
  state: BackupState;
  /** apple = iTunes' or Apple Devices' folder: read and restored, never deleted. */
  source: BackupSource;
  path: string;
  apps: number;
  /** Encrypted and not unlocked in this session. */
  locked: boolean;
}

export interface BackupDeviceInfo {
  deviceId: string;
  platform: DevicePlatform;
  folder: string;
  keep: number;
  /** False on exFAT, FAT32 and network drives: each backup replaces the last. */
  keepsHistory: boolean;
  freeBytes: number | null;
  encrypted: boolean | null;
  cloudBackupEnabled: boolean | null;
  /** Android: contacts and messages need a helper app on the phone (not in this version). */
  needsHelperApp: boolean;
  lastBackup: BackupInfo | null;
}

export type BackupJobKind = 'backup' | 'restore' | 'encryption' | 'extract' | 'export';
export type BackupJobState = 'queued' | 'running' | 'waitingForDevice' | 'done' | 'failed' | 'cancelled';
export type BackupPhase = 'starting' | 'waitingForPhone' | 'preparing' | 'transferring' | 'finishing';

export interface PrunePlan {
  deviceId: string;
  keep: number;
  backups: BackupInfo[];
  freedBytes: number;
}

export interface BackupJob {
  id: string;
  deviceId: string | null;
  deviceName: string;
  kind: BackupJobKind;
  title: string;
  state: BackupJobState;
  phase: BackupPhase | null;
  /** 0–100: the iPhone's own estimate, or files/bytes done. */
  percent: number | null;
  bytes: number;
  files: number;
  totalBytes: number | null;
  totalFiles: number | null;
  bytesPerSecond: number | null;
  backupId: string | null;
  /** Extract: the folder (or the file, for one file). Export: the file or folder. */
  destination: string | null;
  /** A finished backup: older backups beyond the keep count, offered for deletion. */
  prune: PrunePlan | null;
  error: { code: string; message: string } | null;
  createdAt: string;
  finishedAt: string | null;
}

export type BackupDomainKind = 'app' | 'appGroup' | 'appPlugin' | 'system';

export interface BackupDomain {
  domain: string;
  kind: BackupDomainKind;
  appId: string | null;
  name: string;
  files: number;
  bytes: number;
}

export interface BackupEntry {
  domain: string;
  /** Inside the domain, '/' separated, no leading slash. */
  path: string;
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string | null;
}

export interface BackupItemRef {
  domain: string;
  path: string;
}

export interface LabeledValue {
  /** mobile, iphone, home, work, main, homeFax, workFax, otherFax, pager, other, school, homePage, or the user's own text. */
  label: string;
  value: string;
}

export interface PostalAddress {
  label: string;
  street: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  country: string | null;
}

export interface Contact {
  id: string;
  displayName: string;
  firstName: string | null;
  middleName: string | null;
  lastName: string | null;
  prefix: string | null;
  suffix: string | null;
  nickname: string | null;
  organization: string | null;
  department: string | null;
  jobTitle: string | null;
  note: string | null;
  /** "yyyy-MM-dd", or "--MM-dd" without a year. */
  birthday: string | null;
  phones: LabeledValue[];
  emails: LabeledValue[];
  urls: LabeledValue[];
  addresses: PostalAddress[];
  hasPhoto: boolean;
  modified: string | null;
}

export interface ConversationMember {
  handle: string;
  name: string | null;
}

export interface Conversation {
  id: string;
  title: string;
  members: ConversationMember[];
  isGroup: boolean;
  service: string;
  messageCount: number;
  attachmentCount: number;
  lastMessageAt: string | null;
  preview: string | null;
}

export interface MessageAttachment {
  id: string;
  name: string;
  mimeType: string | null;
  size: number;
  inBackup: boolean;
}

export interface Message {
  id: string;
  kind: 'text' | 'event';
  date: string | null;
  fromMe: boolean;
  handle: string | null;
  senderName: string | null;
  text: string | null;
  service: string;
  attachments: MessageAttachment[];
}

export type ContactsFormat = 'vCard' | 'csv';
export type MessagesFormat = 'html' | 'pdf';

export interface BackupDeleteResult {
  deleted: number;
  freedBytes: number;
}

/** The data.code values the backup methods add. */
export const BACKUP_ERROR_CODES = [
  'wrong_password',
  'password_required',
  'busy',
  'backup_missing',
  'backup_corrupt',
  'no_space',
  'refused',
] as const;
