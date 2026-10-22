import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  CheckmarkCircleFilled,
  DismissRegular,
  ErrorCircleFilled,
  OpenRegular,
  PlayRegular,
  WarningFilled,
} from '@fluentui/react-icons';
import { useState, type ReactNode } from 'react';
import { isBridgeError, type DriverLink, type DriverPhoneHint, type DriverStatus } from '../../bridge';
import { useMock } from '../../app/bridgeContext';
import { Button } from '../../components/Button';
import { DeviceArt } from '../../components/DeviceArt';
import { Badge, Card, LinearProgress, Spinner } from '../../components/ui';
import { cx } from '../../components/cx';
import { useI18n, type MessageKey } from '../../i18n';
import { useDriverActions, useDriverDownload, useDriversLive } from './driverQueries';
import {
  ACTION_LABEL,
  CHIP_LABEL,
  CHIP_TONE,
  isDownloading,
  orderDrivers,
  phoneHints,
  rowModel,
  type DriverAction,
  type DriverChip,
  type DriverRowModel,
} from './driverModel';
import styles from './Drivers.module.css';

const ART: Record<string, { platform: 'ios' | 'android'; color: string }> = {
  ios: { platform: 'ios', color: '#E2733A' },
  android: { platform: 'android', color: '#8FA3B8' },
};

const CHIP_ICON: Partial<Record<DriverChip, ReactNode>> = {
  ready: <CheckmarkCircleFilled aria-hidden />,
  action: <WarningFilled aria-hidden />,
  problem: <ErrorCircleFilled aria-hidden />,
};

const ACTION_ICON: Record<DriverAction, ReactNode> = {
  getAppleDevices: <OpenRegular />,
  startAmds: <PlayRegular />,
  downloadAdb: <ArrowDownloadRegular />,
  retryDownload: <ArrowDownloadRegular />,
  startAdb: <PlayRegular />,
  restartAdb: <ArrowSyncRegular />,
};

/**
 * The Drivers card (v0.6): one row per driver (icon, name, a chip, one line, one button when there is something to do),
 * Android's download with progress and Cancel, and help for Android phones Windows sees that can't be used yet.
 * Shown on the Welcome screen and in Settings; it asks the host again every 30 s while on screen.
 */
export function DriversCard({ className }: { className?: string }) {
  const { t } = useI18n();
  const drivers = useDriversLive();
  const list = orderDrivers(drivers.data ?? []);
  const android = list.find((d) => d.providerId === 'android');
  const download = useDriverDownload('android', android !== undefined && android.state !== 'ready');
  const actions = useDriverActions();
  const mock = useMock();
  const [checking, setChecking] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});

  const failed = (providerId: string) => (error: unknown) =>
    setErrors((e) => ({
      ...e,
      [providerId]: isBridgeError(error, 'permission_denied') && providerId === 'ios' ? t('drivers.error.declined') : error instanceof Error ? error.message : String(error),
    }));
  const cleared = (providerId: string) => setErrors(({ [providerId]: _gone, ...rest }) => rest);

  const checkAgain = async () => {
    setChecking(true);
    setErrors({});
    try {
      await Promise.all([drivers.refetch(), android && android.state !== 'ready' ? download.refetch() : Promise.resolve()]);
    } finally {
      setChecking(false);
    }
  };

  const open = (providerId: string, link: DriverLink) => {
    cleared(providerId);
    actions.openLink.mutate(link, { onError: failed(providerId) });
  };

  const run = (row: DriverRowModel) => {
    const id = row.providerId;
    cleared(id);
    switch (row.action) {
      case 'getAppleDevices':
        open(id, 'appleDevicesStore');
        break;
      case 'downloadAdb':
      case 'retryDownload':
        actions.download.mutate(id, { onError: failed(id) });
        break;
      case 'startAmds':
      case 'startAdb':
      case 'restartAdb':
        actions.start.mutate(id, { onError: failed(id) });
        break;
      default:
        break;
    }
  };

  if (list.length === 0) return null;

  return (
    <Card
      title={t('drivers.title')}
      className={cx(styles.card, className)}
      data-testid="drivers"
      actions={
        <Button size="small" variant="subtle" icon={<ArrowSyncRegular />} loading={checking} onClick={() => void checkAgain()} data-testid="drivers-check">
          {checking ? t('drivers.checking') : t('drivers.checkAgain')}
        </Button>
      }
    >
      <div className={styles.rows}>
        {list.map((status) => {
          const row = rowModel(status, status.providerId === 'android' ? (download.data ?? null) : null);
          const pending =
            (actions.start.isPending && actions.start.variables === row.providerId) ||
            (actions.download.isPending && actions.download.variables === row.providerId);
          return (
            <DriverRow
              key={status.providerId}
              status={status}
              row={row}
              pending={pending}
              error={errors[status.providerId] ?? null}
              onAction={() => run(row)}
              onLink={(link) => open(status.providerId, link)}
              onCancel={() => actions.cancel.mutate(status.providerId)}
              onCheck={() => void checkAgain()}
              onRestart={() => actions.start.mutate(status.providerId, { onError: failed(status.providerId) })}
              checking={checking}
              simulation={
                mock && row.action === 'getAppleDevices' ? (
                  <div className={styles.sim}>
                    <span>{t('common.simulation')}:</span>
                    <Button size="small" onClick={() => mock.drivers.installAppleDevices()} data-testid="sim-install-apple-devices">
                      {t('drivers.simInstalled')}
                    </Button>
                  </div>
                ) : null
              }
            />
          );
        })}
      </div>
    </Card>
  );
}

