// Shapes of docs/bridge-contract.md, "WhatsApp (round 4)", hand-written from the C# records in
// src/DeviceDeck.Core/WhatsApp/WhatsAppModels.cs.

export type WhatsAppAccount = 'whatsApp' | 'business';

export interface WhatsAppAccountInfo {
  account: WhatsAppAccount;
  name: string;
  chats: number;
  messages: number;
  /** Messages with a media file. */
  media: number;
  /** Of those, the files the backup holds. */
  mediaInBackup: number;
  mediaBytes: number;
}

export interface WhatsAppStatus {
  backupId: string;
  /** The backup holds WhatsApp's chat database. */
  found: boolean;
  /** The backup lists WhatsApp among the phone's apps. */
  installed: boolean;
  accounts: WhatsAppAccountInfo[];
  /** Tables and columns this WhatsApp version doesn't have ("ZWAMESSAGE.ZSTARRED"): what they hold isn't shown. */
  missing: string[];
}

export type WhatsAppMediaKind = 'image' | 'video' | 'audio' | 'voice' | 'document' | 'sticker' | 'gif' | 'location' | 'contact' | 'other';

export interface WhatsAppChat {
  /** "w:<row>" or "b:<row>" (WhatsApp Business). */
  id: string;
  account: WhatsAppAccount;
  title: string;
  jid: string | null;
  /** "+989121234567" for one-to-one chats. */
  phone: string | null;
  isGroup: boolean;
  /** Everything the chat holds, group events included. */
  messageCount: number;
  mediaCount: number;
  lastMessageAt: string | null;
  preview: string | null;
  previewMedia: WhatsAppMediaKind | null;
  archived: boolean;
}

export type WhatsAppMessageKind = 'text' | 'media' | 'event' | 'deleted' | 'other';

export interface WhatsAppQuote {
  messageId: string | null;
  fromMe: boolean;
  senderName: string | null;
  text: string | null;
  mediaKind: WhatsAppMediaKind | null;
}

export interface WhatsAppMedia {
  /** For whatsapp.thumbnail and whatsapp.media. */
  id: string;
  kind: WhatsAppMediaKind;
  name: string;
  mimeType: string | null;
  size: number;
  durationSeconds: number | null;
  /** False when WhatsApp offloaded the file or never downloaded it. */
  inBackup: boolean;
  hasThumbnail: boolean;
  latitude: number | null;
  longitude: number | null;
}

export interface WhatsAppMessage {
  id: string;
  kind: WhatsAppMessageKind;
  date: string | null;
  fromMe: boolean;
  /** Groups only. */
  senderJid: string | null;
  senderName: string | null;
  /** The message, or a photo's caption. */
  text: string | null;
  starred: boolean;
  quote: WhatsAppQuote | null;
  media: WhatsAppMedia | null;
}

export interface WhatsAppAndroidBackupFile {
  name: string;
  size: number;
  modified: string | null;
}

export interface WhatsAppAndroidApp {
  package: 'com.whatsapp' | 'com.whatsapp.w4b';
  account: WhatsAppAccount;
  name: string;
  /** "/Android/media/com.whatsapp/WhatsApp", or "/WhatsApp" for versions before 2021. */
  folder: string;
  hasFolder: boolean;
  installed: boolean | null;
  mediaFiles: number | null;
  mediaBytes: number | null;
  /** msgstore*.crypt14 / .crypt15, newest first. */
  chatBackups: WhatsAppAndroidBackupFile[];
}

export interface WhatsAppAndroidInfo {
  deviceId: string;
  apps: WhatsAppAndroidApp[];
}
