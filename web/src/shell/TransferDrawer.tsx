import {
  AppsRegular,
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  ArrowExportRegular,
  ArrowUploadRegular,
  BroomRegular,
  CheckmarkRegular,
  DismissRegular,
  ErrorCircleRegular,
  FolderOpenRegular,
  ImageMultipleRegular,
  PauseRegular,
  PlayRegular,
  QuestionCircleRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'framer-motion';
import { useEffect, useRef } from 'react';
import type { DeviceSummary, TransferJob, TransferKind } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { keys, useDevices, useTransfers } from '../app/queries';
import { Button } from '../components/Button';
import { EmptyState, LinearProgress, Tooltip } from '../components/ui';
import { useI18n, type I18n } from '../i18n';
import { GROUP_ORDER, groupTransfers, jobFraction, type TransferGroup } from '../state/transfers';
import { useUi } from '../state/ui';
import styles from './Drawer.module.css';
import { useConflictQueue } from './ConflictDialog';

const KIND_ICON: Record<TransferKind, typeof ArrowDownloadRegular> = {
  photosExport: ImageMultipleRegular,
  photosImport: ArrowUploadRegular,
  copyToPc: ArrowDownloadRegular,
  copyToDevice: ArrowUploadRegular,
  appInstall: AppsRegular,
  appExport: ArrowDownloadRegular,
  dragOut: ArrowExportRegular,
  photosBackup: ImageMultipleRegular,
  photosToApp: ArrowUploadRegular,
  localSend: ImageMultipleRegular,
  whatsAppMedia: ArrowDownloadRegular, // round 4, whatsapp session
};

const GROUP_LABEL: Record<TransferGroup, 'transfers.group.active' | 'transfers.group.queued' | 'transfers.group.done' | 'transfers.group.failed'> = {
  active: 'transfers.group.active',
  queued: 'transfers.group.queued',
  done: 'transfers.group.done',
  failed: 'transfers.group.failed',
};

export function progressLine(job: TransferJob, i18n: I18n, device: DeviceSummary | undefined): string {
  const { t, percent, speed, eta, n } = i18n;
  switch (job.state) {
    case 'running': {
      const parts = [percent(jobFraction(job))];
      if (job.bytesPerSecond) parts.push(speed(job.bytesPerSecond));
      if (job.etaSeconds !== null) parts.push(eta(job.etaSeconds));
      if (job.totalItems > 1) parts.push(t('transfers.items', { done: n(job.doneItems), total: n(job.totalItems) }));
      return parts.join(' · ');
    }
    case 'waitingForDevice':
      return `${percent(jobFraction(job))} · ${t('transfers.state.waitingForDevice', { device: device?.name ?? t('common.unknown') })}`;
    case 'paused':
      return `${percent(jobFraction(job))} · ${t('transfers.state.paused')}`;
    case 'needsDecision':
      return t('transfers.state.needsDecision');
    case 'queued':
      return t('transfers.state.queued');
    case 'done':
      return job.finishedAt ? `${t('transfers.state.done')} · ${i18n.date(job.finishedAt)}` : t('transfers.state.done');
    case 'cancelled':
      return t('transfers.state.cancelled');
    case 'failed':
      return job.error?.message ?? t('transfers.state.failed');
  }
}

function JobRow({ job, device, showDevice }: { job: TransferJob; device: DeviceSummary | undefined; showDevice: boolean }) {
  const i18n = useI18n();
  const { t } = i18n;
  const actions = useActions();
  const destination = useUi((s) => s.jobDestinations[job.id]);
  const conflicts = useConflictQueue();
  const Icon = job.state === 'failed' ? ErrorCircleRegular : job.state === 'done' ? CheckmarkRegular : KIND_ICON[job.kind];
  const tone = job.state === 'failed' ? 'danger' : job.state === 'done' ? 'success' : job.state === 'cancelled' ? 'neutral' : undefined;
  const fraction = jobFraction(job);
  const unfinished = !['done', 'failed', 'cancelled'].includes(job.state);

  return (
    <li className={styles.row} data-testid="transfer-row" data-state={job.state}>
      <span className={styles.rowIcon} data-tone={tone} aria-hidden>
        <Icon />
      </span>
      <span className={styles.rowTitle} title={job.title}>
        {job.title}
      </span>
      <span className={styles.rowActions}>
        {job.state === 'running' || job.state === 'queued' ? (
          <Tooltip content={t('transfers.pause')}>
            <Button variant="subtle" size="small" icon={<PauseRegular />} aria-label={t('transfers.pause')} onClick={() => void actions.transfer('pause', job.id)} />
          </Tooltip>
        ) : null}
        {job.state === 'paused' ? (
          <Tooltip content={t('transfers.resume')}>
            <Button variant="subtle" size="small" icon={<PlayRegular />} aria-label={t('transfers.resume')} onClick={() => void actions.transfer('resume', job.id)} />
          </Tooltip>
        ) : null}
        {job.state === 'needsDecision' ? (
          <Button size="small" icon={<QuestionCircleRegular />} onClick={() => conflicts.open(job.id)}>
            {t('transfers.decide')}
          </Button>
        ) : null}
        {/* A drag to File Explorer can't be retried: nothing waits for its bytes once it ended. */}
        {(job.state === 'failed' || job.state === 'cancelled') && job.kind !== 'dragOut' ? (
          <Button size="small" icon={<ArrowClockwiseRegular />} onClick={() => void actions.transfer('retry', job.id)} data-testid="transfer-retry">
            {t('transfers.retry')}
          </Button>
        ) : null}
        {job.state === 'done' && destination ? (
          <Tooltip content={t('common.showInExplorer')}>
            <Button variant="subtle" size="small" icon={<FolderOpenRegular />} aria-label={t('common.showInExplorer')} onClick={() => void actions.revealInExplorer(destination)} />
          </Tooltip>
        ) : null}
        {unfinished ? (
          <Tooltip content={t('transfers.cancel')}>
            <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={t('transfers.cancel')} onClick={() => void actions.transfer('cancel', job.id)} />
          </Tooltip>
        ) : null}
      </span>
      <span className={styles.rowMeta}>
        {unfinished && job.state !== 'queued' ? (
          <LinearProgress
            value={fraction}
            tone={job.state === 'running' ? 'accent' : 'paused'}
            label={job.title}
          />
        ) : null}
        <span
          className={`${styles.rowLine} ${job.state === 'failed' ? styles.rowLineError : job.state === 'waitingForDevice' || job.state === 'needsDecision' ? styles.rowLineWarning : ''}`}
        >
          <span>{progressLine(job, i18n, device)}</span>
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

export function TransferDrawer() {
  const { t, n } = useI18n();
  const open = useUi((s) => s.drawerOpen);
  const setOpen = useUi((s) => s.setDrawerOpen);
  const jobs = useTransfers().data ?? [];
  const devices = useDevices().data ?? [];
  const groups = groupTransfers(jobs);
  const multiDevice = new Set(jobs.map((j) => j.deviceId)).size > 1;
  const panel = useRef<HTMLElement>(null);
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const finished = jobs.filter((j) => j.state === 'done' || j.state === 'failed' || j.state === 'cancelled');
  const clearFinished = async () => {
    try {
      await client.call('transfers.clear', { jobIds: finished.map((j) => j.id) });
      const gone = new Set(finished.map((j) => j.id));
      queryClient.setQueryData<TransferJob[]>(keys.transfers, (list) => list?.filter((j) => !gone.has(j.id)));
    } catch (error) {
      actions.fail(error);
    }
  };

  useEffect(() => {
    if (!open) return;
    panel.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, setOpen]);

  return (
    <AnimatePresence>
      {open ? (
        <>
          <div className={styles.scrim} onClick={() => setOpen(false)} aria-hidden key="scrim" />
          <motion.aside
            key="drawer"
            ref={panel}
            id="transfer-drawer"
            className={styles.drawer}
            role="dialog"
            aria-modal="false"
            aria-label={t('transfers.title')}
            tabIndex={-1}
            initial={{ opacity: 0, x: 24 * (document.documentElement.dir === 'rtl' ? -1 : 1) }}
            animate={{ opacity: 1, x: 0 }}
            exit={{ opacity: 0, x: 24 * (document.documentElement.dir === 'rtl' ? -1 : 1) }}
            transition={{ duration: 0.32, ease: [0.2, 0, 0, 1] }}
            data-testid="transfer-drawer"
          >
            <div className={styles.pane}>
              <div className={styles.header}>
                <h2 className={styles.title}>{t('transfers.title')}</h2>
                {finished.length > 0 ? (
                  <Tooltip content={t('transfers.clearFinished')}>
                    <Button variant="subtle" icon={<BroomRegular />} aria-label={t('transfers.clearFinished')} onClick={() => void clearFinished()} data-testid="transfers-clear" />
                  </Tooltip>
                ) : null}
                <Button variant="subtle" icon={<DismissRegular />} aria-label={t('common.close')} onClick={() => setOpen(false)} />
              </div>
              <div className={styles.list}>
                {jobs.length === 0 ? (
                  <div className={styles.empty}>
                    <EmptyState icon={<ArrowDownloadRegular />} title={t('transfers.empty.title')} body={t('transfers.empty.body')} />
                  </div>
                ) : (
                  GROUP_ORDER.filter((g) => groups[g].length > 0).map((group) => (
                    <section key={group} className={styles.group} aria-label={t(GROUP_LABEL[group])}>
                      <h3 className={styles.groupTitle}>
                        {t(GROUP_LABEL[group])} <span className={`${styles.count} num`}>{n(groups[group].length)}</span>
                      </h3>
                      <ul className={styles.jobs}>
                        {groups[group].map((job) => (
                          <JobRow key={job.id} job={job} device={devices.find((d) => d.id === job.deviceId)} showDevice={multiDevice} />
                        ))}
                      </ul>
                    </section>
                  ))
                )}
              </div>
            </div>
          </motion.aside>
        </>
      ) : null}
    </AnimatePresence>
  );
}
