// Where the browser is: App documents › VLC › Subtitles. When the names don't fit, the levels in the middle fold
// into a "…" menu (the ones nearest the top first), then the first one too; the current folder stays, and is cut
// with an ellipsis only when even it alone doesn't fit. Every name that is cut has its full name as a tooltip.

import { ChevronRightRegular, FolderRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { useRef, type ReactNode } from 'react';
import { Menu } from '../../components/Menu';
import { useI18n } from '../../i18n';
import { useFitLevel } from './fit';
import { TipButton } from './TipButton';
import styles from './Files.module.css';

export interface Crumb {
  key: string;
  label: string;
  icon?: ReactNode;
  onSelect(): void;
  testId?: string;
}

/** The crumbs shown and the ones folded into the menu, for `fold` levels folded (0 … items.length - 1). */
export function foldCrumbs<T>(items: readonly T[], fold: number): { before: T[]; hidden: T[]; after: T[] } {
  const n = items.length;
  if (fold <= 0 || n < 2) return { before: [], hidden: [], after: [...items] };
  if (fold >= n - 1) return { before: [], hidden: items.slice(0, n - 1), after: items.slice(n - 1) };
  return { before: items.slice(0, 1), hidden: items.slice(1, 1 + fold), after: items.slice(1 + fold) };
}

export function Breadcrumbs({ items, label }: { items: Crumb[]; label: string }) {
  const { t } = useI18n();
  const list = useRef<HTMLOListElement>(null);
  const n = items.length;
  const fold = useFitLevel(
    list,
    Math.max(0, n - 1),
    () => list.current!.scrollWidth <= list.current!.clientWidth + 1,
    items.map((i) => `${i.key}\u0001${i.label}`).join('\u0002'),
  );
  const { before, hidden, after } = foldCrumbs(items, fold);
  const current = items[n - 1];

  const crumb = (item: Crumb, first: boolean) => {
    const isCurrent = item === current;
    return (
      <li key={item.key} className={styles.crumb} data-current={isCurrent || undefined}>
        {first ? null : <ChevronRightRegular className="flip-rtl" aria-hidden />}
        <button
          type="button"
          className={styles.crumbButton}
          title={item.label}
          aria-current={isCurrent ? 'location' : undefined}
          onClick={item.onSelect}
          data-testid={isCurrent ? 'crumb-current' : item.testId}
        >
          {item.icon ? (
            <span className={styles.crumbIcon} aria-hidden>
              {item.icon}
            </span>
          ) : null}
          <span className={styles.crumbLabel} dir="auto">
            {item.label}
          </span>
        </button>
      </li>
    );
  };

  return (
    // All folded but the current one, which may then shrink: data-squeezed.
    <ol ref={list} className={styles.crumbs} aria-label={label} data-squeezed={(n > 1 && fold >= n - 1) || undefined}>
      {before.map((item) => crumb(item, true))}
      {hidden.length > 0 ? (
        <li className={styles.crumb}>
          {before.length > 0 ? <ChevronRightRegular className="flip-rtl" aria-hidden /> : null}
          <Menu
            trigger={
              <TipButton
                variant="subtle"
                size="small"
                className={styles.crumbMore}
                icon={<MoreHorizontalRegular />}
                tip={t('files.crumbsHidden', { count: hidden.length })}
                aria-label={t('files.crumbsHidden', { count: hidden.length })}
                data-testid="crumb-more"
              />
            }
            items={hidden.map((item) => ({
              key: item.key,
              label: (
                <span className={styles.crumbMenuLabel} title={item.label} dir="auto">
                  {item.label}
                </span>
              ),
              icon: item.icon ?? <FolderRegular />,
              onSelect: item.onSelect,
            }))}
          />
        </li>
      ) : null}
      {after.map((item, i) => crumb(item, i === 0 && before.length === 0 && hidden.length === 0))}
    </ol>
  );
}
