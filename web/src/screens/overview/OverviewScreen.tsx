import {
  ArchiveRegular,
  ArrowExportRegular,
  ArrowImportRegular,
  ArrowResetRegular,
  BatteryChargeRegular,
  Battery6Regular,
  ChevronDownRegular,
  CopyRegular,
  EyeOffRegular,
  EyeRegular,
  PowerRegular,
  ScreenshotRegular,
  ShareScreenStartRegular,
} from '@fluentui/react-icons';
import { useRef, useState } from 'react';
import type { BatteryInfo, DetailRow, DeviceSummary, RebootTarget } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useBattery, useDetails, useStorage, useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { ConnectionBadge, modelName, osLabel } from '../../components/DeviceBits';
import { DeviceArt } from '../../components/DeviceArt';
import { ConfirmDialog, Dialog } from '../../components/Dialog';
import { ErrorBanner } from '../../components/ErrorState';
import { Menu, type MenuEntry } from '../../components/Menu';
import { StorageBar } from '../../components/StorageBar';
import { Badge, Banner, Card, LinearProgress, Ring, Skeleton, Tooltip } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import { progressLine } from '../../shell/TransferDrawer';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Overview.module.css';

function healthTone(health: number | null): 'success' | 'warning' | 'danger' {
  if (health === null || health >= 80) return 'success';
  if (health >= 60) return 'warning';
  return 'danger';
}

function BatteryCard({ battery, loading, error, onRetry }: { battery: BatteryInfo | undefined; loading: boolean; error: unknown; onRetry(): void }) {
  const { t, n, percent } = useI18n();
  if (error) return <Card title={t('overview.battery')}><ErrorBanner error={error} onRetry={onRetry} /></Card>;
  return (
    <Card title={t('overview.battery')} data-testid="battery-card">
      {loading || !battery ? (
        <div className={styles.battery}>
          <Skeleton width={112} height={112} radius="50%" />
          <div className={styles.skeletonStack}>
            <Skeleton width="60%" />
            <Skeleton width="80%" />
            <Skeleton width="50%" />
          </div>
        </div>
      ) : (
        <div className={styles.battery}>
          <Ring
            value={battery.healthPercent === null ? 0 : battery.healthPercent / 100}
            tone={healthTone(battery.healthPercent)}
            label={t('overview.healthLabel', { percent: battery.healthPercent === null ? t('common.unknown') : percent(battery.healthPercent / 100) })}
          >
            <div className={styles.ringValue}>
              <span className={styles.ringNumber}>{battery.healthPercent === null ? '—' : percent(battery.healthPercent / 100)}</span>
              <span className={styles.ringCaption}>{t('overview.health')}</span>
            </div>
          </Ring>
          <div>
            {battery.isCharging ? (
              <span className={styles.charging}>
                <BatteryChargeRegular aria-hidden /> {t('overview.charging')}
              </span>
            ) : (
              <span className={styles.onBattery}>
                <Battery6Regular aria-hidden /> {t('overview.onBattery')}
              </span>
            )}
            <dl className={styles.facts}>
              {battery.levelPercent !== null ? (
                <>
                  <dt>{t('overview.charge')}</dt>
                  <dd>{percent(battery.levelPercent / 100)}</dd>
                </>
              ) : null}
              {battery.cycleCount !== null ? (
                <>
                  <dt>{t('overview.cycles')}</dt>
                  <dd>{n(battery.cycleCount)}</dd>
                </>
              ) : null}
              {battery.temperatureCelsius !== null ? (
                <>
                  <dt>{t('overview.temperature')}</dt>
                  <dd>{t('overview.temperatureValue', { value: n(battery.temperatureCelsius, { maximumFractionDigits: 1 }) })}</dd>
                </>
              ) : null}
              {battery.designCapacityMah !== null && battery.fullChargeCapacityMah !== null ? (
                <>
                  <dt>{t('overview.capacity')}</dt>
                  <dd>{t('overview.capacityValue', { current: n(battery.fullChargeCapacityMah), design: n(battery.designCapacityMah) })}</dd>
                </>
              ) : null}
            </dl>
          </div>
        </div>
      )}
    </Card>
  );
}

