// The title bar's update signal (round 4): "Update ready" (or "v0.4.0 on GitHub" for the zip) while there is one, a quiet
// "Downloading 42%" while it downloads, nothing otherwise. Windows' Do Not Disturb can hide the notification; this can't be.

import { ArrowCircleDownRegular, ArrowSyncCircleRegular } from '@fluentui/react-icons';
import { Ring } from '../components/ui';
import { useI18n } from '../i18n';
import { useUpdateStatus } from './desktop';
import styles from './Update.module.css';
import { offerOf, useWhatsNew } from './updates';

export function UpdateButton() {
  const { t, digits, percent } = useI18n();
  const status = useUpdateStatus().data;
  const show = useWhatsNew((s) => s.show);
  if (!status) return null;

  if (status.state === 'downloading' && status.newVersion) {
    const fraction = Math.min(1, Math.max(0, (status.percent ?? 0) / 100));
    const label = t('update.downloadingLabel', { version: digits(status.newVersion), percent: percent(fraction) });
    return (
      <span className={styles.downloading} role="status" aria-label={label} title={label} data-testid="update-downloading">
        <Ring value={fraction} size={14} stroke={2} />
        <span className="num">{t('update.downloading', { percent: percent(fraction) })}</span>
      </span>
    );
  }

  const offer = offerOf(status);
  if (!offer) return null;
  const version = digits(offer.version);
  const label = offer.kind === 'ready' ? t('update.button.readyLabel', { version }) : t('update.button.availableLabel', { version });
  return (
    <button
      type="button"
      className={`${styles.titleButton} no-drag`}
      data-kind={offer.kind}
      aria-label={label}
      title={label}
      onClick={() => show({ version: offer.version, reason: 'offer' })}
      data-testid="update-button"
    >
      <span className={styles.titleButtonIcon} aria-hidden>
        {offer.kind === 'ready' ? <ArrowSyncCircleRegular /> : <ArrowCircleDownRegular />}
      </span>
      <span className={styles.titleButtonLabel}>{offer.kind === 'ready' ? t('update.button.ready') : t('update.button.available', { version })}</span>
    </button>
  );
}
