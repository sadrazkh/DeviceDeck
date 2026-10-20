import { useMemo } from 'react';
import { usePrefs } from '../state/prefs';
import { en, type MessageKey } from './en';
import { fa } from './fa';
import {
  formatBytes,
  formatDate,
  formatDuration,
  formatMonth,
  formatMonthShort,
  formatNumber,
  formatPercent,
  formatSpeed,
  formatYear,
  localDigits,
  monthKey,
  type Lang,
} from './format';
import { formatMessage, type Vars } from './translate';

export type { Lang, MessageKey };

const dictionaries: Record<Lang, Record<MessageKey, string>> = { en, fa };

export function translate(lang: Lang, key: MessageKey, vars?: Vars): string {
  return formatMessage(lang, dictionaries[lang][key] ?? en[key] ?? key, vars);
}

export function makeI18n(lang: Lang) {
  const t = (key: MessageKey, vars?: Vars) => translate(lang, key, vars);
  return {
    lang,
    dir: (lang === 'fa' ? 'rtl' : 'ltr') as 'rtl' | 'ltr',
    t,
    n: (value: number, options?: Intl.NumberFormatOptions) => formatNumber(lang, value, options),
    percent: (fraction: number) => formatPercent(lang, fraction),
    bytes: (bytes: number, digits?: number) => formatBytes(lang, bytes, digits === undefined ? {} : { digits }),
    speed: (bytesPerSecond: number) => formatSpeed(lang, bytesPerSecond),
    date: (iso: string | number | Date, style?: 'short' | 'medium' | 'long') => formatDate(lang, iso, style),
    month: (iso: string | number | Date) => formatMonth(lang, iso),
    monthShort: (iso: string | number | Date) => formatMonthShort(lang, iso),
    year: (iso: string | number | Date) => formatYear(lang, iso),
    monthKey: (iso: string) => monthKey(lang, iso),
    digits: (text: string) => localDigits(lang, text),
    duration: (seconds: number) => formatDuration(lang, seconds),
    eta: (seconds: number) => {
      if (seconds < 60) return t('transfers.eta.seconds', { count: Math.max(1, Math.round(seconds)) });
      if (seconds < 3600) return t('transfers.eta.minutes', { count: Math.round(seconds / 60) });
      return t('transfers.eta.hours', { count: Math.round(seconds / 3600) });
    },
  };
}

export type I18n = ReturnType<typeof makeI18n>;

export function useI18n(): I18n {
  const lang = usePrefs((s) => s.language);
  return useMemo(() => makeI18n(lang), [lang]);
}
