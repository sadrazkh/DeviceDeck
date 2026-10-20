import type { Page } from '@playwright/test';

/**
 * Text that doesn't fit, as the page lays it out now, inside `scope`:
 * - clipped: cut off by overflow without an ellipsis;
 * - untitled: cut with an ellipsis but no tooltip (title on it or an ancestor) to read the rest;
 * - outside: visible text that pokes out of its card;
 * - wrapped: a label or value (dt, dd, th, button, tab, badge) that broke onto a second line;
 * - overlap: two pieces of text, or two buttons, drawn over each other;
 * - overflow: a button whose label and icons don't fit in it.
 */
export async function layoutProblems(page: Page, scope = '[data-testid=content]'): Promise<string[]> {
  return page.evaluate((scope) => {
    // CSS-module class names keep their local name in the dev server (_card_1a2b3), which the tests run against.
    const CARD = 'section[class*=_card_]';
    const BADGE = '[class*=_badge_]';
    const RING = '[class*=_ringWrap_]';
    const ONE_LINE = `dt, dd, th, button, [role=tab], ${BADGE}`;

    const root = document.querySelector(scope);
    if (!root) return [`no ${scope}`];
    const problems: string[] = [];
    const describe = (el: Element) => {
      const text = (el.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 40);
      const cls = typeof el.className === 'string' ? el.className.split(' ')[0] : '';
      return `${el.tagName.toLowerCase()}${cls ? '.' + cls : ''} "${text}"`;
    };
    const shown = (el: Element) => {
      const style = getComputedStyle(el);
      return style.visibility !== 'hidden' && style.display !== 'none' && el.getBoundingClientRect().width > 0;
    };
    const clips = (el: Element) => getComputedStyle(el).overflowX !== 'visible';
    // Masked values (•••) are cut on purpose: there is nothing to read until they're revealed.
    const masked = (el: Element) => /^[\s•:-]+$/.test(el.textContent ?? '');
    // Labels laid out in parts on purpose (a tile's title over its description) may take several lines.
    const inParts = (el: Element) => Array.from(el.querySelectorAll('*')).some((c) => ['block', 'flex', 'grid'].includes(getComputedStyle(c).display));

    /** The part of a text box left after the boxes that clip it, up to `stop`. */
    const visibleRect = (el: Element, rect: DOMRect, stop: Element) => {
      let { left, top, right, bottom } = rect;
      for (let box: Element | null = el; box && box !== stop; box = box.parentElement) {
        if (!clips(box)) continue;
        const b = box.getBoundingClientRect();
        left = Math.max(left, b.left);
        top = Math.max(top, b.top);
        right = Math.min(right, b.right);
        bottom = Math.min(bottom, b.bottom);
      }
      return { left, top, right, bottom };
    };

    const textRects: { el: Element; rect: { left: number; top: number; right: number; bottom: number } }[] = [];
    const owners = new Set<Element>();
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const el = node.parentElement;
      if (!node.textContent?.trim() || !el || !shown(el)) continue;
      if (el.closest('[aria-hidden=true], [role=tooltip], [role=dialog], svg')) continue;
      owners.add(el);
      const range = document.createRange();
      range.selectNodeContents(node);
      for (const rect of Array.from(range.getClientRects())) {
        const seen = visibleRect(el, rect, root);
        if (seen.right - seen.left > 1 && seen.bottom - seen.top > 1) textRects.push({ el, rect: seen });
      }
    }

    const lines = (el: Element) => {
      const rects: DOMRect[] = [];
      const inner = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      for (let node = inner.nextNode(); node; node = inner.nextNode()) {
        if (!node.textContent?.trim()) continue;
        const range = document.createRange();
        range.selectNodeContents(node);
        rects.push(...Array.from(range.getClientRects()).filter((r) => r.width > 1));
      }
      // Two boxes on different lines don't overlap vertically.
      return rects.some((a) => rects.some((b) => b.top >= a.bottom - 2));
    };

    for (const el of owners) {
      for (let box: Element | null = el; box && box !== root; box = box.parentElement) {
        if (!clips(box)) continue;
        if (box.scrollWidth > box.clientWidth + 1) {
          const style = getComputedStyle(box);
          if (style.textOverflow === 'ellipsis') {
            if (!masked(box) && !box.closest('[title]') && !box.querySelector('[title]')) problems.push(`untitled: ${describe(box)}`);
          } else if (style.overflowX !== 'auto' && style.overflowX !== 'scroll') {
            problems.push(`clipped: ${describe(box)} (${box.scrollWidth} > ${box.clientWidth})`);
          }
        }
        break;
      }
      const label = el.closest(ONE_LINE);
      if (label && root.contains(label) && !inParts(label) && lines(label)) problems.push(`wrapped: ${describe(label)}`);
    }

    for (const { el, rect } of textRects) {
      const card = el.closest(CARD);
      if (!card) continue;
      const box = card.getBoundingClientRect();
      if (rect.left < box.left - 1 || rect.right > box.right + 1 || rect.top < box.top - 1 || rect.bottom > box.bottom + 1) {
        problems.push(`outside: ${describe(el)} leaves its card`);
      }
    }

    for (let i = 0; i < textRects.length; i++) {
      for (let j = i + 1; j < textRects.length; j++) {
        const a = textRects[i];
        const b = textRects[j];
        if (a.el === b.el || a.el.contains(b.el) || b.el.contains(a.el)) continue;
        const x = Math.min(a.rect.right, b.rect.right) - Math.max(a.rect.left, b.rect.left);
        const y = Math.min(a.rect.bottom, b.rect.bottom) - Math.max(a.rect.top, b.rect.top);
        if (x > 2 && y > 2) problems.push(`overlap: ${describe(a.el)} / ${describe(b.el)}`);
      }
    }

    // Buttons keep their label and icons inside, don't cover each other and stay in their card.
    const buttons = Array.from(root.querySelectorAll('button')).filter(shown);
    for (const button of buttons) {
      if (button.scrollWidth > button.clientWidth + 1) problems.push(`overflow: ${describe(button)} is wider than the button`);
      const card = button.closest(CARD);
      if (card) {
        const a = button.getBoundingClientRect();
        const b = card.getBoundingClientRect();
        if (a.left < b.left - 1 || a.right > b.right + 1) problems.push(`outside: ${describe(button)} leaves its card`);
      }
    }
    for (let i = 0; i < buttons.length; i++) {
      for (let j = i + 1; j < buttons.length; j++) {
        const a = buttons[i].getBoundingClientRect();
        const b = buttons[j].getBoundingClientRect();
        const x = Math.min(a.right, b.right) - Math.max(a.left, b.left);
        const y = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        if (x > 1 && y > 1 && !buttons[i].contains(buttons[j]) && !buttons[j].contains(buttons[i])) {
          problems.push(`overlap: ${describe(buttons[i])} / ${describe(buttons[j])}`);
        }
      }
    }

    // Text inside a ring stays inside its hole (the stroke takes the outer ~10%).
    for (const ring of Array.from(root.querySelectorAll(RING))) {
      const hole = ring.getBoundingClientRect();
      const inset = hole.width * 0.12;
      for (const { el, rect } of textRects) {
        if (!ring.contains(el)) continue;
        if (rect.left < hole.left + inset || rect.right > hole.right - inset) problems.push(`outside: ${describe(el)} is wider than its ring`);
      }
    }
    return [...new Set(problems)];
  }, scope);
}
