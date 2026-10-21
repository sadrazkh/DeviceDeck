import { AddRegular } from '@fluentui/react-icons';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { LocalSendTarget, PhotosToAppStarted } from '../../bridge/localSend';
import type { AppInfo, DeviceSummary } from '../../bridge/types';
import { useApps, useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Badge, RadioCards } from '../../components/ui';
import { useI18n } from '../../i18n';
import { isFinished } from '../../state/transfers';
import { SUGGESTED_APPS, lastApp, useBackupActions } from './backupQueries';
import { IntoAppPanel, IntoAppSent } from './IntoAppPanel';
import { LocalSendPanel } from './LocalSendPanel';
import { LocalSendProgress } from './LocalSendProgress';
import { useLocalSendActions, useLocalSendJob } from './localSendQueries';
import styles from './Backup.module.css';
import lsStyles from './LocalSend.module.css';

type Route = 'localsend' | 'app';

function sortApps(apps: AppInfo[]): AppInfo[] {
  const rank = (a: AppInfo) => {
    const i = SUGGESTED_APPS.indexOf(a.id);
    return i < 0 ? SUGGESTED_APPS.length : i;
  };
  return [...apps].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
}

/**
 * "Add to iPhone": an iPhone takes no photos into its Photos library over USB, so there are two ways.
 * First (recommended): straight into Photos through the free LocalSend app, over the cable or Wi-Fi, with live status.
 * Second: into the Documents of an app that shares files, in a dated folder, with the steps to save them all at once
 * from the Files app. Dropping photos on the Photos screen opens this too.
 */
