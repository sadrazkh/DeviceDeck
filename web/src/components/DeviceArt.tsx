import { useId, type CSSProperties } from 'react';
import type { DevicePlatform } from '../bridge/types';
import styles from './device.module.css';

/**
 * Simple phone silhouettes, tinted with the phone's own colour when known (DeviceSummary.colorHex):
 * an iPhone with the Dynamic Island, an Android phone with a punch-hole camera. No product photos.
 */
export function DeviceArt({
  platform,
  colorHex,
  width = 150,
  dimmed,
  label,
}: {
  platform: DevicePlatform;
  colorHex: string | null;
  width?: number;
  dimmed?: boolean;
  label?: string;
}) {
  const gradient = `screen-${useId().replace(/:/g, '')}`;
  const height = Math.round(width * 2.05);
  const style = { width, height, ...(colorHex ? { '--device-color': colorHex } : {}) } as CSSProperties;
  const ios = platform === 'ios';
  return (
    <svg
      className={styles.art}
      data-dimmed={dimmed || undefined}
      style={style}
      viewBox="0 0 150 308"
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    >
      <defs>
        <linearGradient id={gradient} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" className={styles.wallTop} />
          <stop offset="1" className={styles.wallBottom} />
        </linearGradient>
      </defs>
      {/* frame */}
      <rect x="1" y="1" width="148" height="306" rx={ios ? 30 : 18} className={styles.frame} />
      {/* screen */}
      <rect x="7" y="7" width="136" height="294" rx={ios ? 24 : 13} className={styles.screen} />
      <rect x="7" y="7" width="136" height="294" rx={ios ? 24 : 13} fill={`url(#${gradient})`} className={styles.wallpaper} />
      {ios ? (
        <rect x="52" y="15" width="46" height="14" rx="7" className={styles.island} />
      ) : (
        <circle cx="75" cy="19" r="5" className={styles.island} />
      )}
      {/* side buttons */}
      {ios ? (
        <>
          <rect x="-1.5" y="66" width="2.5" height="18" rx="1" className={styles.button} />
          <rect x="-1.5" y="94" width="2.5" height="30" rx="1" className={styles.button} />
          <rect x="149" y="96" width="2.5" height="42" rx="1" className={styles.button} />
        </>
      ) : (
        <>
          <rect x="149" y="78" width="2.5" height="44" rx="1" className={styles.button} />
          <rect x="149" y="132" width="2.5" height="24" rx="1" className={styles.button} />
        </>
      )}
      {/* glass highlight */}
      <path d="M12 40 Q 20 14 46 12 L 30 12 Q 12 14 12 34 Z" className={styles.highlight} />
    </svg>
  );
}
