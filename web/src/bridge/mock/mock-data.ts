// All sample data for the mock backend, from the design brief's "Sample data". Names are made up.
// Sizes are decimal (1 GB = 1,000,000,000 bytes), the way phones report them.

import type {
  AppInfo,
  BatteryInfo,
  DeviceCapability,
  DeviceDetails,
  DeviceSummary,
  DriverStatus,
  Settings,
  StorageInfo,
  TransferJob,
} from '../types';

export const GB = 1_000_000_000;
export const MB = 1_000_000;
export const KB = 1_000;

export const IPHONE_ID = '00008150-001A2C3E0E91002E';
export const ANDROID_ID = 'R5CY10QWERT';

export const IPHONE_FULL_CAPABILITIES: DeviceCapability[] = [
  'info',
  'storage',
  'battery',
  'photosRead',
  'photosDelete',
  'files',
  'appDocuments',
  'appsList',
  'appsInstall',
  'appsUninstall',
  'backup',
  'restore',
  'contacts',
  'messages',
  'screenshot',
  'syslog',
  'crashReports',
  'reboot',
  'shutdown',
];

/** iPhone without Apple's driver, over PTP: camera-roll photos only. */
export const IPHONE_LIMITED_CAPABILITIES: DeviceCapability[] = ['info', 'photosRead', 'photosDelete'];

export const ANDROID_CAPABILITIES: DeviceCapability[] = [
  'info',
  'storage',
  'battery',
  'photosRead',
  'photosDelete',
  'photosImport',
  'files',
  'appDocuments',
  'appsList',
  'appsInstall',
  'appsUninstall',
  'appsExport',
  'backup',
  'contacts',
  'messages',
  'screenshot',
  'screenMirror',
  'syslog',
  'crashReports',
  'reboot',
  'shutdown',
  'rebootToRecovery',
  'rebootToBootloader',
];

export const iphone: DeviceSummary = {
  id: IPHONE_ID,
  name: 'Nima’s iPhone',
  platform: 'ios',
  model: 'iPhone18,1',
  marketingName: 'iPhone 17 Pro',
  osVersion: '26.1',
  connection: 'usb',
  state: 'ready',
  mode: 'full',
  capabilities: IPHONE_FULL_CAPABILITIES,
  colorHex: '#E2733A', // Cosmic Orange
};

export const android: DeviceSummary = {
  id: ANDROID_ID,
  name: 'Galaxy S25 Ultra',
  platform: 'android',
  model: 'SM-S938B',
  marketingName: 'Galaxy S25 Ultra',
  osVersion: '16',
  connection: 'wifi',
  state: 'ready',
  mode: 'full',
  capabilities: ANDROID_CAPABILITIES,
  colorHex: '#8FA3B8', // Titanium Silverblue
};


export const storage: Record<string, StorageInfo> = {
  [IPHONE_ID]: {
    volumes: [
      {
        key: 'internal',
        label: 'Internal storage',
        totalBytes: Math.round(256 * GB),
        freeBytes: Math.round(101.0 * GB),
        categories: [
          { key: 'photos', bytes: Math.round(61.8 * GB) },
          { key: 'videos', bytes: Math.round(24.0 * GB) },
          { key: 'apps', bytes: Math.round(38.4 * GB) },
          { key: 'media', bytes: Math.round(6.2 * GB) },
          { key: 'system', bytes: Math.round(14.9 * GB) },
          { key: 'other', bytes: Math.round(9.7 * GB) },
        ],
      },
    ],
  },
  [ANDROID_ID]: {
    volumes: [
      {
        key: 'internal',
        label: 'Internal storage',
        totalBytes: Math.round(512 * GB),
        freeBytes: Math.round(278.0 * GB),
        categories: [
          { key: 'photos', bytes: Math.round(88.3 * GB) },
          { key: 'videos', bytes: Math.round(41.7 * GB) },
          { key: 'apps', bytes: Math.round(52.9 * GB) },
          { key: 'media', bytes: Math.round(7.4 * GB) },
          { key: 'system', bytes: Math.round(28.6 * GB) },
          { key: 'other', bytes: Math.round(15.1 * GB) },
        ],
      },
    ],
  },
};

