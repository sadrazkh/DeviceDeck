import { ArrowDownloadRegular, BatteryCheckmarkRegular, InfoRegular, RecordRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import type { BatteryDevice, BatterySnapshot } from '../../bridge/cleanupTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useDevices } from '../../app/queries';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, Card, EmptyState, Segmented, Skeleton, Tooltip } from '../../components/ui';
import { useI18n, type I18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Battery.module.css';
import { BatteryChart, type ChartPoint } from './BatteryChart';

type Measure = 'health' | 'capacity' | 'cycles';

const TABLE_COLLAPSED = 12;

function useBatteryData(deviceId: string) {
  const client = useClient();
  const queryClient = useQueryClient();
  useEffect(
    () =>
      client.on('battery.recorded', (snapshot) => {
        void queryClient.invalidateQueries({ queryKey: ['battery.devices'] });
        void queryClient.invalidateQueries({ queryKey: ['battery.history', snapshot.deviceId] });
      }),
    [client, queryClient],
  );
  const devices = useQuery({ queryKey: ['battery.devices'], queryFn: () => client.call('battery.devices'), refetchOnMount: 'always' });
  const history = useQuery({
    queryKey: ['battery.history', deviceId],
    queryFn: () => client.call('battery.history', { deviceId }),
    refetchOnMount: 'always',
  });
  return { devices, history };
}

function Tile({ label, value, sub, info, testId }: { label: string; value: string; sub?: string; info?: ReactNode; testId?: string }) {
  return (
    <div className={styles.tile} data-testid={testId}>
      <span className={styles.tileLabel}>
        {label}
        {info}
      </span>
      <span className={styles.tileValue}>{value}</span>
      {sub ? <span className={styles.tileSub}>{sub}</span> : null}
    </div>
  );
}

function Tiles({ history, i18n }: { history: BatterySnapshot[]; i18n: I18n }) {
  const { t, n, percent, date } = i18n;
  const latest = history[history.length - 1]!;
  const withHealth = history.filter((s) => s.healthPercent !== null);
  const change = withHealth.length > 1 ? withHealth[withHealth.length - 1]!.healthPercent! - withHealth[0]!.healthPercent! : null;
  const withCycles = history.filter((s) => s.cycleCount !== null);
  const cyclesSince = withCycles.length > 1 ? withCycles[withCycles.length - 1]!.cycleCount! - withCycles[0]!.cycleCount! : null;
  const signed = (value: number) => (value > 0 ? `+${n(value)}` : value < 0 ? `−${n(-value)}` : n(0));
  const info = latest.healthEstimated ? (
    <Tooltip content={t('battery.estimatedBody')}>
      <Button variant="subtle" size="small" icon={<InfoRegular />} aria-label={t('battery.estimatedTitle')} data-testid="battery-estimated-info" />
    </Tooltip>
  ) : undefined;
  return (
    <div className={styles.tiles} data-testid="battery-tiles">
      <Tile
        label={t(latest.healthEstimated ? 'battery.tile.healthEstimated' : 'battery.tile.health')}
        value={latest.healthPercent === null ? '—' : percent(latest.healthPercent / 100)}
        sub={change !== null ? t('battery.tile.change', { change: signed(change), first: date(withHealth[0]!.at, 'short') }) : undefined}
        info={info}
        testId="battery-tile-health"
      />
      <Tile
        label={t('battery.tile.cycles')}
        value={latest.cycleCount === null ? '—' : n(latest.cycleCount)}
        sub={cyclesSince !== null ? t('battery.tile.cyclesSince', { count: signed(cyclesSince), first: date(withCycles[0]!.at, 'short') }) : undefined}
      />
      <Tile
        label={t('battery.tile.capacity')}
        value={latest.fullChargeCapacityMah === null ? '—' : t('battery.mah', { value: n(latest.fullChargeCapacityMah) })}
        sub={latest.designCapacityMah !== null ? t('battery.tile.capacityOf', { design: t('battery.mah', { value: n(latest.designCapacityMah) }) }) : undefined}
      />
      <Tile
        label={t('battery.tile.temperature')}
        value={latest.temperatureCelsius === null ? '—' : t('battery.celsius', { value: n(latest.temperatureCelsius, { maximumFractionDigits: 1 }) })}
        sub={t('battery.tile.temperatureAt')}
      />
    </div>
  );
}

function ReadingsTable({ history, i18n }: { history: BatterySnapshot[]; i18n: I18n }) {
  const { t, n, percent, date, digits } = i18n;
  const [all, setAll] = useState(false);
  const rows = [...history].reverse();
  const shown = all ? rows : rows.slice(0, TABLE_COLLAPSED);
  return (
    <Card title={t('battery.table.title')} data-testid="battery-table">
      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>{t('battery.table.date')}</th>
              <th>{t('battery.table.level')}</th>
              <th>{t('battery.table.health')}</th>
              <th>{t('battery.table.cycles')}</th>
              <th>{t('battery.table.capacity')}</th>
              <th>{t('battery.table.temperature')}</th>
              <th>{t('battery.table.system')}</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((s) => (
              <tr key={s.id} data-testid="battery-row">
                <td>{date(s.at)}</td>
                <td>
                  {s.levelPercent === null ? '—' : percent(s.levelPercent / 100)}
                  {s.isCharging ? <span className={styles.muted}> · {t('battery.table.charging')}</span> : null}
                </td>
                <td>{s.healthPercent === null ? '—' : percent(s.healthPercent / 100)}</td>
                <td>{s.cycleCount === null ? '—' : n(s.cycleCount)}</td>
                <td>{s.fullChargeCapacityMah === null ? '—' : t('battery.mah', { value: n(s.fullChargeCapacityMah) })}</td>
                <td>{s.temperatureCelsius === null ? '—' : t('battery.celsius', { value: n(s.temperatureCelsius, { maximumFractionDigits: 1 }) })}</td>
                <td>{digits(`${s.platform === 'ios' ? 'iOS' : 'Android'} ${s.osVersion}`)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > TABLE_COLLAPSED ? (
        <div>
          <Button variant="subtle" size="small" onClick={() => setAll((v) => !v)}>
            {all ? t('cleanup.showFewer') : t('cleanup.showAll', { count: rows.length })}
          </Button>
        </div>
      ) : null}
    </Card>
  );
}

export function BatteryScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, n, percent, date, digits, lang } = i18n;
  const client = useClient();
  const actions = useActions();
  const queryClient = useQueryClient();
  const [phoneId, setPhoneId] = useState(device.id);
  const [measure, setMeasure] = useState<Measure>('health');
  const [busy, setBusy] = useState<'record' | 'export' | null>(null);
  const { devices, history } = useBatteryData(phoneId);
  const connected = useDevices().data ?? [];

  useEffect(() => setPhoneId(device.id), [device.id]);

  // The phone shown here, plus every phone with history (connected or not).
  const phones = useMemo(() => {
    const list: Pick<BatteryDevice, 'deviceId' | 'name'>[] = [...(devices.data ?? [])];
    if (!list.some((d) => d.deviceId === device.id)) list.unshift({ deviceId: device.id, name: device.name });
    return list;
  }, [devices.data, device]);

  const snapshots = history.data ?? [];
  const latest = snapshots[snapshots.length - 1];
  const isCurrent = phoneId === device.id;
  const name = isCurrent ? device.name : (phones.find((p) => p.deviceId === phoneId)?.name ?? phoneId);

  const points = useMemo<ChartPoint[]>(() => {
    const pick = (s: BatterySnapshot) => (measure === 'health' ? s.healthPercent : measure === 'capacity' ? s.fullChargeCapacityMah : s.cycleCount);
    return snapshots
      .filter((s) => pick(s) !== null)
      .map((s) => ({ at: s.at, value: pick(s)!, note: digits(`${s.platform === 'ios' ? 'iOS' : 'Android'} ${s.osVersion}`) }));
  }, [snapshots, measure, digits]);

  // The chart's own labels stay short: "mAh" in both languages, bare numbers on the capacity axis.
  const format = (v: number) =>
    measure === 'health' ? percent(v / 100) : measure === 'capacity' ? t('battery.mahShort', { value: n(Math.round(v)) }) : n(Math.round(v));
  const tickFormat = (v: number) => (measure === 'capacity' ? n(Math.round(v)) : format(v));
  const measureName = t(measure === 'health' ? 'battery.chart.health' : measure === 'capacity' ? 'battery.chart.capacity' : 'battery.chart.cycles');
  const chartLabel =
    points.length > 0
      ? t('battery.chart.label', {
          measure: measureName,
          count: points.length,
          first: date(points[0]!.at, 'short'),
          last: date(points[points.length - 1]!.at, 'short'),
          latest: format(points[points.length - 1]!.value),
        })
      : measureName;

  const record = async () => {
    setBusy('record');
    try {
      await client.call('battery.record', { deviceId: device.id });
      await queryClient.invalidateQueries({ queryKey: ['battery.history', device.id] });
      await queryClient.invalidateQueries({ queryKey: ['battery.devices'] });
      toast({ tone: 'success', title: t('battery.recorded'), duration: 3000 });
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
    }
  };

  const exportReport = async () => {
    const folder = await actions.pickFolder(t('battery.exportTitle'));
    if (!folder) return;
    setBusy('export');
    try {
      const { path } = await client.call('battery.export', { deviceId: phoneId, destination: folder, language: lang });
      toast({ tone: 'success', title: t('battery.exported'), body: t('battery.exportHint'), action: { label: t('common.showInExplorer'), run: () => void actions.revealInExplorer(path) } });
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
    }
  };

  const canRecord = isCurrent && device.state === 'ready' && device.capabilities.includes('battery');

  return (
    <ScreenLayout
      title={t('battery.title')}
      subtitle={latest ? t('battery.subtitle', { count: snapshots.length, first: date(snapshots[0]!.at, 'short') }) : t('battery.subtitleNone')}
      testId="screen-battery"
      actions={
        <>
          {phones.length > 1 ? (
            <Segmented
              label={t('battery.phone')}
              value={phoneId}
              onChange={setPhoneId}
              options={phones.map((p) => ({
                value: p.deviceId,
                label: connected.some((d) => d.id === p.deviceId && d.state !== 'disconnected') ? p.name : `${p.name} (${t('battery.notConnected')})`,
              }))}
            />
          ) : null}
          {canRecord ? (
            <Button icon={<RecordRegular />} onClick={() => void record()} loading={busy === 'record'} data-testid="battery-record">
              {t('battery.recordNow')}
            </Button>
          ) : null}
          <Button variant="accent" icon={<ArrowDownloadRegular />} disabled={!latest} onClick={() => void exportReport()} loading={busy === 'export'} data-testid="battery-export">
            {t('battery.export')}
          </Button>
        </>
      }
    >
      <div className={styles.stack} data-testid="battery-body">
        {history.isPending ? (
          <Skeleton height={160} />
        ) : history.error ? (
          <ErrorBanner error={history.error} onRetry={() => void history.refetch()} />
        ) : !latest ? (
          <div className={styles.empty}>
            <EmptyState
              icon={<BatteryCheckmarkRegular />}
              title={t('battery.empty.title', { device: name })}
              body={t('battery.empty.body')}
              action={
                canRecord ? (
                  <Button variant="accent" icon={<RecordRegular />} onClick={() => void record()} loading={busy === 'record'}>
                    {t('battery.recordNow')}
                  </Button>
                ) : null
              }
            />
          </div>
        ) : (
          <>
            <Tiles history={snapshots} i18n={i18n} />
            {latest.healthEstimated ? (
              <Banner tone="info" title={t('battery.estimatedTitle')}>
                {t('battery.estimatedBody')}
              </Banner>
            ) : null}
            <Card data-testid="battery-chart-card" aria-label={measureName}>
              <div className={styles.chartHead}>
                <h2 className={styles.chartTitle}>{t('battery.chart.title')}</h2>
                <Segmented
                  label={t('battery.chart.measure')}
                  value={measure}
                  onChange={setMeasure}
                  options={[
                    { value: 'health', label: t('battery.chart.health') },
                    { value: 'capacity', label: t('battery.chart.capacity') },
                    { value: 'cycles', label: t('battery.chart.cycles') },
                  ]}
                />
              </div>
              <BatteryChart
                key={`${phoneId}:${measure}`}
                points={points}
                format={format}
                tickFormat={tickFormat}
                label={chartLabel}
                floorAtZero={measure === 'cycles'}
                cap={measure === 'health' ? 100 : undefined}
                reference={
                  measure === 'capacity' && latest.designCapacityMah !== null
                    ? { value: latest.designCapacityMah, label: `${t('battery.chart.design')} ${n(latest.designCapacityMah)}` }
                    : null
                }
              />
            </Card>
            <ReadingsTable history={snapshots} i18n={i18n} />
          </>
        )}
      </div>
    </ScreenLayout>
  );
}
