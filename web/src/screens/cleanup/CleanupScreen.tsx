import { ArrowClockwiseRegular, BroomRegular, SearchRegular, StopRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { CleanupReport, CleanupStatus } from '../../bridge/cleanupTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, EmptyState, LinearProgress, Skeleton } from '../../components/ui';
import { useI18n, type I18n, type MessageKey } from '../../i18n';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Cleanup.module.css';
import { ConfirmCleanup, type CleanupPlan } from './ConfirmCleanup';
import { AppFilesCard, AppsCard, CachesCard, DuplicatesCard, MediaCard, type SectionProps } from './Sections';
import { useCleanupActions, useCleanupStatus } from './useCleanup';

function Tile({ label, value, sub, testId }: { label: string; value: string; sub?: string; testId?: string }) {
  return (
    <div className={styles.tile} data-testid={testId}>
      <span className={styles.tileLabel}>{label}</span>
      <span className={styles.tileValue}>{value}</span>
      {sub ? <span className={styles.tileSub}>{sub}</span> : null}
    </div>
  );
}

function Tiles({ report, i18n }: { report: CleanupReport; i18n: I18n }) {
  const { t, bytes } = i18n;
  const extras = report.duplicates ? report.duplicates.groups.reduce((n, g) => n + g.assets.length - 1, 0) : 0;
  return (
    <div className={styles.tiles} data-testid="cleanup-tiles">
      {report.freeBytes !== null ? (
        <Tile
          label={t('cleanup.tile.free')}
          value={bytes(report.freeBytes)}
          sub={report.totalBytes !== null ? t('cleanup.tile.ofTotal', { total: bytes(report.totalBytes, 0) }) : undefined}
          testId="cleanup-tile-free"
        />
      ) : null}
      {report.duplicates ? (
        <Tile label={t('cleanup.tile.duplicates')} value={bytes(report.duplicates.reclaimableBytes)} sub={t('cleanup.tile.copies', { count: extras })} testId="cleanup-tile-duplicates" />
      ) : null}
      {report.media ? (
        <Tile label={t('cleanup.tile.videos')} value={bytes(report.media.videoBytes)} sub={t('cleanup.tile.items', { count: report.media.videoCount })} />
      ) : null}
      {report.appFiles ? (
        <Tile label={t('cleanup.tile.appFiles')} value={bytes(report.appFiles.totalBytes)} sub={t('cleanup.tile.files', { count: report.appFiles.files.length })} />
      ) : null}
      {report.caches ? (
        <Tile
          label={t('cleanup.tile.caches')}
          value={report.caches.totalBytes !== null ? bytes(report.caches.totalBytes) : '—'}
          sub={report.caches.totalBytes === null ? t('cleanup.tile.notCounted') : undefined}
        />
      ) : null}
    </div>
  );
}

function Progress({ status, i18n }: { status: CleanupStatus; i18n: I18n }) {
  const { t, bytes } = i18n;
  const progress = status.progress;
  if (!progress) return null;
  const section = t(`cleanup.section.${progress.section}` as MessageKey);
  const fraction = progress.total > 0 ? progress.done / progress.total : null;
  return (
    <div className={styles.progress} data-testid="cleanup-progress">
      <div className={styles.progressLine}>
        <span>{t('cleanup.running', { section })}</span>
        {progress.section === 'duplicates' && progress.total > 0 ? (
          <span>{t('cleanup.runningBytes', { done: bytes(progress.done), total: bytes(progress.total) })}</span>
        ) : null}
      </div>
      <LinearProgress value={fraction} label={t('cleanup.running', { section })} />
    </div>
  );
}

export function CleanupScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const status = useCleanupStatus(device);
  const actions = useCleanupActions(device);
  const settings = useSettings().data;
  const [plan, setPlan] = useState<CleanupPlan | null>(null);

  const current = status.data;
  const report = current?.report ?? null;
  const running = current?.state === 'running';
  const android = device.platform === 'android';

  const section: SectionProps = {
    device,
    confirm: setPlan,
    folders: { photos: settings?.photoExportFolder ?? '', files: settings?.fileCopyFolder ?? '' },
    copyMedia: (ids) => void actions.copyMedia(ids, settings?.photoExportFolder ?? '', settings?.photoFolderPattern ?? 'yyyy/MM'),
    locked: running,
  };

  const subtitle =
    report?.freeBytes != null && report.totalBytes != null
      ? t('cleanup.subtitle', { free: bytes(report.freeBytes), total: bytes(report.totalBytes, 0) })
      : t('cleanup.subtitleIdle', { device: device.name });

  const headerActions = running ? (
    <Button icon={<StopRegular />} onClick={() => void actions.cancel()} data-testid="cleanup-stop">
      {t('cleanup.stop')}
    </Button>
  ) : (
    <Button variant={report ? 'standard' : 'accent'} icon={report ? <ArrowClockwiseRegular /> : <SearchRegular />} onClick={() => void actions.analyze()} data-testid="cleanup-analyze">
      {report ? t('cleanup.analyzeAgain') : t('cleanup.analyze')}
    </Button>
  );

  const hasSections = !!report && !!(report.apps || report.media || report.duplicates || report.appFiles || report.caches);

  return (
    <ScreenLayout
      title={t('cleanup.title')}
      subtitle={subtitle}
      actions={headerActions}
      testId="screen-cleanup"
      banners={<p className={styles.promise}>{t('cleanup.promise')}</p>}
    >
      <div className={styles.stack} data-state={current?.state ?? 'loading'} data-testid="cleanup-body">
        {status.isPending ? (
          <Skeleton height={120} />
        ) : status.error ? (
          <ErrorBanner error={status.error} onRetry={() => void status.refetch()} />
        ) : !report || (!hasSections && !running) ? (
          <div className={styles.idle}>
            <EmptyState
              icon={<BroomRegular />}
              title={t('cleanup.idle.title', { device: device.name })}
              body={t(android ? 'cleanup.idle.bodyAndroid' : 'cleanup.idle.body')}
              action={
                running ? null : (
                  <Button variant="accent" icon={<SearchRegular />} onClick={() => void actions.analyze()}>
                    {t('cleanup.analyze')}
                  </Button>
                )
              }
            />
          </div>
        ) : null}

        {current && running ? <Progress status={current} i18n={i18n} /> : null}
        {current?.state === 'cancelled' ? <Banner tone="info">{t('cleanup.cancelled')}</Banner> : null}
        {current?.state === 'failed' && current.error ? (
          <Banner tone="danger" title={t('cleanup.failed')} details={current.error.code}>
            {current.error.message}
          </Banner>
        ) : null}

        {report && hasSections ? (
          <>
            <Tiles report={report} i18n={i18n} />
            {report.notes.map((note) => (
              <Banner key={`${note.section}:${note.message}`} tone="warning" details={note.code}>
                {note.message}
              </Banner>
            ))}
            {report.media && !report.media.canDelete ? (
              <Banner tone="info" title={t('cleanup.media.iphoneTitle')}>
                {t('cleanup.media.iphoneBody')}
              </Banner>
            ) : null}
            {report.duplicates ? <DuplicatesCard {...section} data={report.duplicates} /> : null}
            {report.media ? <MediaCard {...section} data={report.media} kind="videos" /> : null}
            {report.apps ? <AppsCard {...section} data={report.apps} /> : null}
            {report.appFiles ? <AppFilesCard {...section} data={report.appFiles} /> : null}
            {report.caches ? <CachesCard {...section} data={report.caches} /> : null}
            {report.media ? <MediaCard {...section} data={report.media} kind="photos" /> : null}
            {current?.finishedAt && !running ? <p className={styles.hint}>{t('cleanup.analysedAt', { when: i18n.date(current.finishedAt) })}</p> : null}
          </>
        ) : null}
      </div>

      <ConfirmCleanup plan={plan} device={device} onClose={() => setPlan(null)} run={actions.run} />
    </ScreenLayout>
  );
}
