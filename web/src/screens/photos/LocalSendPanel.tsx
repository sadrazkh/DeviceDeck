import {
  CheckmarkCircleRegular,
  PlugDisconnectedRegular,
  SearchRegular,
  WarningRegular,
  Wifi1Regular,
} from '@fluentui/react-icons';
import { useEffect, useMemo, useState } from 'react';
import { receiverToTarget, type LocalSendReceiver, type LocalSendTarget } from '../../bridge/localSend';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Badge, RadioCards, Spinner } from '../../components/ui';
import { useClient } from '../../app/bridgeContext';
import { useI18n } from '../../i18n';
import { useLocalSendDiscovery } from './localSendQueries';
import styles from './LocalSend.module.css';
import backupStyles from './Backup.module.css';

/**
 * Before sending with LocalSend: the three steps on the iPhone, and a live line that says whether LocalSend answers
 * over the cable (looking → found «name» over USB, or not open / in the background). When the cable route doesn't
 * answer, the person can look on Wi-Fi and pick the phone. The chosen receiver goes up through `onTarget`.
 */
export function LocalSendPanel({
  device,
  active,
  onTarget,
}: {
  device: DeviceSummary;
  /** False while another route is chosen: no looking then. */
  active: boolean;
  onTarget(target: LocalSendTarget | null): void;
}) {
  const { t } = useI18n();
  const [wifi, setWifi] = useState(false);
  const [picked, setPicked] = useState<string | null>(null);
  const client = useClient();
  const found = useLocalSendDiscovery(device, { enabled: active, wifi });
  const discovery = found.data;
  const usb = discovery?.usbReceiver ?? null;
  const wifiList = useMemo(() => discovery?.wifi ?? [], [discovery?.wifi]);

  // The cable wins while it answers; on Wi-Fi the likely phone is picked for the person, who can change it.
  const wifiChoice: LocalSendReceiver | null = wifi
    ? (wifiList.find((r) => r.id === picked) ?? wifiList.find((r) => r.likely) ?? null)
    : null;
  const receiver = wifi && wifiChoice ? wifiChoice : usb;
  const targetKey = receiver?.id ?? null;
  // Only when the receiver itself changes: the objects are new on every poll.
  useEffect(() => onTarget(receiver ? receiverToTarget(receiver) : null), [targetKey]); // eslint-disable-line react-hooks/exhaustive-deps

  let tone: 'found' | 'warn' | 'looking' = 'looking';
  let icon = <Spinner />;
  let text = t('localsend.status.looking', { device: device.name });
  if (receiver?.route === 'wifi') {
    tone = 'found';
    icon = <Wifi1Regular />;
    text = t('localsend.status.foundWifi', { name: receiver.alias });
  } else if (discovery?.usb === 'found' && usb) {
    tone = 'found';
    icon = <CheckmarkCircleRegular />;
    text = t('localsend.status.foundUsb', { name: usb.alias });
  } else if (discovery?.usb === 'notListening') {
    tone = 'warn';
    icon = <PlugDisconnectedRegular />;
    text = t('localsend.status.notListening', { device: device.name });
  } else if (discovery?.usb === 'notAnswering') {
    tone = 'warn';
    icon = <WarningRegular />;
    text = t('localsend.status.notAnswering', { device: device.name });
  } else if (discovery?.usb === 'unavailable') {
    tone = 'warn';
    icon = <WarningRegular />;
    text = t('localsend.status.unavailable', { device: device.name });
  }

  const searching = wifi && (discovery?.wifiSearching ?? true);
  const lookAgain = async () => {
    await client.call('localsend.discover', { deviceId: device.id, wifi: true, rescan: true }).catch(() => undefined);
    await found.refetch();
  };
  return (
    <div className={backupStyles.section}>
      <span className={backupStyles.label}>{t('localsend.steps.title')}</span>
      <ol className={styles.steps}>
        <li>{t('localsend.steps.install')}</li>
        <li>{t('localsend.steps.open')}</li>
        <li>{t('localsend.steps.accept')}</li>
      </ol>

      <div
        className={cx(styles.status, tone === 'found' && styles.found, tone === 'warn' && styles.warn)}
        role="status"
        aria-live="polite"
        data-testid="localsend-status"
        data-state={receiver ? `found-${receiver.route}` : (discovery?.usb ?? 'looking')}
      >
        <span className={styles.statusIcon} aria-hidden>
          {icon}
        </span>
        <span className={styles.statusText}>{text}</span>
      </div>

      {!wifi && discovery && discovery.usb !== 'found' ? (
        <Button icon={<Wifi1Regular />} onClick={() => setWifi(true)} data-testid="localsend-wifi-look">
          {t('localsend.wifi.look')}
        </Button>
      ) : null}

      {wifi ? (
        <div className={styles.wifi} data-testid="localsend-wifi">
          {wifiList.length > 0 ? (
            <div className={styles.stretch} data-testid="localsend-wifi-list">
              <RadioCards
                label={t('localsend.wifi.pick')}
                value={wifiChoice?.id ?? ''}
                onChange={setPicked}
                options={wifiList.map((r) => ({
                  value: r.id,
                  label: (
                    <>
                      {r.alias}{' '}
                      {r.likely ? (
                        <Badge tone="accent" className="t-caption">
                          {t('localsend.wifi.likely')}
                        </Badge>
                      ) : null}
                    </>
                  ),
                  hint: <span dir="ltr">{[r.deviceModel, r.address].filter(Boolean).join(' · ')}</span>,
                }))}
              />
            </div>
          ) : searching ? (
            <span className={cx(styles.row, backupStyles.hint)}>
              <Spinner /> {t('localsend.wifi.looking')}
            </span>
          ) : (
            <span className={backupStyles.hint}>{t('localsend.wifi.none')}</span>
          )}
          <div className={styles.row}>
            {!searching ? (
              <Button size="small" icon={<SearchRegular />} onClick={() => void lookAgain()} data-testid="localsend-wifi-again">
                {t('localsend.wifi.again')}
              </Button>
            ) : null}
            <Button size="small" variant="subtle" onClick={() => setWifi(false)}>
              {t('localsend.wifi.useCable')}
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