export function ImportToAppDialog({
  device,
  open,
  onOpenChange,
  initialFiles,
}: {
  device: DeviceSummary;
  open: boolean;
  onOpenChange(open: boolean): void;
  initialFiles: File[];
}) {
  const { t, bytes } = useI18n();
  const [route, setRoute] = useState<Route>('localsend');
  const [files, setFiles] = useState<File[]>(initialFiles);
  const [busy, setBusy] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  // LocalSend
  const [target, setTarget] = useState<LocalSendTarget | null>(null);
  const [sendJobId, setSendJobId] = useState<string | null>(null);
  const localSend = useLocalSendActions(device);
  const sending = useLocalSendJob(sendJobId);

  // Into an app
  const apps = useApps(device, false);
  const backup = useBackupActions(device);
  const sharing = useMemo(() => sortApps((apps.data ?? []).filter((a) => a.fileSharing)), [apps.data]);
  const [appId, setAppId] = useState<string | null>(null);
  const [sent, setSent] = useState<{ started: PhotosToAppStarted; appName: string } | null>(null);
  const transfers = useTransfers();
  const sentJob = useMemo(() => (sent ? (transfers.data?.find((j) => j.id === sent.started.jobId) ?? null) : null), [transfers.data, sent]);

  useEffect(() => setFiles(initialFiles), [initialFiles]);
  useEffect(() => {
    if (appId && sharing.some((a) => a.id === appId)) return;
    const remembered = lastApp.get(device.id);
    setAppId(sharing.find((a) => a.id === remembered)?.id ?? sharing[0]?.id ?? null);
  }, [sharing, appId, device.id]);

  const app = sharing.find((a) => a.id === appId);
  const total = files.reduce((sum, f) => sum + f.size, 0);
  const started = sendJobId !== null || sent !== null;

  const start = async () => {
    if (files.length === 0) return;
    setBusy(true);
    if (route === 'localsend') {
      if (target) setSendJobId(await localSend.send(target, files));
    } else if (app) {
      const result = await backup.importToApp(app.id, files);
      if (result) {
        lastApp.set(device.id, app.id);
        setSent({ started: result, appName: app.name });
      }
    }
    setBusy(false);
  };

  const job = sending.job;
  const jobRunning = job !== null && !isFinished(job.state);
  const close = () => onOpenChange(false);

  let footer;
  if (sendJobId) {
    footer = jobRunning ? (
      <>
        <Button onClick={() => localSend.cancel(sendJobId)} data-testid="localsend-cancel">
          {t('localsend.cancel')}
        </Button>
        <Button variant="accent" onClick={close} data-testid="localsend-hide">
          {t('localsend.hide')}
        </Button>
      </>
    ) : (
      <Button variant={job?.state === 'done' ? 'accent' : 'standard'} onClick={close} data-testid="localsend-close">
        {t('localsend.close')}
      </Button>
    );
  } else if (sent) {
    footer = (
      <Button variant="accent" onClick={close} data-testid="to-app-done">
        {t('photos.toApp.done')}
      </Button>
    );
  } else {
    const ready = files.length > 0 && (route === 'localsend' ? target !== null : app !== undefined);
    footer = (
      <>
        <Button onClick={close}>{t('common.cancel')}</Button>
        <Button variant="accent" onClick={() => void start()} loading={busy} disabled={!ready} data-testid="to-app-start">
          {route === 'localsend'
            ? t('localsend.send', { count: files.length })
            : app
              ? t('photos.toApp.start', { count: files.length, app: app.name })
              : t('photos.toApp.startNoApp')}
        </Button>
      </>
    );
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={t('photos.toApp.title', { device: device.name })} footer={footer} wide>
      {sendJobId ? (
        <div className={styles.body}>
          <LocalSendProgress
            device={device}
            job={job}
            status={sending.status}
            receiverName={target?.alias ?? device.name}
            onRetry={(pin) => localSend.retry(sendJobId, pin)}
          />
        </div>
      ) : sent ? (
        <IntoAppSent started={sent.started} appName={sent.appName} job={sentJob} />
      ) : (
        <div className={styles.body}>
          <div className={styles.section}>
            <span className={styles.label}>{t('photos.toApp.files', { count: files.length })}</span>
            {files.length > 0 ? (
              <ul className={styles.fileList} data-testid="to-app-files">
                {files.map((f, i) => (
                  <li key={`${f.name}-${i}`}>
                    <span className={styles.fileName}>{f.name}</span>
                    <span className="t-secondary num">{bytes(f.size)}</span>
                  </li>
                ))}
              </ul>
            ) : null}
            <div className={lsStyles.row}>
              <Button icon={<AddRegular />} onClick={() => input.current?.click()} data-testid="to-app-choose">
                {t('photos.toApp.choose')}
              </Button>
              {files.length > 0 ? <span className={styles.hint}>{t('photos.toApp.total', { count: files.length, size: bytes(total) })}</span> : null}
            </div>
            <input
              ref={input}
              type="file"
              multiple
              hidden
              accept="image/*,video/*,.heic,.heif"
              data-testid="to-app-input"
              onChange={(e) => {
                const picked = Array.from(e.target.files ?? []);
                e.target.value = '';
                if (picked.length) setFiles((current) => [...current, ...picked]);
              }}
            />
          </div>

          <div className={styles.section}>
            <span className={styles.label}>{t('photos.toApp.route')}</span>
            <div className={lsStyles.stretch} data-testid="to-app-routes">
              <RadioCards<Route>
                label={t('photos.toApp.route')}
                value={route}
                onChange={setRoute}
                options={[
                  {
                    value: 'localsend',
                    label: (
                      <>
                        {t('localsend.route.photos')}{' '}
                        <Badge tone="accent" className="t-caption">
                          {t('localsend.route.recommended')}
                        </Badge>
                      </>
                    ),
                    hint: t('localsend.route.photosHint'),
                  },
                  { value: 'app', label: t('localsend.route.app'), hint: t('localsend.route.appHint') },
                ]}
              />
            </div>
          </div>

          {route === 'localsend' ? (
            <LocalSendPanel device={device} active={open && !started} onTarget={setTarget} />
          ) : (
            <IntoAppPanel loading={apps.isPending} sharing={sharing} appId={appId} onAppId={setAppId} count={files.length} />
          )}
        </div>
      )}
    </Dialog>
  );
}
