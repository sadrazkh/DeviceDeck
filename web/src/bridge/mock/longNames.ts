// Long, Persian and mixed-direction names for the file browser's layout checks (?names=long). Real phones have them:
// an app whose name runs to a whole line, folders nested three deep, Persian file names with Latin parts and digits.
// Off by default, so the other screens' sample data (and the counts their tests check) stay as they are.

import type { AppInfo } from '../types';
import type { MockFileSystem, SampleSpec } from './files';
import { ANDROID_ID, GB, IPHONE_ID, KB, MB } from './mock-data';

const installedAt = '2026-03-14T09:20:00.000Z';

/** A file-sharing iPhone app with a long name. */
export const LONG_IPHONE_APP: AppInfo = {
  id: 'com.example.filemanager.pro',
  name: 'File Manager Pro — Documents, Downloads & Network Drives',
  version: '12.4.1',
  appBytes: Math.round(148 * MB),
  dataBytes: Math.round(2.4 * GB),
  source: 'store',
  fileSharing: true,
  installedAt,
};

/** An Android app with a long Persian name and Latin parts; its files are in Android/media/<package>. */
export const LONG_ANDROID_APP: AppInfo = {
  id: 'ir.example.ketabkhan.reader',
  name: 'کتاب‌خوان همراه — PDF، EPUB و کتاب صوتی Pro',
  version: '5.2.0',
  appBytes: Math.round(64 * MB),
  dataBytes: Math.round(910 * MB),
  source: 'store',
  fileSharing: false,
  installedAt,
};

/** Three folders deep, long and mixed-direction names, dates in the longest Persian month names. */
const TREE: SampleSpec[] = [
  ['Work Projects 2026 — Quarterly Reports and Board Presentations', [
    ['گزارش‌های فصلی شرکت (نسخهٔ نهایی پس از بازبینی)', [
      ['Q3 2026 – Final review drafts for the board meeting in Tehran', [
        ['Annual financial statement for the year ending March 2026 — final signed copy.pdf', Math.round(4.2 * MB), { daysAgo: 160 }],
        ['صورت‌جلسهٔ هیئت مدیره دربارهٔ بودجهٔ سال ۱۴۰۵ و برنامه‌های توسعهٔ شعبه‌ها.docx', Math.round(840 * KB), { daysAgo: 165 }],
        ['گزارش Q3 2026 – Sales report (نسخهٔ ۲) final.xlsx', Math.round(312 * KB), { daysAgo: 31 }],
        ['IMG_2026-10-01 عکس‌های سفر شیراز و تخت جمشید.HEIC', Math.round(3.4 * MB), { daysAgo: 18 }],
        ['notes.txt', Math.round(2 * KB), { daysAgo: 2 }],
      ]],
      ['کارنامهٔ ترم پاییز.pdf', Math.round(220 * KB), { daysAgo: 300 }],
    ]],
    ['Read me first — how this shared folder is organised and who to ask.txt', Math.round(6 * KB), { daysAgo: 161 }],
  ]],
  ['کتاب‌ها و جزوه‌های دانشگاه', [
    ['جزوهٔ ریاضی عمومی ۲ — دکتر احمدی (اسکن کامل).pdf', Math.round(38 * MB), { daysAgo: 220 }],
  ]],
  ['پوشهٔ خالی', []],
  ['Movie night — The Grand Budapest Hotel (2014) 1080p BluRay x264 DTS-HD MA 5.1.mkv', Math.round(2.1 * GB), { daysAgo: 162 }],
  ['Shajarian & Lotfi — Live in Concert, Royal Albert Hall (1985) [Remastered].flac', Math.round(412 * MB), { daysAgo: 100 }],
  ['یادداشت.txt', Math.round(1 * KB), { daysAgo: 0 }],
];

/** Adds the long-named apps to the phones' app lists. */
export function addLongNameApps(appLists: Map<string, AppInfo[]>): void {
  appLists.get(IPHONE_ID)?.push({ ...LONG_IPHONE_APP });
  appLists.get(ANDROID_ID)?.push({ ...LONG_ANDROID_APP });
}

/** Puts the long-named app's files on the phone (before its app folders are mounted as roots). */
export function addLongNameFiles(deviceId: string, fs: MockFileSystem): void {
  if (deviceId === IPHONE_ID) fs.addSample(`app:${LONG_IPHONE_APP.id}`, '/', TREE, 91);
  if (deviceId === ANDROID_ID) fs.addSample('internal', '/Android/media', [[LONG_ANDROID_APP.id, TREE]], 92);
}
