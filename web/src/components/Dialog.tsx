import { DismissRegular } from '@fluentui/react-icons';
import { Dialog as RadixDialog } from 'radix-ui';
import type { ReactNode } from 'react';
import { useI18n } from '../i18n';
import { Button } from './Button';
import { cx } from './cx';
import styles from './Dialog.module.css';

export interface DialogProps {
  open: boolean;
  onOpenChange(open: boolean): void;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  footerStart?: ReactNode;
  wide?: boolean;
  /** Set for dialogs that must be answered (a name conflict). */
  dismissible?: boolean;
  /** Extra class for the dialog box (a size of its own, say). */
  className?: string;
}

export function Dialog({ open, onOpenChange, title, description, children, footer, footerStart, wide, dismissible = true, className }: DialogProps) {
  const { t } = useI18n();
  return (
    <RadixDialog.Root open={open} onOpenChange={(v) => (dismissible || v ? onOpenChange(v) : undefined)}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className={styles.overlay} />
        <RadixDialog.Content
          className={cx(styles.content, wide && styles.wide, className)}
          onEscapeKeyDown={(e) => (dismissible ? undefined : e.preventDefault())}
          onPointerDownOutside={(e) => (dismissible ? undefined : e.preventDefault())}
        >
          <div className={styles.header}>
            <RadixDialog.Title className={styles.title}>{title}</RadixDialog.Title>
            {dismissible ? (
              <RadixDialog.Close asChild>
                <Button variant="subtle" icon={<DismissRegular />} aria-label={t('common.close')} />
              </RadixDialog.Close>
            ) : null}
          </div>
          {description ? (
            <RadixDialog.Description className={styles.description}>{description}</RadixDialog.Description>
          ) : (
            <RadixDialog.Description className="visually-hidden">{typeof title === 'string' ? title : ''}</RadixDialog.Description>
          )}
          {children ? <div className={styles.body}>{children}</div> : null}
          {footer ? (
            <div className={styles.footer}>
              {footerStart ? <div className={styles.footerStart}>{footerStart}</div> : null}
              {footer}
            </div>
          ) : null}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

/** A yes/no question; `danger` makes the confirm button red (delete, uninstall). */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  body,
  confirmLabel,
  onConfirm,
  danger,
  busy,
}: {
  open: boolean;
  onOpenChange(open: boolean): void;
  title: ReactNode;
  body?: ReactNode;
  confirmLabel: ReactNode;
  onConfirm(): void;
  danger?: boolean;
  busy?: boolean;
}) {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={title}
      description={body}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant={danger ? 'dangerSolid' : 'accent'} onClick={onConfirm} loading={busy} autoFocus>
            {confirmLabel}
          </Button>
        </>
      }
    />
  );
}