export const battery: Record<string, BatteryInfo> = {
  [IPHONE_ID]: {
    levelPercent: 76,
    isCharging: true,
    healthPercent: 87,
    cycleCount: 412,
    temperatureCelsius: 31,
    designCapacityMah: 4252,
    fullChargeCapacityMah: 3699,
  },
  [ANDROID_ID]: {
    levelPercent: 58,
    isCharging: false,
    healthPercent: 92,
    cycleCount: 268,
    temperatureCelsius: 29,
    designCapacityMah: 5000,
    fullChargeCapacityMah: 4600,
  },
};

export const details: Record<string, DeviceDetails> = {
  [IPHONE_ID]: {
    rows: [
      { key: 'model', label: 'Model', value: 'iPhone 17 Pro (iPhone18,1)', sensitive: false },
      { key: 'os', label: 'iOS', value: '26.1 (23B85)', sensitive: false },
      { key: 'serial', label: 'Serial number', value: 'F2LXK9Q7N1VW', sensitive: true },
      { key: 'imei', label: 'IMEI', value: '35 892614 527301 8', sensitive: true },
      { key: 'udid', label: 'UDID', value: IPHONE_ID, sensitive: true },
      { key: 'wifi', label: 'Wi-Fi address', value: 'A4:C3:F0:5E:21:9B', sensitive: false },
      { key: 'bluetooth', label: 'Bluetooth', value: 'A4:C3:F0:5E:21:9C', sensitive: false },
      { key: 'carrier', label: 'Carrier', value: 'Irancell', sensitive: false },
    ],
  },
  [ANDROID_ID]: {
    rows: [
      { key: 'model', label: 'Model', value: 'Galaxy S25 Ultra (SM-S938B)', sensitive: false },
      { key: 'os', label: 'Android', value: '16 · One UI 8', sensitive: false },
      { key: 'serial', label: 'Serial number', value: ANDROID_ID, sensitive: true },
      { key: 'imei', label: 'IMEI', value: '35 417820 640193 2', sensitive: true },
      { key: 'wifi', label: 'Wi-Fi address', value: '5C:E9:1E:A0:44:7D', sensitive: false },
      { key: 'bluetooth', label: 'Bluetooth', value: '5C:E9:1E:A0:44:7E', sensitive: false },
      { key: 'carrier', label: 'Carrier', value: 'Hamrah-e Aval', sensitive: false },
    ],
  },
};

/** Brand-ish tile colours for the generated app icons (letter tiles, never real logos). */
export const appIconColours: Record<string, [string, string]> = {
  'org.videolan.vlc-ios': ['#ff8a00', '#f25c05'],
  'org.videolan.vlc': ['#ff8a00', '#f25c05'],
  'com.readdle.ReaddleDocsIPad': ['#4f8ef7', '#2563eb'],
  'com.firecore.infuse': ['#ff7a3d', '#d9480f'],
  'com.goodiware.GoodReader': ['#38bdf8', '#0369a1'],
  'ph.telegra.Telegraph': ['#3fb2ef', '#1d8fd1'],
  'org.telegram.messenger': ['#3fb2ef', '#1d8fd1'],
  'net.whatsapp.WhatsApp': ['#3ddc84', '#16a34a'],
  'com.whatsapp': ['#3ddc84', '#16a34a'],
  'com.burbn.instagram': ['#f77737', '#c13584'],
  'com.instagram.android': ['#f77737', '#c13584'],
  'com.spotify.client': ['#2bd86b', '#15803d'],
  'com.spotify.music': ['#2bd86b', '#15803d'],
  'com.google.Maps': ['#4ade80', '#3b82f6'],
  'com.niksoftware.snapseed': ['#a3e635', '#4d7c0f'],
  'com.termux': ['#2f2f2f', '#000000'],
  'com.android.chrome': ['#fbbf24', '#ef4444'],
  'com.samsung.android.app.notes': ['#fb923c', '#ea580c'],
  'com.sec.android.app.launcher': ['#60a5fa', '#2563eb'],
  'com.samsung.android.honeyboard': ['#94a3b8', '#475569'],
  'com.google.android.gms': ['#60a5fa', '#16a34a'],
  'com.android.settings': ['#9ca3af', '#4b5563'],
};

