// Strings of the "Add to iPhone" dialog (round 3, localsend session): photos straight into Photos with LocalSend, and
// the "into an app" route's save-them-all steps. Spread into en.ts and fa.ts; every English key has a Persian one.
// iOS's own labels (Files, On My iPhone, Select All, Share, Accept, Quick Save) stay as the iPhone shows them.

export const localSendEn = {
  'photos.toApp.route': 'How',
  'localsend.route.photos': 'Straight into Photos (with LocalSend)',
  'localsend.route.photosHint': 'Free app from the App Store. Photos and videos land in Photos; nothing to save by hand.',
  'localsend.route.recommended': 'Recommended',
  'localsend.route.app': 'Into an app’s folder',
  'localsend.route.appHint': 'No extra app. Then save them all at once in the Files app.',

  'localsend.steps.title': 'On the iPhone',
  'localsend.steps.install': 'Install LocalSend from the App Store (it’s free).',
  'localsend.steps.open': 'Open LocalSend and keep it in front until it’s done.',
  'localsend.steps.accept': 'Tap Accept when it asks, or turn on Quick Save in LocalSend’s settings so nothing needs a tap.',

  'localsend.status.looking': 'Looking for LocalSend on {device}…',
  'localsend.status.notListening': 'LocalSend isn’t open on {device}. Open it and keep it open.',
  'localsend.status.notAnswering': 'LocalSend is in the background on {device}. Bring it to the front.',
  'localsend.status.unavailable': '{device} can’t be reached over its cable. Look on Wi-Fi instead.',
  'localsend.status.foundUsb': 'Found «{name}» over USB',
  'localsend.status.foundWifi': 'Sending to «{name}» on Wi-Fi',

  'localsend.wifi.look': 'Look on Wi-Fi instead',
  'localsend.wifi.looking': 'Looking on Wi-Fi…',
  'localsend.wifi.none': 'Nothing found on Wi-Fi. The PC and the iPhone must be on the same network, with LocalSend open.',
  'localsend.wifi.pick': 'On Wi-Fi',
  'localsend.wifi.likely': 'This iPhone?',
  'localsend.wifi.again': 'Look again',
  'localsend.wifi.useCable': 'Use the cable',

  'localsend.send': '{count, plural, =0 {Send to Photos} one {Send # file to Photos} other {Send # files to Photos}}',
  'localsend.started': 'Sending to {device} with LocalSend',

  'localsend.job.connecting': 'Reaching «{name}»…',
  'localsend.job.waiting': 'Waiting for Accept: tap Accept in LocalSend on {device}.',
  'localsend.job.sending': 'Sending {done} of {total}…',
  'localsend.job.done': '{count, plural, one {Done: # file is in Photos now.} other {Done: # files are in Photos now.}}',
  'localsend.job.doneSome': '{failed, plural, one {# file didn’t go.} other {# files didn’t go.}}',
  'localsend.job.paused': 'Paused. Resume it in Transfers.',
  'localsend.job.waitingPhone': 'Waiting for {device} to come back.',
  'localsend.job.cancelled': 'Cancelled. Nothing more goes to the iPhone.',
  'localsend.job.failed': 'It didn’t go',
  'localsend.job.where': 'Images and videos go into Photos; any other file goes into LocalSend’s own folder.',

  'localsend.pin.title': 'LocalSend asks for a PIN',
  'localsend.pin.wrong': 'That PIN didn’t match. Check the PIN in LocalSend and try again.',
  'localsend.pin.label': 'The PIN set in LocalSend’s settings',
  'localsend.pin.send': 'Send with this PIN',

  'localsend.retry': 'Try again',
  'localsend.cancel': 'Cancel the send',
  'localsend.hide': 'Keep going in the background',
  'localsend.close': 'Close',

  'localsend.files.title': 'Each file',
  'localsend.file.done': 'In Photos',
  'localsend.file.failed': 'Didn’t go',
  'localsend.file.skipped': 'Already there',
  'localsend.file.queued': 'Waiting',
  'localsend.file.sending': 'Sending',

  'error.next.localsend_not_listening': 'Open LocalSend on the iPhone, keep it in front, and try again.',
  'error.next.localsend_connection_lost': 'Keep LocalSend open in front until it’s done, then try again.',
  'error.next.localsend_pin_required': 'Enter the PIN LocalSend shows, then send again.',
  'error.next.localsend_pin_wrong': 'Check the PIN in LocalSend’s settings and try again.',
  'error.next.localsend_rejected': 'It was declined on the iPhone. Try again and tap Accept, or turn on Quick Save.',
  'error.next.localsend_busy': 'Wait for LocalSend’s other transfer to finish, then try again.',
  'error.next.localsend_checksum': 'Try again. If it keeps happening, copy the file to this PC again.',
  'error.next.localsend_session_lost': 'The send was stopped on the iPhone. Try again.',
  'error.next.localsend_fingerprint': 'A different LocalSend answered. Look for the iPhone again, then send.',
  'error.next.localsend_failed': 'Try again. If it keeps happening, close and reopen LocalSend.',

  // "Into an app": the photos go into a dated folder; Files saves a whole folder at once.
  'photos.toApp.saveAll.title': 'Then save them all at once',
  'photos.toApp.saveAll.open': 'On the iPhone, open this folder in the Files app:',
  'photos.toApp.saveAll.select': 'Tap Select (or ⋯ › Select), then Select All.',
  'photos.toApp.saveAll.share': '{count, plural, =0 {Tap Share › Save Images.} other {Tap Share › Save # Images.}}',
  'photos.toApp.saveAll.parts': 'More than 200 go into folders of 200 inside it ({first}, …): do the same in each.',
  'photos.toApp.saveAll.notInFiles': 'If {app} isn’t in Files, open {app} itself and do the same there.',
  'photos.toApp.sent.title': 'On their way to {app}',
  'photos.toApp.done': 'Done',
} as const;

