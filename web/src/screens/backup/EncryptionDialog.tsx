import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useClient } from '../../app/bridgeContext';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { describeError } from '../../components/ErrorState';
import { Banner } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './Backup.module.css';
import { JobProgress, PasswordField } from './BackupBits';
import type { EncryptionMode } from './BackupSettingsCard';
import { backupKeys, isActive, rememberJob, useBackupJob } from './backupQueries';

const TITLE: Record<EncryptionMode, MessageKey> = {
  on: 'backup.encrypt.onTitle',
  off: 'backup.encrypt.offTitle',
  change: 'backup.encrypt.changeTitle',
};

const DONE: Record<EncryptionMode, MessageKey> = {
  on: 'backup.encrypt.doneOn',
  off: 'backup.encrypt.doneOff',
  change: 'backup.encrypt.doneChange',
};

/**
 * Turns iPhone backup encryption on or off, or changes its password. The phone asks for its passcode before it
 * agrees; DeviceDeck never keeps the password.
 */
export function EncryptionDialog({ device, mode, onOpenChange }: { device: DeviceSummary; mode: EncryptionMode | null; onOpenChange(open: boolean): void }) {
  const { t } = useI18n();
  const client = useClient();
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState('');
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [jobId, setJobId] = useState<string | null>(null);
  const job = useBackupJob(jobId);

  useEffect(() => {
    if (mode) {
      setCurrent('');
      setPassword('');
      setRepeat('');
      setError(null);
      setJobId(null);
    }
  }, [mode]);

  useEffect(() => {
    if (job?.state === 'done') void queryClient.invalidateQueries({ queryKey: backupKeys.device(device.id) });
  }, [job?.state, device.id, queryClient]);

  const needsNew = mode === 'on' || mode === 'change';
  const needsCurrent = mode === 'off' || mode === 'change';
  const mismatch = needsNew && repeat.length > 0 && password !== repeat;
  const ready = (!needsNew || (password.length > 0 && password === repeat)) && (!needsCurrent || current.length > 0);

  const submit = async () => {
    if (!mode || !ready || busy) return;
    setBusy(true);
    setError(null);
    try {
      const started = await client.call('backup.setEncryption', {
        deviceId: device.id,
        enable: mode !== 'off',
        ...(needsNew ? { password } : {}),
        ...(needsCurrent ? { oldPassword: current } : {}),
      });
      rememberJob(queryClient, started);
      setJobId(started.id);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  let body;
  let footer;
  if (job && mode) {
    body = (
      <div className={styles.steps}>
        {job.state === 'done' ? (
          <Banner tone="success">{t(DONE[mode], { device: device.name })}</Banner>
        ) : job.state === 'failed' ? (
          <Banner tone="danger" title={job.error?.code === 'wrong_password' ? t('backup.unlock.wrong') : t('backup.encrypt.failed')}>
            {job.error?.message}
          </Banner>
        ) : (
          <>
            <JobProgress job={job} />
            <p>{t('backup.encrypt.phone', { device: device.name })}</p>
          </>
        )}
      </div>
    );
    footer = isActive(job) ? (
      <Button onClick={() => void client.call('backup.cancel', { jobId: job.id })}>{t('common.cancel')}</Button>
    ) : job.state === 'failed' ? (
      <>
        <Button onClick={() => onOpenChange(false)}>{t('common.close')}</Button>
        <Button variant="accent" onClick={() => setJobId(null)}>
          {t('common.retry')}
        </Button>
      </>
    ) : (
      <Button variant="accent" onClick={() => onOpenChange(false)}>
        {t('common.close')}
      </Button>
    );
  } else {
    body = (
      <div className={styles.steps}>
        {needsCurrent ? (
          <PasswordField label={t('backup.encrypt.current')} value={current} onChange={setCurrent} autoFocus testId="encryption-current" />
        ) : null}
        {needsNew ? (
          <>
            <PasswordField label={t(mode === 'change' ? 'backup.encrypt.new' : 'backup.encrypt.password')} value={password} onChange={setPassword} autoFocus={!needsCurrent} testId="encryption-password" />
            <PasswordField
              label={t('backup.encrypt.repeat')}
              value={repeat}
              onChange={setRepeat}
              error={mismatch ? t('backup.encrypt.mismatch') : null}
              onEnter={() => void submit()}
              testId="encryption-repeat"
            />
          </>
        ) : null}
        <p className={styles.note}>{mode === 'off' ? t('backup.encrypt.offNote') : t('backup.encrypt.warning')}</p>
        <p className={styles.note}>{t('backup.encrypt.phone', { device: device.name })}</p>
        {error ? <Banner tone="danger" title={describeError(error, t).message}>{describeError(error, t).next}</Banner> : null}
      </div>
    );
    footer = (
      <>
        <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
        <Button variant="accent" disabled={!ready} loading={busy} onClick={() => void submit()} data-testid="encryption-submit">
          {t('backup.encrypt.confirm')}
        </Button>
      </>
    );
  }

  return (
    <Dialog open={mode !== null} onOpenChange={onOpenChange} title={mode ? t(TITLE[mode], { device: device.name }) : ''} footer={footer}>
      {body}
    </Dialog>
  );
}
