import { LockClosedRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useClient } from '../../app/bridgeContext';
import { isBridgeError } from '../../bridge/errors';
import type { BackupInfo, DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { describeError } from '../../components/ErrorState';
import { Banner, Checkbox, TextField } from '../../components/ui';
import { useI18n } from '../../i18n';
import styles from './Backup.module.css';
import { backupDate, JobProgress, PasswordField } from './BackupBits';
import { isActive, rememberJob, useBackupJob } from './backupQueries';

type Step = 'choose' | 'options' | 'confirm';

function newer(a: string | null, b: string): boolean {
  if (!a) return false;
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const x = pa[i] ?? 0;
    const y = pb[i] ?? 0;
    if (x !== y) return x > y;
  }
  return false;
}

/** The phone's name as typed: case and surrounding spaces don't matter, as on the host. */
export function nameMatches(typed: string, name: string): boolean {
  return typed.trim().toLocaleLowerCase() === name.trim().toLocaleLowerCase();
}

/**
 * Restores an iPhone backup onto the connected iPhone: pick the backup, read what happens (everything replaced,
 * Find My off, the phone restarts), choose settings and give the password, then type the phone's name to confirm.
 */
export function RestoreDialog({
  device,
  backups,
  initial,
  open,
  onOpenChange,
}: {
  device: DeviceSummary;
  backups: BackupInfo[];
  initial: string | null;
  open: boolean;
  onOpenChange(open: boolean): void;
}) {
  const i18n = useI18n();
  const { t, bytes, digits } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const [step, setStep] = useState<Step>('choose');
  const [chosen, setChosen] = useState<string | null>(initial);
  const [restoreSettings, setRestoreSettings] = useState(true);
  const [password, setPassword] = useState('');
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [jobId, setJobId] = useState<string | null>(null);
  const job = useBackupJob(jobId);

  const candidates = backups
    .filter((b) => b.platform === 'ios' && b.state === 'complete')
    .sort((a, b) => Number(b.deviceId === device.id) - Number(a.deviceId === device.id) || (b.createdAt ?? '').localeCompare(a.createdAt ?? ''));
  const backup = candidates.find((b) => b.id === chosen) ?? null;

  useEffect(() => {
    if (open) {
      setStep(initial ? 'options' : 'choose');
      setChosen(initial ?? candidates.find((b) => b.deviceId === device.id && !newer(b.osVersion, device.osVersion))?.id ?? null);
      setTyped('');
      setPassword('');
      setPasswordError(null);
      setError(null);
      setRestoreSettings(true);
      if (job && !isActive(job)) setJobId(null);
    }
    // Only when the dialog opens.
  }, [open]);

  const restore = async () => {
    if (!backup || busy) return;
    setBusy(true);
    setError(null);
    try {
      const started = await client.call('backup.restore', {
        deviceId: device.id,
        backupId: backup.id,
        confirmName: typed,
        restoreSettings,
        ...(backup.encrypted ? { password } : {}),
      });
      rememberJob(queryClient, started);
      setJobId(started.id);
    } catch (e) {
      if (isBridgeError(e, 'wrong_password') || isBridgeError(e, 'password_required')) {
        setPasswordError(t('backup.unlock.wrong'));
        setStep('options');
      } else {
        setError(e);
      }
    } finally {
      setBusy(false);
    }
  };

  let body;
  let footer;
  if (job) {
    body = (
      <div className={styles.steps}>
        <JobProgress job={job} />
        {job.state === 'done' ? (
          <Banner tone="success" title={t('backup.restore.done.title')}>
            {t('backup.restore.done.body', { device: device.name })}
          </Banner>
        ) : isActive(job) ? (
          <p className={styles.note}>{t('backup.restore.keepPhone', { device: device.name })}</p>
        ) : null}
      </div>
    );
    footer = isActive(job) ? (
      <Button variant="accent" onClick={() => onOpenChange(false)}>
        {t('backup.new.background')}
      </Button>
    ) : (
      <Button variant="accent" onClick={() => onOpenChange(false)}>
        {t('common.close')}
      </Button>
    );
  } else if (step === 'choose') {
    body =
      candidates.length === 0 ? (
        <p>{t('backup.restore.noBackups')}</p>
      ) : (
        <div className={styles.choices} role="list" data-testid="restore-choices">
          {candidates.map((b) => {
            const tooNew = newer(b.osVersion, device.osVersion);
            return (
              <button
                key={b.id}
                type="button"
                role="listitem"
                className={styles.choice}
                aria-pressed={chosen === b.id}
                disabled={tooNew}
                onClick={() => setChosen(b.id)}
                data-testid="restore-choice"
              >
                <span className={styles.rowText}>
                  <span className={styles.rowTitle}>
                    {backupDate(b, i18n)}
                    {b.encrypted ? <LockClosedRegular aria-label={t('backup.row.encrypted')} /> : null}
                  </span>
                  <span className={styles.rowMeta}>
                    {[b.deviceId === device.id ? b.deviceName : `${t('backup.restore.fromOther')}: ${b.deviceName}`, b.osVersion ? digits(`iOS ${b.osVersion}`) : null, bytes(b.sizeBytes)]
                      .filter(Boolean)
                      .join(' · ')}
                  </span>
                  {tooNew ? <span className={styles.note}>{digits(t('backup.restore.newerIos', { backup: b.osVersion ?? '', device: device.osVersion }))}</span> : null}
                </span>
              </button>
            );
          })}
        </div>
      );
    footer = (
      <>
        <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
        <Button variant="accent" disabled={!backup} onClick={() => setStep('options')} data-testid="restore-next">
          {t('backup.restore.next')}
        </Button>
      </>
    );
  } else if (step === 'options' && backup) {
    body = (
      <div className={styles.steps}>
        <Banner tone="warning" title={t('backup.restore.warning', { device: device.name })}>
          {t('backup.restore.findMy')}
        </Banner>
        <dl className={styles.facts}>
          <dt>{t('backup.restore.backup')}</dt>
          <dd>
            {backup.deviceName} · {backupDate(backup, i18n)}
          </dd>
        </dl>
        <label className={styles.check}>
          <Checkbox checked={restoreSettings} onChange={setRestoreSettings} label={t('backup.restore.settings')} />
          <span>{t('backup.restore.settings')}</span>
        </label>
        {backup.encrypted ? (
          <PasswordField
            label={t('backup.restore.password')}
            value={password}
            onChange={(v) => {
              setPassword(v);
              setPasswordError(null);
            }}
            error={passwordError}
            autoFocus
            testId="restore-password"
          />
        ) : null}
      </div>
    );
    footer = (
      <>
        <Button onClick={() => (initial ? onOpenChange(false) : setStep('choose'))}>{initial ? t('common.cancel') : t('common.back')}</Button>
        <Button variant="accent" disabled={backup.encrypted && !password} onClick={() => setStep('confirm')} data-testid="restore-next">
          {t('backup.restore.next')}
        </Button>
      </>
    );
  } else {
    const matches = nameMatches(typed, device.name);
    body = (
      <div className={styles.steps}>
        <p>{t('backup.restore.confirmBody', { device: device.name })}</p>
        <TextField label={t('backup.restore.confirmLabel', { name: device.name })} value={typed} onChange={setTyped} autoFocus onEnter={() => matches && void restore()} />
        <p className={styles.note}>{t('backup.restore.confirmHint')}</p>
        {error ? <Banner tone="danger" title={describeError(error, t).message}>{describeError(error, t).next}</Banner> : null}
      </div>
    );
    footer = (
      <>
        <Button onClick={() => setStep('options')}>{t('common.back')}</Button>
        <Button variant="dangerSolid" disabled={!matches} loading={busy} onClick={() => void restore()} data-testid="restore-start">
          {t('backup.restore.start')}
        </Button>
      </>
    );
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={t('backup.restore.title', { device: device.name })} footer={footer} wide dismissible={!busy}>
      {body}
    </Dialog>
  );
}