const DETAIL_LABELS: Record<string, MessageKey> = {
  model: 'details.model',
  serial: 'details.serial',
  imei: 'details.imei',
  udid: 'details.udid',
  wifi: 'details.wifi',
  bluetooth: 'details.bluetooth',
  carrier: 'details.carrier',
};

/** Known rows get the page's own words; anything else keeps the host's label. */
function detailLabel(row: DetailRow, platform: DeviceSummary['platform'], t: ReturnType<typeof useI18n>['t']): string {
  if (row.key === 'os') return t(platform === 'ios' ? 'details.os.ios' : 'details.os.android');
  const key = DETAIL_LABELS[row.key];
  return key ? t(key) : row.label;
}

function mask(value: string): string {
  return value.replace(/[^\s:-]/g, '•');
}

function DetailsCard({ rows, loading, platform }: { rows: DetailRow[] | undefined; loading: boolean; platform: DeviceSummary['platform'] }) {
  const { t } = useI18n();
  const [revealed, setRevealed] = useState<Set<string>>(new Set());
  const [copied, setCopied] = useState<string | null>(null);
  const copy = async (row: DetailRow) => {
    try {
      await navigator.clipboard.writeText(row.value);
      setCopied(row.key);
      setTimeout(() => setCopied((c) => (c === row.key ? null : c)), 1500);
    } catch {
      /* clipboard can be unavailable in tests */
    }
  };
  return (
    <Card title={t('overview.details')} data-testid="details-card">
      {loading || !rows ? (
        <div className={styles.skeletonStack}>
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} />
          ))}
        </div>
      ) : (
        <dl className={styles.details}>
          {rows.map((row) => {
            const hidden = row.sensitive && !revealed.has(row.key);
            return (
              <div key={row.key} className={styles.detailRow}>
                <dt className={styles.detailLabel}>{detailLabel(row, platform, t)}</dt>
                <dd className={styles.detailValue} title={hidden ? undefined : row.value}>
                  {hidden ? mask(row.value) : row.value}
                </dd>
                <span className={styles.detailActions}>
                  {row.sensitive ? (
                    <Tooltip content={hidden ? t('common.reveal') : t('common.hide')}>
                      <Button
                        variant="subtle"
                        size="small"
                        icon={hidden ? <EyeRegular /> : <EyeOffRegular />}
                        aria-label={`${hidden ? t('common.reveal') : t('common.hide')} ${row.label}`}
                        onClick={() =>
                          setRevealed((s) => {
                            const next = new Set(s);
                            if (next.has(row.key)) next.delete(row.key);
                            else next.add(row.key);
                            return next;
                          })
                        }
                      />
                    </Tooltip>
                  ) : null}
                  <Tooltip content={copied === row.key ? t('common.copied') : t('common.copy')}>
                    <Button variant="subtle" size="small" icon={<CopyRegular />} aria-label={`${t('common.copy')} ${row.label}`} onClick={() => void copy(row)} />
                  </Tooltip>
                </span>
              </div>
            );
          })}
        </dl>
      )}
    </Card>
  );
}

