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
  'photos.backup.space.missing.title': 'The drive {root} isn’t connected',
  'photos.backup.space.missing.body': 'Connect it, or choose another folder for this phone’s photos.',
  'photos.backup.space.short.title': 'Not enough space on {root}',
  'photos.backup.space.short.body': 'The new photos need about {needed} with room to spare, and {free} is free. Free some space, or choose another folder.',
  'photos.backup.space.nearlyFull': '{root} is nearly full: {free} free.',
  'photos.backup.space.choose': 'Choose another folder',
  'settings.photoBackup.nearlyFull': '{root} is nearly full ({free} free). Backing up a whole phone can take tens of gigabytes; a folder on a drive with more room is safer.',
  'settings.photoBackup.missing': 'The drive {root} isn’t connected.',
} as const;

export const photoBackupFa: Record<keyof typeof photoBackupEn, string> = {
  'photos.backup.range': 'عکس‌هایی که پشتیبان می‌گیرد',
  'photos.backup.range.all': 'همه',
  'photos.backup.range.lastDays': '{count, plural, one {روز گذشته} other {# روز گذشته}}',
  'photos.backup.range.since': 'از یک تاریخ',
  'photos.backup.range.sinceLabel': 'گرفته‌شده از',
  'photos.backup.range.hint': 'بر اساس تاریخ گرفتن هر عکس. عکس‌های بی‌تاریخ همیشه پشتیبان گرفته می‌شوند.',
  'photos.backup.outOfRange': '{count, plural, one {# عکس قدیمی‌تر بیرون از بازهٔ تاریخ است و کنار گذاشته می‌شود.} other {# عکس قدیمی‌تر بیرون از بازهٔ تاریخ‌اند و کنار گذاشته می‌شوند.}}',
  'photos.backup.space.missing.title': 'درایو {root} وصل نیست',
  'photos.backup.space.missing.body': 'وصلش کنید، یا پوشهٔ دیگری برای عکس‌های این گوشی انتخاب کنید.',
  'photos.backup.space.short.title': 'جای کافی روی {root} نیست',
  'photos.backup.space.short.body': 'عکس‌های تازه با کمی جای اضافه حدود {needed} لازم دارند و {free} خالی است. کمی جا باز کنید، یا پوشهٔ دیگری انتخاب کنید.',
  'photos.backup.space.nearlyFull': '{root} تقریباً پر است: {free} خالی.',
  'photos.backup.space.choose': 'انتخاب پوشهٔ دیگر',
  'settings.photoBackup.nearlyFull': '{root} تقریباً پر است ({free} خالی). پشتیبان کامل یک گوشی ممکن است ده‌ها گیگابایت جا بگیرد؛ پوشه‌ای روی درایوی با جای بیشتر امن‌تر است.',
  'settings.photoBackup.missing': 'درایو {root} وصل نیست.',
};
