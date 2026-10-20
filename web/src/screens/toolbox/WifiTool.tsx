import { ArrowClockwiseRegular, PlugConnectedRegular, PlugDisconnectedRegular, QrCodeRegular, UsbPlugRegular, Wifi1Regular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useDevices } from '../../app/queries';
import type { QrStage, WifiQrEvent } from '../../bridge/toolbox';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Badge, Banner, Segmented, Spinner, Switch, TextField } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n, type ToolboxKey } from './i18n';
import { encodeQr, qrPath } from './qr';
import styles from './Toolbox.module.css';

type Method = 'code' | 'qr' | 'connect';

/** "192.168.1.5:37099" pasted into the address box fills both fields. */
function splitAddress(text: string): { host: string; port: string } | null {
  const m = /^\s*\[?([0-9a-fA-F:.]+|[A-Za-z0-9.-]+)\]?:(\d{1,5})\s*$/.exec(text);
  return m ? { host: m[1]!, port: m[2]! } : null;
}

export function WifiTool({ device, wirelessDebugging }: { device: DeviceSummary; wirelessDebugging: boolean }) {
  const { tt } = useToolboxI18n();
  const devices = useDevices();
  return (
    <div className={styles.tool} data-testid="wifi-tool" style={{ overflow: 'auto' }}>
      <div className={styles.wifiGrid}>
        <div className={cx(styles.panel, styles.section)}>
          <span className={styles.sectionTitle}>{tt('wifi.connection')}</span>
          {(devices.data ?? []).map((d) => (
            <ConnectionLine key={d.id} device={d} />
          ))}
        </div>
        {device.platform === 'ios' ? <IphoneSync device={device} /> : null}
        {wirelessDebugging ? <AndroidPairing /> : device.platform === 'android' ? <Banner tone="warning">{tt('wifi.noAdb')}</Banner> : null}
      </div>
    </div>
  );
}

