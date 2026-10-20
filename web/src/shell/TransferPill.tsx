import { useTransfers } from '../app/queries';
import { Ring } from '../components/ui';
import { useI18n } from '../i18n';
import { overallProgress } from '../state/transfers';
import { useUi } from '../state/ui';
import styles from './Shell.module.css';

/** Opens the transfer drawer; while it is closed it shows overall progress. */
export function TransferPill() {
  const { t, percent } = useI18n();
  const jobs = useTransfers().data ?? [];
  const { active, fraction, failed } = overallProgress(jobs);
  const open = useUi((s) => s.drawerOpen);
  const setOpen = useUi((s) => s.setDrawerOpen);
  return (
    <button
      type="button"
      className={`${styles.pill} no-drag`}
      data-active={active > 0 || undefined}
      aria-expanded={open}
      aria-controls="transfer-drawer"
      onClick={() => setOpen(!open)}
      data-testid="transfer-pill"
    >
      {active > 0 ? <Ring value={fraction} size={18} stroke={3} /> : null}
      <span className={styles.pillLabel}>{t('shell.transfers')}</span>
      {active > 0 && fraction !== null ? <span className="num">{percent(fraction)}</span> : null}
      {failed > 0 && active === 0 ? <span className={styles.pillAlert} aria-label={t('transfers.group.failed')} /> : null}
    </button>
  );
}
