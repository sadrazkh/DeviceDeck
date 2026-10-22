import { PhoneRegular, PlugDisconnectedRegular } from '@fluentui/react-icons';
import { useState, type CSSProperties } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { ANDROID_ID, IPHONE_ID } from '../../bridge/mock/mock-data';
import { useMock } from '../../app/bridgeContext';
import { useDevices, useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { DeviceArt } from '../../components/DeviceArt';
import { Dialog } from '../../components/Dialog';
import { Banner, Card, Spinner } from '../../components/ui';
import { DriversCard } from '../drivers/DriversCard';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './Welcome.module.css';

function CableArt() {
  const { t } = useI18n();
  return (
    <svg className={styles.art} viewBox="0 0 340 170" role="img" aria-label={t('welcome.illustration')}>
      {/* laptop */}
      <rect x="150" y="30" width="170" height="104" rx="8" className={styles.laptop} />
      <rect x="160" y="40" width="150" height="84" rx="3" className={styles.laptopScreen} />
      <path d="M134 134 H336 L326 146 H144 Z" className={styles.laptopBase} />
      {/* phone */}
      <rect x="22" y="36" width="58" height="114" rx="12" className={styles.phone} />
      <rect x="26" y="40" width="50" height="106" rx="9" className={styles.phoneScreen} />
      <rect x="41" y="45" width="20" height="6" rx="3" className={styles.phone} />
      {/* cable */}
      <path d="M51 150 C 51 166, 100 166, 120 150 S 150 128, 150 120" className={styles.cable} />
      <path d="M51 150 C 51 166, 100 166, 120 150 S 150 128, 150 120" className={styles.pulse} />
      <circle cx="235" cy="82" r="14" className={styles.spark} opacity="0.18" />
      <path d="M229 82 l4 4 l8 -9" className={styles.check} />
    </svg>
  );
}

const GUIDE_STEPS: Record<'ios' | 'android', MessageKey[]> = {
  ios: ['welcome.guide.iphone.1', 'welcome.guide.iphone.2', 'welcome.guide.iphone.3'],
  android: ['welcome.guide.android.1', 'welcome.guide.android.2', 'welcome.guide.android.3'],
};

function Guide({ platform, onClose }: { platform: 'ios' | 'android'; onClose(): void }) {
  const { t, n } = useI18n();
  const [step, setStep] = useState(0);
  const steps = GUIDE_STEPS[platform];
  const last = step === steps.length - 1;
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={t(platform === 'ios' ? 'welcome.guide.iphone.title' : 'welcome.guide.android.title')}
      wide
      footerStart={
        <div className={styles.guideDots} aria-hidden>
          {steps.map((s, i) => (
            <span key={s} className={styles.guideDot} data-active={i === step || undefined} />
          ))}
        </div>
      }
      footer={
        <>
          {step > 0 ? <Button onClick={() => setStep(step - 1)}>{t('common.back')}</Button> : null}
          <Button variant="accent" onClick={() => (last ? onClose() : setStep(step + 1))} autoFocus>
            {last ? t('welcome.guide.done') : t('welcome.guide.next')}
          </Button>
        </>
      }
    >
      <div className={styles.guide}>
        <div className={styles.guideArt} aria-hidden>
          <DeviceArt platform={platform} colorHex={platform === 'ios' ? '#E2733A' : '#8FA3B8'} width={64} />
        </div>
        <div>
          <div className={styles.guideStep}>{t('welcome.guide.step', { n: n(step + 1) })}</div>
          <p className="t-subtitle">{t(steps[step]!)}</p>
        </div>
      </div>
    </Dialog>
  );
}

