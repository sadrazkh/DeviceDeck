// Release notes for "What's new" (round 4, updates): the language's part of the notes, and the safe markdown subset
// they are shown with. The owner writes them as GitHub markdown: a `#` heading, `###` sections, emoji, **bold**
// one-liners and lists, then a <details> block with the whole thing in Persian.
//
// Only this subset becomes elements: headings, paragraphs, lists (one level of nesting), quotes, code, a rule, bold,
// italics, inline code, and links to https://github.com. Everything else stays text: raw HTML tags are dropped (the
// contents of script, style and the like with them), images are dropped, any other link keeps only its words. The tree
// is turned into React elements by ReleaseNotesView; nothing is ever given to the page as HTML.

export type Inline =
  | { kind: 'text'; text: string }
  | { kind: 'strong'; children: Inline[] }
  | { kind: 'em'; children: Inline[] }
  | { kind: 'code'; text: string }
  | { kind: 'link'; href: string; children: Inline[] };

export interface ListBlock {
  kind: 'list';
  ordered: boolean;
  items: { children: Inline[]; sublist?: ListBlock }[];
}

export type Block =
  | { kind: 'heading'; level: 1 | 2 | 3 | 4; children: Inline[] }
  | { kind: 'paragraph'; children: Inline[] }
  | ListBlock
  | { kind: 'quote'; children: Inline[] }
  | { kind: 'code'; text: string }
  | { kind: 'rule' };

const PERSIAN = /[؀-ۿ]/;

/**
 * The part of the notes for the UI's language: the Persian <details> block when the UI is Persian and the notes have
 * one, otherwise everything outside it. `lang` says which one it is (the notes may have no Persian part).
 */
export function notesForLanguage(markdown: string, lang: 'en' | 'fa'): { text: string; lang: 'en' | 'fa' } {
  const text = markdown.replace(/\r\n?/g, '\n');
  const details = [...text.matchAll(/<details\b[^>]*>([\s\S]*?)(?:<\/details\s*>|$)/gi)].find((m) => PERSIAN.test(m[1] ?? ''));
  if (!details) return { text, lang: 'en' };
  const persian = (details[1] ?? '').replace(/<summary\b[^>]*>[\s\S]*?<\/summary\s*>/i, '').trim();
  const english = (text.slice(0, details.index) + text.slice(details.index! + details[0].length)).trim();
  return lang === 'fa' && persian ? { text: persian, lang: 'fa' } : { text: english, lang: 'en' };
}

