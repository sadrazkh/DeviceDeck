import { STORAGE_CATEGORY_ORDER, type StorageVolume } from '../bridge/types';
import { useI18n } from '../i18n';
import { cx } from './cx';
import styles from './device.module.css';

/** One volume's bar split by category, always in the same order, with a legend in GB. */
export function StorageBar({ volume, showLabel, compact }: { volume: StorageVolume; showLabel?: boolean; compact?: boolean }) {
  const { t, bytes } = useI18n();
  const used = volume.totalBytes - volume.freeBytes;
  const byKey = new Map(volume.categories.map((c) => [c.key, c.bytes]));
  const pct = (b: number) => `${volume.totalBytes > 0 ? (b / volume.totalBytes) * 100 : 0}%`;
  return (
    <div className={styles.storage}>
      <div className={styles.storageHead}>
        {showLabel ? <span className={styles.storageLabel}>{volume.label}</span> : <span />}
        <span className={cx(styles.storageUsed, 'num')}>
          {t('overview.storageUsed', { used: '\u0000', total: bytes(volume.totalBytes, 0) })
            .split('\u0000')
            .flatMap((part, i) => (i === 0 ? [part] : [<strong key={i}>{bytes(used)}</strong>, part]))}
        </span>
      </div>
      <div
        className={cx(styles.bar, compact && styles.barCompact)}
        role="img"
        aria-label={STORAGE_CATEGORY_ORDER.map((k) => `${t(`storage.${k}`)} ${bytes(byKey.get(k) ?? 0)}`).join(', ')}
      >
        {STORAGE_CATEGORY_ORDER.map((key) => {
          const b = byKey.get(key) ?? 0;
          return b > 0 ? <div key={key} className={cx(styles.segment, styles[key])} style={{ width: pct(b) }} /> : null;
        })}
      </div>
      {!compact && (
        <ul className={styles.legend}>
          {STORAGE_CATEGORY_ORDER.map((key) => (
            <li key={key} className={styles.legendItem}>
              <span className={cx(styles.swatch, styles[key])} aria-hidden />
              <span className={styles.legendName}>{t(`storage.${key}`)}</span>
              <span className={styles.legendValue}>{bytes(byKey.get(key) ?? 0)}</span>
            </li>
          ))}
          <li className={styles.legendItem}>
            <span className={cx(styles.swatch, styles.free)} aria-hidden />
            <span className={styles.legendName}>{t('storage.free')}</span>
            <span className={styles.legendValue}>{bytes(volume.freeBytes)}</span>
          </li>
        </ul>
      )}
    </div>
  );
}