export type LocalSendMessageKey = keyof typeof localSendEn;

export const localSendFa: Record<LocalSendMessageKey, string> = {
  'photos.toApp.route': 'روش',
  'localsend.route.photos': 'مستقیم به Photos (با LocalSend)',
  'localsend.route.photosHint': 'برنامهٔ رایگان App Store. عکس‌ها و ویدیوها مستقیم به Photos می‌روند؛ بدون ذخیرهٔ دستی.',
  'localsend.route.recommended': 'پیشنهادی',
  'localsend.route.app': 'به پوشهٔ یک برنامه',
  'localsend.route.appHint': 'بدون برنامهٔ اضافه. بعد همه را یک‌جا در برنامهٔ Files ذخیره کنید.',

  'localsend.steps.title': 'روی آیفون',
  'localsend.steps.install': 'LocalSend را از App Store نصب کنید (رایگان است).',
  'localsend.steps.open': 'LocalSend را باز کنید و تا پایان کار جلوی صفحه نگهش دارید.',
  'localsend.steps.accept': 'وقتی پرسید Accept را بزنید، یا Quick Save را در تنظیمات LocalSend روشن کنید تا نیازی به زدن نباشد.',

  'localsend.status.looking': 'در حال جست‌وجوی LocalSend روی {device}…',
  'localsend.status.notListening': 'LocalSend روی {device} باز نیست. آن را باز کنید و باز نگه دارید.',
  'localsend.status.notAnswering': 'LocalSend روی {device} در پس‌زمینه است. آن را به جلوی صفحه بیاورید.',
  'localsend.status.unavailable': '{device} از راه کابل در دسترس نیست. به‌جایش در Wi-Fi جست‌وجو کنید.',
  'localsend.status.foundUsb': '«{name}» از راه USB پیدا شد',
  'localsend.status.foundWifi': 'فرستادن به «{name}» در Wi-Fi',

  'localsend.wifi.look': 'به‌جایش در Wi-Fi جست‌وجو کن',
  'localsend.wifi.looking': 'در حال جست‌وجو در Wi-Fi…',
  'localsend.wifi.none': 'در Wi-Fi چیزی پیدا نشد. PC و آیفون باید در یک شبکه باشند و LocalSend باز باشد.',
  'localsend.wifi.pick': 'در Wi-Fi',
  'localsend.wifi.likely': 'همین آیفون؟',
  'localsend.wifi.again': 'دوباره جست‌وجو کن',
  'localsend.wifi.useCable': 'از راه کابل',

  'localsend.send': '{count, plural, =0 {فرستادن به Photos} other {فرستادن {count} فایل به Photos}}',
  'localsend.started': 'فرستادن به {device} با LocalSend',

  'localsend.job.connecting': 'در حال رسیدن به «{name}»…',
  'localsend.job.waiting': 'در انتظار Accept: در LocalSend روی {device} دکمهٔ Accept را بزنید.',
  'localsend.job.sending': 'در حال فرستادن {done} از {total}…',
  'localsend.job.done': '{count, plural, other {انجام شد: {count} فایل اکنون در Photos است.}}',
  'localsend.job.doneSome': '{failed, plural, other {{failed} فایل نرفت.}}',
  'localsend.job.paused': 'متوقف شد. در بخش انتقال‌ها ادامه‌اش دهید.',
  'localsend.job.waitingPhone': 'در انتظار برگشتن {device}.',
  'localsend.job.cancelled': 'لغو شد. دیگر چیزی به آیفون نمی‌رود.',
  'localsend.job.failed': 'نرفت',
  'localsend.job.where': 'عکس‌ها و ویدیوها به Photos می‌روند؛ هر فایل دیگری به پوشهٔ خود LocalSend.',

  'localsend.pin.title': 'LocalSend یک PIN می‌خواهد',
  'localsend.pin.wrong': 'این PIN درست نبود. PIN را در LocalSend ببینید و دوباره امتحان کنید.',
  'localsend.pin.label': 'PIN تعیین‌شده در تنظیمات LocalSend',
  'localsend.pin.send': 'فرستادن با این PIN',

  'localsend.retry': 'دوباره امتحان کن',
  'localsend.cancel': 'لغو فرستادن',
  'localsend.hide': 'در پس‌زمینه ادامه بده',
  'localsend.close': 'بستن',

  'localsend.files.title': 'هر فایل',
  'localsend.file.done': 'در Photos',
  'localsend.file.failed': 'نرفت',
  'localsend.file.skipped': 'از قبل آن‌جا بود',
  'localsend.file.queued': 'در انتظار',
  'localsend.file.sending': 'در حال فرستادن',

  'error.next.localsend_not_listening': 'LocalSend را روی آیفون باز کنید، جلوی صفحه نگهش دارید و دوباره امتحان کنید.',
  'error.next.localsend_connection_lost': 'تا پایان کار LocalSend را باز و جلوی صفحه نگه دارید، سپس دوباره امتحان کنید.',
  'error.next.localsend_pin_required': 'PIN نشان‌داده‌شده در LocalSend را وارد کنید و دوباره بفرستید.',
  'error.next.localsend_pin_wrong': 'PIN را در تنظیمات LocalSend بررسی کنید و دوباره امتحان کنید.',
  'error.next.localsend_rejected': 'روی آیفون رد شد. دوباره امتحان کنید و Accept را بزنید، یا Quick Save را روشن کنید.',
  'error.next.localsend_busy': 'صبر کنید انتقال دیگر LocalSend تمام شود، سپس دوباره امتحان کنید.',
  'error.next.localsend_checksum': 'دوباره امتحان کنید. اگر تکرار شد، فایل را دوباره روی این PC کپی کنید.',
  'error.next.localsend_session_lost': 'فرستادن روی آیفون متوقف شد. دوباره امتحان کنید.',
  'error.next.localsend_fingerprint': 'LocalSend دیگری پاسخ داد. دوباره آیفون را پیدا کنید، سپس بفرستید.',
  'error.next.localsend_failed': 'دوباره امتحان کنید. اگر تکرار شد، LocalSend را ببندید و دوباره باز کنید.',

  'photos.toApp.saveAll.title': 'بعد همه را یک‌جا ذخیره کنید',
  'photos.toApp.saveAll.open': 'روی آیفون این پوشه را در برنامهٔ Files باز کنید:',
  // \u2066…\u2069 keep iOS's labels left to right, so the › between them doesn't turn round in Persian.
  'photos.toApp.saveAll.select': '\u2066Select\u2069 (یا \u2066⋯ › Select\u2069) و سپس \u2066Select All\u2069 را بزنید.',
  'photos.toApp.saveAll.share': '{count, plural, =0 {\u2066Share › Save Images\u2069 را بزنید.} other {\u2066Share › Save {count} Images\u2069 را بزنید.}}',
  'photos.toApp.saveAll.parts': 'بیش از ۲۰۰ فایل در پوشه‌های ۲۰۰تایی داخل آن می‌روند ({first} و …): در هر کدام همین کار را بکنید.',
  'photos.toApp.saveAll.notInFiles': 'اگر {app} در Files نیست، خود {app} را باز کنید و همین کار را آن‌جا بکنید.',
  'photos.toApp.sent.title': 'در راه {app}',
  'photos.toApp.done': 'تمام',
};