const d = (iso: string) => new Date(iso).toISOString();

export const apps: Record<string, AppInfo[]> = {
  [IPHONE_ID]: [
    { id: 'org.videolan.vlc-ios', name: 'VLC', version: '3.6.4', appBytes: Math.round(214 * MB), dataBytes: Math.round(3.1 * GB), source: 'store', fileSharing: true, installedAt: d('2025-02-11T10:20:00Z') },
    { id: 'com.readdle.ReaddleDocsIPad', name: 'Documents', version: '8.21', appBytes: Math.round(312 * MB), dataBytes: Math.round(0.888 * GB), source: 'store', fileSharing: true, installedAt: d('2024-09-03T08:00:00Z') },
    { id: 'com.firecore.infuse', name: 'Infuse', version: '8.1.2', appBytes: Math.round(96 * MB), dataBytes: Math.round(4.704 * GB), source: 'store', fileSharing: true, installedAt: d('2025-06-21T19:44:00Z') },
    // A file-sharing app with nothing in it yet (the brief's "empty app" state).
    { id: 'com.goodiware.GoodReader', name: 'GoodReader', version: '5.20', appBytes: Math.round(54 * MB), dataBytes: 0, source: 'store', fileSharing: true, installedAt: d('2026-10-12T09:30:00Z') },
    { id: 'ph.telegra.Telegraph', name: 'Telegram', version: '12.1', appBytes: Math.round(160 * MB), dataBytes: Math.round(252 * MB), source: 'store', fileSharing: false, installedAt: d('2023-12-01T12:00:00Z') },
    { id: 'net.whatsapp.WhatsApp', name: 'WhatsApp', version: '26.20.75', appBytes: Math.round(210 * MB), dataBytes: Math.round(1.59 * GB), source: 'store', fileSharing: false, installedAt: d('2023-12-01T12:05:00Z') },
    { id: 'com.burbn.instagram', name: 'Instagram', version: '402.0', appBytes: Math.round(380 * MB), dataBytes: Math.round(310 * MB), source: 'store', fileSharing: false, installedAt: d('2024-03-14T17:30:00Z') },
    { id: 'com.spotify.client', name: 'Spotify', version: '9.1.12', appBytes: Math.round(190 * MB), dataBytes: Math.round(2.11 * GB), source: 'store', fileSharing: false, installedAt: d('2023-12-02T09:10:00Z') },
    { id: 'com.google.Maps', name: 'Google Maps', version: '26.41', appBytes: Math.round(290 * MB), dataBytes: Math.round(90 * MB), source: 'store', fileSharing: false, installedAt: d('2024-01-20T07:00:00Z') },
    { id: 'com.niksoftware.snapseed', name: 'Snapseed', version: '2.23', appBytes: Math.round(128 * MB), dataBytes: Math.round(32 * MB), source: 'store', fileSharing: false, installedAt: d('2024-07-08T15:00:00Z') },
  ],
  [ANDROID_ID]: [
    { id: 'org.telegram.messenger', name: 'Telegram', version: '12.1.1', appBytes: Math.round(120 * MB), dataBytes: Math.round(1.4 * GB), source: 'store', fileSharing: false, installedAt: d('2025-03-01T10:00:00Z') },
    { id: 'com.whatsapp', name: 'WhatsApp', version: '2.26.20.75', appBytes: Math.round(140 * MB), dataBytes: Math.round(2.2 * GB), source: 'store', fileSharing: false, installedAt: d('2025-03-01T10:02:00Z') },
    { id: 'com.instagram.android', name: 'Instagram', version: '402.0.0.45', appBytes: Math.round(210 * MB), dataBytes: Math.round(640 * MB), source: 'store', fileSharing: false, installedAt: d('2025-03-01T10:05:00Z') },
    { id: 'org.videolan.vlc', name: 'VLC', version: '3.6.5', appBytes: Math.round(88 * MB), dataBytes: Math.round(24 * MB), source: 'store', fileSharing: false, installedAt: d('2025-04-12T18:30:00Z') },
    { id: 'com.spotify.music', name: 'Spotify', version: '9.1.12.1006', appBytes: Math.round(150 * MB), dataBytes: Math.round(3.6 * GB), source: 'store', fileSharing: false, installedAt: d('2025-03-02T08:00:00Z') },
    { id: 'com.termux', name: 'Termux', version: '0.119.0', appBytes: Math.round(112 * MB), dataBytes: Math.round(820 * MB), source: 'sideloaded', fileSharing: false, installedAt: d('2025-08-30T21:15:00Z') },
    { id: 'com.android.chrome', name: 'Chrome', version: '141.0.7390.122', appBytes: Math.round(410 * MB), dataBytes: Math.round(520 * MB), source: 'system', fileSharing: false, installedAt: null },
    { id: 'com.samsung.android.app.notes', name: 'Samsung Notes', version: '4.6.04.29', appBytes: Math.round(260 * MB), dataBytes: Math.round(180 * MB), source: 'system', fileSharing: false, installedAt: null },
    { id: 'com.sec.android.app.launcher', name: 'One UI Home', version: '17.0.03.4', appBytes: Math.round(98 * MB), dataBytes: Math.round(60 * MB), source: 'system', fileSharing: false, installedAt: null },
    { id: 'com.samsung.android.honeyboard', name: 'Samsung Keyboard', version: '6.2.10.21', appBytes: Math.round(140 * MB), dataBytes: Math.round(44 * MB), source: 'system', fileSharing: false, installedAt: null },
    { id: 'com.google.android.gms', name: 'Google Play services', version: '26.41.32', appBytes: Math.round(620 * MB), dataBytes: Math.round(410 * MB), source: 'system', fileSharing: false, installedAt: null },
    { id: 'com.android.settings', name: 'Settings', version: '16', appBytes: Math.round(54 * MB), dataBytes: Math.round(12 * MB), source: 'system', fileSharing: false, installedAt: null },
  ],
};

