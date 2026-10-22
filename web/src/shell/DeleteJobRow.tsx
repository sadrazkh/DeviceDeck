// Deleting photos as a transfer (v0.4.1, deletejob): a delete's row in the Transfers drawer (its stage, a bar, Stop
// and Results), the results dialog, and what the page says when a delete starts and when it ends. v0.6: files,
// folders and apps are deleted the same way (filesDelete, appsUninstall), with their results from transfers.items.

import { CheckmarkCircleRegular, CheckmarkRegular, DeleteRegular, DismissCircleRegular, DismissRegular, ErrorCircleRegular, SubtractCircleRegular, TextBulletListLtrRegular, WarningRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import { create } from 'zustand';
import type { DeleteKind, TransferItemState, TransferItemView } from '../bridge/deleteTypes';
import type { DeviceSummary, TransferJob } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useTransfers } from '../app/queries';
import { Button } from '../components/Button';
import { cx } from '../components/cx';
import { Dialog } from '../components/Dialog';
import { LinearProgress, Tooltip } from '../components/ui';
import { useI18n, type I18n, type MessageKey } from '../i18n';
import { PhotoDeleteResults } from '../screens/photos/PhotoDeleteResults';
import { toast } from '../state/toasts';
import { useUi } from '../state/ui';
import photoStyles from '../screens/photos/PhotoDelete.module.css';
import styles from './Drawer.module.css';

export const deleteJobKey = (jobId: string) => ['photos.deleteJob', jobId] as const;
export const deleteItemsKey = (jobId: string) => ['transfers.items', jobId] as const;

/** The delete whose results are open (from the drawer or a toast). */
export const useDeleteResults = create<{ jobId: string | null; kind: DeleteKind; open(jobId: string, kind?: DeleteKind): void; close(): void }>((set) => ({
  jobId: null,
  kind: 'photosDelete',
  open: (jobId, kind = 'photosDelete') => set({ jobId, kind }),
  close: () => set({ jobId: null }),
}));

const removes = (job: Pick<TransferJob, 'kind'>) => job.kind === 'appsUninstall';

/** "Deleting 39 photos and videos from Sadal" (or items, or apps), in the page's language. */
export function deleteJobTitle(job: TransferJob, i18n: I18n, device: DeviceSummary | undefined): string {
  if (!device || job.totalItems === 0) return job.title;
  const key: MessageKey = job.kind === 'filesDelete' ? 'deleteJob.files.title' : job.kind === 'appsUninstall' ? 'deleteJob.apps.title' : 'deleteJob.title';
  return i18n.t(key, { count: job.totalItems, device: device.name });
}

const skippedOf = (job: TransferJob) => job.skippedItems ?? 0;
const notTried = (job: TransferJob) => Math.max(0, job.totalItems - job.doneItems - job.failedItems - skippedOf(job));

/** The line under the title: the stage while it runs, what happened once it ended. */
export function deleteJobLine(job: TransferJob, i18n: I18n, device: DeviceSummary | undefined): string {
  const { t, bytes } = i18n;
  const ios = device?.platform !== 'android';
  const apps = removes(job);
  const stage = job.stage;
  const counts = () => {
    const parts: string[] = [];
    if (job.failedItems > 0) parts.push(t(apps ? 'deleteJob.line.notRemoved' : 'deleteJob.line.failed', { count: job.failedItems }));
    if (skippedOf(job) > 0) parts.push(t('deleteJob.line.skipped', { count: skippedOf(job) }));
    if (notTried(job) > 0) parts.push(t('deleteJob.line.notTried', { count: notTried(job) }));
    return parts;
  };
  const deleted = () => t(apps ? 'deleteJob.line.removed' : 'deleteJob.line.deleted', { count: job.doneItems, size: bytes(job.doneBytes) });
  const nothing = () => t(apps ? 'deleteJob.line.nothingRemoved' : 'deleteJob.line.nothing');
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
          if (job.kind !== 'photosDelete') return t('deleteJob.stage.readingChanges');
          return stage.total > 0
            ? t(ios ? 'deleteJob.stage.readingIphone' : 'deleteJob.stage.readingPhone', { done: stage.done, total: stage.total })
            : t(ios ? 'deleteJob.stage.readingIphoneStart' : 'deleteJob.stage.readingPhoneStart');
        case 'deleting':
          return t('deleteJob.stage.deleting', { done: stage.done, total: stage.total });
        case 'removing':
          return t('deleteJob.stage.removing', { done: stage.done, total: stage.total });
        default:
          return t(ios ? 'deleteJob.stage.checkingIphone' : 'deleteJob.stage.checkingPhone');
      }
    case 'done':
      return [job.doneItems > 0 ? deleted() : nothing(), ...counts()].join(' · ');
    case 'cancelled':
      return [t('deleteJob.line.stopped'), ...(job.doneItems > 0 ? [deleted()] : []), ...counts()].join(' · ');
    case 'failed':
      return job.doneItems > 0 ? [deleted(), ...counts(), job.error?.message].filter(Boolean).join(' · ') : job.error?.message ?? nothing();
  }
}