function DriverRow({
  status,
  row,
  pending,
  error,
  onAction,
  onLink,
  onCancel,
  onCheck,
  onRestart,
  checking,
  simulation,
}: {
  status: DriverStatus;
  row: DriverRowModel;
  pending: boolean;
  error: string | null;
  onAction(): void;
  onLink(link: DriverLink): void;
  onCancel(): void;
  onCheck(): void;
  onRestart(): void;
  checking: boolean;
  simulation: ReactNode;
}) {
  const { t, bytes, digits } = useI18n();
  const art = ART[status.providerId];
  const version = status.version ? digits(status.version) : null;
  const download = row.download && isDownloading(row.download) ? row.download : null;
  const hints = status.providerId === 'android' ? phoneHints(status) : [];
  return (
    <div className={styles.row} data-testid={`driver-row-${status.providerId}`} data-state={status.state} data-chip={row.chip}>
      <div className={styles.tile} aria-hidden>
        {art ? <DeviceArt platform={art.platform} colorHex={art.color} width={15} /> : null}
      </div>
      <div className={styles.main}>
        <div className={styles.head}>
          <span className={styles.name}>{t(row.name)}</span>
          <Badge tone={CHIP_TONE[row.chip]} icon={CHIP_ICON[row.chip]} data-testid={`driver-chip-${status.providerId}`}>
            {t(CHIP_LABEL[row.chip])}
          </Badge>
          {row.chip === 'ready' && version ? (
            <span className={styles.version} dir="ltr">
              {version}
            </span>
          ) : null}
        </div>

        <p className={styles.line} data-testid={`driver-line-${status.providerId}`}>
          {row.chip === 'starting' ? <Spinner label={t(row.line)} /> : null}
          {t(row.line)}
        </p>
        {row.detail ? (
          <p className={styles.detail} title={row.detail}>
            {/* adb's own (English) words, isolated so they read left to right inside a Persian row. */}
            <bdi dir="ltr">{row.detail}</bdi>
          </p>
        ) : null}

        {download ? (
          <div className={styles.progress} data-testid="driver-progress" data-state={download.state}>
            <LinearProgress
              value={download.state === 'downloading' && download.totalBytes ? download.receivedBytes / download.totalBytes : null}
              label={t('drivers.download.label')}
            />
            <span className={styles.progressText}>
              {download.state === 'downloading' && download.totalBytes
                ? t('drivers.download.progress', { received: bytes(download.receivedBytes, 1), total: bytes(download.totalBytes, 1) })
                : t(row.line)}
            </span>
            <Button size="small" icon={<DismissRegular />} onClick={onCancel} data-testid="driver-cancel">
              {t('drivers.download.cancel')}
            </Button>
          </div>
        ) : null}

        {row.action ? (
          <div className={styles.actions}>
            <Button variant="accent" icon={ACTION_ICON[row.action]} loading={pending} onClick={onAction} data-testid={`driver-action-${status.providerId}`} data-action={row.action}>
              {t(ACTION_LABEL[row.action])}
            </Button>
            {row.action === 'getAppleDevices' ? (
              <Button variant="link" onClick={() => onLink('itunes')} data-testid="driver-itunes">
                {t('drivers.ios.orItunes')}
              </Button>
            ) : null}
          </div>
        ) : null}

        {row.action === 'getAppleDevices' ? <p className={styles.note}>{t('drivers.ios.turnsGreen')}</p> : null}
        {row.action === 'startAmds' ? <p className={styles.note}>{t('drivers.ios.startNote')}</p> : null}
        {row.action === 'downloadAdb' || row.action === 'retryDownload' || download ? (
          <p className={styles.note}>
            {t('drivers.android.license')}{' '}
            <Button variant="link" onClick={() => onLink('platformToolsLicense')} data-testid="driver-license">
              {t('drivers.android.licenseLink')}
            </Button>
          </p>
        ) : null}

        {error ? (
          <p className={styles.error} role="alert" data-testid={`driver-error-${status.providerId}`}>
            {error}
          </p>
        ) : null}

        {hints.map((hint) => (
          <PhoneHelp key={`${hint.name}-${hint.problem}`} hint={hint} onLink={onLink} onCheck={onCheck} onRestart={onRestart} checking={checking} />
        ))}
        {simulation}
      </div>
    </div>
  );
}