function ActivityCard({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t } = i18n;
  const jobs = (useTransfers().data ?? []).filter((j) => j.deviceId === device.id);
  const recent = [...jobs].sort((a, b) => b.createdAt.localeCompare(a.createdAt)).slice(0, 3);
  return (
    <Card title={t('overview.activity')} className={styles.wide}>
      {recent.length === 0 ? (
        <p className="t-secondary">{t('overview.activityEmpty')}</p>
      ) : (
        <div className={styles.activity}>
          {recent.map((job) => (
            <div key={job.id} className={styles.activityItem}>
              <div className={styles.activityTitle} title={job.title}>
                {job.title}
              </div>
              <LinearProgress
                value={jobFraction(job)}
                tone={job.state === 'done' ? 'success' : job.state === 'failed' ? 'danger' : job.state === 'running' ? 'accent' : 'paused'}
                label={job.title}
              />
              <div className={styles.activityMeta}>{progressLine(job, i18n, device)}</div>
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}

export function OverviewScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, digits } = i18n;
  const client = useClient();
  const actions = useActions();
  const navigate = useUi((s) => s.navigate);
  const storage = useStorage(device);
  const battery = useBattery(device);
  const details = useDetails(device);
  const [shot, setShot] = useState<string | null>(null);
  const [shotBusy, setShotBusy] = useState(false);
  const [reboot, setReboot] = useState<RebootTarget | 'shutdown' | null>(null);
  const importInput = useRef<HTMLInputElement>(null);
  const caps = new Set(device.capabilities);
  const limited = device.mode === 'limited';

  const takeScreenshot = async () => {
    setShotBusy(true);
    try {
      const { url } = await client.call('tools.screenshot', { deviceId: device.id });
      setShot(url);
    } catch (error) {
      actions.fail(error);
    } finally {
      setShotBusy(false);
    }
  };

  const doReboot = async () => {
    const target = reboot;
    setReboot(null);
    if (!target) return;
    try {
      if (target === 'shutdown') await client.call('tools.shutdown', { deviceId: device.id });
      else await client.call('tools.reboot', { deviceId: device.id, target });
    } catch (error) {
      actions.fail(error);
    }
  };

  const rebootItems: MenuEntry[] = [
    { key: 'system', label: t('overview.rebootSystem'), icon: <ArrowResetRegular />, disabled: !caps.has('reboot'), onSelect: () => setReboot('system') },
    ...(caps.has('rebootToRecovery') ? [{ key: 'recovery', label: t('overview.rebootRecovery'), onSelect: () => setReboot('recovery') }] : []),
    ...(caps.has('rebootToBootloader') ? [{ key: 'bootloader', label: t('overview.rebootBootloader'), onSelect: () => setReboot('bootloader') }] : []),
    'separator',
    { key: 'shutdown', label: t('overview.shutdown'), icon: <PowerRegular />, disabled: !caps.has('shutdown'), danger: true, onSelect: () => setReboot('shutdown') },
  ];

  const volumes = storage.data?.volumes ?? [];
  const internal = volumes[0];
  const almostFull = internal ? internal.freeBytes / internal.totalBytes < 0.1 : false;

  return (
    <ScreenLayout
      title={t('overview.title')}
      subtitle={`${modelName(device)} · ${osLabel(device, t, digits)} · ${t(device.connection === 'wifi' ? 'device.connection.wifi' : 'device.connection.usb')}`}
      testId="screen-overview"
      actions={
        <>
          {caps.has('photosImport') ? (
            <Button icon={<ArrowImportRegular />} onClick={() => importInput.current?.click()}>
              {t('overview.importPhotos')}
            </Button>
          ) : (
            <Button icon={<ArrowExportRegular />} onClick={() => navigate('photos')} disabled={!caps.has('photosRead')}>
              {t('overview.exportPhotos')}
            </Button>
          )}
          <Tooltip content={t('overview.comingLater')}>
            <span>
              <Button variant="accent" icon={<ArchiveRegular />} disabled>
                {t('overview.backupNow')}
              </Button>
            </span>
          </Tooltip>
        </>
      }
      banners={
        <>
          {limited ? <Banner tone="warning" title={t('device.mode.limited')}>{t('overview.limitedBanner')}</Banner> : null}
          {almostFull && internal ? <Banner tone="warning">{t('overview.storageAlmostFull', { free: i18n.bytes(internal.freeBytes) })}</Banner> : null}
        </>
      }
    >
      <input
        ref={importInput}
        type="file"
        multiple
        accept="image/*,video/*,.heic"
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (files.length) void actions.importPhotos(device, files);
        }}
      />
      <div className={styles.grid}>
        <Card className={styles.device}>
          <DeviceArt platform={device.platform} colorHex={device.colorHex} label={modelName(device)} />
          <div className={styles.deviceName}>
            <span className="t-title">{device.name}</span>
            <span className="t-secondary">{modelName(device)}</span>
          </div>
          <div className={styles.badges}>
            <Badge>{osLabel(device, t, digits)}</Badge>
            <Badge>
              <ConnectionBadge device={device} />
            </Badge>
            {limited ? <Badge tone="warning">{t('device.mode.limited')}</Badge> : <Badge tone="success">{t(device.platform === 'ios' ? 'device.trusted.ios' : 'device.trusted.android')}</Badge>}
          </div>
          <div className={styles.quickTitle}>{t('overview.quickActions')}</div>
          <div className={styles.quick}>
            <Button icon={<ScreenshotRegular />} onClick={() => void takeScreenshot()} loading={shotBusy} disabled={!caps.has('screenshot')}>
              {t('overview.screenshot')}
            </Button>
            <Menu
              trigger={
                <Button icon={<ArrowResetRegular />} iconAfter={<ChevronDownRegular />} disabled={!caps.has('reboot')} aria-label={t('overview.rebootMenu')}>
                  {t('overview.reboot')}
                </Button>
              }
              items={rebootItems}
            />
            {device.platform === 'android' ? (
              <Tooltip content={t('overview.comingLater')}>
                <span className={styles.quickSlot}>
                  <Button icon={<ShareScreenStartRegular />} disabled>
                    {t('overview.mirror')}
                  </Button>
                </span>
              </Tooltip>
            ) : null}
          </div>
        </Card>

        <Card title={t('overview.storage')} className={styles.wide} data-testid="storage-card">
          {storage.error ? (
            <ErrorBanner error={storage.error} onRetry={() => void storage.refetch()} />
          ) : !caps.has('storage') ? (
            <p className="t-secondary">{t('error.next.not_supported')}</p>
          ) : volumes.length === 0 ? (
            <div className={styles.skeletonStack}>
              <Skeleton height={14} radius="999px" />
              <Skeleton width="70%" />
              <Skeleton width="55%" />
            </div>
          ) : (
            volumes.map((volume) => <StorageBar key={volume.key} volume={volume} showLabel={volumes.length > 1} />)
          )}
        </Card>

        {caps.has('battery') ? (
          <BatteryCard battery={battery.data} loading={battery.isPending} error={battery.error} onRetry={() => void battery.refetch()} />
        ) : null}
        <DetailsCard rows={details.data?.rows} loading={details.isPending} platform={device.platform} />
        <ActivityCard device={device} />
      </div>

      <Dialog
        open={shot !== null}
        onOpenChange={(open) => (open ? undefined : setShot(null))}
        title={t('overview.screenshotTitle', { device: device.name })}
        footer={<Button variant="accent" onClick={() => setShot(null)}>{t('common.close')}</Button>}
      >
        {shot ? <img src={shot} alt={t('overview.screenshotTitle', { device: device.name })} className={`${styles.shot} no-mirror`} /> : null}
      </Dialog>

      <ConfirmDialog
        open={reboot !== null}
        onOpenChange={(open) => (open ? undefined : setReboot(null))}
        title={t(reboot === 'shutdown' ? 'overview.shutdownConfirm.title' : 'overview.rebootConfirm.title', { device: device.name })}
        body={t('overview.rebootConfirm.body')}
        confirmLabel={reboot === 'shutdown' ? t('overview.shutdown') : reboot === 'recovery' ? t('overview.rebootRecovery') : reboot === 'bootloader' ? t('overview.rebootBootloader') : t('overview.rebootSystem')}
        danger={reboot === 'shutdown'}
        onConfirm={() => void doReboot()}
      />
    </ScreenLayout>
  );
}
