import { ContextMenu as RadixContextMenu, DropdownMenu } from 'radix-ui';
import type { ReactNode } from 'react';
import { cx } from './cx';
import styles from './Menu.module.css';

export interface MenuItem {
  key: string;
  label: ReactNode;
  icon?: ReactNode;
  hint?: ReactNode;
  danger?: boolean;
  disabled?: boolean;
  onSelect(): void;
}

export type MenuEntry = MenuItem | 'separator' | { label: ReactNode; key: string; heading: true };

function isItem(entry: MenuEntry): entry is MenuItem {
  return typeof entry === 'object' && 'onSelect' in entry;
}

/** A dropdown menu on a trigger (the device switcher, Restart ⌄). */
export function Menu({ trigger, items, align = 'start' }: { trigger: ReactNode; items: MenuEntry[]; align?: 'start' | 'end' }) {
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>{trigger}</DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content className={styles.menu} align={align} sideOffset={6}>
          {items.map((entry, i) =>
            entry === 'separator' ? (
              <DropdownMenu.Separator key={`sep-${i}`} className={styles.separator} />
            ) : isItem(entry) ? (
              <DropdownMenu.Item
                key={entry.key}
                className={cx(styles.item, entry.danger && styles.itemDanger)}
                disabled={entry.disabled}
                onSelect={entry.onSelect}
              >
                {entry.icon ? <span className={styles.itemIcon}>{entry.icon}</span> : null}
                <span className={styles.itemLabel}>{entry.label}</span>
                {entry.hint ? <span className={styles.itemHint}>{entry.hint}</span> : null}
              </DropdownMenu.Item>
            ) : (
              <DropdownMenu.Label key={entry.key} className={styles.label}>
                {entry.label}
              </DropdownMenu.Label>
            ),
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/** A right-click menu around a row or tile. */
export function ContextMenu({ children, items, onOpenChange }: { children: ReactNode; items: MenuEntry[]; onOpenChange?(open: boolean): void }) {
  return (
    <RadixContextMenu.Root onOpenChange={onOpenChange}>
      <RadixContextMenu.Trigger asChild>{children}</RadixContextMenu.Trigger>
      <RadixContextMenu.Portal>
        <RadixContextMenu.Content className={styles.menu}>
          {items.map((entry, i) =>
            entry === 'separator' ? (
              <RadixContextMenu.Separator key={`sep-${i}`} className={styles.separator} />
            ) : isItem(entry) ? (
              <RadixContextMenu.Item
                key={entry.key}
                className={cx(styles.item, entry.danger && styles.itemDanger)}
                disabled={entry.disabled}
                onSelect={entry.onSelect}
              >
                {entry.icon ? <span className={styles.itemIcon}>{entry.icon}</span> : null}
                <span className={styles.itemLabel}>{entry.label}</span>
                {entry.hint ? <span className={styles.itemHint}>{entry.hint}</span> : null}
              </RadixContextMenu.Item>
            ) : (
              <RadixContextMenu.Label key={entry.key} className={styles.label}>
                {entry.label}
              </RadixContextMenu.Label>
            ),
          )}
        </RadixContextMenu.Content>
      </RadixContextMenu.Portal>
    </RadixContextMenu.Root>
  );
}
