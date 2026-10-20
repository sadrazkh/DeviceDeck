import { CheckmarkCircleFilled, DismissRegular, ErrorCircleFilled, InfoFilled, WarningFilled } from '@fluentui/react-icons';
import { AnimatePresence, motion } from 'framer-motion';
import { useEffect } from 'react';
import { Button } from '../components/Button';
import { useI18n } from '../i18n';
import { useToasts, type Toast } from '../state/toasts';
import { useUi } from '../state/ui';
import styles from './Toaster.module.css';

const ICONS = { info: InfoFilled, success: CheckmarkCircleFilled, warning: WarningFilled, danger: ErrorCircleFilled };

function ToastView({ toast }: { toast: Toast }) {
  const { t } = useI18n();
  const dismiss = useToasts((s) => s.dismiss);
  useEffect(() => {
    if (toast.duration <= 0) return;
    const timer = setTimeout(() => dismiss(toast.id), toast.duration);
    return () => clearTimeout(timer);
  }, [toast.id, toast.duration, dismiss]);
  const Icon = ICONS[toast.tone];
  return (
    <motion.li
      layout
      className={styles.toast}
      data-tone={toast.tone}
      initial={{ opacity: 0, y: -12, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      exit={{ opacity: 0, scale: 0.98, transition: { duration: 0.12 } }}
      transition={{ duration: 0.2, ease: [0.2, 0, 0, 1] }}
      role={toast.tone === 'danger' ? 'alert' : 'status'}
    >
      <span className={styles.icon} aria-hidden>
        <Icon />
      </span>
      <div className={styles.body}>
        <div className={styles.title}>{toast.title}</div>
        {toast.body ? <div className={styles.text}>{toast.body}</div> : null}
        {toast.action ? (
          <div className={styles.actions}>
            <Button
              size="small"
              onClick={() => {
                toast.action!.run();
                dismiss(toast.id);
              }}
            >
              {toast.action.label}
            </Button>
          </div>
        ) : null}
      </div>
      <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={t('common.close')} onClick={() => dismiss(toast.id)} />
    </motion.li>
  );
}

export function Toaster() {
  const toasts = useToasts((s) => s.toasts);
  const drawerOpen = useUi((s) => s.drawerOpen);
  return (
    <ol className={styles.region} aria-live="polite" data-beside-drawer={drawerOpen || undefined}>
      <AnimatePresence initial={false}>
        {toasts.map((toast) => (
          <ToastView key={toast.id} toast={toast} />
        ))}
      </AnimatePresence>
    </ol>
  );
}