export const iphoneAlbumNames = [
  'Trip to Shiraz',
  'Isfahan 2026',
  'Family',
  'Kids',
  'Cats',
  'Recipes',
  'Receipts',
  'Garden',
  'Hiking',
  'Concert',
  'Wallpapers',
  'Work',
];

export const androidFolderNames = [
  'Camera',
  'Screenshots',
  'WhatsApp Images',
  'WhatsApp Video',
  'Telegram Images',
  'Telegram Video',
  'Instagram',
  'Download',
  'Snapseed',
  'Pictures',
  'Edited',
  'Lightroom',
  'Messenger',
  'Signal',
  'Twitter',
  'Bale',
  'Rubika',
  'Eitaa',
  'Scans',
  'Documents',
  'Wallpapers',
  'Memes',
  'Recipes',
  'Car',
  'House',
  'Receipts',
  'Kids',
  'School',
  'Trips',
  'Mashhad 2025',
  'Tabriz',
  'Gilan',
  'Night shots',
  'Expert RAW',
];

export const drivers: DriverStatus[] = [
  { providerId: 'ios', state: 'ready', version: 'Apple Mobile Device Support 18.4.0.4', detail: null },
  { providerId: 'android', state: 'ready', version: 'adb 36.0.0 (port 5137)', detail: null },
  { providerId: 'mtp', state: 'ready', version: 'Windows Portable Devices', detail: null },
];

