import {
  ArchiveRegular,
  ArrowSyncRegular,
  LockClosedRegular,
  LockOpenRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useState, type ReactNode } from 'react';
import { useClient } from '../../app/bridgeContext';
import { isBridgeError } from '../../bridge/errors';
import type { BackupInfo, BackupJob, PrunePlan } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { describeError } from '../../components/ErrorState';
import { Badge, Banner, LinearProgress } from '../../components/ui';
import ui from '../../components/ui.module.css';
import { useI18n, type I18n } from '../../i18n';
import { toast } from '../../state/toasts';
import styles from './Backup.module.css';
import { backupFraction, backupKeys, useBackupUi } from './backupQueries';

/** A password input in the app's field style (TextField has no password mode). */
export function PasswordField({
  label,
  value,
  onChange,
  error,
  autoFocus,
  onEnter,
  testId,
}: {
  label: string;
  value: string;
  onChange(value: string): void;
  error?: string | null;
  autoFocus?: boolean;
  onEnter?(): void;
  testId?: string;
}) {
  const id = useId();
  return (
    <div className={ui.field}>
      <label htmlFor={id} className={ui.fieldLabel}>
        {label}
      </label>
      <input
        id={id}
        type="password"
        className={ui.input}
        value={value}
        autoFocus={autoFocus}
        autoComplete="off"
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') onEnter?.();
        }}
        aria-invalid={error ? true : undefined}
        data-testid={testId}
      />
      {error ? <div className={ui.fieldError}>{error}</div> : null}
    </div>
  );
}

/** What a job is doing, in words. */
export function phaseText(job: BackupJob, { t }: I18n): string {
  const device = job.deviceName;
  switch (job.state) {
    case 'waitingForDevice':
      return t('backup.job.waitingForDevice', { device });
    case 'done':
      return t('backup.job.done');
    case 'failed':
      return job.error?.message ?? t('backup.job.failed');
    case 'cancelled':
      return t('backup.job.cancelled');
    case 'queued':
      return t('backup.job.queued');
    case 'running':
      break;
  }
  switch (job.phase) {
    case 'starting':
      return t('backup.job.phase.starting', { device });
    case 'waitingForPhone':
      return job.kind === 'encryption' ? t('backup.encrypt.waiting', { device }) : t('backup.job.phase.waitingForPhone', { device });
    case 'preparing':
      return job.kind === 'restore' ? t('backup.job.phase.preparingRestore', { device }) : t('backup.job.phase.preparing', { device });
    case 'transferring':
      return job.kind === 'backup'
        ? t('backup.job.phase.transferring', { device })
        : job.kind === 'restore'
          ? t('backup.job.phase.restoring', { device })
          : t('backup.job.phase.copying');
    case 'finishing':
      return t('backup.job.phase.finishing');
    default:
      return t('backup.job.phase.starting', { device });
  }
}

/** Phase, percent, bar and the bytes/files/speed line of one job. */
export function JobProgress({ job }: { job: BackupJob }) {
  const i18n = useI18n();
  const { t, bytes, speed, percent } = i18n;
  const fraction = backupFraction(job);
  const waiting = job.state === 'waitingForDevice';
  const line = [
    job.bytes > 0 ? t('backup.job.copied', { size: bytes(job.bytes) }) : null,
    job.files > 0 ? t('backup.job.files', { count: job.files }) : null,
    job.bytesPerSecond ? speed(job.bytesPerSecond) : null,
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <div className={styles.job} data-testid="backup-job" data-state={job.state} data-phase={job.phase ?? ''}>
      <div className={styles.jobHead}>
        <span className={styles.jobPhase} role="status">
          {phaseText(job, i18n)}
        </span>
        {fraction !== null && job.state !== 'failed' ? <span className={styles.jobPercent}>{percent(fraction)}</span> : null}
      </div>
      {job.state === 'failed' || job.state === 'cancelled' ? null : (
        <LinearProgress value={waiting ? (fraction ?? 0) : fraction} tone={waiting ? 'paused' : job.state === 'done' ? 'success' : 'accent'} large label={job.title} />
      )}
      {line ? <span className={styles.jobLine}>{line}</span> : null}
    </div>
  );
}

/** "12 Oct 2026, 21:14", or a word when the date is unknown. */
export function backupDate(backup: BackupInfo, { t, date }: I18n): string {
  return backup.createdAt ? date(backup.createdAt, 'long') : t('common.unknown');
}

/** One backup in a list: icon, date and badges, then device, model, iOS/Android version, size and app count. */
export function BackupRow({ backup, showDevice, children }: { backup: BackupInfo; showDevice?: boolean; children?: ReactNode }) {
  const i18n = useI18n();
  const { t, bytes, digits } = i18n;
  const icon =
    backup.state === 'inProgress' ? <ArrowSyncRegular /> : backup.state === 'incomplete' ? <WarningRegular /> : backup.locked ? <LockClosedRegular /> : <ArchiveRegular />;
  const tone = backup.state === 'incomplete' ? 'warning' : backup.source === 'apple' ? 'muted' : undefined;
  const os = backup.osVersion ? digits(t(backup.platform === 'ios' ? 'device.os.ios' : 'device.os.android', { version: backup.osVersion })) : null;
  const meta = [showDevice ? backup.deviceName : null, backup.marketingName ?? backup.model, os, bytes(backup.sizeBytes), t('backup.row.apps', { count: backup.apps })]
    .filter(Boolean)
    .join(' · ');
  return (
    <div className={styles.row} data-testid="backup-row" data-backup={backup.id} data-state={backup.state}>
      <span className={styles.rowIcon} data-tone={tone} aria-hidden>
        {icon}
      </span>
      <div className={styles.rowText}>
        <span className={styles.rowTitle}>
          {backupDate(backup, i18n)}
          {backup.state === 'incomplete' ? <Badge tone="warning">{t('backup.row.incomplete')}</Badge> : null}
          {backup.state === 'inProgress' ? <Badge tone="accent">{t('backup.row.inProgress')}</Badge> : null}
          {backup.encrypted ? (
            <Badge icon={backup.locked ? <LockClosedRegular /> : <LockOpenRegular />}>{t(backup.locked ? 'backup.row.locked' : 'backup.row.encrypted')}</Badge>
          ) : null}
          {backup.source === 'apple' ? <Badge tone="info">{t('backup.row.itunes')}</Badge> : null}
        </span>
        <span className={styles.rowMeta}>{meta}</span>
      </div>
      {children ? <div className={styles.rowActions}>{children}</div> : null}
    </div>
  );
}