const STEPS: MessageKey[] = ['welcome.guide.android.1', 'welcome.guide.android.2', 'welcome.guide.android.3'];

/** A small card for an Android phone Windows (or adb) sees that can't be used yet: what to do, in a few lines. */
function PhoneHelp({
  hint,
  onLink,
  onCheck,
  onRestart,
  checking,
}: {
  hint: DriverPhoneHint;
  onLink(link: DriverLink): void;
  onCheck(): void;
  onRestart(): void;
  checking: boolean;
}) {
  const { t } = useI18n();
  const name = hint.name && hint.name !== 'Android phone' ? hint.name : t('drivers.phone.someone');
  const title = t(`drivers.phone.${hint.problem}.title` as MessageKey, { name });
  return (
    <div className={styles.help} data-testid={`driver-phone-${hint.problem}`}>
      <div className={styles.helpTitle}>{title}</div>
      {hint.problem === 'usbDebuggingOff' ? (
        <ol className={styles.steps}>
          {STEPS.map((step) => (
            <li key={step}>{t(step)}</li>
          ))}
        </ol>
      ) : (
        <p className={styles.helpBody}>{t(`drivers.phone.${hint.problem}.body` as MessageKey)}</p>
      )}
      <div className={styles.actions}>
        <Button size="small" icon={<ArrowSyncRegular />} loading={checking} onClick={onCheck} data-testid="driver-phone-check">
          {t('drivers.checkAgain')}
        </Button>
        {hint.problem === 'notSeenByAdb' ? (
          <Button size="small" icon={<ArrowSyncRegular />} onClick={onRestart} data-testid="driver-phone-restart">
            {t('drivers.android.restart')}
          </Button>
        ) : null}
        {hint.problem === 'driverMissing' ? (
          <>
            <Button
              size="small"
              variant="link"
              icon={<OpenRegular />}
              onClick={() => onLink(hint.maker === 'samsung' ? 'samsungUsbDriver' : 'oemUsbDrivers')}
              data-testid="driver-link-maker"
            >
              {t(hint.maker === 'samsung' ? 'drivers.phone.samsungDriver' : 'drivers.phone.oemDrivers')}
            </Button>
            <Button size="small" variant="link" icon={<OpenRegular />} onClick={() => onLink('googleUsbDriver')} data-testid="driver-link-google">
              {t('drivers.phone.googleDriver')}
            </Button>
          </>
        ) : null}
      </div>
    </div>
  );
}
