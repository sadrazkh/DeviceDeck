import {
  CheckmarkCircleRegular,
  DismissCircleRegular,
  ErrorCircleRegular,
  HandRightRegular,
  PauseCircleRegular,
  PhoneRegular,
  PlugDisconnectedRegular,
} from '@fluentui/react-icons';
import { useState, type ReactNode } from 'react';
import type { LocalSendJobStatus } from '../../bridge/localSend';
import type { DeviceSummary, TransferJob } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { LinearProgress, Spinner, TextField } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { jobFraction } from '../../state/transfers';
import styles from './LocalSend.module.css';
import backupStyles from './Backup.module.css';

const PIN_CODES = new Set(['localsend_pin_required', 'localsend_pin_wrong']);

/**
 * LocalSend stopped answering in the middle of the send: the iPhone locked or another app came to the front, and iOS
 * suspended LocalSend. A dropped or stalled upload says so directly; "not answering" says so once bytes had gone.
 */
export function wentToBackground(job: TransferJob | null, status: LocalSendJobStatus | null, sawSending: boolean): boolean {
  if (!job || !(job.state === 'failed' || (job.state === 'done' && job.failedItems > 0))) return false;
  const codes = [job.error?.code, status?.error?.code, ...(status?.files ?? []).map((f) => f.error?.code)];
  if (codes.includes('localsend_connection_lost')) return true;
  return codes.includes('localsend_not_listening') && (sawSending || job.doneItems > 0);
}

/**
 * A LocalSend send under way, in the dialog: reaching the phone → waiting for Accept → sending 12 of 40 → done, in
 * Photos now. A PIN request shows a field for it; any other failure says what to do and offers to try again.
 */
