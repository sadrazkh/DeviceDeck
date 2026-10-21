// Strings of the photo backup added in round 3 (date range, start on connect, free space, edited photos),
// spread into en.ts and fa.ts. Same rules as there: every English key has a Persian one; plurals use "{count, plural, …}".

export const photoBackupEn = {
  'photos.backup.range': 'Photos to back up',
  'photos.backup.range.all': 'Everything',
  'photos.backup.range.lastDays': '{count, plural, one {Last day} other {Last # days}}',
  'photos.backup.range.since': 'Since a date',
  'photos.backup.range.sinceLabel': 'Taken on or after',
  'photos.backup.range.hint': 'By the date each photo was taken. Photos without a date are always included.',
  'photos.backup.outOfRange': '{count, plural, one {# older photo is left out by the date range.} other {# older photos are left out by the date range.}}',
} as const;

export const photoBackupFa: Record<keyof typeof photoBackupEn, string> = {
  'photos.backup.range': 'عکس‌هایی که پشتیبان می‌گیرد',
  'photos.backup.range.all': 'همه',
  'photos.backup.range.lastDays': '{count, plural, one {روز گذشته} other {# روز گذشته}}',
  'photos.backup.range.since': 'از یک تاریخ',
  'photos.backup.range.sinceLabel': 'گرفته‌شده از',
  'photos.backup.range.hint': 'بر اساس تاریخ گرفتن هر عکس. عکس‌های بی‌تاریخ همیشه پشتیبان گرفته می‌شوند.',
  'photos.backup.outOfRange': '{count, plural, one {# عکس قدیمی‌تر بیرون از بازهٔ تاریخ است و کنار گذاشته می‌شود.} other {# عکس قدیمی‌تر بیرون از بازهٔ تاریخ‌اند و کنار گذاشته می‌شوند.}}',
};
