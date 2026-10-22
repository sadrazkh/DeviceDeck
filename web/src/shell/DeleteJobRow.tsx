// Deleting photos as a transfer (v0.4.1, deletejob): a delete's row in the Transfers drawer (its stage, a bar, Stop
// and Results), the results dialog, and what the page says when a delete starts and when it ends.

import { CheckmarkRegular, DeleteRegular, DismissRegular, ErrorCircleRegular, TextBulletListLtrRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { create } from 'zustand';
import type { DeviceSummary, TransferJob } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { Button } from '../components/Button';
import { Dialog } from '../components/Dialog';
import { LinearProgress, Tooltip } from '../components/ui';
import { useI18n, type I18n } from '../i18n';
import { PhotoDeleteResults } from '../screens/photos/PhotoDeleteResults';
import { toast } from '../state/toasts';
import { useUi } from '../state/ui';
import photoStyles from '../screens/photos/PhotoDelete.module.css';
import styles from './Drawer.module.css';

export const deleteJobKey = (jobId: string) => ['photos.deleteJob', jobId] as const;

/** The delete whose results are open (from the drawer or a toast). */
export const useDeleteResults = create<{ jobId: string | null; open(jobId: string): void; close(): void }>((set) => ({
  jobId: null,
  open: (jobId) => set({ jobId }),
  close: () => set({ jobId: null }),
}));

/** "Deleting 39 photos and videos from Sadal", in the page's language. */
export function deleteJobTitle(job: TransferJob, i18n: I18n, device: DeviceSummary | undefined): string {
  return device && job.totalItems > 0 ? i18n.t('deleteJob.title', { count: job.totalItems, device: device.name }) : job.title;
}

const notTried = (job: TransferJob) => Math.max(0, job.totalItems - job.doneItems - job.failedItems);

/** The line under the title: the stage while it runs, what happened once it ended. */
export function deleteJobLine(job: TransferJob, i18n: I18n, device: DeviceSummary | undefined): string {
  const { t, bytes } = i18n;
  const ios = device?.platform !== 'android';
  const stage = job.stage;
  const counts = () => {
    const parts: string[] = [];
    if (job.failedItems > 0) parts.push(t('deleteJob.line.failed', { count: job.failedItems }));
    if (notTried(job) > 0) parts.push(t('deleteJob.line.notTried', { count: notTried(job) }));
    return parts;
  };
  const deleted = () => t('deleteJob.line.deleted', { count: job.doneItems, size: bytes(job.doneBytes) });
  switch (job.state) {
    case 'queued':
    case 'waitingForDevice':
    case 'paused':
    case 'needsDecision':
      return t('deleteJob.stage.waiting');
    case 'running':
      if (!stage) return t('deleteJob.stage.starting');
      switch (stage.name) {
        case 'copying':
          return t('deleteJob.stage.copying', { done: stage.done, total: stage.total });
        case 'reading':
          return stage.total > 0
            ? t(ios ? 'deleteJob.stage.readingIphone' : 'deleteJob.stage.readingPhone', { done: stage.done, total: stage.total })
            : t(ios ? 'deleteJob.stage.readingIphoneStart' : 'deleteJob.stage.readingPhoneStart');
        case 'deleting':
          return t('deleteJob.stage.deleting', { done: stage.done, total: stage.total });
        default:
          return t(ios ? 'deleteJob.stage.checkingIphone' : 'deleteJob.stage.checkingPhone');
      }
    case 'done':
      return [job.doneItems > 0 ? deleted() : t('deleteJob.line.nothing'), ...counts()].join(' · ');
    case 'cancelled':
      return [t('deleteJob.line.stopped'), ...(job.doneItems > 0 ? [deleted()] : []), ...counts()].join(' · ');
    case 'failed':
      return job.doneItems > 0
        ? [deleted(), ...counts(), job.error?.message].filter(Boolean).join(' · ')
        : job.error?.message ?? t('deleteJob.line.nothing');
  }
}

/** A delete's row: its stage with a bar while it runs; Stop until it checks on the phone; Results once it ended. */
export function DeleteJobRow({ job, device, showDevice }: { job: TransferJob; device: DeviceSummary | undefined; showDevice: boolean }) {
  const i18n = useI18n();
  const { t } = i18n;
  const actions = useActions();
  const openResults = useDeleteResults((s) => s.open);
  const finished = job.state === 'done' || job.state === 'failed' || job.state === 'cancelled';
  const Icon = job.state === 'failed' ? ErrorCircleRegular : job.state === 'done' ? CheckmarkRegular : DeleteRegular;
  const tone = job.state === 'failed' ? 'danger' : job.state === 'done' ? 'success' : job.state === 'cancelled' ? 'neutral' : undefined;
  const stage = job.stage;
  // Checking only reads what went: it always runs to the end, so there is nothing to stop.
  const stoppable = !finished && stage?.name !== 'checking';
  const title = deleteJobTitle(job, i18n, device);
  const stopLabel = stage?.name === 'deleting' ? t('deleteJob.cancelBatch') : t('deleteJob.cancel');
  const line = deleteJobLine(job, i18n, device);

  return (
    <li className={styles.row} data-testid="transfer-row" data-kind="photosDelete" data-state={job.state} data-stage={stage?.name}>
      <span className={styles.rowIcon} data-tone={tone} aria-hidden>
        <Icon />
      </span>
      <span className={`${styles.rowTitle} ${styles.rowTitleWrap}`} title={title} data-testid="delete-job-title">
        {title}
      </span>
      <span className={styles.rowActions}>
        {finished ? (
          <Button size="small" icon={<TextBulletListLtrRegular />} onClick={() => openResults(job.id)} data-testid="delete-job-results">
            {t('deleteJob.results')}
          </Button>
        ) : null}
        {stoppable ? (
          <Tooltip content={stopLabel}>
            <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={stopLabel} onClick={() => void actions.transfer('cancel', job.id)} data-testid="delete-job-stop" />
          </Tooltip>
        ) : null}
      </span>
      <span className={styles.rowMeta}>
        {job.state === 'running' ? (
          <LinearProgress value={stage && stage.total > 0 ? stage.done / stage.total : null} label={title} />
        ) : null}
        <span className={`${styles.rowLine} ${job.state === 'failed' ? styles.rowLineError : ''}`} data-testid="delete-job-line">
          <span title={line}>{line}</span>
        </span>
        {showDevice && device ? (
          <span className={styles.rowLine}>
            <span>{t('transfers.otherDevice', { device: device.name })}</span>
          </span>
        ) : null}
      </span>
    </li>
  );
}

/** Every item of a delete and what happened to it (from the drawer's Results, or the toast at the end). */
export function DeleteResultsDialog() {
  const { t } = useI18n();
  const client = useClient();
  const jobId = useDeleteResults((s) => s.jobId);
  const close = useDeleteResults((s) => s.close);
  const status = useQuery({
    queryKey: deleteJobKey(jobId ?? ''),
    queryFn: () => client.call('photos.deleteJob', { jobId: jobId! }),
    enabled: jobId !== null,
  });
  if (!jobId) return null;
  const report = status.data;
  return (
    <Dialog
      open
      wide
      onOpenChange={(open) => (open ? undefined : close())}
      title={report?.state === 'cancelled' ? t('deleteJob.results.stopped') : t('photoDelete.result.title')}
      footer={
        <Button variant="accent" onClick={close} autoFocus data-testid="delete-results-done">
          {t('photoDelete.done')}
        </Button>
      }
    >
      {report ? (
        <div data-testid="delete-results" data-state={report.state}>
          {report.error?.code === 'interrupted' ? <p className={photoStyles.warn}>{t('deleteJob.results.restarted')}</p> : null}
          <PhotoDeleteResults items={report.items} deleted={report.deleted} freedBytes={report.freedBytes} goesTo={report.goesTo} copyIncomplete={report.copyIncomplete} />
        </div>
      ) : (
        <p>{status.isError ? (status.error as Error).message : t('deleteJob.results.loading')}</p>
      )}
    </Dialog>
  );
}

/** Right after a delete was queued: the dialog has closed; say where it went and show the drawer. */
export function announceDeleteStarted(i18n: I18n, started: { jobId: string | null; skipped: number }) {
  const { t } = i18n;
  if (!started.jobId) {
    toast({ tone: 'warning', title: t('deleteJob.toast.nothingLeft') });
    return;
  }
  toast({
    tone: 'info',
    title: t('deleteJob.toast.title'),
    body: [t('deleteJob.toast.body'), started.skipped > 0 ? t('deleteJob.toast.skipped', { count: started.skipped }) : ''].filter(Boolean).join(' '),
    duration: 4000,
  });
  useUi.getState().setDrawerOpen(true);
}

/** When a delete ends: what went, with its results a click away. */
export function announceDeleteFinished(i18n: I18n, job: TransferJob, device: DeviceSummary | undefined) {
  const { t, bytes } = i18n;
  const results = { label: t('deleteJob.results'), run: () => useDeleteResults.getState().open(job.id) };
  if (job.state === 'done') {
    toast({
      tone: job.failedItems > 0 ? 'warning' : 'success',
      title: job.doneItems > 0 ? t('deleteJob.line.deleted', { count: job.doneItems, size: bytes(job.doneBytes) }) : t('deleteJob.line.nothing'),
      action: results,
    });
  } else if (job.state === 'cancelled') {
    toast({
      tone: 'info',
      title: job.doneItems > 0 ? t('deleteJob.finished.stopped', { deleted: job.doneItems, notTried: notTried(job) }) : t('deleteJob.finished.stoppedNothing'),
      action: results,
    });
  } else if (job.state === 'failed') {
    toast({
      tone: 'danger',
      title: job.doneItems > 0 ? t('deleteJob.line.deleted', { count: job.doneItems, size: bytes(job.doneBytes) }) : t('deleteJob.finished.failed', { device: device?.name ?? '' }),
      body: job.error?.message,
      action: results,
    });
  }
}
