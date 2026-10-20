// Small building blocks: card, badge, progress, ring, skeleton, empty state, banner,
// segmented control, switch, checkbox, tooltip, radio cards and a text field.

import { CheckmarkRegular, ErrorCircleRegular, InfoRegular, WarningRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { Checkbox as RadixCheckbox, RadioGroup, Switch as RadixSwitch, ToggleGroup, Tooltip as RadixTooltip } from 'radix-ui';
import { useId, useState, type HTMLAttributes, type ReactNode } from 'react';
import { useI18n } from '../i18n';
import { cx } from './cx';
import styles from './ui.module.css';

export function Card({
  title,
  actions,
  children,
  className,
  ...rest
}: { title?: ReactNode; actions?: ReactNode } & HTMLAttributes<HTMLElement>) {
  const headingId = useId();
  return (
    <section className={cx(styles.card, className)} aria-labelledby={title ? headingId : undefined} {...rest}>
      {(title || actions) && (
        <div className={styles.cardHeader}>
          {title ? (
            <h2 id={headingId} className={styles.cardTitle}>
              {title}
            </h2>
          ) : (
            <span />
          )}
          {actions ? <div className={styles.cardActions}>{actions}</div> : null}
        </div>
      )}
      {children}
    </section>
  );
}

export type Tone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info';

export function Badge({ tone = 'neutral', icon, children, className, ...rest }: { tone?: Tone; icon?: ReactNode } & HTMLAttributes<HTMLSpanElement>) {
  return (
    <span className={cx(styles.badge, tone !== 'neutral' && styles[`badge-${tone}`], className)} {...rest}>
      {icon}
      {children}
    </span>
  );
}

export function StatusDot({ tone = 'neutral', label }: { tone?: Tone; label?: string }) {
  return <span className={cx(styles.dot, tone !== 'neutral' && styles[`dot-${tone}`])} role={label ? 'img' : undefined} aria-label={label} aria-hidden={label ? undefined : true} />;
}

export function LinearProgress({
  value,
  tone = 'accent',
  large,
  label,
  className,
}: {
  /** 0..1, or null for indeterminate. */
  value: number | null;
  tone?: 'accent' | 'success' | 'danger' | 'paused';
  large?: boolean;
  label?: string;
  className?: string;
}) {
  const pct = value === null ? null : Math.round(Math.max(0, Math.min(1, value)) * 100);
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={pct ?? undefined}
      className={cx(styles.progress, large && styles.progressLarge, tone !== 'accent' && styles[`progress-${tone}`], pct === null && styles.progressIndeterminate, className)}
    >
      <div className={styles.progressFill} style={pct === null ? undefined : { width: `${pct}%` }} />
    </div>
  );
}

export function Ring({
  value,
  size = 112,
  stroke = 10,
  tone = 'accent',
  children,
  label,
}: {
  value: number | null;
  size?: number;
  stroke?: number;
  tone?: 'accent' | 'success' | 'warning' | 'danger';
  children?: ReactNode;
  label?: string;
}) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const fraction = value === null ? 0.25 : Math.max(0, Math.min(1, value));
  return (
    <div className={styles.ringWrap} style={{ width: size, height: size }} role={label ? 'img' : undefined} aria-label={label}>
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        className={cx(styles.ring, tone !== 'accent' && styles[`ring-${tone}`], value === null && styles.ringIndeterminate)}
        aria-hidden
      >
        <circle className={styles.ringTrack} cx={size / 2} cy={size / 2} r={r} fill="none" strokeWidth={stroke} />
        <circle
          className={styles.ringFill}
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={c * (1 - fraction)}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
      </svg>
      {children ? (
        <div className={styles.ringCenter}>{children}</div>
      ) : null}
    </div>
  );
}

export function Spinner({ large, label }: { large?: boolean; label?: string }) {
  return <span className={cx(styles.spinner, large && styles.spinnerLarge)} role="status" aria-label={label} />;
}

export function Skeleton({ width, height = 16, radius, className }: { width?: number | string; height?: number | string; radius?: string; className?: string }) {
  return <span className={cx(styles.skeleton, className)} aria-hidden style={{ width: width ?? '100%', height, borderRadius: radius }} />;
}

export function EmptyState({ icon, title, body, action }: { icon?: ReactNode; title: ReactNode; body?: ReactNode; action?: ReactNode }) {
  return (
    <div className={styles.empty}>
      {icon ? (
        <div className={styles.emptyArt} aria-hidden>
          {icon}
        </div>
      ) : null}
      <div className={styles.emptyTitle}>{title}</div>
      {body ? <div className={styles.emptyBody}>{body}</div> : null}
      {action ? <div className={styles.emptyAction}>{action}</div> : null}
    </div>
  );
}

