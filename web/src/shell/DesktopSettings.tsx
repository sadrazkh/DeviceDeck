// Settings › Window and Updates: close to the notification area, the phone-connected notification, and updates from
// GitHub Releases (round 4: the version, channel and kind of copy; the last check and what it found; Check now, What's
// new, Restart and update or Download; the automatic checks and what they do).

import { ArrowCircleDownRegular, ArrowClockwiseRegular, ArrowSyncCircleRegular, SparkleRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { UpdateStatus } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useSettings, useTransfers, useUpdateSettings } from '../app/queries';
import { Button } from '../components/Button';
import { Card, Spinner, Switch } from '../components/ui';
import { useI18n, type I18n } from '../i18n';
import { overallProgress } from '../state/transfers';
import styles from '../screens/Screen.module.css';
import { updateKey, useUpdateStatus } from './desktop';
import updateStyles from './Update.module.css';
import { markBannerSeen, offerOf, useWhatsNew } from './updates';

/** What is happening or was found, in a sentence; null when there is nothing to say beyond the facts. */
function statusLine(status: UpdateStatus, { t, digits, percent, date }: I18n): { text: string; tone?: 'error' } | null {
  const next = digits(status.newVersion ?? '');
  switch (status.state) {
    case 'checking':
      return { text: t('update.status.checking') };
    case 'downloading':
      return { text: t('update.status.downloading', { version: next, percent: percent((status.percent ?? 0) / 100) }) };
    case 'ready':
      return { text: t('update.status.ready', { version: next }) };
    case 'available':
      return { text: t('update.status.available', { version: next }) };
    case 'upToDate':
      return { text: t('update.status.upToDate', { version: digits(status.currentVersion) }) };
    case 'error':
      return {
        text: status.retryAt ? t('update.status.rateLimited', { time: date(status.retryAt) }) : t('update.status.offline'),
        tone: 'error',
      };
    default:
      return null;
  }
}

function lastCheck(status: UpdateStatus, { t, digits, date }: I18n): string {
  if (!status.lastChecked) return t('update.settings.never');
  const result =
    status.lastResult === 'upToDate'
      ? t('update.result.upToDate')
      : status.lastResult === 'newVersion'
        ? status.newVersion
          ? t('update.result.newVersion', { version: digits(status.newVersion) })
          : t('update.result.newVersionPlain')
        : status.lastResult === 'error'
          ? t('update.result.error')
          : null;
  const when = date(status.lastChecked);
  return result ? t('update.settings.lastCheckValue', { when, result }) : when;
}

export function DesktopSettings() {
  const i18n = useI18n();
  const { t, digits } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const update = useUpdateSettings();
  const status = useUpdateStatus().data;
  const showWhatsNew = useWhatsNew((s) => s.show);
  const copying = overallProgress(useTransfers().data ?? []).active > 0;
  const [busy, setBusy] = useState(false);
  if (!settings) return null;

  const working = busy || status?.state === 'checking' || status?.state === 'downloading';
  const offer = offerOf(status);
  const line = status ? statusLine(status, i18n) : null;
  const check = async () => {
    setBusy(true);
    try {
      queryClient.setQueryData(updateKey, await client.call('app.checkForUpdates'));
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(false);
    }
  };
  const act = async () => {
    if (!offer) return;
    markBannerSeen(offer.version);
    // With copies running, What's new says what a restart would stop first.
    if (offer.kind === 'ready' && copying) {
      showWhatsNew({ version: offer.version, reason: 'offer' });
      return;
    }
    try {
      if (offer.kind === 'ready') await client.call('app.applyUpdate');
      else await client.call('app.openReleasePage', { version: offer.version });
    } catch (error) {
      actions.fail(error);
    }
  };

  return (
    <>
      <Card title={t('settings.window')} data-testid="settings-window">
        <div className={styles.settingRow}>
          <Switch checked={settings.closeToTray} onChange={(closeToTray) => update.mutate({ closeToTray })} label={t('settings.closeToTray')} />
        </div>
        <div className={styles.settingRow}>
          <Switch checked={settings.notifyOnConnect} onChange={(notifyOnConnect) => update.mutate({ notifyOnConnect })} label={t('settings.notifyOnConnect')} />
        </div>
        <p className={styles.muted}>{t('settings.windowHint')}</p>
      </Card>
      <Card title={t('settings.updates')} data-testid="settings-updates">
        {status ? (
          <dl className={updateStyles.facts}>
            <dt>{t('update.settings.version')}</dt>
            <dd data-testid="update-version">{digits(status.currentVersion)}</dd>
            <dt>{t('update.settings.channel')}</dt>
            <dd>
              {t(status.channel === 'prerelease' ? 'update.channel.prerelease' : 'update.channel.stable')} · {status.architecture === 'arm64' ? 'ARM64' : 'x64'}
            </dd>
            <dt>{t('update.settings.install')}</dt>
            <dd>{t(status.install === 'portable' ? 'update.install.portable' : 'update.install.setup')}</dd>
            <dt>{t('update.settings.lastCheck')}</dt>
            <dd data-testid="update-last-check" title={lastCheck(status, i18n)}>
              {lastCheck(status, i18n)}
            </dd>
          </dl>
        ) : null}
        {line ? (
          <p className={updateStyles.status} data-tone={line.tone} data-testid="update-status" title={line.tone === 'error' ? (status?.error ?? undefined) : undefined}>
            {status?.state === 'checking' ? <Spinner /> : null}
            <span>{line.text}</span>
          </p>
        ) : null}
        <div className={updateStyles.buttons}>
          {offer ? (
            <Button
              variant="accent"
              size="small"
              icon={offer.kind === 'ready' ? <ArrowSyncCircleRegular /> : <ArrowCircleDownRegular />}
              onClick={() => void act()}
              data-testid="settings-update-act"
            >
              {offer.kind === 'ready' ? t('update.settings.restart') : t('update.settings.download')}
            </Button>
          ) : null}
          <Button size="small" icon={working ? <Spinner /> : <ArrowClockwiseRegular />} disabled={working || offer?.kind === 'ready'} onClick={() => void check()} data-testid="check-updates">
            {t('update.settings.check')}
          </Button>
          {status ? (
            <Button
              size="small"
              icon={<SparkleRegular />}
              onClick={() => showWhatsNew(offer ? { version: offer.version, reason: 'offer' } : { version: status.currentVersion, reason: 'current' })}
              data-testid="settings-whats-new"
            >
              {t('update.settings.whatsNew')}
            </Button>
          ) : null}
        </div>
        <div className={styles.settingRow}>
          <Switch checked={settings.autoUpdate} onChange={(autoUpdate) => update.mutate({ autoUpdate })} label={t('update.settings.auto')} />
        </div>
        <p className={updateStyles.autoHint} data-testid="update-auto-hint">
          {status?.install === 'portable' ? t('update.settings.autoPortable', { hours: 12 }) : t('update.settings.autoSetup', { hours: 6 })}
        </p>
      </Card>
    </>
  );
}
