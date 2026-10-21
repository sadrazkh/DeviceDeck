import { AppsRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import type { PhotosToAppStarted } from '../../bridge/localSend';
import type { AppInfo, TransferJob } from '../../bridge/types';
import { cx } from '../../components/cx';
import { Badge, EmptyState, LinearProgress, RadioCards, Skeleton, Spinner } from '../../components/ui';
import { useI18n } from '../../i18n';
import { jobFraction } from '../../state/transfers';
import { SUGGESTED_APPS } from './backupQueries';
import styles from './LocalSend.module.css';
import backupStyles from './Backup.module.css';

/** The parts a send of `count` files fills, as the host makes them: the folder itself, or "folder/1-200", "folder/201-400", …. */
export function partsFor(folder: string, count: number): { path: string; count: number }[] {
  if (count <= 200) return [{ path: folder, count }];
  const parts: { path: string; count: number }[] = [];
  for (let start = 0; start < count; start += 200) {
    const size = Math.min(200, count - start);
    parts.push({ path: `${folder}/${start + 1}-${start + size}`, count: size });
  }
  return parts;
}

/** Today's folder name, before the host has picked it ("DeviceDeck 2026-10-21"; it adds " (2)" when that's taken). */
export function todaysFolder(now = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `DeviceDeck ${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

const MAX_PATHS = 6;

/** Files › On My iPhone › app › folder › Select › Select All › Share › Save N Images, for each folder. */
function SaveAllSteps({ app, parts }: { app: string; parts: { path: string; count: number }[] }) {
  const { t } = useI18n();
  const shown = parts.slice(0, MAX_PATHS);
  return (
    <div className={backupStyles.section} data-testid="to-app-save-all">
      <span className={backupStyles.label}>{t('photos.toApp.saveAll.title')}</span>
      <ol className={styles.steps}>
        <li>
          {t('photos.toApp.saveAll.open')}
          {shown.map((p) => (
            <div key={p.path} className={styles.path} data-testid="to-app-path">
              {['Files', 'On My iPhone', app, ...p.path.split('/')].join(' › ')}
            </div>
          ))}
          {parts.length > MAX_PATHS ? <div className={styles.path}>…</div> : null}
        </li>
        <li>{t('photos.toApp.saveAll.select')}</li>
        <li>{t('photos.toApp.saveAll.share', { count: parts[0]?.count ?? 0 })}</li>
      </ol>
      {parts.length > 1 ? <p className={backupStyles.fine}>{t('photos.toApp.saveAll.parts', { first: parts[0]!.path.split('/').at(-1) ?? '' })}</p> : null}
      <p className={backupStyles.fine}>{t('photos.toApp.saveAll.notInFiles', { app })}</p>
    </div>
  );
}

/** Before sending: the app to put the photos in, and how they'll be saved all at once afterwards. */
export function IntoAppPanel({
  loading,
  sharing,
  appId,
  onAppId,
  count,
}: {
  loading: boolean;
  sharing: AppInfo[];
  appId: string | null;
  onAppId(appId: string): void;
  count: number;
}) {
  const { t } = useI18n();
  const app = sharing.find((a) => a.id === appId);
  return (
    <>
      <div className={backupStyles.section}>
        <span className={backupStyles.label}>{t('photos.toApp.app')}</span>
        {loading ? (
          <Skeleton height={56} width="100%" />
        ) : sharing.length === 0 ? (
          <EmptyState icon={<AppsRegular />} title={t('photos.toApp.noApps.title')} body={t('photos.toApp.noApps.body')} />
        ) : (
          <div className={cx(backupStyles.apps, styles.stretch)} data-testid="to-app-apps">
            <RadioCards
              label={t('photos.toApp.app')}
              value={appId ?? ''}
              onChange={onAppId}
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
              }))}
            />
          </div>
        )}
      </div>
      {app ? <SaveAllSteps app={app.name} parts={partsFor(todaysFolder(), Math.max(count, 1))} /> : null}
      <p className={backupStyles.fine}>{t('photos.toApp.why')}</p>
    </>
  );
}

/** After sending into an app: where the files go, the copy's progress, and the save-them-all steps with the real folders. */
export function IntoAppSent({ started, appName, job }: { started: PhotosToAppStarted; appName: string; job: TransferJob | null }) {
  const { t, n, percent } = useI18n();
  const done = job?.state === 'done';
  const fraction = job ? jobFraction(job) : 0;
  return (
    <div className={backupStyles.body} data-testid="to-app-sent">
      <div className={cx(styles.status, done && styles.found)} role="status" aria-live="polite">
        <span className={styles.statusIcon} aria-hidden>
          {done ? <CheckmarkCircleRegular /> : <Spinner />}
        </span>
        <span className={styles.statusText}>
          <span className={styles.statusTitle}>{t('photos.toApp.sent.title', { app: appName })}</span>
          {job ? (
            <span className="num">
              {percent(fraction)} · {t('transfers.items', { done: n(job.doneItems), total: n(job.totalItems) })}
            </span>
          ) : null}
        </span>
      </div>
      {job && !done ? <LinearProgress value={fraction} label={t('photos.toApp.sent.title', { app: appName })} /> : null}
      <SaveAllSteps app={appName} parts={started.parts} />
    </div>
  );
}
