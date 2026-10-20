import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';

/**
 * WCAG AA text contrast (axe-core's color-contrast rule) for what the page shows now: one line per element whose
 * text is too faint against what is behind it, with axe's measured ratio and colours. Iframes are left out: they
 * hold documents (the device report's sandboxed preview), which axe can't enter.
 */
export async function contrastProblems(page: Page): Promise<string[]> {
  const results = await new AxeBuilder({ page }).withRules(['color-contrast']).exclude('iframe').analyze();
  return results.violations.flatMap((rule) =>
    rule.nodes.map((node) => `${node.target.join(' ')} "${node.html.replace(/\s+/g, ' ').slice(0, 80)}": ${node.any[0]?.message ?? node.failureSummary}`),
  );
}
