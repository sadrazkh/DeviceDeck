import {
  ArrowResetRegular,
  CalendarLtrRegular,
  CheckmarkCircleRegular,
  CloudOffRegular,
  FolderOpenRegular,
  LockClosedRegular,
  PauseRegular,
  PlayRegular,
} from '@fluentui/react-icons';
import { useState } from 'react';
import type { DeviceSummary, PhotoBackupRun, PhotoBackupStatus, TransferJob } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { ConfirmDialog, Dialog } from '../../components/Dialog';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, LinearProgress, RadioCards, Segmented, Skeleton, Switch } from '../../components/ui';
import { useI18n, type I18n } from '../../i18n';
import { jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import {
  PATTERN_PRESETS,
  RANGE_DAYS,
  exampleFolder,
  isoDay,
  rangeFor,
  useBackupActions,
  useBackupStatus,
  useConfigureBackup,
  type ConfigurePatch,
} from './backupQueries';
import styles from './Backup.module.css';
import { HiddenBackupSection } from './HiddenBackupOptions';

function runSummary(run: PhotoBackupRun, i18n: I18n): string {
  const { t } = i18n;
  const parts = [t('photos.backup.run.copied', { count: run.copied })];
  if (run.duplicates > 0) parts.push(t('photos.backup.run.duplicates', { count: run.duplicates }));
  if (run.failed > 0) parts.push(t('photos.backup.run.failed', { count: run.failed }));
  if (run.keptHeic > 0) parts.push(t('photos.backup.run.keptHeic', { count: run.keptHeic }));
  if (run.state === 'cancelled') parts.push(t('photos.backup.run.cancelled'));
  if (run.edited > 0) parts.push(t('photos.backup.run.edited', { count: run.edited }));
  if (run.editsMissing > 0) parts.push(t('photos.backup.run.editsMissing', { count: run.editsMissing }));
  if (run.trigger === 'automatic') parts.push(t('photos.backup.run.automatic'));
  if (run.trigger === 'notification') parts.push(t('photos.backup.run.fromNotification'));
  return parts.join(' · ');
}

/** "N new since the last backup", where they go, the options, the last run and the button. */
export function BackupPanel({ device, open, onOpenChange }: { device: DeviceSummary; open: boolean; onOpenChange(open: boolean): void }) {
  const i18n = useI18n();
  const { t, bytes, date } = i18n;
  const status = useBackupStatus(device);
  const configure = useConfigureBackup(device);
  const backup = useBackupActions(device);
  const actions = useActions();
  const transfers = useTransfers();
  const [busy, setBusy] = useState(false);
  const [confirmForget, setConfirmForget] = useState(false);

  const data = status.data;
  const activeJob: TransferJob | undefined = data?.activeJobId ? transfers.data?.find((j) => j.id === data.activeJobId) : undefined;
  const running = activeJob && !['done', 'failed', 'cancelled'].includes(activeJob.state) ? activeJob : undefined;
  const count = data?.count;

  const start = async () => {
    if (!data) return;
    setBusy(true);
    const jobId = await backup.start(data.options.destination);
    setBusy(false);
    if (jobId) useUi.getState().setDrawerOpen(true);
  };

  const pickDestination = async () => {
    const picked = await actions.pickFolder(t('photos.backup.destination'));
    if (picked) configure.mutate({ destination: picked });
  };

  return (
    <>
      <Dialog
        open={open}
        onOpenChange={onOpenChange}
        wide
        title={t('photos.backup.title')}
        description={t('photos.backup.lead', { device: device.name })}
        footerStart={
          data && data.backedUp > 0 ? (
            <Button variant="subtle" icon={<ArrowResetRegular />} onClick={() => setConfirmForget(true)} data-testid="backup-forget">
              {t('photos.backup.forget')}
            </Button>
          ) : undefined
        }
        footer={
          <>
            <Button onClick={() => onOpenChange(false)}>{t('common.close')}</Button>
            {running ? (
              running.state === 'paused' ? (
                <Button variant="accent" icon={<PlayRegular />} onClick={() => void actions.transfer('resume', running.id)} data-testid="backup-resume">
                  {t('transfers.resume')}
                </Button>
              ) : (
                <Button icon={<PauseRegular />} onClick={() => void actions.transfer('pause', running.id)} data-testid="backup-pause">
                  {t('transfers.pause')}
                </Button>
              )
            ) : (
              <Button
                variant="accent"
                onClick={() => void start()}
                loading={busy}
                disabled={!count || count.newCount + count.newEdits === 0 || !data.space.enough}
                data-testid="backup-start"
              >
                {count && count.newCount > 0
                  ? t('photos.backup.start', { count: count.newCount })
                  : count && count.newEdits > 0
                    ? t('photos.backup.startEdits', { count: count.newEdits })
                    : t('photos.backup.upToDate')}
              </Button>
            )}
          </>
        }
      >
        {status.error ? <ErrorBanner error={status.error} onRetry={() => void status.refetch()} /> : null}
        {!data ? (
          status.error ? null : (
            <div className={styles.loading} aria-busy>
              <Skeleton height={44} width="60%" />
              <Skeleton height={16} width="40%" />
              <Skeleton height={36} />
            </div>
          )
        ) : (
          <Body data={data} device={device} running={running} onPick={() => void pickDestination()} configure={configure.mutate} i18n={i18n} />
        )}
        {data?.lastRun && data.lastRun.state !== 'running' ? (
          <div className={styles.lastRun} data-testid="backup-last-run">
            <span className={styles.label}>{t('photos.backup.lastRun')}</span>
            <span>
              {date(data.lastRun.finishedAt ?? data.lastRun.startedAt)} — {runSummary(data.lastRun, i18n)}
            </span>
            {data.lastRun.copied > 0 ? <span className="t-caption t-secondary num">{bytes(data.lastRun.bytes)}</span> : null}
          </div>
        ) : null}
        {data && data.backedUp > 0 ? (
          <p className={styles.fine}>{t('photos.backup.remembered', { count: data.backedUp, device: device.name })}</p>
        ) : null}
      </Dialog>
      <ConfirmDialog
        open={confirmForget}
        onOpenChange={setConfirmForget}
        title={t('photos.backup.forget.title', { device: device.name })}
        body={t('photos.backup.forget.body')}
        confirmLabel={t('photos.backup.forget.confirm')}
        onConfirm={async () => {
          setConfirmForget(false);
          await backup.forget();
        }}
      />
    </>
  );
}

/** No room, no drive, or nearly full: said before the button is pressed, with a way out. */
function SpaceBanner({ data, onPick, disabled }: { data: PhotoBackupStatus; onPick(): void; disabled: boolean }) {
  const { t, bytes } = useI18n();
  const { space, count } = data;
  const choose = (
    <Button onClick={onPick} disabled={disabled} data-testid="backup-space-choose">
      {t('photos.backup.space.choose')}
    </Button>
  );
  const root = `⁦${space.root}⁩`;
  if (space.driveMissing) {
    return (
      <div data-testid="backup-space" data-state="missing">
        <Banner tone="danger" title={t('photos.backup.space.missing.title', { root })} actions={choose}>
          {t('photos.backup.space.missing.body')}
        </Banner>
      </div>
    );
  }
  if (!space.enough && count.newCount > 0) {
    return (
      <div data-testid="backup-space" data-state="short">
        <Banner tone="warning" title={t('photos.backup.space.short.title', { root })} actions={choose}>
          {t('photos.backup.space.short.body', { needed: bytes(space.neededBytes), free: bytes(space.freeBytes ?? 0) })}
        </Banner>
      </div>
    );
  }
  if (space.nearlyFull && space.freeBytes !== null) {
    return (
      <div data-testid="backup-space" data-state="nearlyFull">
        <Banner tone="warning">{t('photos.backup.space.nearlyFull', { root, free: bytes(space.freeBytes) })}</Banner>
      </div>
    );
  }
  return null;
}

function Body({
  data,
  device,
  running,
  onPick,
  configure,
  i18n,
}: {
  data: PhotoBackupStatus;
  device: DeviceSummary;
  running: TransferJob | undefined;
  onPick(): void;
  configure(patch: ConfigurePatch): void;
  i18n: I18n;
}) {
  const { t, n, bytes, percent } = i18n;
  const actions = useActions();
  // The Hidden album's size stays private until it is revealed on the Photos screen.
  const hiddenRevealed = useUi((s) => s.hiddenRevealed[device.id] ?? false);
  const { count, options } = data;
  const today = new Date();
  const presets: string[] = PATTERN_PRESETS.includes(options.folderPattern as (typeof PATTERN_PRESETS)[number])
    ? [...PATTERN_PRESETS]
    : [...PATTERN_PRESETS, options.folderPattern];

  return (
    <div className={styles.body}>
      <div className={styles.hero} data-testid="backup-count">
        {count.newCount > 0 ? (
          <>
            <span className={styles.big}>{n(count.newCount)}</span>
            <span className={styles.heroText}>
              <span className={styles.heroTitle}>{t('photos.backup.new')}</span>
              <span className="t-caption t-secondary num">
                {t('photos.backup.newDetail', { photos: count.newPhotos, videos: count.newVideos })}
                {/* How many are hidden stays private until the Hidden album is revealed on the Photos screen. */}
                {count.newHidden > 0 && hiddenRevealed ? ` · ${t('photos.backup.hidden.newHidden', { count: count.newHidden })}` : ''}
                {count.newEdits > 0 ? ` · ${t('photos.backup.newEdits', { count: count.newEdits })}` : ''}
                {count.newBytes > 0 ? ` · ${t('photos.backup.about', { size: bytes(count.newBytes) })}` : ''}
              </span>
            </span>
          </>
        ) : (
          <>
            <span className={styles.doneIcon} aria-hidden>
              <CheckmarkCircleRegular />
            </span>
            <span className={styles.heroText}>
              <span className={styles.heroTitle}>{t('photos.backup.allDone')}</span>
              <span className="t-caption t-secondary">{t('photos.backup.allDoneDetail', { device: device.name })}</span>
            </span>
          </>
        )}
      </div>

      {running ? (
        <div className={styles.progress} data-testid="backup-progress">
          <LinearProgress value={jobFraction(running)} label={running.title} />
          <span className="t-caption t-secondary num">
            {percent(jobFraction(running))} · {t('transfers.items', { done: n(running.doneItems), total: n(running.totalItems) })}
            {running.state === 'paused' ? ` · ${t('transfers.state.paused')}` : ''}
            {running.state === 'waitingForDevice' ? ` · ${t('transfers.state.waitingForDevice', { device: device.name })}` : ''}
          </span>
        </div>
      ) : null}

      {count.cloudOnly > 0 ? (
        <Banner icon={<CloudOffRegular />}>{t('photos.backup.cloudOnly', { count: count.cloudOnly })}</Banner>
      ) : null}
      {count.outOfRange > 0 ? (
        <div data-testid="backup-out-of-range">
          <Banner icon={<CalendarLtrRegular />}>{t('photos.backup.outOfRange', { count: count.outOfRange })}</Banner>
        </div>
      ) : null}
      {count.hiddenLeftOut > 0 ? (
        <Banner icon={<LockClosedRegular />}>
          {hiddenRevealed ? t('photos.backup.hiddenLeftOut', { count: count.hiddenLeftOut }) : t('photos.backup.hiddenLeftOutLocked')}
        </Banner>
      ) : null}
      {count.normalLeftOut > 0 ? (
        <div data-testid="backup-normal-left-out">
          <Banner icon={<LockClosedRegular />}>{t('photos.backup.hidden.normalLeftOut', { count: count.normalLeftOut })}</Banner>
        </div>
      ) : null}

      <SpaceBanner data={data} onPick={onPick} disabled={!!running} />

      <div className={styles.section}>
        <span className={styles.label}>{t('photos.backup.destination')}</span>
        <div className={styles.destination}>
          <span className={styles.path} title={options.destination} data-testid="backup-destination">
            {options.destination}
          </span>
          <Button icon={<FolderOpenRegular />} onClick={onPick} disabled={!!running} data-testid="backup-change-destination">
            {t('common.change')}
          </Button>
          <Button variant="subtle" onClick={() => void actions.revealInExplorer(options.destination)}>
            {t('common.showInExplorer')}
          </Button>
        </div>
      </div>

      <div className={styles.section}>
        <span className={styles.label}>{t('photos.backup.folders')}</span>
        <Segmented
          label={t('photos.backup.folders')}
          value={options.folderPattern === '' ? 'none' : options.folderPattern}
          onChange={(v) => configure({ folderPattern: v === 'none' ? '' : v })}
          options={presets.map((p) => ({
            value: p === '' ? 'none' : p,
            label: p === '' ? t('photos.backup.oneFolder') : <span className="num">{`⁦${exampleFolder(p, today)}⁩`}</span>,
          }))}
        />
      </div>

      <div className={styles.section} data-testid="backup-range">
        <span className={styles.label}>{t('photos.backup.range')}</span>
        <Segmented
          label={t('photos.backup.range')}
          value={options.range.kind}
          onChange={(kind) => configure({ range: rangeFor(kind, today, options.range.since) })}
          options={[
            { value: 'all', label: t('photos.backup.range.all') },
            { value: 'lastDays', label: t('photos.backup.range.lastDays', { count: options.range.days ?? RANGE_DAYS }) },
            { value: 'since', label: t('photos.backup.range.since') },
          ]}
        />
        {options.range.kind === 'since' ? (
          <label className={styles.dateField}>
            <span>{t('photos.backup.range.sinceLabel')}</span>
            <input
              type="date"
              className={styles.dateInput}
              value={options.range.since ?? ''}
              max={isoDay(today)}
              onChange={(e) => {
                if (e.target.value) configure({ range: rangeFor('since', today, e.target.value) });
              }}
              data-testid="backup-range-since"
            />
          </label>
        ) : null}
        <span className={styles.hint}>{t('photos.backup.range.hint')}</span>
      </div>

      <div className={styles.section}>
        <Switch checked={options.convertHeic} onChange={(convertHeic) => configure({ convertHeic })} label={t('photos.backup.convertHeic')} />
        <span className={styles.hint}>{t('photos.backup.convertHeicHint')}</span>
        {options.convertHeic && data.heicConversion === 'missingCodec' ? (
          <Banner tone="warning" title={t('photos.backup.codecMissing.title')}>
            {t('photos.backup.codecMissing.body')}
          </Banner>
        ) : null}
      </div>

      {device.platform === 'ios' ? (
        <HiddenBackupSection
          value={options}
          onChange={configure}
          destination={options.destination}
          pattern={options.folderPattern}
          convertHeic={options.convertHeic && data.heicConversion !== 'missingCodec'}
          disabled={!!running}
          movesNote={data.backedUp > 0}
        />
      ) : null}

      {device.platform === 'ios' ? (
        <div className={styles.section} data-testid="backup-save-edited">
          <Switch checked={options.saveEdited} onChange={(saveEdited) => configure({ saveEdited })} label={t('photos.backup.saveEdited')} />
          <span className={styles.hint}>{t('photos.backup.saveEditedHint')}</span>
        </div>
      ) : null}

      <div className={styles.section} data-testid="backup-on-connect">
        <span className={styles.label}>{t('photos.backup.onConnect')}</span>
        <RadioCards
          label={t('photos.backup.onConnect')}
          value={data.onConnect}
          onChange={(onConnect) => configure({ onConnect })}
          options={[
            { value: 'nothing', label: t('photos.backup.onConnect.nothing'), hint: t('photos.backup.onConnect.nothingHint') },
            { value: 'notify', label: t('photos.backup.onConnect.notify'), hint: t('photos.backup.onConnect.notifyHint') },
            { value: 'backUp', label: t('photos.backup.onConnect.backUp'), hint: t('photos.backup.onConnect.backUpHint') },
          ]}
        />
      </div>

      <p className={styles.fine}>
        {t('photos.backup.how')}{' '}
        {data.customized ? (
          <Button variant="link" onClick={() => configure({ useDefaults: true })} data-testid="backup-use-defaults">
            {t('photos.backup.useDefaults')}
          </Button>
        ) : (
          t('photos.backup.fromSettings')
        )}
      </p>
    </div>
  );
}
