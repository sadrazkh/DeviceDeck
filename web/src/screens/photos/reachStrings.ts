// Items this PC can't delete (v0.4.2, deletejob2): Windows doesn't show them at all (photos moved over from another
// iPhone, older imports), so only the Photos app on the iPhone can delete them. Spread into en.ts and fa.ts.

export const reachEn = {
  'reach.badge': 'Delete on the iPhone',
  'reach.badgeWhy':
    'Windows doesn’t show this item to the PC, so only the Photos app on the iPhone can delete it. Photos moved over from another iPhone, and older imports, often aren’t shown to Windows.',
  'reach.summary': '{count, plural, one {# of {total} can only be deleted on the iPhone} other {# of {total} can only be deleted on the iPhone}}',
  'reach.checking': 'Checking which items this PC can delete…',
  'reach.checkingProgress': 'Checking which items this PC can delete… {done} of {total}',
  'reach.onlyOnPhone': 'Delete these in the Photos app on the iPhone',
  'reach.someOnPhone': '{count, plural, one {# of them can only be deleted on the iPhone} other {# of them can only be deleted on the iPhone}}',
  'reach.leftOut':
    '{count, plural, one {# item can’t be deleted from this PC — delete it in the Photos app} other {# items can’t be deleted from this PC — delete them in the Photos app}}',
  'reach.leftOutWhy': 'Windows doesn’t show them to this PC, so they stay on the iPhone. Photos moved over from another iPhone, and older imports, often aren’t shown.',
} as const;

export type ReachKey = keyof typeof reachEn;

export const reachFa: Record<ReachKey, string> = {
  'reach.badge': 'حذف روی آیفون',
  'reach.badgeWhy':
    'ویندوز این مورد را به رایانه نشان نمی‌دهد، پس فقط برنامهٔ Photos روی آیفون می‌تواند حذفش کند. عکس‌هایی که از آیفون دیگری منتقل شده‌اند و واردشده‌های قدیمی‌تر اغلب به ویندوز نشان داده نمی‌شوند.',
  'reach.summary': '{count} از {total} مورد را فقط روی آیفون می‌شود حذف کرد',
  'reach.checking': 'در حال بررسی اینکه این رایانه کدام موردها را می‌تواند حذف کند…',
  'reach.checkingProgress': 'در حال بررسی اینکه این رایانه کدام موردها را می‌تواند حذف کند… {done} از {total}',
  'reach.onlyOnPhone': 'این‌ها را در برنامهٔ Photos روی آیفون حذف کنید',
  'reach.someOnPhone': '{count} مورد از این‌ها را فقط روی آیفون می‌شود حذف کرد',
  'reach.leftOut': '{count} مورد از این رایانه حذف‌شدنی نیست — آن را در برنامهٔ Photos حذف کنید',
  'reach.leftOutWhy': 'ویندوز آن‌ها را به این رایانه نشان نمی‌دهد، پس روی آیفون می‌مانند. عکس‌هایی که از آیفون دیگری منتقل شده‌اند و واردشده‌های قدیمی‌تر اغلب نشان داده نمی‌شوند.',
};
