// Settings › Window and Updates: close to the notification area, the phone-connected notification,
// and updates from GitHub Releases (Check now, automatic checks, Restart to update).

import { ArrowClockwiseRegular, ArrowSyncRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { UpdateStatus } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useSettings, useUpdateSettings } from '../app/queries';
import { Button } from '../components/Button';
import { Card, Spinner, Switch } from '../components/ui';
import { useI18n, type I18n } from '../i18n';
import styles from '../screens/Screen.module.css';
import { updateKey, useUpdateStatus } from './desktop';

function updateLine(status: UpdateStatus | undefined, { t, digits, percent }: I18n): string {
  if (!status) return '';
  const version = digits(status.currentVersion);
  const next = digits(status.newVersion ?? '');
  switch (status.state) {
    case 'notInstalled':
      return t('settings.update.notInstalled', { version });
    case 'checking':
      return t('settings.update.checking');
    case 'upToDate':
      return t('settings.update.upToDate', { version });
    case 'downloading':
      return t('settings.update.downloading', { version: next, percent: percent((status.percent ?? 0) / 100) });
    case 'ready':
      return t('settings.update.ready', { version: next });
    case 'error':
      return status.error ?? t('settings.update.error');
    default:
      return t('settings.update.idle', { version });
  }
}

export function DesktopSettings() {
  const i18n = useI18n();
  const { t, date } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const update = useUpdateSettings();
  const status = useUpdateStatus().data;
  const [busy, setBusy] = useState(false);
  if (!settings) return null;

  const installed = status?.state !== 'notInstalled';
  const working = busy || status?.state === 'checking' || status?.state === 'downloading';
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
  const restart = async () => {
    try {
      await client.call('app.applyUpdate');
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
      <Card
        title={t('settings.updates')}
        data-testid="settings-updates"
        actions={
          status?.state === 'ready' ? (
            <Button variant="accent" size="small" icon={<ArrowSyncRegular />} onClick={() => void restart()}>
              {t('settings.update.restart')}
            </Button>
          ) : (
            <Button size="small" icon={working ? <Spinner /> : <ArrowClockwiseRegular />} disabled={!installed || working} onClick={() => void check()} data-testid="check-updates">
              {t('settings.update.check')}
            </Button>
          )
        }
      >
        <p className={styles.settingLabel} data-testid="update-status">
          {updateLine(status, i18n)}
          {status?.lastChecked && status.state === 'upToDate' ? ` · ${t('settings.update.lastChecked', { when: date(status.lastChecked) })}` : ''}
        </p>
        {installed ? (
          <div className={styles.settingRow}>
            <Switch checked={settings.autoUpdate} onChange={(autoUpdate) => update.mutate({ autoUpdate })} label={t('settings.autoUpdate')} />
          </div>
        ) : null}
      </Card>
    </>
  );
}
