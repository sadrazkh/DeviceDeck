import { describe, expect, it } from 'vitest';
import { en } from '../../src/i18n/en';
import { fa } from '../../src/i18n/fa';
import { formatBytes, formatDuration, localDigits, monthKey } from '../../src/i18n/format';
import { makeI18n } from '../../src/i18n';
import { formatMessage } from '../../src/i18n/translate';

describe('dictionaries', () => {
  it('Persian has every English key and nothing else', () => {
    expect(Object.keys(fa).sort()).toEqual(Object.keys(en).sort());
  });

  it('every placeholder in English exists in Persian', () => {
    // "{name}" or "{name, plural, …}"; plural bodies like "{# photos}" are not placeholders.
    const names = (s: string) => new Set([...s.matchAll(/\{(\w+)(?=[},])/g)].map((m) => m[1]));
    for (const key of Object.keys(en) as (keyof typeof en)[]) {
      const english = names(en[key]);
      const persian = names(fa[key]);
      for (const name of english) expect(persian.has(name), `${key}: {${name}}`).toBe(true);
    }
  });
});

describe('formatMessage', () => {
  it('fills placeholders and formats numbers for the language', () => {
    expect(formatMessage('en', 'Hello {name}', { name: 'Nima' })).toBe('Hello Nima');
    expect(formatMessage('en', '{count} items', { count: 12481 })).toBe('12,481 items');
    expect(formatMessage('fa', '{count} مورد', { count: 12481 })).toBe('۱۲٬۴۸۱ مورد');
  });

  it('picks plural forms, with # as the number', () => {
    const t = '{count, plural, one {# photo} other {# photos}}';
    expect(formatMessage('en', t, { count: 1 })).toBe('1 photo');
    expect(formatMessage('en', t, { count: 1284 })).toBe('1,284 photos');
    expect(formatMessage('en', '{n, plural, =0 {none} other {#}}', { n: 0 })).toBe('none');
  });

  it('leaves unknown placeholders visible', () => {
    expect(formatMessage('en', 'Hi {who}')).toBe('Hi {who}');
  });
});

describe('formatting', () => {
  it('uses decimal units like the phones do', () => {
    expect(formatBytes('en', 61.8e9)).toBe('61.8 GB');
    expect(formatBytes('en', 256e9, { digits: 0 })).toBe('256 GB');
    expect(formatBytes('en', 512)).toBe('512 B');
    expect(formatBytes('fa', 61.8e9)).toBe('۶۱٫۸ گیگابایت');
  });

  it('writes Persian digits in Persian', () => {
    expect(localDigits('fa', 'iOS 26.1')).toBe('iOS ۲۶٫۱');
    expect(localDigits('en', 'iOS 26.1')).toBe('iOS 26.1');
    expect(formatDuration('en', 75)).toBe('1:15');
    expect(formatDuration('fa', 3725)).toBe('۱:۰۲:۰۵');
  });

  it('groups months by the UI calendar: Gregorian in English, Solar Hijri in Persian', () => {
    expect(monthKey('en', '2026-10-15T12:00:00')).toBe('2026-10');
    // 15 October 2026 is 23 Mehr 1405 in the Solar Hijri calendar.
    expect(monthKey('fa', '2026-10-15T12:00:00')).toBe('1405-07');
    expect(monthKey('fa', '2026-10-25T12:00:00')).toBe('1405-08');
  });

  it('turns seconds left into a short phrase', () => {
    const i18n = makeI18n('en');
    expect(i18n.eta(20)).toBe('20 seconds left');
    expect(i18n.eta(120)).toBe('2 min left');
    expect(i18n.eta(7200)).toBe('2 hours left');
    expect(makeI18n('fa').eta(120)).toBe('۲ دقیقه مانده');
  });
});