/** A link the notes may keep: https on github.com, nothing else (no other host, scheme, port or credentials). */
export function safeHref(href: string): string | null {
  let url: URL;
  try {
    url = new URL(href.trim());
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' || (url.hostname !== 'github.com' && url.hostname !== 'www.github.com')) return null;
  if (url.username || url.password || url.port) return null;
  return url.href;
}

/** Raw HTML out of markdown text (fenced code aside): comments and script-like elements with their contents, other tags alone. */
function stripHtml(text: string): string {
  return (
    text
      // <https://github.com/…> is a link, not a tag.
      .replace(/<(https?:\/\/[^\s<>]+)>/gi, '[$1]($1)')
      .replace(/<!--[\s\S]*?(?:-->|$)/g, '')
      .replace(/<(script|style|iframe|object|embed|template|noscript|textarea|title|svg|math|select)\b[\s\S]*?(?:<\/\1\s*>|$)/gi, '')
      .replace(/<br\s*\/?>/gi, ' ')
      // A tag may span lines but not run into the next one; an unclosed "<img src=…" just stays text.
      .replace(/<\/?[a-z][^<>]*>/gi, '')
  );
}

const ENTITIES: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function decodeEntities(text: string): string {
  return text.replace(/&(#x[0-9a-f]{1,6}|#\d{1,7}|[a-z]+);/gi, (whole, name: string) => {
    if (name[0] === '#') {
      const code = name[1] === 'x' || name[1] === 'X' ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
      return code > 0 && code <= 0x10ffff && !(code >= 0xd800 && code <= 0xdfff) ? String.fromCodePoint(code) : '';
    }
    return ENTITIES[name.toLowerCase()] ?? whole;
  });
}

const LIST_ITEM = /^( *)([-*+]|\d{1,9}[.)])[ \t]+(.*)$/;
const HEADING = /^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$/;
const RULE = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const QUOTE = /^ {0,3}>[ \t]?(.*)$/;
const FENCE = /^ {0,3}(`{3,}|~{3,})/;

/** The notes as blocks of the safe subset. */
export function parseNotes(markdown: string): Block[] {
  const lines = markdown.replace(/\r\n?/g, '\n').split('\n');
  // Strip HTML outside fenced code, with the lines rejoined so a tag or comment over several lines goes as a whole.
  const parts: { code: boolean; lines: string[] }[] = [];
  for (let i = 0; i < lines.length; i++) {
    const fence = FENCE.exec(lines[i]!);
    if (fence) {
      const close = lines.findIndex((l, j) => j > i && l.trim().startsWith(fence[1]!));
      const end = close < 0 ? lines.length : close;
      parts.push({ code: true, lines: lines.slice(i + 1, end) });
      i = end;
      continue;
    }
    const last = parts.at(-1);
    if (last && !last.code) last.lines.push(lines[i]!);
    else parts.push({ code: false, lines: [lines[i]!] });
  }

  const blocks: Block[] = [];
  for (const part of parts) {
    if (part.code) blocks.push({ kind: 'code', text: part.lines.join('\n') });
    else blocks.push(...parseText(stripHtml(part.lines.join('\n')).split('\n')));
  }
  return blocks;
}

function parseText(lines: string[]): Block[] {
  const blocks: Block[] = [];
  let paragraph: string[] = [];
  const flush = () => {
    const text = paragraph.join(' ').trim();
    if (text) blocks.push({ kind: 'paragraph', children: parseInline(text) });
    paragraph = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    if (!line.trim()) {
      flush();
      continue;
    }

    const heading = HEADING.exec(line);
    if (heading) {
      flush();
      const level = Math.min(heading[1]!.length, 4) as 1 | 2 | 3 | 4;
      const children = parseInline(heading[2]!);
      if (children.length) blocks.push({ kind: 'heading', level, children });
      continue;
    }

    if (RULE.test(line)) {
      flush();
      blocks.push({ kind: 'rule' });
      continue;
    }

    const quote = QUOTE.exec(line);
    if (quote) {
      flush();
      const quoted = [quote[1]!];
      while (i + 1 < lines.length && QUOTE.test(lines[i + 1]!)) quoted.push(QUOTE.exec(lines[++i]!)![1]!);
      const children = parseInline(quoted.join(' ').trim());
      if (children.length) blocks.push({ kind: 'quote', children });
      continue;
    }

    if (LIST_ITEM.test(line)) {
      flush();
      i = parseList(lines, i, blocks);
      continue;
    }

    paragraph.push(line.trim());
  }
  flush();
  return blocks;
}

/** A list from lines[start]: items at its indent, deeper items as one sublist under the item before them. Returns the last line used. */
function parseList(lines: string[], start: number, blocks: Block[]): number {
  const first = LIST_ITEM.exec(lines[start]!)!;
  const indent = first[1]!.length;
  const ordered = /\d/.test(first[2]!);
  const list: ListBlock = { kind: 'list', ordered, items: [] };
  let i = start;
  for (; i < lines.length; i++) {
    const line = lines[i]!;
    if (!line.trim()) {
      // A blank line between items keeps the list going.
      const next = lines.slice(i + 1).find((l) => l.trim());
      const nextItem = next ? LIST_ITEM.exec(next) : null;
      if (nextItem && (nextItem[1]!.length > indent || /\d/.test(nextItem[2]!) === ordered)) continue;
      break;
    }
    const item = LIST_ITEM.exec(line);
    if (item && item[1]!.length <= indent + 1) {
      if (/\d/.test(item[2]!) !== ordered) break;
      list.items.push({ children: parseInline(item[3]!.trim()) });
      continue;
    }
    const owner = list.items.at(-1);
    if (item && owner) {
      // A deeper item: one level of nesting is kept; anything deeper joins that level.
      owner.sublist ??= { kind: 'list', ordered: /\d/.test(item[2]!), items: [] };
      owner.sublist.items.push({ children: parseInline(item[3]!.trim()) });
      continue;
    }
    if (owner && /^\s/.test(line) && !HEADING.test(line)) {
      // A continuation line of the item before.
      const target = owner.sublist?.items.at(-1) ?? owner;
      target.children.push({ kind: 'text', text: ' ' }, ...parseInline(line.trim()));
      continue;
    }
    break;
  }
  // An item that was only an image (or raw HTML) is no bullet at all.
  list.items = list.items.filter((item) => item.children.length > 0 || item.sublist);
  if (list.items.length > 0) blocks.push(list);
  return i - 1;
}

/** Bold, italics, inline code, links and bare https://github.com addresses; the rest is text. */
export function parseInline(source: string): Inline[] {
  const out: Inline[] = [];
  let text = '';
  const push = (node: Inline) => {
    if (text) out.push({ kind: 'text', text: decodeEntities(text) });
    text = '';
    out.push(node);
  };

  let i = 0;
  while (i < source.length) {
    const rest = source.slice(i);
    const char = source[i]!;

    if (char === '\\' && i + 1 < source.length && /[\\`*_{}[\]()#+\-.!<>|~]/.test(source[i + 1]!)) {
      text += source[i + 1];
      i += 2;
      continue;
    }

    if (char === '`') {
      const ticks = /^`+/.exec(rest)![0];
      const close = source.indexOf(ticks, i + ticks.length);
      if (close > 0) {
        push({ kind: 'code', text: source.slice(i + ticks.length, close).trim() });
        i = close + ticks.length;
        continue;
      }
    }

    // ![alt](src): never shown, not even as a link.
    const image = /^!\[[^\]]*\]\([^)]*\)/.exec(rest);
    if (image) {
      i += image[0].length;
      continue;
    }

    const link = /^\[((?:[^\]\\]|\\.)*)\]\(\s*<?([^\s)>]*)>?(?:\s+(?:"[^"]*"|'[^']*'))?\s*\)/.exec(rest);
    if (link) {
      const href = safeHref(link[2]!);
      const label = parseInline(link[1]!).filter((n) => n.kind !== 'link');
      if (href) push({ kind: 'link', href, children: label.length ? label : [{ kind: 'text', text: href }] });
      else {
        if (text) out.push({ kind: 'text', text: decodeEntities(text) });
        text = '';
        out.push(...label);
      }
      i += link[0].length;
      continue;
    }

    if ((char === 'h' || char === 'H') && (i === 0 || !/[\w/]/.test(source[i - 1]!))) {
      const bare = /^https?:\/\/[^\s<>()[\]"']+/i.exec(rest);
      if (bare) {
        const url = bare[0].replace(/[.,;:!?]+$/, '');
        const href = safeHref(url);
        if (href) push({ kind: 'link', href, children: [{ kind: 'text', text: url }] });
        else text += url;
        i += url.length;
        continue;
      }
    }

    const strong = /^(\*\*|__)(?=\S)([\s\S]*?\S)\1/.exec(rest);
    if (strong && (strong[1] === '**' || i === 0 || !/\w/.test(source[i - 1]!))) {
      push({ kind: 'strong', children: parseInline(strong[2]!) });
      i += strong[0].length;
      continue;
    }

    const em = /^(\*|_)(?=[^\s*_])([\s\S]*?[^\s\\])\1(?!\1)/.exec(rest);
    if (em && (em[1] === '*' || ((i === 0 || !/\w/.test(source[i - 1]!)) && !/\w/.test(source[i + em[0].length] ?? '')))) {
      push({ kind: 'em', children: parseInline(em[2]!) });
      i += em[0].length;
      continue;
    }

    text += char;
    i++;
  }
  if (text) out.push({ kind: 'text', text: decodeEntities(text) });
  return out;
}

/** The plain words of some inline nodes (a heading's text for its id, tests). */
export function inlineText(nodes: Inline[]): string {
  return nodes.map((n) => (n.kind === 'text' || n.kind === 'code' ? n.text : inlineText(n.children))).join('');
}
