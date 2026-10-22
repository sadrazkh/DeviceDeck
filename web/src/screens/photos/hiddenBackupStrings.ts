// Strings of hidden photos in the photo backup (round 4, hiddenbackup), spread into en.ts and fa.ts. Same rules as
// there: every English key has a Persian one; plurals use "{count, plural, …}". Folder names in paths stay as Windows
// writes them (Latin digits), inside an LTR isolate.

export const hiddenBackupEn = {
  'photos.backup.hidden': 'Normal and hidden photos',
  'photos.backup.hidden.exclude': 'Normal only',
  'photos.backup.hidden.include': 'Both',
  'photos.backup.hidden.only': 'Hidden only',
  'photos.backup.hidden.excludeHint': 'Normal only is the default: hidden photos are locked on the iPhone, but a backup folder isn’t.',
  'photos.backup.hidden.layout': 'Hidden photos go',
  'photos.backup.hidden.separate': 'In a folder of their own',
  'photos.backup.hidden.together': 'With the other photos',
  'photos.backup.hidden.folder': 'Folder name',
  'photos.backup.hidden.suggest': 'Use “{name}”',
  'photos.backup.hidden.example': 'For example',
  'photos.backup.hidden.privacy': 'Hidden photos are copied as ordinary files: anyone who can open this folder on the PC can see them.',
  'photos.backup.hidden.moves': 'Changing this moves nothing on the PC: photos already backed up stay where they are, also if you hide or unhide them later. Only new ones follow it.',
  'photos.backup.hidden.iphoneOnly': 'iPhone only: an Android phone has no Hidden album.',
  'photos.backup.hidden.newHidden': '{count, plural, one {# hidden} other {# hidden}}',
  'photos.backup.hidden.normalLeftOut':
    '{count, plural, one {# new photo that isn’t hidden is left out: this phone backs up hidden photos only.} other {# new photos that aren’t hidden are left out: this phone backs up hidden photos only.}}',
  'photos.backup.hidden.problem.empty': 'Type a folder name.',
  'photos.backup.hidden.problem.tooLong': 'At most {max} characters.',
  'photos.backup.hidden.problem.separator': 'One folder name, without \\ or /.',
  'photos.backup.hidden.problem.characters': 'Without : * ? " < > | or invisible characters.',
  'photos.backup.hidden.problem.dot': 'It can’t end with a dot.',
  'photos.backup.hidden.problem.device': 'Windows keeps this name for devices.',
  'photos.backup.hidden.problem.date': 'It can’t look like a date folder (or “Undated”).',
} as const;

export const hiddenBackupFa: Record<keyof typeof hiddenBackupEn, string> = {
  'photos.backup.hidden': 'عکس‌های عادی و پنهان',
  'photos.backup.hidden.exclude': 'فقط عادی',
  'photos.backup.hidden.include': 'هر دو',
  'photos.backup.hidden.only': 'فقط پنهان',
  'photos.backup.hidden.excludeHint': 'پیش‌فرض «فقط عادی» است: عکس‌های پنهان روی آیفون قفل‌اند، ولی پوشهٔ پشتیبان قفل نیست.',
  'photos.backup.hidden.layout': 'جای عکس‌های پنهان',
  'photos.backup.hidden.separate': 'در پوشه‌ای جدا',
  'photos.backup.hidden.together': 'کنار بقیهٔ عکس‌ها',
  'photos.backup.hidden.folder': 'نام پوشه',
  'photos.backup.hidden.suggest': 'استفاده از «{name}»',
  'photos.backup.hidden.example': 'برای نمونه',
  'photos.backup.hidden.privacy': 'عکس‌های پنهان مثل فایل‌های معمولی کپی می‌شوند: هر کسی که بتواند این پوشه را روی رایانه باز کند، آن‌ها را می‌بیند.',
  'photos.backup.hidden.moves': 'این تغییر چیزی را روی رایانه جابه‌جا نمی‌کند: عکس‌هایی که پشتیبانشان گرفته شده سر جایشان می‌مانند، حتی اگر بعداً پنهان یا آشکارشان کنید. فقط عکس‌های تازه طبق آن می‌روند.',
  'photos.backup.hidden.iphoneOnly': 'فقط برای آیفون: گوشی اندروید آلبوم Hidden ندارد.',
  'photos.backup.hidden.newHidden': '{count, plural, one {# پنهان} other {# پنهان}}',
  'photos.backup.hidden.normalLeftOut':
    '{count, plural, one {# عکس تازهٔ غیرپنهان کنار گذاشته می‌شود: این گوشی فقط از عکس‌های پنهان پشتیبان می‌گیرد.} other {# عکس تازهٔ غیرپنهان کنار گذاشته می‌شوند: این گوشی فقط از عکس‌های پنهان پشتیبان می‌گیرد.}}',
  'photos.backup.hidden.problem.empty': 'یک نام پوشه بنویسید.',
  'photos.backup.hidden.problem.tooLong': 'حداکثر {max} نویسه.',
  'photos.backup.hidden.problem.separator': 'فقط یک نام پوشه، بدون \\ یا /.',
  'photos.backup.hidden.problem.characters': 'بدون : * ? " < > | یا نویسه‌های نامرئی.',
  'photos.backup.hidden.problem.dot': 'نمی‌تواند با نقطه تمام شود.',
  'photos.backup.hidden.problem.device': 'ویندوز این نام را برای دستگاه‌ها نگه می‌دارد.',
  'photos.backup.hidden.problem.date': 'نباید شبیه پوشهٔ تاریخ (یا «Undated») باشد.',
};
