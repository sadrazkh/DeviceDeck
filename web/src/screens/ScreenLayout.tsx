import type { ReactNode } from 'react';
import { cx } from '../components/cx';
import styles from './Screen.module.css';

/** The common frame of a screen: Display title, one-line subtitle, actions, then the body. */
export function ScreenLayout({
  title,
  subtitle,
  actions,
  banners,
  children,
  fill,
  testId,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  banners?: ReactNode;
  children: ReactNode;
  /** The body manages its own scrolling (virtual lists). */
  fill?: boolean;
  testId?: string;
}) {
  return (
    <div className={styles.screen} data-testid={testId}>
      <div className={styles.header}>
        <div className={styles.heading}>
          <h1 className="t-display">{title}</h1>
          {subtitle ? <p className={styles.subtitle}>{subtitle}</p> : null}
        </div>
        {actions ? <div className={styles.actions}>{actions}</div> : null}
      </div>
      {banners ? <div className={styles.banners}>{banners}</div> : null}
      <div className={cx(fill ? styles.bodyFill : styles.body)}>{children}</div>
    </div>
  );
}