export function LocalSendProgress({
  device,
  job,
  status,
  receiverName,
  background,
  onRetry,
}: {
  device: DeviceSummary;
  job: TransferJob | null;
  status: LocalSendJobStatus | null;
  receiverName: string;
  /** See wentToBackground: then it says to open LocalSend again, not a generic failure. */
  background: boolean;
  onRetry(pin?: string): Promise<boolean>;
}) {
  const { t, n, percent } = useI18n();
  const [pin, setPin] = useState('');
  const [busy, setBusy] = useState(false);
  const state = job?.state ?? 'queued';
  const phase = status?.phase ?? 'connecting';
  const error = job?.error ?? status?.error ?? null;
  const name = status?.receiverAlias ?? receiverName;

  let tone: 'found' | 'warn' | 'bad' | null = null;
  let icon: ReactNode = <Spinner />;
  let title = t('localsend.job.connecting', { name });
  let detail: string | null = null;
  if (background) {
    tone = 'warn';
    icon = <PhoneRegular />;
    title = t('localsend.background.title');
    detail = t('localsend.background.text');
  } else if (state === 'done') {
    tone = job && job.failedItems > 0 ? 'warn' : 'found';
    icon = <CheckmarkCircleRegular />;
    title = t('localsend.job.done', { count: job ? job.doneItems : 0 });
    detail = job && job.failedItems > 0 ? t('localsend.job.doneSome', { failed: job.failedItems }) : t('localsend.job.where');
  } else if (state === 'failed') {
    tone = 'bad';
    icon = <ErrorCircleRegular />;
    title = error?.code === 'localsend_pin_required' ? t('localsend.pin.title') : error?.code === 'localsend_pin_wrong' ? t('localsend.pin.wrong') : t('localsend.job.failed');
    detail = error && !PIN_CODES.has(error.code) ? `${error.message} ${t(`error.next.${error.code}` as MessageKey)}` : null;
  } else if (state === 'cancelled') {
    icon = <DismissCircleRegular />;
    title = t('localsend.job.cancelled');
  } else if (state === 'paused') {
    icon = <PauseCircleRegular />;
    title = t('localsend.job.paused');
  } else if (state === 'waitingForDevice') {
    tone = 'warn';
    icon = <PlugDisconnectedRegular />;
    title = t('localsend.job.waitingPhone', { device: device.name });
  } else if (phase === 'waitingForAccept') {
    tone = 'warn';
    icon = <HandRightRegular />;
    title = t('localsend.job.waiting', { device: device.name });
  } else if (phase === 'sending' || (state === 'running' && (job?.doneBytes ?? 0) > 0)) {
    title = t('localsend.job.sending', { done: n(Math.min((job?.doneItems ?? 0) + 1, job?.totalItems ?? 0)), total: n(job?.totalItems ?? 0) });
  }

  const askPin = !background && state === 'failed' && error !== null && PIN_CODES.has(error.code);
  const running = state === 'running' || state === 'queued';
  const retry = async (withPin?: string) => {
    setBusy(true);
    await onRetry(withPin);
    setBusy(false);
  };
  const fraction = job ? jobFraction(job) : 0;
  const showProgress = state === 'running' || state === 'queued' || state === 'paused' || state === 'waitingForDevice';
  const files = status?.files ?? null;
  const showFiles = files !== null && (state === 'failed' || (state === 'done' && (job?.failedItems ?? 0) > 0));

  return (
    <div className={backupStyles.section} data-testid="localsend-progress" data-state={state} data-phase={phase} data-background={background}>
      <div
        className={cx(styles.status, tone === 'found' && styles.found, tone === 'warn' && styles.warn, tone === 'bad' && styles.bad)}
        role="status"
        aria-live="polite"
      >
        <span className={styles.statusIcon} aria-hidden>
          {icon}
        </span>
        <span className={styles.statusText}>
          <span className={styles.statusTitle} data-testid="localsend-progress-title">
            {title}
          </span>
          {detail ? <span>{detail}</span> : null}
        </span>
      </div>

      {running ? (
        <div className={styles.keepOpen} data-testid="localsend-keep-open">
          <PhoneRegular aria-hidden />
          <span>{t('localsend.keepOpen')}</span>
        </div>
      ) : null}

      {showProgress && job ? (
        <div className={styles.progress}>
          <LinearProgress value={phase === 'sending' || job.doneBytes > 0 ? fraction : null} tone={state === 'running' || state === 'queued' ? 'accent' : 'paused'} label={title} />
          <span className={cx(backupStyles.hint, 'num')}>
            {percent(fraction)} · {t('transfers.items', { done: n(job.doneItems), total: n(job.totalItems) })}
          </span>
        </div>
      ) : null}

      {askPin ? (
        <div className={styles.pin}>
          <TextField label={t('localsend.pin.label')} value={pin} onChange={setPin} autoFocus onEnter={() => (pin.trim() ? void retry(pin) : undefined)} />
          <Button variant="accent" disabled={!pin.trim()} loading={busy} onClick={() => void retry(pin)} data-testid="localsend-pin-send">
            {t('localsend.pin.send')}
          </Button>
        </div>
      ) : null}

      {(state === 'failed' && !askPin) || background ? (
        <Button variant="accent" loading={busy} onClick={() => void retry()} data-testid="localsend-retry">
          {t('localsend.retry')}
        </Button>
      ) : null}
      {state === 'done' && (job?.failedItems ?? 0) > 0 && !background ? (
        <Button loading={busy} onClick={() => void retry()} data-testid="localsend-retry">
          {t('localsend.retry')}
        </Button>
      ) : null}

      {showFiles ? (
        <>
          <span className={backupStyles.label}>{t('localsend.files.title')}</span>
          <ul className={styles.results} data-testid="localsend-results">
            {files.map((f) => (
              <li key={f.index}>
                <span className={styles.resultName}>{f.name}</span>
                <span className={cx(styles.resultState, f.state === 'done' && styles.resultDone, f.state === 'failed' && styles.resultFailed)}>
                  {t(`localsend.file.${f.state}` as MessageKey)}
                </span>
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </div>
  );
}
