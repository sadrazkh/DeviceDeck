import { PauseRegular, PlayRegular } from '@fluentui/react-icons';
import { useMemo } from 'react';
import type { DeviceSummary, TransferJob, TransferKind } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { Banner, LinearProgress, Spinner } from '../../components/ui';
import { useI18n } from '../../i18n';
import { isFinished, jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import styles from './Backup.module.css';

const PHOTO_KINDS: TransferKind[] = ['photosBackup', 'photosImport', 'photosToApp'];

/** The phone's newest unfinished backup or photo import, for progress right on the Photos screen. */
export function useActivePhotoJob(deviceId: string): TransferJob | undefined {
  const transfers = useTransfers();
  return useMemo(
    () => (transfers.data ?? []).filter((j) => j.deviceId === deviceId && PHOTO_KINDS.includes(j.kind) && !isFinished(j.state)).at(-1),
    [transfers.data, deviceId],
  );
}

export function PhotoJobBanner({ job, device }: { job: TransferJob; device: DeviceSummary }) {
  const { t, n, percent } = useI18n();
  const actions = useActions();
  const fraction = jobFraction(job);
  const detail = [
    percent(fraction),
    job.totalItems > 0 ? t('transfers.items', { done: n(job.doneItems), total: n(job.totalItems) }) : null,
    job.state === 'paused' ? t('transfers.state.paused') : null,
    job.state === 'waitingForDevice' ? t('transfers.state.waitingForDevice', { device: device.name }) : null,
    job.state === 'queued' ? t('transfers.state.queued') : null,
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <Banner
      icon={job.state === 'running' ? <Spinner /> : undefined}
      title={job.title}
      actions={
        <>
          {job.state === 'paused' ? (
            <Button icon={<PlayRegular />} onClick={() => void actions.transfer('resume', job.id)}>
              {t('transfers.resume')}
            </Button>
          ) : job.state === 'running' || job.state === 'queued' ? (
            <Button icon={<PauseRegular />} onClick={() => void actions.transfer('pause', job.id)}>
              {t('transfers.pause')}
            </Button>
          ) : null}
          <Button variant="subtle" onClick={() => useUi.getState().setDrawerOpen(true)}>
            {t('photos.job.details')}
          </Button>
        </>
      }
    >
      <div className={styles.jobBanner} data-testid="photo-job-banner" data-kind={job.kind}>
        <LinearProgress value={fraction} label={job.title} />
        <span className="t-caption t-secondary num">{detail}</span>
      </div>
    </Banner>
  );
}