/** Asks for an encrypted backup's password; the keys stay in the host's memory until Lock or exit. */
export function UnlockDialog({ backup, onOpenChange, onUnlocked }: { backup: BackupInfo | null; onOpenChange(open: boolean): void; onUnlocked(backup: BackupInfo): void }) {
  const i18n = useI18n();
  const { t } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const close = (open: boolean) => {
    if (!open) {
      setPassword('');
      setError(null);
    }
    onOpenChange(open);
  };

  const submit = async () => {
    if (!backup || !password || busy) return;
    setBusy(true);
    setError(null);
    try {
      await client.call('backup.unlock', { backupId: backup.id, password });
      await queryClient.invalidateQueries({ queryKey: backupKeys.list });
      setPassword('');
      onUnlocked({ ...backup, locked: false });
    } catch (e) {
      setError(isBridgeError(e, 'wrong_password') ? t('backup.unlock.wrong') : describeError(e, t).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={backup !== null}
      onOpenChange={close}
      title={t('backup.unlock.title')}
      description={backup ? t('backup.unlock.body', { device: backup.deviceName, date: backupDate(backup, i18n) }) : undefined}
      footer={
        <>
          <Button onClick={() => close(false)}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void submit()} loading={busy} disabled={!password} data-testid="unlock-submit">
            {t('backup.unlock.submit')}
          </Button>
        </>
      }
    >
      <div className={styles.steps}>
        <PasswordField label={t('backup.unlock.password')} value={password} onChange={setPassword} error={error} autoFocus onEnter={() => void submit()} testId="unlock-password" />
        <p className={styles.note}>{busy ? t('backup.unlock.slow') : t('backup.unlock.memory')}</p>
      </div>
    </Dialog>
  );
}

/**
 * Confirms deleting backups: lists each one and what the PC gets back. Used for the keep-count plan after a backup
 * and for deleting one backup from the list. Nothing is deleted before the user presses Delete here.
 */
export function DeleteBackupsDialog({
  request,
  onOpenChange,
}: {
  request: { plan: PrunePlan; kind: 'prune' | 'delete'; newest?: boolean } | null;
  onOpenChange(open: boolean): void;
}) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const plan = request?.plan;
  const count = plan?.backups.length ?? 0;

  const remove = async () => {
    if (!plan || busy) return;
    setBusy(true);
    try {
      const result = await client.call('backup.delete', { backupIds: plan.backups.map((b) => b.id) });
      useBackupUi.getState().setPrune(null, plan.deviceId);
      await queryClient.invalidateQueries({ queryKey: backupKeys.list });
      await queryClient.invalidateQueries({ queryKey: backupKeys.device(plan.deviceId) });
      toast({ tone: 'success', title: t('backup.deleted', { count: result.deleted, size: bytes(result.freedBytes) }) });
      onOpenChange(false);
    } catch (e) {
      const { message, next } = describeError(e, t);
      toast({ tone: 'danger', title: message, body: next });
    } finally {
      setBusy(false);
    }
  };

  const keep = () => {
    if (request?.kind === 'prune' && plan) useBackupUi.getState().setPrune(null, plan.deviceId);
    onOpenChange(false);
  };

  return (
    <Dialog
      open={request !== null}
      onOpenChange={(open) => (open ? onOpenChange(true) : keep())}
      title={request?.kind === 'prune' ? t('backup.prune.title') : t('backup.delete.title', { count })}
      footer={
        <>
          <Button onClick={keep}>{request?.kind === 'prune' ? t('backup.prune.keep') : t('common.cancel')}</Button>
          <Button variant="dangerSolid" onClick={() => void remove()} loading={busy} data-testid="delete-backups-confirm">
            {request?.kind === 'prune' ? t('backup.prune.delete') : t('common.delete')}
          </Button>
        </>
      }
    >
      {plan ? (
        <div className={styles.steps}>
          <p>
            {request?.kind === 'prune'
              ? t('backup.prune.body', { keep: plan.keep, count, size: bytes(plan.freedBytes) })
              : t('backup.delete.body', { size: bytes(plan.freedBytes) })}
          </p>
          <ul className={styles.doomed} data-testid="doomed-backups">
            {plan.backups.map((b) => (
              <li key={b.id}>
                {b.deviceName} · {backupDate(b, i18n)} · {bytes(b.sizeBytes)}
              </li>
            ))}
          </ul>
          {request?.newest ? <Banner tone="warning">{t('backup.delete.newest', { device: plan.backups[0]?.deviceName ?? '' })}</Banner> : null}
          <p className={styles.note}>{t('backup.delete.shared')}</p>
        </div>
      ) : null}
    </Dialog>
  );
}
