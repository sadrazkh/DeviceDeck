// What happened to each photo of a delete (round 4, photodelete): Photos and Clean-up show it after the run.

import { CheckmarkCircleRegular, DismissCircleRegular, SubtractCircleRegular, WarningRegular } from '@fluentui/react-icons';
import type { PhotoDeleteDestination, PhotoDeleteOutcome, PhotoDeleteResult } from '../../bridge/photoDeleteTypes';
import { cx } from '../../components/cx';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './PhotoDelete.module.css';

const OUTCOME: Record<PhotoDeleteOutcome, MessageKey> = {
  deleted: 'photoDelete.outcome.deleted',
  mismatch: 'photoDelete.outcome.mismatch',
  failed: 'photoDelete.outcome.failed',
  notTried: 'photoDelete.outcome.notTried',
};

const WENT: Record<PhotoDeleteDestination, MessageKey> = {
  permanent: 'photoDelete.result.went.permanent',
  recentlyDeleted: 'photoDelete.result.went.recentlyDeleted',
  unknown: 'photoDelete.result.went.unknown',
};

const ICON = {
  deleted: <CheckmarkCircleRegular />,
  mismatch: <WarningRegular />,
  failed: <DismissCircleRegular />,
  notTried: <SubtractCircleRegular />,
};

/** Why an item wasn't deleted, in the page's language (the host's sentence stays below it as a detail). */
function reasonKey(r: PhotoDeleteResult): MessageKey | null {
  switch (r.outcome) {
    case 'mismatch':
      return 'photoDelete.reason.mismatch';
    case 'notTried':
      return 'photoDelete.reason.notTried';
    case 'failed':
      return r.code === 'permission_denied' ? 'photoDelete.reason.refused' : r.code === 'device_disconnected' ? 'photoDelete.reason.disconnected' : 'photoDelete.reason.failed';
    default:
      return null;
  }
}

export function PhotoDeleteResults({
  items,
  deleted,
  freedBytes,
  goesTo,
  copyIncomplete,
}: {
  items: PhotoDeleteResult[];
  deleted: number;
  freedBytes: number;
  goesTo: PhotoDeleteDestination;
  copyIncomplete: boolean;
}) {
  const { t, bytes } = useI18n();
  // Problems first, so a long list still shows what needs a look.
  const order: PhotoDeleteOutcome[] = ['failed', 'mismatch', 'notTried', 'deleted'];
  const sorted = [...items].sort((a, b) => order.indexOf(a.outcome) - order.indexOf(b.outcome));
  return (
    <div className={styles.body} data-testid="photo-delete-results">
      <p className={styles.summary} data-testid="photo-delete-summary">
        {deleted > 0 ? t('photoDelete.result.summary', { count: deleted, size: bytes(freedBytes) }) : t('photoDelete.result.none')}
      </p>
      {copyIncomplete ? <p className={styles.warn}>{t('photoDelete.result.copyIncomplete')}</p> : null}
      {deleted > 0 ? <p className={styles.hint} data-testid="photo-delete-went">{t(WENT[goesTo])}</p> : null}
      <ul className={styles.list} aria-label={t('photoDelete.result.title')}>
        {sorted.map((r) => {
          const why = reasonKey(r);
          return (
            <li key={r.id} className={styles.row} data-testid="photo-delete-result" data-outcome={r.outcome}>
              <span className={cx(styles.icon, styles[r.outcome])} aria-hidden>
                {ICON[r.outcome]}
              </span>
              <span className={styles.text}>
                <span className={styles.name}>
                  {r.fileName} · <span className={styles[r.outcome]}>{t(OUTCOME[r.outcome])}</span>
                </span>
                {r.also.length > 0 ? <span className={styles.detail}>{t('photoDelete.also', { names: r.also.join(', ') })}</span> : null}
                {why ? <span className={styles.detail}>{t(why)}</span> : null}
                {r.message ? (
                  <span className={styles.detail} dir="auto">
                    {r.message}
                  </span>
                ) : null}
              </span>
              <span className={styles.size}>{bytes(r.size)}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