function ConnectionLine({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [busy, setBusy] = useState(false);
  const wifi = device.connection === 'wifi';
  return (
    <div className={styles.deviceLine} data-testid={`connection-${device.id}`}>
      <span style={{ flex: 1 }}>{device.name}</span>
      <Badge tone={wifi ? 'info' : 'neutral'} icon={wifi ? <Wifi1Regular /> : <UsbPlugRegular />}>
        {wifi ? tt('wifi.wifi') : tt('wifi.usb')}
      </Badge>
      {wifi && device.platform === 'android' ? (
        <Button
          size="small"
          icon={<PlugDisconnectedRegular />}
          loading={busy}
          onClick={async () => {
            setBusy(true);
            try {
              await client.call('wifi.disconnect', { deviceId: device.id });
              toast({ tone: 'info', title: tt('wifi.disconnected') });
            } catch (error) {
              actions.fail(error);
            } finally {
              setBusy(false);
            }
          }}
        >
          {tt('wifi.disconnect')}
        </Button>
      ) : null}
    </div>
  );
}

function IphoneSync({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const key = ['wifi.iphoneSync', device.id];
  const sync = useQuery({ queryKey: key, queryFn: () => client.call('wifi.iphoneSync', { deviceId: device.id }) });
  const [busy, setBusy] = useState(false);
  return (
    <div className={cx(styles.panel, styles.section)} data-testid="iphone-sync">
      <span className={styles.sectionTitle}>{tt('wifi.iphone')}</span>
      {sync.isPending ? (
        <Spinner />
      ) : (
        <Switch
          checked={sync.data?.enabled === true}
          label={tt('wifi.iphoneSwitch')}
          onChange={async (enabled) => {
            if (busy) return;
            setBusy(true);
            try {
              const result = await client.call('wifi.setIphoneSync', { deviceId: device.id, enabled });
              queryClient.setQueryData(key, result);
            } catch (error) {
              actions.fail(error);
            } finally {
              setBusy(false);
            }
          }}
        />
      )}
      {sync.data && sync.data.enabled === null ? <span className={styles.muted}>{tt('wifi.iphoneUnknown')}</span> : null}
      <span className={cx(styles.muted, styles.small)}>{tt('wifi.iphoneHint')}</span>
    </div>
  );
}

function AndroidPairing() {
  const { tt } = useToolboxI18n();
  const [method, setMethod] = useState<Method>('code');
  return (
    <div className={cx(styles.panel, styles.section)} data-testid="android-pairing">
      <span className={styles.sectionTitle}>{tt('wifi.android')}</span>
      <span className={cx(styles.muted, styles.small)}>{tt('wifi.androidHint')}</span>
      <Segmented
        value={method}
        onChange={setMethod}
        label={tt('wifi.android')}
        options={[
          { value: 'code', label: tt('wifi.method.code') },
          { value: 'qr', label: tt('wifi.method.qr') },
          { value: 'connect', label: tt('wifi.method.connect') },
        ]}
      />
      {method === 'code' ? <PairWithCode onPaired={() => setMethod('connect')} /> : method === 'qr' ? <PairWithQr /> : <ConnectForm />}
    </div>
  );
}

function AddressFields({ host, port, setHost, setPort }: { host: string; port: string; setHost(v: string): void; setPort(v: string): void }) {
  const { tt } = useToolboxI18n();
  return (
    <div className={styles.fields}>
      <TextField
        label={tt('wifi.ip')}
        value={host}
        onChange={(v) => {
          const split = splitAddress(v);
          if (split) {
            setHost(split.host);
            setPort(split.port);
          } else setHost(v);
        }}
      />
      <TextField label={tt('wifi.port')} value={port} onChange={(v) => setPort(v.replace(/\D/g, '').slice(0, 5))} />
    </div>
  );
}

function PairWithCode({ onPaired }: { onPaired(): void }) {
  const client = useClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [host, setHost] = useState('');
  const [port, setPort] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const ready = host.trim() !== '' && Number(port) > 0 && /^\d{6}$/.test(code);
  return (
    <>
      <span>{tt('wifi.codeHint')}</span>
      <AddressFields host={host} port={port} setHost={setHost} setPort={setPort} />
      <TextField label={tt('wifi.code')} value={code} onChange={(v) => setCode(v.replace(/\D/g, '').slice(0, 6))} />
      <div>
        <Button
          variant="accent"
          icon={<PlugConnectedRegular />}
          disabled={!ready}
          loading={busy}
          data-testid="wifi-pair"
          onClick={async () => {
            setBusy(true);
            try {
              await client.call('wifi.pair', { host: host.trim(), port: Number(port), code });
              toast({ tone: 'success', title: tt('wifi.paired') });
              onPaired();
            } catch (error) {
              actions.fail(error);
            } finally {
              setBusy(false);
            }
          }}
        >
          {tt('wifi.pair')}
        </Button>
      </div>
    </>
  );
}

function ConnectForm() {
  const client = useClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [host, setHost] = useState('');
  const [port, setPort] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <>
      <span>{tt('wifi.connectHint')}</span>
      <AddressFields host={host} port={port} setHost={setHost} setPort={setPort} />
      <div>
        <Button
          variant="accent"
          icon={<Wifi1Regular />}
          disabled={host.trim() === '' || !(Number(port) > 0)}
          loading={busy}
          data-testid="wifi-connect"
          onClick={async () => {
            setBusy(true);
            try {
              const result = await client.call('wifi.connect', { host: host.trim(), port: Number(port) });
              toast({ tone: 'success', title: tt('wifi.connected', { serial: result.serial ?? `${host}:${port}` }) });
            } catch (error) {
              actions.fail(error);
            } finally {
              setBusy(false);
            }
          }}
        >
          {tt('wifi.connect')}
        </Button>
      </div>
    </>
  );
}

const STAGE_TEXT: Record<QrStage, ToolboxKey> = {
  waiting: 'wifi.qr.waiting',
  pairing: 'wifi.qr.pairing',
  connecting: 'wifi.qr.connecting',
  done: 'wifi.qr.done',
  failed: 'wifi.qr.failed',
  cancelled: 'wifi.qr.expired',
  expired: 'wifi.qr.expired',
};

function PairWithQr() {
  const client = useClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [round, setRound] = useState(0);
  const [session, setSession] = useState<{ id: string; payload: string } | null>(null);
  const [event, setEvent] = useState<WifiQrEvent | null>(null);
  const failRef = useRef(actions.fail);
  failRef.current = actions.fail;

  useEffect(() => {
    let active = true;
    let id: string | null = null;
    setEvent(null);
    const off = client.on('wifi.qr', (e) => {
      if (e.sessionId === id) setEvent(e);
    });
    client
      .call('wifi.qrStart')
      .then((started) => {
        if (!active) {
          void client.call('wifi.qrCancel', { sessionId: started.sessionId }).catch(() => undefined);
          return;
        }
        id = started.sessionId;
        setSession({ id: started.sessionId, payload: started.payload });
      })
      .catch((error: unknown) => active && failRef.current(error));
    return () => {
      active = false;
      off();
      if (id) void client.call('wifi.qrCancel', { sessionId: id }).catch(() => undefined);
    };
  }, [client, round]);

  const qr = useMemo(() => {
    if (!session) return null;
    const code = encodeQr(session.payload, 'M');
    return { path: qrPath(code), size: code.size + 8 };
  }, [session]);
  const stage = event?.stage ?? 'waiting';
  const over = stage === 'done' || stage === 'failed' || stage === 'cancelled' || stage === 'expired';

  const ttRef = useRef(tt);
  ttRef.current = tt;
  useEffect(() => {
    if (stage === 'done' && event?.serial) toast({ tone: 'success', title: ttRef.current('wifi.connected', { serial: event.serial }) });
  }, [stage, event?.serial]);

  return (
    <>
      <span>{tt('wifi.qrHint')}</span>
      <div className={styles.qr}>
        <div className={styles.qrCode} data-testid="wifi-qr">
          {qr ? (
            <svg viewBox={`0 0 ${qr.size} ${qr.size}`} role="img" aria-label={tt('wifi.method.qr')} shapeRendering="crispEdges">
              <rect width={qr.size} height={qr.size} fill="#ffffff" />
              <path d={qr.path} fill="#000000" />
            </svg>
          ) : (
            <Spinner />
          )}
        </div>
        <div className={styles.steps} style={{ flex: 1, minWidth: 180 }}>
          <span data-testid="wifi-qr-stage">
            {stage === 'done' && !event?.serial ? tt('wifi.qr.donePaired') : tt(STAGE_TEXT[stage])}
          </span>
          {stage === 'failed' && event?.error ? <span className={styles.muted}>{event.error.message}</span> : null}
          {!over ? <Spinner /> : null}
          {over ? (
            <div>
              <Button icon={<ArrowClockwiseRegular />} onClick={() => setRound((r) => r + 1)}>
                {tt('wifi.qr.again')}
              </Button>
            </div>
          ) : null}
        </div>
      </div>
      <span className={cx(styles.muted, styles.small)}>
        <QrCodeRegular aria-hidden /> {tt('wifi.qrNote')}
      </span>
    </>
  );
}