export const driversIosMissing: DriverStatus[] = [
  {
    providerId: 'ios',
    state: 'missing',
    version: null,
    detail: 'Apple Mobile Device Support is not installed, so only photos can be read from an iPhone.',
  },
  drivers[1]!,
  drivers[2]!,
];

export const defaultSettings: Settings = {
  language: 'en',
  theme: 'system',
  density: 'comfortable',
  photoExportFolder: 'D:\\Photos',
  photoFolderPattern: 'yyyy/MM',
  convertHeic: false,
  fileCopyFolder: 'D:\\Phone files',
  conflictRule: 'ask',
};

/** Folders shell.pickFolder hands back in turn (the mock has no real picker). */
export const pickableFolders = ['D:\\Photos\\2026', 'E:\\Backup\\Phones', 'C:\\Users\\Nima\\Pictures'];

/** The transfer queue at start: two active, one queued, one failed, one done. */
export function seedTransfers(now: number): TransferJob[] {
  const iso = (offsetSeconds: number) => new Date(now - offsetSeconds * 1000).toISOString();
  const exportTotal = Math.round(12.3 * GB);
  const filmTotal = Math.round(12.4 * GB);
  return [
    {
      id: 'job-export-1284',
      deviceId: IPHONE_ID,
      kind: 'photosExport',
      title: 'Exporting 1,284 photos to D:\\Photos\\2026',
      state: 'running',
      totalItems: 1284,
      doneItems: 809,
      failedItems: 0,
      totalBytes: exportTotal,
      doneBytes: Math.round(exportTotal * 0.63),
      bytesPerSecond: Math.round(38 * MB),
      etaSeconds: Math.round((exportTotal * 0.37) / (Math.round(38 * MB))),
      conflict: null,
      error: null,
      createdAt: iso(200),
      finishedAt: null,
    },
    {
      id: 'job-interstellar',
      deviceId: IPHONE_ID,
      kind: 'copyToDevice',
      title: 'Interstellar.mkv into VLC',
      state: 'running',
      totalItems: 1,
      doneItems: 0,
      failedItems: 0,
      totalBytes: filmTotal,
      doneBytes: Math.round(filmTotal * 0.21),
      bytesPerSecond: Math.round(41 * MB),
      etaSeconds: Math.round((filmTotal * 0.79) / (Math.round(41 * MB))),
      conflict: null,
      error: null,
      createdAt: iso(70),
      finishedAt: null,
    },
    {
      id: 'job-snapseed',
      deviceId: IPHONE_ID,
      kind: 'appInstall',
      title: 'Installing Snapseed.ipa',
      state: 'queued',
      totalItems: 1,
      doneItems: 0,
      failedItems: 0,
      totalBytes: Math.round(118 * MB),
      doneBytes: 0,
      bytesPerSecond: null,
      etaSeconds: null,
      conflict: null,
      error: null,
      createdAt: iso(30),
      finishedAt: null,
    },
    {
      id: 'job-img4412',
      deviceId: IPHONE_ID,
      kind: 'photosExport',
      title: 'IMG_4412.HEIC',
      state: 'failed',
      totalItems: 1,
      doneItems: 0,
      failedItems: 1,
      totalBytes: Math.round(3.4 * MB),
      doneBytes: 0,
      bytesPerSecond: null,
      etaSeconds: null,
      conflict: null,
      error: { code: 'device_disconnected', message: 'The phone was disconnected.' },
      createdAt: iso(3600),
      finishedAt: iso(3590),
    },
    {
      id: 'job-done-48',
      deviceId: ANDROID_ID,
      kind: 'copyToPc',
      title: 'Copied 48 files to D:\\Phone files',
      state: 'done',
      totalItems: 48,
      doneItems: 48,
      failedItems: 0,
      totalBytes: Math.round(642 * MB),
      doneBytes: Math.round(642 * MB),
      bytesPerSecond: null,
      etaSeconds: null,
      conflict: null,
      error: null,
      createdAt: iso(7300),
      finishedAt: iso(7200),
    },
  ];
}