export function Banner({
  tone = 'info',
  title,
  children,
  actions,
  details,
  icon,
  className,
}: {
  tone?: 'info' | 'warning' | 'danger' | 'success';
  title?: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
  /** Technical text behind a Details expander. */
  details?: string | null;
  icon?: ReactNode;
  className?: string;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const defaultIcon =
    tone === 'warning' ? <WarningRegular /> : tone === 'danger' ? <ErrorCircleRegular /> : tone === 'success' ? <CheckmarkCircleRegular /> : <InfoRegular />;
  return (
    <div className={cx(styles.banner, tone !== 'info' && styles[`banner-${tone}`], className)} role={tone === 'danger' ? 'alert' : 'status'}>
      <span className={styles.bannerIcon} aria-hidden>
        {icon ?? defaultIcon}
      </span>
      <div className={styles.bannerBody}>
        {title ? <div className={styles.bannerTitle}>{title}</div> : null}
        {children ? <div>{children}</div> : null}
        {details ? (
          <>
            <button type="button" className={styles.detailsToggle} aria-expanded={open} onClick={() => setOpen((v) => !v)}>
              {t('common.details')}
            </button>
            {open ? <pre className={styles.detailsText}>{details}</pre> : null}
          </>
        ) : null}
      </div>
      {actions ? <div className={styles.bannerActions}>{actions}</div> : null}
    </div>
  );
}

export function Segmented<T extends string>({
  value,
  onChange,
  options,
  label,
}: {
  value: T;
  onChange(value: NoInfer<T>): void;
  options: { value: NoInfer<T>; label: ReactNode }[];
  label: string;
}) {
  return (
    <ToggleGroup.Root
      type="single"
      value={value}
      onValueChange={(v) => v && onChange(v as T)}
      aria-label={label}
      className={cx(styles.segmented, 'no-drag')}
    >
      {options.map((o) => (
        <ToggleGroup.Item key={o.value} value={o.value} className={styles.segment}>
          {o.label}
        </ToggleGroup.Item>
      ))}
    </ToggleGroup.Root>
  );
}

export function Switch({ checked, onChange, label }: { checked: boolean; onChange(checked: boolean): void; label: ReactNode }) {
  const id = useId();
  return (
    <span className={styles.switchRow}>
      <RadixSwitch.Root id={id} checked={checked} onCheckedChange={onChange} className={styles.switch}>
        <RadixSwitch.Thumb className={styles.switchThumb} />
      </RadixSwitch.Root>
      <label htmlFor={id}>{label}</label>
    </span>
  );
}

export function Checkbox({
  checked,
  onChange,
  label,
  className,
}: {
  checked: boolean | 'indeterminate';
  onChange(checked: boolean): void;
  label: string;
  className?: string;
}) {
  return (
    <RadixCheckbox.Root
      checked={checked}
      onCheckedChange={(v) => onChange(v === true)}
      aria-label={label}
      className={cx(styles.checkbox, className)}
      onClick={(e) => e.stopPropagation()}
    >
      <RadixCheckbox.Indicator>{checked === 'indeterminate' ? <span>–</span> : <CheckmarkRegular />}</RadixCheckbox.Indicator>
    </RadixCheckbox.Root>
  );
}

export function Tooltip({ content, shortcut, children, side = 'bottom' }: { content: ReactNode; shortcut?: string; children: ReactNode; side?: 'top' | 'bottom' | 'left' | 'right' }) {
  return (
    <RadixTooltip.Root delayDuration={400}>
      <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
      <RadixTooltip.Portal>
        <RadixTooltip.Content side={side} sideOffset={6} className={styles.tooltip}>
          {content}
          {shortcut ? <Kbd>{shortcut}</Kbd> : null}
        </RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className={styles.kbd}>{children}</kbd>;
}

export function RadioCards<T extends string>({
  value,
  onChange,
  options,
  label,
}: {
  value: T;
  onChange(value: NoInfer<T>): void;
  options: { value: NoInfer<T>; label: ReactNode; hint?: ReactNode }[];
  label: string;
}) {
  return (
    <RadioGroup.Root value={value} onValueChange={(v) => onChange(v as T)} aria-label={label} className={styles.radioGroup}>
      {options.map((o) => (
        <RadioGroup.Item key={o.value} value={o.value} className={styles.radioCard}>
          <span className={styles.radioDot} aria-hidden />
          <span className={styles.radioText}>
            <span>{o.label}</span>
            {o.hint ? <span className={styles.radioHint}>{o.hint}</span> : null}
          </span>
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}

export function TextField({
  label,
  value,
  onChange,
  error,
  autoFocus,
  onEnter,
}: {
  label: string;
  value: string;
  onChange(value: string): void;
  error?: string | null;
  autoFocus?: boolean;
  onEnter?(): void;
}) {
  const id = useId();
  return (
    <div className={styles.field}>
      <label htmlFor={id} className={styles.fieldLabel}>
        {label}
      </label>
      <input
        id={id}
        className={styles.input}
        value={value}
        autoFocus={autoFocus}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') onEnter?.();
        }}
        aria-invalid={error ? true : undefined}
        spellCheck={false}
      />
      {error ? <div className={styles.fieldError}>{error}</div> : null}
    </div>
  );
}
