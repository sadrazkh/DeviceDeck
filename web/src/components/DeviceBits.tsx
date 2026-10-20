import { PlugConnectedRegular, Wifi1Regular } from '@fluentui/react-icons';
import type { DeviceState, DeviceSummary } from '../bridge/types';
import { useI18n } from '../i18n';
import styles from './device.module.css';
import { StatusDot, type Tone } from './ui';

export function stateTone(state: DeviceState): Tone {
  switch (state) {
    case 'ready':
      return 'success';
    case 'awaitingTrust':
    case 'unauthorized':
    case 'locked':
    case 'connecting':
      return 'warning';
    case 'disconnected':
      return 'neutral';
  }
}

export function ConnectionBadge({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  return (
    <span className={styles.connection}>
      {device.connection === 'wifi' ? <Wifi1Regular aria-hidden /> : <PlugConnectedRegular aria-hidden />}
      {t(device.connection === 'wifi' ? 'device.connection.wifi' : 'device.connection.usb')}
    </span>
  );
}

export function DeviceStateDot({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  return <StatusDot tone={stateTone(device.state)} label={t(`device.state.${device.state}`)} />;
}

/** "iPhone 17 Pro" or the raw model when there is no marketing name. */
export function modelName(device: DeviceSummary): string {
  return device.marketingName ?? device.model;
}

export function osLabel(device: DeviceSummary, t: ReturnType<typeof useI18n>['t'], digits: (s: string) => string): string {
  return t(device.platform === 'ios' ? 'device.os.ios' : 'device.os.android', { version: digits(device.osVersion) });
}
