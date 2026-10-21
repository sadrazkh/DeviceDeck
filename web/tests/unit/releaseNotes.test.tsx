// Updates you can see (round 4): the release notes' language parts, the safe markdown subset, and what reaches the page.

/// <reference types="node" />
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { HOSTILE_NOTES, ownerNotes } from '../../src/bridge/mock/updates';
import { inlineText, notesForLanguage, parseInline, parseNotes, safeHref, type Block } from '../../src/shell/releaseNotes';
import { ReleaseNotesView } from '../../src/shell/ReleaseNotesView';

afterEach(cleanup);

// The real v0.3.0 notes (docs/releases, outside the web root), exactly as GitHub serves them: CRLF line ends.
const v030 = readFileSync(resolve(process.cwd(), '../docs/releases/v0.3.0.md'), 'utf8').replace(/\r?\n/g, '\r\n');

const headings = (blocks: Block[]) => blocks.filter((b) => b.kind === 'heading').map((b) => [b.level, inlineText(b.children)]);

describe('language parts', () => {
  it('takes the English part outside <details> and the Persian part inside it', () => {
    const english = notesForLanguage(v030, 'en');
    expect(english.lang).toBe('en');
    expect(english.text.startsWith('# 🚀 DeviceDeck v0.3.0')).toBe(true);
    expect(english.text).not.toMatch(/details|summary|فارسی/);

    const persian = notesForLanguage(v030, 'fa');
    expect(persian.lang).toBe('fa');
    expect(persian.text.startsWith('# 🚀 دیوایس‌دک v0.3.0')).toBe(true);
    expect(persian.text).not.toMatch(/<\/?(details|summary)|🇮🇷/);
    expect(persian.text).not.toContain('Straight into iPhone Photos');
  });

  it('falls back to English when there is no Persian part', () => {
    const notes = '# v1\n\n- one\n\n<details><summary>Checksums</summary>abc</details>';
    expect(notesForLanguage(notes, 'fa')).toEqual({ text: notes, lang: 'en' });
  });
});

