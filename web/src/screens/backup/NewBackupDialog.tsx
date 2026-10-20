import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useClient } from '../../app/bridgeContext';
import type { BackupDeviceInfo, BackupInfo, DeviceSummary, PrunePlan } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { describeError } from '../../components/ErrorState';
import { Banner, Checkbox } from '../../components/ui';
import { useI18n } from '../../i18n';
import styles from './Backup.module.css';
import { backupDate, JobProgress } from './BackupBits';
import { isActive, rememberJob, useBackupJob, useBackupUi } from './backupQueries';

/**
 * The new-backup wizard: what will happen (where it goes, encrypted or not, everything or what changed), then the
 * job's progress (the dialog may close; the job keeps running), then the result with any older backups the keep
 * count offers for deletion.
 */
export function NewBackupDialog({
  device,
  info,
  last,
  open,
  onOpenChange,
  onPrune,
}: {
  device: DeviceSummary;
  info: BackupDeviceInfo | undefined;
  last: BackupInfo | undefined;
  open: boolean;
  onOpenChange(open: boolean): void;
  onPrune(plan: PrunePlan): void;
}) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const [full, setFull] = useState(false);
  const [jobId, setJobId] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [starting, setStarting] = useState(false);
  const job = useBackupJob(jobId);
  const ios = device.platform === 'ios';

  const close = (value: boolean) => {
    if (!value) {
      // A finished job's dialog starts afresh next time; a running one keeps going in the background.
      if (!job || !isActive(job)) setJobId(null);
      setError(null);
      setFull(false);
    }
    onOpenChange(value);
  };

  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      const started = await client.call('backup.start', { deviceId: device.id, ...(full ? { full: true } : {}) });
      rememberJob(queryClient, started);
      setJobId(started.id);
    } catch (e) {
      setError(e);
    } finally {
      setStarting(false);
    }
  };

  const keepOld = () => {
    useBackupUi.getState().setPrune(null, device.id);
    close(false);
  };

  let body;
  let footer;
  if (!job) {
    body = (
      <div className={styles.steps}>
        <dl className={styles.facts}>
          <dt>{t('backup.new.destination')}</dt>
          <dd>
            <span className={styles.path}>{info?.folder ?? ''}</span>
            {info?.freeBytes != null ? <span className={styles.note}> · {t('backup.settings.free', { free: bytes(info.freeBytes) })}</span> : null}
          </dd>
          {ios ? (
            <>
              <dt>{t('backup.encryption.title')}</dt>
              <dd>{info?.encrypted ? t('backup.new.encrypted') : t('backup.new.notEncrypted')}</dd>
            </>
          ) : null}
          <dt>{t('backup.new.what')}</dt>
          <dd>{last ? t('backup.new.incremental', { date: backupDate(last, i18n) }) : t('backup.new.first', { device: device.name })}</dd>
        </dl>
        {!ios ? <p className={styles.note}>{t('backup.excludes.android')}</p> : null}
        {ios && last ? (
          <label className={styles.check}>
            <Checkbox checked={full} onChange={setFull} label={t('backup.new.full')} />
            <span>{t('backup.new.full')}</span>
          </label>
        ) : null}
        <p className={styles.note}>{t('backup.new.keepPhone', { device: device.name })}</p>
        {error ? <Banner tone="danger" title={describeError(error, t).message}>{describeError(error, t).next}</Banner> : null}
      </div>
    );
    footer = (
      <>
        <Button onClick={() => close(false)}>{t('common.cancel')}</Button>
        <Button variant="accent" onClick={() => void start()} loading={starting} data-testid="backup-start">
          {t('backup.new.start')}
        </Button>
      </>
    );
  } else if (isActive(job)) {
    body = (
      <div className={styles.steps}>
        <JobProgress job={job} />
        <p className={styles.note}>{t('backup.new.keepPhone', { device: device.name })}</p>
      </div>
    );
    footer = (
      <>
        <Button onClick={() => void client.call('backup.cancel', { jobId: job.id })} data-testid="backup-stop">
          {t('backup.job.cancel')}
        </Button>
        <Button variant="accent" onClick={() => close(false)}>
          {t('backup.new.background')}
        </Button>
      </>
    );
  } else if (job.state === 'done') {
    const plan = job.prune;
    body = (
      <div className={styles.steps} data-testid="backup-done">
        <Banner tone="success" title={t('backup.new.done.title')}>
          {t('backup.new.done.body', { device: device.name, size: bytes(job.bytes) })}
        </Banner>
        {plan && plan.backups.length > 0 ? (
          <div className={styles.steps} data-testid="backup-prune">
            <strong>{t('backup.prune.title')}</strong>
            <p>{t('backup.prune.body', { keep: plan.keep, count: plan.backups.length, size: bytes(plan.freedBytes) })}</p>
            <ul className={styles.doomed}>
              {plan.backups.map((b) => (
                <li key={b.id}>
                  {backupDate(b, i18n)} · {bytes(b.sizeBytes)}
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </div>
    );
    footer =
      plan && plan.backups.length > 0 ? (
        <>
          <Button onClick={keepOld}>{t('backup.prune.keep')}</Button>
          <Button
            variant="danger"
            onClick={() => {
              close(false);
              onPrune(plan);
            }}
            data-testid="backup-prune-review"
          >
            {t('backup.prune.delete')}
          </Button>
        </>
      ) : (
        <Button variant="accent" onClick={() => close(false)}>
          {t('common.close')}
        </Button>
      );
  } else {
    body = (
      <div className={styles.steps}>
        <Banner tone={job.state === 'failed' ? 'danger' : 'info'} title={job.state === 'failed' ? t('backup.new.failed.title') : t('backup.job.cancelled')}>
          {job.error?.message}
        </Banner>
      </div>
    );
    footer = (
      <>
        <Button onClick={() => close(false)}>{t('common.close')}</Button>
        <Button
          variant="accent"
          onClick={() => {
            setJobId(null);
            void start();
          }}
        >
          {t('common.retry')}
        </Button>
      </>
    );
  }

  return (
    <Dialog open={open} onOpenChange={close} title={t('backup.new.title', { device: device.name })} footer={footer} wide>
      {body}
    </Dialog>
  );
}
