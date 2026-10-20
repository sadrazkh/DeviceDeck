import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import styles from './Button.module.css';
import { cx } from './cx';

export type ButtonVariant = 'standard' | 'accent' | 'subtle' | 'danger' | 'dangerSolid' | 'link';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: 'small' | 'medium' | 'large';
  icon?: ReactNode;
  /** Icon after the label (chevrons). */
  iconAfter?: ReactNode;
  loading?: boolean;
  /** Icon-only buttons must still say what they do. */
  'aria-label'?: string;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'standard', size = 'medium', icon, iconAfter, loading, children, className, type = 'button', disabled, ...rest },
  ref,
) {
  const iconOnly = !children && Boolean(icon || loading);
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cx(
        styles.button,
        styles[variant],
        size === 'large' && styles.large,
        size === 'small' && styles.small,
        iconOnly && styles.iconOnly,
        'no-drag',
        className,
      )}
      {...rest}
    >
      {loading ? <span className={styles.spinner} aria-hidden /> : icon ? <span className={styles.icon} aria-hidden>{icon}</span> : null}
      {children}
      {iconAfter ? <span className={styles.icon} aria-hidden>{iconAfter}</span> : null}
    </button>
  );
});