describe('the safe subset', () => {
  it('reads the owner’s notes: a title, sections, bold one-liners in lists', () => {
    const blocks = parseNotes(notesForLanguage(v030, 'en').text);
    expect(headings(blocks)).toEqual([
      [1, '🚀 DeviceDeck v0.3.0 — photos straight into iPhone Photos, automatic photo backup, and drag and drop that works'],
      [3, '📱 iPhone and Android'],
      [3, '🪟 Windows'],
      [3, '🐞 Fixes'],
      [3, 'ℹ️ Good to know'],
    ]);
    const lists = blocks.filter((b) => b.kind === 'list');
    expect(lists.map((l) => l.items.length)).toEqual([8, 2, 2, 3]);
    const first = lists[0]!.items[0]!.children;
    expect(first[0]).toEqual({ kind: 'text', text: '🖼️ ' });
    expect(first[1]).toEqual({ kind: 'strong', children: [{ kind: 'text', text: 'Straight into iPhone Photos' }] });
    expect(inlineText(first)).toContain('— with the free LocalSend app on the iPhone');
  });

  it('reads the Persian part the same way', () => {
    const blocks = parseNotes(notesForLanguage(v030, 'fa').text);
    expect(headings(blocks).map(([level]) => level)).toEqual([1, 3, 3, 3, 3]);
    expect(blocks.filter((b) => b.kind === 'list').map((l) => l.items.length)).toEqual([8, 2, 2, 3]);
  });

  it('keeps one level of nesting, ordered lists, quotes, code and rules', () => {
    const blocks = parseNotes('1. first\n2. second\n   - inner a\n   - inner b\n\n> quoted\n\n```\n<b>as is</b>\n```\n\n---\n\nUse `vpk pack` *now*');
    expect(blocks.map((b) => b.kind)).toEqual(['list', 'quote', 'code', 'rule', 'paragraph']);
    const list = blocks[0] as Extract<Block, { kind: 'list' }>;
    expect(list.ordered).toBe(true);
    expect(list.items[1]!.sublist!.items.map((i) => inlineText(i.children))).toEqual(['inner a', 'inner b']);
    expect(blocks[2]).toEqual({ kind: 'code', text: '<b>as is</b>' });
    const last = (blocks[4] as Extract<Block, { kind: 'paragraph' }>).children;
    expect(last).toEqual([
      { kind: 'text', text: 'Use ' },
      { kind: 'code', text: 'vpk pack' },
      { kind: 'text', text: ' ' },
      { kind: 'em', children: [{ kind: 'text', text: 'now' }] },
    ]);
  });

  it('keeps links to https://github.com only', () => {
    expect(safeHref('https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0')).toBe('https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0');
    for (const bad of [
      'javascript:alert(1)',
      ' JavaScript:alert(1)',
      'data:text/html,x',
      'http://github.com/x',
      'https://github.com.evil.example/x',
      'https://evil.example/?https://github.com',
      'https://user:pass@github.com/x',
      'https://github.com:8443/x',
      '/relative',
      '//github.com/x',
    ]) {
      expect(safeHref(bad), bad).toBeNull();
    }
    expect(parseInline('[other](https://evil.example/x)')).toEqual([{ kind: 'text', text: 'other' }]);
    expect(parseInline('see https://evil.example/x.')).toEqual([{ kind: 'text', text: 'see https://evil.example/x.' }]);
    expect(parseInline('see https://github.com/a.')).toEqual([
      { kind: 'text', text: 'see ' },
      { kind: 'link', href: 'https://github.com/a', children: [{ kind: 'text', text: 'https://github.com/a' }] },
      { kind: 'text', text: '.' },
    ]);
  });

  it('drops raw HTML, images and scripts; entities stay text', () => {
    const text = JSON.stringify(parseNotes(HOSTILE_NOTES));
    expect(text.replace('<script> written as text', '')).not.toMatch(/<script|<img|<iframe|<style|onerror|onclick|javascript:|data:text|evil\.example\/pixel|tracker|hidden comment|__pwned = 1/);
    expect(text).toContain('real bold');
    expect(text).toContain('<script> written as text');
    // The image-only item is gone, not an empty bullet.
    expect(text).not.toContain('{"children":[]}');
    const links = JSON.stringify(parseNotes(HOSTILE_NOTES)).match(/"href":"[^"]+"/g);
    expect(links).toEqual(['"href":"https://github.com/sadrazkh/DeviceDeck/issues/1"', '"href":"https://github.com/sadrazkh/DeviceDeck/releases"']);
  });

  it('an unclosed tag stays text instead of swallowing the rest', () => {
    expect(inlineText((parseNotes('a <img src=x onerror=alert(1)\n\nnext')[1] as Extract<Block, { kind: 'paragraph' }>).children)).toBe('next');
  });
});

describe('ReleaseNotesView', () => {
  it('renders hostile notes as plain elements: no script, image, frame or style, no handlers, links to GitHub only', () => {
    const { container } = render(<ReleaseNotesView markdown={HOSTILE_NOTES} lang="en" />);
    expect(container.querySelectorAll('script, img, iframe, style, object, embed, svg')).toHaveLength(0);
    for (const element of container.querySelectorAll('*')) {
      expect([...element.attributes].map((a) => a.name).filter((n) => n.startsWith('on'))).toEqual([]);
    }
    const anchors = [...container.querySelectorAll('a')];
    expect(anchors.map((a) => a.getAttribute('href'))).toEqual(['https://github.com/sadrazkh/DeviceDeck/issues/1', 'https://github.com/sadrazkh/DeviceDeck/releases']);
    for (const a of anchors) {
      expect(a.getAttribute('target')).toBe('_blank');
      expect(a.getAttribute('rel')).toBe('noreferrer noopener');
    }
    expect(container.querySelector('strong')?.textContent).toBe('real bold');
    expect(container.textContent).toContain('other site');
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it('shows the Persian part right-to-left in Persian, and English left-to-right otherwise', () => {
    const fa = render(<ReleaseNotesView markdown={ownerNotes('0.4.0')} lang="fa" />);
    const notes = fa.getByTestId('release-notes');
    expect(notes.getAttribute('dir')).toBe('rtl');
    expect(notes.getAttribute('lang')).toBe('fa');
    expect(notes.querySelector('h3')?.textContent).toContain('دیوایس‌دک v0.4.0');
    expect(notes.querySelectorAll('ul > li > ul > li')).toHaveLength(1);
    fa.unmount();

    const en = render(<ReleaseNotesView markdown={ownerNotes('0.4.0')} lang="en" />);
    expect(en.getByTestId('release-notes').getAttribute('dir')).toBe('ltr');
    expect(en.getByTestId('release-notes').querySelectorAll('h4')).toHaveLength(3);
  });
});
