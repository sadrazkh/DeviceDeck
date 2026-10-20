import { AddRegular, AppsRegular } from '@fluentui/react-icons';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { AppInfo, DeviceSummary } from '../../bridge/types';
import { useApps } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Badge, Banner, EmptyState, RadioCards, Skeleton } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useUi } from '../../state/ui';
import { SUGGESTED_APPS, lastApp, useBackupActions } from './backupQueries';
import styles from './Backup.module.css';

function sortApps(apps: AppInfo[]): AppInfo[] {
  const rank = (a: AppInfo) => {
    const i = SUGGESTED_APPS.indexOf(a.id);
    return i < 0 ? SUGGESTED_APPS.length : i;
  };
  return [...apps].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
}

/**
 * iPhone: photos can't be written into the Photos library over USB, so they go into the Documents of an app that
 * shares files. Says so up front, lets the user pick the app, and says where the files went once they're sent.
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
  const apps = useApps(device, false);
  const backup = useBackupActions(device);
  const sharing = useMemo(() => sortApps((apps.data ?? []).filter((a) => a.fileSharing)), [apps.data]);
  const [appId, setAppId] = useState<string | null>(null);
  const [files, setFiles] = useState<File[]>(initialFiles);
  const [busy, setBusy] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => setFiles(initialFiles), [initialFiles]);
  useEffect(() => {
    if (appId && sharing.some((a) => a.id === appId)) return;
    const remembered = lastApp.get(device.id);
    setAppId(sharing.find((a) => a.id === remembered)?.id ?? sharing[0]?.id ?? null);
  }, [sharing, appId, device.id]);

  const app = sharing.find((a) => a.id === appId);
  const total = files.reduce((sum, f) => sum + f.size, 0);

  const start = async () => {
    if (!app || files.length === 0) return;
    setBusy(true);
    const jobId = await backup.importToApp(app.id, app.name, files);
    setBusy(false);
    if (!jobId) return;
    lastApp.set(device.id, app.id);
    useUi.getState().setDrawerOpen(true);
    onOpenChange(false);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={t('photos.toApp.title', { device: device.name })}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void start()} loading={busy} disabled={!app || files.length === 0} data-testid="to-app-start">
            {app ? t('photos.toApp.start', { count: files.length, app: app.name }) : t('photos.toApp.startNoApp')}
          </Button>
        </>
      }
    >
      <div className={styles.body}>
        <Banner title={t('photos.toApp.whyTitle')}>{t('photos.toApp.why')}</Banner>

        <div className={styles.section}>
          <span className={styles.label}>{t('photos.toApp.app')}</span>
          {apps.isPending ? (
            <Skeleton height={56} width="100%" />
          ) : sharing.length === 0 ? (
            <EmptyState icon={<AppsRegular />} title={t('photos.toApp.noApps.title')} body={t('photos.toApp.noApps.body')} />
          ) : (
            <div className={styles.apps} data-testid="to-app-apps">
              <RadioCards
                label={t('photos.toApp.app')}
                value={appId ?? ''}
                onChange={setAppId}
                options={sharing.map((a) => ({
                  value: a.id,
                  label: (
                    <>
                      {a.name}{' '}
                      {SUGGESTED_APPS.includes(a.id) ? (
                        <Badge tone="accent" className="t-caption">
                          {t('photos.toApp.suggested')}
                        </Badge>
                      ) : null}
                    </>
                  ),
                  hint: t('photos.toApp.appHint', { app: a.name }),
                }))}
              />
            </div>
          )}
        </div>

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
          <Button icon={<AddRegular />} onClick={() => input.current?.click()} data-testid="to-app-choose">
            {t('photos.toApp.choose')}
          </Button>
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
          {files.length > 0 ? <span className={styles.hint}>{t('photos.toApp.total', { count: files.length, size: bytes(total) })}</span> : null}
        </div>

        {app ? <p className={styles.fine}>{t('photos.toApp.where', { app: app.name })}</p> : null}
      </div>
    </Dialog>
  );
}
