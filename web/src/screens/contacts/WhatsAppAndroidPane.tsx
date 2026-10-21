import { ArrowClockwiseRegular, ArrowDownloadRegular, ChatRegular, FolderOpenRegular, LockClosedRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useSettings, useTransfers } from '../../app/queries';
import type { DeviceSummary, WhatsAppAndroidApp } from '../../bridge/types';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, Card, EmptyState, LinearProgress, Skeleton } from '../../components/ui';
import { useI18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import backupStyles from '../backup/Backup.module.css';
import wa from './WhatsApp.module.css';

/**
 * WhatsApp on an Android phone: its chats can't be opened (encrypted with a key only the phone has), so this says so,
 * shows what WhatsApp keeps on the phone, and copies its media and its own encrypted backups to the PC.
 */
export function WhatsAppAndroidPane({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t } = i18n;
  const client = useClient();
  const actions = useActions();
  const settings = useSettings().data;
  const ready = device.state === 'ready' && device.capabilities.includes('files');
  const info = useQuery({
    queryKey: ['whatsapp.android', device.id],
    queryFn: () => client.call('whatsapp.android', { deviceId: device.id }),
    enabled: ready,
    retry: false,
  });
  const [folder, setFolder] = useState<string | null>(null);
  const [started, setStarted] = useState<{ jobId: string; folder: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const job = (useTransfers().data ?? []).find((j) => j.id === started?.jobId) ?? null;
  const destination = folder ?? settings?.fileCopyFolder ?? '';
  const running = job !== null && ['queued', 'running', 'waitingForDevice', 'paused', 'needsDecision'].includes(job.state);

  const backUp = async () => {
    setBusy(true);
    try {
      const result = await client.call('whatsapp.backupAndroid', { deviceId: device.id, destination });
      useUi.getState().rememberDestination(result.jobId, result.folder);
      setStarted(result);
      toast({ tone: 'info', title: t('whatsapp.android.backup.started', { folder: result.folder }), duration: 3000 });
    } catch (e) {
      actions.fail(e);
    } finally {
      setBusy(false);
    }
  };

  const apps = info.data?.apps ?? [];
  return (
    <div className={wa.android} data-testid="whatsapp-android">
      <Banner tone="info" icon={<LockClosedRegular />} title={t('whatsapp.android.locked.title')}>
        {t('whatsapp.android.locked.body')}
      </Banner>
      {info.error ? (
        <ErrorBanner error={info.error} onRetry={() => void info.refetch()} />
      ) : info.isPending && ready ? (
        <Skeleton height={160} />
      ) : apps.length === 0 ? (
        <EmptyState
          icon={<ChatRegular />}
          title={t('whatsapp.android.none.title', { device: device.name })}
          body={t('whatsapp.android.none.body')}
          action={
            <Button icon={<ArrowClockwiseRegular />} onClick={() => void info.refetch()}>
              {t('whatsapp.android.refresh')}
            </Button>
          }
        />
      ) : (
        <>
          <div className={wa.cards}>
            {apps.map((app) => (
              <AppCard key={app.package} app={app} deviceId={device.id} />
            ))}
          </div>
          <Card title={t('whatsapp.android.backup.title')} className={wa.backupCard} data-testid="whatsapp-android-backup">
            <p className={wa.prose}>{t('whatsapp.android.backup.body')}</p>
            <dl className={backupStyles.facts}>
              <dt>{t('contacts.exportTo')}</dt>
              <dd>
                <span className={backupStyles.path}>{destination}</span>{' '}
                <Button
                  size="small"
                  variant="link"
                  onClick={async () => {
                    const picked = await actions.pickFolder(t('contacts.exportTo'));
                    if (picked) setFolder(picked);
                  }}
                >
                  {t('common.change')}
                </Button>
              </dd>
            </dl>
            <div className={wa.backupRow}>
              <Button
                variant="accent"
                icon={<ArrowDownloadRegular />}
                onClick={() => void backUp()}
                loading={busy}
                disabled={!destination || running || !apps.some((a) => a.hasFolder)}
                data-testid="whatsapp-android-start"
              >
                {t('whatsapp.android.backup.action')}
              </Button>
              {job?.state === 'done' && started ? (
                <Button variant="subtle" icon={<FolderOpenRegular />} onClick={() => void actions.revealInExplorer(started.folder)}>
                  {t('common.showInExplorer')}
                </Button>
              ) : null}
            </div>
            {job && started ? (
              <div className={wa.progress} data-testid="whatsapp-android-progress" aria-live="polite">
                {job.state === 'done' ? (
                  <span>{t('whatsapp.android.backup.done', { folder: started.folder })}</span>
                ) : job.state === 'failed' ? (
                  <span>{t('whatsapp.android.backup.failed', { message: job.error?.message ?? '' })}</span>
                ) : (
                  <>
                    <LinearProgress value={jobFraction(job)} label={t('whatsapp.android.backup.started', { folder: started.folder })} />
                    <span>{t('whatsapp.android.backup.progress', { done: job.doneItems, total: job.totalItems })}</span>
                  </>
                )}
              </div>
            ) : null}
          </Card>
        </>
      )}
      <Card title={t('whatsapp.android.restore.title')}>
        <p className={wa.prose}>{t('whatsapp.android.restore.body')}</p>
      </Card>
      <p className={wa.prose}>{t('whatsapp.android.readTip')}</p>
    </div>
  );
}

function AppCard({ app, deviceId }: { app: WhatsAppAndroidApp; deviceId: string }) {
  const { t, bytes, date } = useI18n();
  const client = useClient();
  return (
    <Card data-testid="whatsapp-android-app">
      <div className={wa.cardHead}>
        {app.installed ? <img className={wa.appIcon} src={client.images.icon(deviceId, app.package)} alt="" loading="lazy" /> : null}
        <span className={wa.cardTitle}>{app.name}</span>
      </div>
      {app.installed === false ? <p className={`${wa.prose} t-caption`}>{t('whatsapp.android.notInstalled')}</p> : null}
      {app.hasFolder ? (
        <dl className={wa.facts}>
          <dt>{t('whatsapp.android.folder')}</dt>
          <dd>
            <span className={wa.path}>{app.folder}</span>
            {app.folder.startsWith('/Android/') ? null : <div className="t-caption t-secondary">{t('whatsapp.android.legacy')}</div>}
          </dd>
          <dt>{t('whatsapp.android.media')}</dt>
          <dd>{app.mediaFiles !== null && app.mediaBytes !== null ? t('whatsapp.android.mediaValue', { count: app.mediaFiles, size: bytes(app.mediaBytes) }) : t('whatsapp.android.mediaUnknown')}</dd>
          <dt>{t('whatsapp.android.chatBackups')}</dt>
          <dd>
            {app.chatBackups.length === 0 ? (
              t('whatsapp.android.noChatBackups')
            ) : (
              <ul>
                {app.chatBackups.slice(0, 3).map((b) => (
                  <li key={b.name}>
                    <span className={wa.path}>{b.name}</span>
                    <span className={wa.factMeta}>{[bytes(b.size), b.modified ? date(b.modified, 'medium') : null].filter(Boolean).join(' · ')}</span>
                  </li>
                ))}
              </ul>
            )}
          </dd>
        </dl>
      ) : (
        <p className={wa.prose}>{t('whatsapp.android.empty')}</p>
      )}
    </Card>
  );
}
