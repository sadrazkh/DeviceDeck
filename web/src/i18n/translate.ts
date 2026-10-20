// A tiny message formatter: {name} placeholders and ICU-style plurals
// "{count, plural, one {# photo} other {# photos}}". Numbers are formatted for the language.

import { formatNumber, type Lang } from './format';

export type Vars = Record<string, string | number>;

const pluralRules = new Map<Lang, Intl.PluralRules>();
function plural(lang: Lang, n: number): string {
  let rules = pluralRules.get(lang);
  if (!rules) {
    rules = new Intl.PluralRules(lang === 'fa' ? 'fa-IR' : 'en-GB');
    pluralRules.set(lang, rules);
  }
  return rules.select(n);
}

function display(lang: Lang, value: string | number | undefined): string {
  if (value === undefined) return '';
  return typeof value === 'number' ? formatNumber(lang, value) : value;
}

/** Index of the brace that closes the one at `open`. */
function closing(text: string, open: number): number {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return i;
    }
  }
  return -1;
}

function parseForms(body: string): Map<string, string> {
  const forms = new Map<string, string>();
  let i = 0;
  while (i < body.length) {
    const open = body.indexOf('{', i);
    if (open < 0) break;
    const selector = body.slice(i, open).trim();
    const close = closing(body, open);
    if (close < 0) break;
    forms.set(selector, body.slice(open + 1, close));
    i = close + 1;
  }
  return forms;
}

export function formatMessage(lang: Lang, template: string, vars: Vars = {}): string {
  let out = '';
  let i = 0;
  while (i < template.length) {
    const open = template.indexOf('{', i);
    if (open < 0) {
      out += template.slice(i);
      break;
    }
    out += template.slice(i, open);
    const close = closing(template, open);
    if (close < 0) {
      out += template.slice(open);
      break;
    }
    const inner = template.slice(open + 1, close);
    const comma = inner.indexOf(',');
    if (comma < 0) {
      const name = inner.trim();
      out += name in vars ? display(lang, vars[name]) : `{${name}}`;
    } else {
      const name = inner.slice(0, comma).trim();
      const rest = inner.slice(comma + 1);
      const kindEnd = rest.indexOf(',');
      const kind = rest.slice(0, kindEnd).trim();
      if (kind === 'plural') {
        const n = Number(vars[name] ?? 0);
        const forms = parseForms(rest.slice(kindEnd + 1));
        const form = forms.get(`=${n}`) ?? forms.get(plural(lang, n)) ?? forms.get('other') ?? '';
        out += formatMessage(lang, form.replace(/#/g, `{${name}}`), vars);
      } else {
        out += template.slice(open, close + 1);
      }
    }
    i = close + 1;
  }
  return out;
}
