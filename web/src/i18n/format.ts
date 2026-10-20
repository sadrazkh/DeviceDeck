// Locale-aware formatting. Persian uses Persian digits and the Solar Hijri calendar.

export type Lang = 'en' | 'fa';

const LOCALE: Record<Lang, string> = { en: 'en-GB', fa: 'fa-IR' };
export const localeOf = (lang: Lang) => LOCALE[lang];

const numberFormats = new Map<string, Intl.NumberFormat>();
function nf(lang: Lang, options: Intl.NumberFormatOptions = {}): Intl.NumberFormat {
  const key = `${lang}|${JSON.stringify(options)}`;
  let f = numberFormats.get(key);
  if (!f) {
    f = new Intl.NumberFormat(LOCALE[lang], options);
    numberFormats.set(key, f);
  }
  return f;
}

export function formatNumber(lang: Lang, value: number, options?: Intl.NumberFormatOptions): string {
  return nf(lang, options).format(value);
}

export function formatPercent(lang: Lang, fraction: number): string {
  return nf(lang, { style: 'percent', maximumFractionDigits: 0 }).format(Math.max(0, Math.min(1, fraction)));
}

const PERSIAN_DIGITS = '۰۱۲۳۴۵۶۷۸۹';

/** Swaps ASCII digits for Persian ones (for version strings and the like) in Persian. */
export function localDigits(lang: Lang, text: string): string {
  if (lang !== 'fa') return text;
  return text.replace(/[0-9]/g, (d) => PERSIAN_DIGITS[Number(d)]!).replace(/(\d|[۰-۹])\.(?=[۰-۹])/g, '$1٫');
}

const UNITS: Record<Lang, string[]> = {
  en: ['B', 'KB', 'MB', 'GB', 'TB'],
  fa: ['بایت', 'کیلوبایت', 'مگابایت', 'گیگابایت', 'ترابایت'],
};

/** Decimal units (1 GB = 10^9 bytes), the way phones report storage. */
export function formatBytes(lang: Lang, bytes: number, options: { digits?: number } = {}): string {
  const units = UNITS[lang];
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  const digits = options.digits ?? (unit === 0 ? 0 : value >= 100 ? 0 : 1);
  return `${formatNumber(lang, value, { maximumFractionDigits: digits, minimumFractionDigits: unit === 0 ? 0 : Math.min(digits, 1) })} ${units[unit]}`;
}

export function formatSpeed(lang: Lang, bytesPerSecond: number): string {
  const perSecond = lang === 'fa' ? '/ثانیه' : '/s';
  return `${formatBytes(lang, bytesPerSecond, { digits: bytesPerSecond >= 100e6 ? 0 : 0 })}${perSecond}`;
}

const dateFormats = new Map<string, Intl.DateTimeFormat>();
function df(lang: Lang, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `${lang}|${JSON.stringify(options)}`;
  let f = dateFormats.get(key);
  if (!f) {
    f = new Intl.DateTimeFormat(LOCALE[lang], options);
    dateFormats.set(key, f);
  }
  return f;
}

export function formatDate(lang: Lang, iso: string | number | Date, style: 'short' | 'medium' | 'long' = 'medium'): string {
  const date = new Date(iso);
  if (style === 'short') return df(lang, { day: 'numeric', month: 'short', year: 'numeric' }).format(date);
  if (style === 'long') return df(lang, { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date);
  return df(lang, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date);
}

export function formatMonth(lang: Lang, iso: string | number | Date): string {
  return df(lang, { month: 'long', year: 'numeric' }).format(new Date(iso));
}

export function formatMonthShort(lang: Lang, iso: string | number | Date): string {
  return df(lang, { month: 'short' }).format(new Date(iso));
}

export function formatYear(lang: Lang, iso: string | number | Date): string {
  return df(lang, { year: 'numeric' }).format(new Date(iso));
}

const monthKeyFormats = new Map<Lang, Intl.DateTimeFormat>();

/**
 * "2026-10" in the UI's calendar: Gregorian in English, Solar Hijri in Persian, so the photo grid's
 * month groups match the month names people read.
 */
export function monthKey(lang: Lang, iso: string): string {
  let f = monthKeyFormats.get(lang);
  if (!f) {
    f = new Intl.DateTimeFormat(lang === 'fa' ? 'fa-IR-u-nu-latn' : 'en-GB-u-nu-latn', {
      year: 'numeric',
      month: '2-digit',
    });
    monthKeyFormats.set(lang, f);
  }
  const parts = f.formatToParts(new Date(iso));
  const year = parts.find((p) => p.type === 'year')?.value ?? '0';
  const month = parts.find((p) => p.type === 'month')?.value ?? '0';
  return `${year}-${month.padStart(2, '0')}`;
}

export function formatDuration(lang: Lang, seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  const m = Math.floor(s / 60);
  const rest = s % 60;
  const h = Math.floor(m / 60);
  const text = h > 0 ? `${h}:${String(m % 60).padStart(2, '0')}:${String(rest).padStart(2, '0')}` : `${m}:${String(rest).padStart(2, '0')}`;
  return localDigits(lang, text);
}