/** What Stop says: a photo batch on its way still goes; a file or an app on its way finishes first. */
function stopLabel(job: TransferJob, t: I18n['t']): string {
  const stage = job.stage?.name;
  if (job.kind === 'photosDelete') return stage === 'deleting' ? t('deleteJob.cancelBatch') : t('deleteJob.cancel');
  if (stage === 'deleting' || stage === 'removing') return t('deleteJob.cancelOne');
  return t(removes(job) ? 'deleteJob.cancelRemove' : 'deleteJob.cancel');
}

/** A delete's row: its stage with a bar while it runs; Stop until it checks on the phone; Results once it ended. */
export function DeleteJobRow({ job, device, showDevice }: { job: TransferJob & { kind: DeleteKind }; device: DeviceSummary | undefined; showDevice: boolean }) {
  const i18n = useI18n();
  const { t } = i18n;
  const actions = useActions();
  const openResults = useDeleteResults((s) => s.open);
  const finished = job.state === 'done' || job.state === 'failed' || job.state === 'cancelled';
  const Icon = job.state === 'failed' ? ErrorCircleRegular : job.state === 'done' ? CheckmarkRegular : DeleteRegular;
  const tone = job.state === 'failed' ? 'danger' : job.state === 'done' ? 'success' : job.state === 'cancelled' ? 'neutral' : undefined;
  const stage = job.stage;
  // A photo delete's check reads only what went: it always runs to the end, so there is nothing to stop.
  const stoppable = !finished && !(job.kind === 'photosDelete' && stage?.name === 'checking');
  const title = deleteJobTitle(job, i18n, device);
  const stop = stopLabel(job, t);
  const line = deleteJobLine(job, i18n, device);

  return (
    <li className={styles.row} data-testid="transfer-row" data-kind={job.kind} data-state={job.state} data-stage={stage?.name}>
      <span className={styles.rowIcon} data-tone={tone} aria-hidden>
        <Icon />
      </span>
      <span className={`${styles.rowTitle} ${styles.rowTitleWrap}`} title={title} data-testid="delete-job-title">
        {title}
      </span>
      <span className={styles.rowActions}>
        {finished ? (
          <Button size="small" icon={<TextBulletListLtrRegular />} onClick={() => openResults(job.id, job.kind)} data-testid="delete-job-results">
            {t('deleteJob.results')}
          </Button>
        ) : null}
        {stoppable ? (
          <Tooltip content={stop}>
            <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={stop} onClick={() => void actions.transfer('cancel', job.id)} data-testid="delete-job-stop" />
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
  const jobId = useDeleteResults((s) => s.jobId);
  const kind = useDeleteResults((s) => s.kind);
  if (!jobId) return null;
  return kind === 'photosDelete' ? <PhotoResultsDialog jobId={jobId} /> : <ItemResultsDialog jobId={jobId} kind={kind} />;
}

function PhotoResultsDialog({ jobId }: { jobId: string }) {
  const { t } = useI18n();
  const client = useClient();
  const close = useDeleteResults((s) => s.close);
  const status = useQuery({ queryKey: deleteJobKey(jobId), queryFn: () => client.call('photos.deleteJob', { jobId }) });
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

/** How an item's state looks: the colour and icon it borrows from the photo results. */
const ITEM: Record<TransferItemState, { tone: 'deleted' | 'mismatch' | 'failed' | 'notTried'; icon: ReactElement }> = {
  done: { tone: 'deleted', icon: <CheckmarkCircleRegular /> },
  skipped: { tone: 'mismatch', icon: <WarningRegular /> },
  failed: { tone: 'failed', icon: <DismissCircleRegular /> },
  queued: { tone: 'notTried', icon: <SubtractCircleRegular /> },
  running: { tone: 'notTried', icon: <SubtractCircleRegular /> },
};

function itemLabel(item: TransferItemView, apps: boolean): MessageKey {
  switch (item.state) {
    case 'done':
      return apps ? 'deleteJob.item.removed' : 'deleteJob.item.done';
    case 'failed':
      return apps ? 'deleteJob.item.notRemoved' : 'deleteJob.item.failed';
    case 'skipped':
      return 'deleteJob.item.skipped';
    case 'running':
      return 'deleteJob.item.running';
    default:
      return 'deleteJob.item.notTried';
  }
}

/** Why an item didn't go, in the page's language (the host's sentence stays below it as a detail). */
function itemReason(item: TransferItemView, apps: boolean): MessageKey | null {
  switch (item.state) {
    case 'skipped':
      if (item.code === 'missing') return apps ? 'deleteJob.reason.appGone' : 'deleteJob.reason.missing';
      return 'deleteJob.reason.changed';
    case 'queued':
      return 'deleteJob.reason.notTried';
    case 'failed':
      switch (item.code) {
        case 'interrupted':
          return 'deleteJob.reason.interrupted';
        case 'permission_denied':
          return 'deleteJob.reason.refused';
        case 'path_not_found':
          return 'deleteJob.reason.missing';
        case 'device_disconnected':
          return 'deleteJob.reason.disconnected';
        default:
          return 'deleteJob.reason.failed';
      }
    default:
      return null;
  }
}

function ItemResultsDialog({ jobId, kind }: { jobId: string; kind: DeleteKind }) {
  const { t, bytes } = useI18n();
  const client = useClient();
  const close = useDeleteResults((s) => s.close);
  const job = useTransfers().data?.find((j) => j.id === jobId);
  const items = useQuery({ queryKey: deleteItemsKey(jobId), queryFn: () => client.call('transfers.items', { jobId }) });
  const apps = kind === 'appsUninstall';
  const stopped = job?.state === 'cancelled';
  // Problems first, so a long list still shows what needs a look.
  const order: TransferItemState[] = ['failed', 'skipped', 'running', 'queued', 'done'];
  const sorted = [...(items.data ?? [])].sort((a, b) => order.indexOf(a.state) - order.indexOf(b.state) || a.index - b.index);
  const gone = (items.data ?? []).filter((i) => i.state === 'done');
  const freed = gone.reduce((n, i) => n + i.size, 0);
  return (
    <Dialog
      open
      wide
      onOpenChange={(open) => (open ? undefined : close())}
      title={stopped ? t(apps ? 'deleteJob.results.removeStopped' : 'deleteJob.results.stopped') : t(apps ? 'deleteJob.results.removeTitle' : 'photoDelete.result.title')}
      footer={
        <Button variant="accent" onClick={close} autoFocus data-testid="delete-results-done">
          {t('photoDelete.done')}
        </Button>
      }
    >
      {items.data ? (
        <div className={photoStyles.body} data-testid="delete-results" data-state={job?.state} data-kind={kind}>
          {job?.error?.code === 'interrupted' ? <p className={photoStyles.warn}>{t('deleteJob.results.restarted')}</p> : null}
          <p className={photoStyles.summary} data-testid="delete-results-summary">
            {gone.length > 0
              ? t(apps ? 'deleteJob.results.summaryRemoved' : 'photoDelete.result.summary', { count: gone.length, size: bytes(freed) })
              : t(apps ? 'deleteJob.results.noneRemoved' : 'photoDelete.result.none')}
          </p>
          {job?.error?.code === 'copy_incomplete' ? <p className={photoStyles.warn}>{t('photoDelete.result.copyIncomplete')}</p> : null}
          <ul className={photoStyles.list} aria-label={t('photoDelete.result.title')}>
            {sorted.map((item) => {
              const look = ITEM[item.state];
              const why = itemReason(item, apps);
              return (
                <li key={item.index} className={photoStyles.row} data-testid="delete-result" data-state={item.state}>
                  <span className={cx(photoStyles.icon, photoStyles[look.tone])} aria-hidden>
                    {look.icon}
                  </span>
                  <span className={photoStyles.text}>
                    <span className={photoStyles.name}>
                      {item.name} · <span className={photoStyles[look.tone]}>{t(itemLabel(item, apps))}</span>
                    </span>
                    {why ? <span className={photoStyles.detail}>{t(why)}</span> : null}
                    {item.message && item.state !== 'done' ? (
                      <span className={photoStyles.detail} dir="auto">
                        {item.message}
                      </span>
                    ) : null}
                  </span>
                  <span className={photoStyles.size}>{item.size > 0 ? bytes(item.size) : ''}</span>
                </li>
              );
            })}
          </ul>
        </div>
      ) : (
        <p>{items.isError ? (items.error as Error).message : t('deleteJob.results.loading')}</p>
      )}
    </Dialog>
  );
}

/** Right after a delete was queued: the dialog has closed; say where it went and show the drawer. */
export function announceDeleteStarted(i18n: I18n, started: { jobId: string | null; skipped?: number }, kind: DeleteKind = 'photosDelete') {
  const { t } = i18n;
  if (!started.jobId) {
    toast({ tone: 'warning', title: t('deleteJob.toast.nothingLeft') });
    return;
  }
  const skipped = started.skipped ?? 0;
  toast({
    tone: 'info',
    title: t(kind === 'appsUninstall' ? 'deleteJob.toast.removeTitle' : 'deleteJob.toast.title'),
    body: [t('deleteJob.toast.body'), skipped > 0 ? t('deleteJob.toast.skipped', { count: skipped }) : ''].filter(Boolean).join(' '),
    duration: 4000,
  });
  useUi.getState().setDrawerOpen(true);
}

/** When a delete ends: what went, with its results a click away. */
export function announceDeleteFinished(i18n: I18n, job: TransferJob & { kind: DeleteKind }, device: DeviceSummary | undefined) {
  const { t, bytes } = i18n;
  const apps = removes(job);
  const results = { label: t('deleteJob.results'), run: () => useDeleteResults.getState().open(job.id, job.kind) };
  const deleted = () => t(apps ? 'deleteJob.line.removed' : 'deleteJob.line.deleted', { count: job.doneItems, size: bytes(job.doneBytes) });
  if (job.state === 'done') {
    toast({
      tone: job.failedItems > 0 ? 'warning' : 'success',
      title: job.doneItems > 0 ? deleted() : t(apps ? 'deleteJob.line.nothingRemoved' : 'deleteJob.line.nothing'),
      action: results,
    });
  } else if (job.state === 'cancelled') {
    toast({
      tone: 'info',
      title:
        job.doneItems > 0
          ? t(apps ? 'deleteJob.finished.removeStopped' : 'deleteJob.finished.stopped', { deleted: job.doneItems, notTried: notTried(job) })
          : t(apps ? 'deleteJob.finished.removeStoppedNothing' : 'deleteJob.finished.stoppedNothing'),
      action: results,
    });
  } else if (job.state === 'failed') {
    toast({
      tone: 'danger',
      title: job.doneItems > 0 ? deleted() : t(apps ? 'deleteJob.finished.removeFailed' : 'deleteJob.finished.failed', { device: device?.name ?? '' }),
      body: job.error?.message,
      action: results,
    });
  }
}