function DeviceStateCard({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const mock = useMock();
  const jobs = useTransfers().data ?? [];
  const waiting = jobs.filter((j) => j.deviceId === device.id && j.state === 'waitingForDevice').length;
  const name = device.name;
  const style = (device.colorHex ? { '--device-color': device.colorHex } : {}) as CSSProperties;

  let title: string;
  let body: string;
  let waitingText: string | null = null;
  let prompt: string | null = null;
  switch (device.state) {
    case 'awaitingTrust':
      title = t('welcome.trust.title', { device: name });
      body = t('welcome.trust.body');
      waitingText = t('welcome.trust.waiting');
      prompt = 'Trust This Computer?';
      break;
    case 'unauthorized':
      title = t('welcome.adb.title', { device: name });
      body = t('welcome.adb.body');
      waitingText = t('welcome.adb.waiting');
      prompt = 'Allow USB debugging?';
      break;
    case 'locked':
      title = t('welcome.locked.title', { device: name });
      body = t('welcome.locked.body');
      waitingText = t('welcome.trust.waiting');
      break;
    case 'connecting':
      title = t('welcome.connecting.title', { device: name });
      body = t('welcome.connecting.body');
      waitingText = '';
      break;
    default:
      title = t('welcome.disconnected.title', { device: name });
      body = t('welcome.disconnected.body');
      break;
  }

  return (
    <div className={styles.state} style={style} data-testid={`welcome-state-${device.state}`}>
      <DeviceArt platform={device.platform} colorHex={device.colorHex} width={96} dimmed={device.state === 'disconnected'} />
      <div className={styles.stateText}>
        <h2 className="t-title">{title}</h2>
        <p className="t-secondary">{body}</p>
        {device.stateDetail ? (
          <p className={styles.detail} data-testid="state-detail">
            {device.stateDetail}
          </p>
        ) : null}
        {prompt ? (
          <div className={styles.prompt} aria-hidden>
            <div className={styles.promptTitle}>{prompt}</div>
          </div>
        ) : null}
        {device.state === 'disconnected' && waiting > 0 ? (
          <Banner tone="warning" icon={<PlugDisconnectedRegular />}>
            {t('transfers.pausedBanner', { device: name, count: waiting })}
          </Banner>
        ) : null}
        {waitingText !== null ? (
          <div className={styles.waiting}>
            <Spinner label={waitingText || title} />
            {waitingText}
          </div>
        ) : null}
        {mock && (device.state === 'awaitingTrust' || device.state === 'unauthorized' || device.state === 'locked') ? (
          <div className={styles.sim}>
            <span>{t('common.simulation')}:</span>
            <Button size="small" onClick={() => mock.approve(device.id)} data-testid="sim-approve">
              {device.state === 'unauthorized' ? t('welcome.simAllow') : t('welcome.simTrust')}
            </Button>
          </div>
        ) : null}
        {mock && device.state === 'disconnected' ? (
          <div className={styles.sim}>
            <span>{t('common.simulation')}:</span>
            <Button size="small" onClick={() => mock.plugBack(device.id)}>
              {t('palette.simPlugBack', { device: name })}
            </Button>
          </div>
        ) : null}
      </div>
    </div>
  );
}

/** Shown with no phone, or while the selected phone is not ready (Trust, USB debugging, locked, gone). */
export function WelcomeScreen({ device }: { device: DeviceSummary | null }) {
  const { t } = useI18n();
  const mock = useMock();
  const devices = useDevices().data ?? [];
  const [guide, setGuide] = useState<'ios' | 'android' | null>(null);

  return (
    <div className={styles.welcome} data-testid="screen-welcome">
      {device ? (
        <DeviceStateCard device={device} />
      ) : (
        <div className={styles.hero}>
          <CableArt />
          <h1 className="t-display">{t('welcome.title')}</h1>
          <p className={styles.subtitle}>{t('welcome.subtitle')}</p>
        </div>
      )}

      <div className={styles.cards}>
        <Card>
          <div className={styles.platformCard}>
            <DeviceArt platform="ios" colorHex="#E2733A" width={44} />
            <div className={styles.platformText}>
              <h2 className="t-subtitle">{t('welcome.iphone.title')}</h2>
              <p className={styles.platformBody}>{t('welcome.iphone.body')}</p>
              <div>
                <Button variant="link" onClick={() => setGuide('ios')}>
                  {t('welcome.showMe')}
                </Button>
              </div>
            </div>
          </div>
        </Card>
        <Card>
          <div className={styles.platformCard}>
            <DeviceArt platform="android" colorHex="#8FA3B8" width={44} />
            <div className={styles.platformText}>
              <h2 className="t-subtitle">{t('welcome.android.title')}</h2>
              <p className={styles.platformBody}>{t('welcome.android.body')}</p>
              <div>
                <Button variant="link" onClick={() => setGuide('android')}>
                  {t('welcome.showMe')}
                </Button>
              </div>
            </div>
          </div>
        </Card>
      </div>

      {/* Drivers card (v0.6): live status, and the one thing to do when a driver is missing or stopped. */}
      <DriversCard className={styles.driversCard} />

      {mock && (!devices.some((d) => d.id === IPHONE_ID) || !devices.some((d) => d.id === ANDROID_ID)) ? (
        <div className={styles.sim}>
          <span>{t('common.simulation')}:</span>
          {!devices.some((d) => d.id === IPHONE_ID) ? (
            <Button size="small" icon={<PhoneRegular />} onClick={() => mock.connectNew(IPHONE_ID)} data-testid="sim-iphone">
              {t('welcome.simIphone')}
            </Button>
          ) : null}
          {!devices.some((d) => d.id === ANDROID_ID) ? (
            <Button size="small" icon={<PhoneRegular />} onClick={() => mock.connectNew(ANDROID_ID)}>
              {t('welcome.simAndroid')}
            </Button>
          ) : null}
        </div>
      ) : null}

      {guide ? <Guide platform={guide} onClose={() => setGuide(null)} /> : null}
    </div>
  );
}
