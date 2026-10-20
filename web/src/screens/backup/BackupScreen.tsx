import {
  ArchiveRegular,
  ArrowCounterclockwiseRegular,
  DeleteRegular,
  FolderOpenRegular,
  LockClosedRegular,
  LockOpenRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { BackupInfo, DeviceSummary, PrunePlan } from '../../bridge/types';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Menu, type MenuEntry } from '../../components/Menu';
import { Banner, Card, EmptyState, Skeleton } from '../../components/ui';
import { useI18n } from '../../i18n';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Backup.module.css';
import { BackupBrowser } from './BackupBrowser';
import { backupDate, BackupRow, DeleteBackupsDialog, JobProgress, UnlockDialog } from './BackupBits';
import { BackupSettingsCard, type EncryptionMode } from './BackupSettingsCard';
import { backupKeys, useActiveDeviceJob, useBackupDevice, useBackups, useBackupUi } from './backupQueries';
import { EncryptionDialog } from './EncryptionDialog';
import { NewBackupDialog } from './NewBackupDialog';
import { RestoreDialog } from './RestoreDialog';

type DeleteRequest = { plan: PrunePlan; kind: 'prune' | 'delete'; newest?: boolean };

/** Backup and restore for the selected phone: its backups, the running job, settings, and the other backups on this PC. */
export function BackupScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const backups = useBackups();
  const info = useBackupDevice(device);
  const job = useActiveDeviceJob(device.id);
  const prune = useBackupUi((s) => s.prune[device.id]);
  // Overview's "Back up now" asks for the wizard; the request stands until the wizard is closed.
  const wizardRequested = useBackupUi((s) => s.wizardFor === device.id);
  const [browsing, setBrowsing] = useState<BackupInfo | null>(null);
  const [wizard, setWizard] = useState(false);
  const [restore, setRestore] = useState<{ initial: string | null } | null>(null);
  const [encryption, setEncryption] = useState<EncryptionMode | null>(null);
  const [unlock, setUnlock] = useState<{ backup: BackupInfo; then: (b: BackupInfo) => void } | null>(null);
  const [deleting, setDeleting] = useState<DeleteRequest | null>(null);

  const ios = device.platform === 'ios';
  const canBackup = device.capabilities.includes('backup');
  const canRestore = ios && device.capabilities.includes('restore');
  const all = backups.data ?? [];
  const mine = all.filter((b) => b.deviceId === device.id);
  const others = all.filter((b) => b.deviceId !== device.id);
  const last = mine.find((b) => b.state === 'complete');

  useEffect(() => setBrowsing(null), [device.id]);

  if (browsing) return <BackupBrowser backup={browsing} onBack={() => setBrowsing(null)} />;

  const whenUnlocked = (backup: BackupInfo, then: (b: BackupInfo) => void) => (backup.locked ? setUnlock({ backup, then }) : then(backup));

  const askDelete = async (backup: BackupInfo) => {
    try {
      const plan = await client.call('backup.deletePlan', { backupIds: [backup.id] });
      const newest = backup.source === 'deviceDeck' && all.filter((b) => b.deviceId === backup.deviceId && b.state === 'complete')[0]?.id === backup.id;
      setDeleting({ plan, kind: 'delete', newest });
    } catch (e) {
      actions.fail(e);
    }
  };

  const lock = async (backup: BackupInfo) => {
    try {
      await client.call('backup.lock', { backupId: backup.id });
      await queryClient.invalidateQueries({ queryKey: backupKeys.list });
    } catch (e) {
      actions.fail(e);
    }
  };

  const rowActions = (backup: BackupInfo) => {
    const restorable = canRestore && backup.platform === 'ios' && backup.state === 'complete';
    const items: MenuEntry[] = [
      ...(restorable
        ? [{ key: 'restore', label: t('backup.action.restore', { device: device.name }), icon: <ArrowCounterclockwiseRegular />, disabled: Boolean(job), onSelect: () => setRestore({ initial: backup.id }) }]
        : []),
      { key: 'explorer', label: t('common.showInExplorer'), icon: <FolderOpenRegular />, onSelect: () => void actions.revealInExplorer(backup.path) },
      ...(backup.encrypted && !backup.locked ? [{ key: 'lock', label: t('backup.action.lock'), icon: <LockClosedRegular />, onSelect: () => void lock(backup) }] : []),
      ...(backup.source === 'deviceDeck'
        ? [
            'separator' as const,
            { key: 'delete', label: t('backup.action.delete'), icon: <DeleteRegular />, danger: true, disabled: backup.state === 'inProgress', onSelect: () => void askDelete(backup) },
          ]
        : []),
    ];
    return (
      <>
        {backup.state === 'complete' ? (
          <Button
            size="small"
            icon={backup.locked ? <LockOpenRegular /> : undefined}
            onClick={() => whenUnlocked(backup, setBrowsing)}
            data-testid="backup-browse"
          >
            {backup.locked ? t('backup.action.unlock') : t('backup.action.browse')}
          </Button>
        ) : backup.state === 'incomplete' && backup.deviceId === device.id && canBackup && !job ? (
          <Button size="small" variant="accent" onClick={() => setWizard(true)} data-testid="backup-resume">
            {t('backup.action.resume')}
          </Button>
        ) : null}
        <Menu
          align="end"
          trigger={<Button size="small" variant="subtle" icon={<MoreHorizontalRegular />} aria-label={t('backup.action.more')} data-testid="backup-more" />}
          items={items}
        />
      </>
    );
  };

  const subtitle = last
    ? t('backup.subtitle.last', { date: backupDate(last, i18n), size: bytes(last.sizeBytes) })
    : backups.isPending
      ? ' '
      : t('backup.subtitle.none', { device: device.name });

  return (
    <ScreenLayout
      title={t('backup.title')}
      subtitle={subtitle}
      testId="screen-backup"
      actions={
        <>
          {canRestore ? (
            <Button icon={<ArrowCounterclockwiseRegular />} disabled={Boolean(job)} onClick={() => setRestore({ initial: null })} data-testid="restore-open">
              {t('backup.restore')}
            </Button>
          ) : null}
          {canBackup ? (
            <Button variant="accent" icon={<ArchiveRegular />} disabled={Boolean(job)} onClick={() => setWizard(true)} data-testid="backup-now">
              {t('backup.backUpNow')}
            </Button>
          ) : null}
        </>
      }
      banners={
        <>
          {prune ? (
            <Banner
              tone="info"
              title={t('backup.prune.banner', { count: prune.backups.length, size: bytes(prune.freedBytes) })}
              actions={
                <Button onClick={() => setDeleting({ plan: prune, kind: 'prune' })} data-testid="prune-review">
                  {t('backup.prune.review')}
                </Button>
              }
            />
          ) : null}
          {info.data && !info.data.keepsHistory ? <Banner tone="warning">{t('backup.settings.noHistory')}</Banner> : null}
        </>
      }
    >
      {backups.error ? <ErrorBanner error={backups.error} onRetry={() => void backups.refetch()} /> : null}
      <div className={styles.layout}>
        <div className={styles.column}>
          {job ? (
            <Card
              title={job.title}
              data-testid="backup-job-card"
              actions={
                <Button size="small" onClick={() => void client.call('backup.cancel', { jobId: job.id })} data-testid="backup-job-stop">
                  {t('backup.job.cancel')}
                </Button>
              }
            >
              <JobProgress job={job} />
            </Card>
          ) : null}
          <Card title={t('backup.list.title', { device: device.name })} data-testid="backup-list">
            {backups.isPending ? (
              <Skeleton height={56} />
            ) : mine.length === 0 ? (
              <EmptyState
                icon={<ArchiveRegular />}
                title={t('backup.list.empty.title')}
                body={t('backup.list.empty.body', { device: device.name })}
                action={
                  canBackup ? (
                    <Button variant="accent" onClick={() => setWizard(true)} disabled={Boolean(job)}>
                      {t('backup.backUpNow')}
                    </Button>
                  ) : undefined
                }
              />
            ) : (
              <div className={styles.list}>
                {mine.map((b) => (
                  <BackupRow key={b.id} backup={b}>
                    {rowActions(b)}
                  </BackupRow>
                ))}
              </div>
            )}
            {mine.filter((b) => b.state === 'complete').length > 1 ? <p className={styles.note}>{t('backup.sharedNote')}</p> : null}
          </Card>
          {others.length > 0 ? (
            <Card title={t('backup.list.others')} data-testid="backup-others">
              <div className={styles.list}>
                {others.map((b) => (
                  <BackupRow key={b.id} backup={b} showDevice>
                    {rowActions(b)}
                  </BackupRow>
                ))}
              </div>
            </Card>
          ) : null}
        </div>
        <div className={styles.column}>
          <BackupSettingsCard device={device} info={info.data} onEncryption={setEncryption} />
          <Card title={t('backup.includes.title')}>
            <p>{t(ios ? 'backup.includes.ios' : 'backup.includes.android')}</p>
            {!ios ? <p className={styles.note}>{t('backup.excludes.android')}</p> : null}
          </Card>
        </div>
      </div>

      <NewBackupDialog
        device={device}
        info={info.data}
        last={last}
        open={wizard || wizardRequested}
        onOpenChange={(open) => {
          setWizard(open);
          if (!open) useBackupUi.getState().openWizard(null);
        }}
        onPrune={(plan) => setDeleting({ plan, kind: 'prune' })}
      />
      {restore ? (
        <RestoreDialog device={device} backups={all} initial={restore.initial} open onOpenChange={(open) => !open && setRestore(null)} />
      ) : null}
      <EncryptionDialog device={device} mode={encryption} onOpenChange={(open) => !open && setEncryption(null)} />
      <UnlockDialog
        backup={unlock?.backup ?? null}
        onOpenChange={(open) => !open && setUnlock(null)}
        onUnlocked={(b) => {
          const then = unlock?.then;
          setUnlock(null);
          then?.(b);
        }}
      />
      <DeleteBackupsDialog request={deleting} onOpenChange={(open) => !open && setDeleting(null)} />
    </ScreenLayout>
  );
}
