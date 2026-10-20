import { ArchiveRegular, LockClosedRegular, PeopleRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { BackupInfo, DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, Card, EmptyState, Segmented, Skeleton } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useUi } from '../../state/ui';
import { backupDate, UnlockDialog } from '../backup/BackupBits';
import { useBackups, useBackupUi } from '../backup/backupQueries';
import { ScreenLayout } from '../ScreenLayout';
import { ContactsPane } from './ContactsPane';
import styles from './Contacts.module.css';
import { MessagesPane } from './MessagesPane';

type Tab = 'contacts' | 'messages';

/**
 * Contacts and messages, read from an iPhone backup on this PC (the phone's own, newest first; any other iPhone
 * backup can be picked). Android needs a helper app on the phone for these, which this version explains.
 */
export function ContactsScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t } = i18n;
  const backups = useBackups();
  const navigate = useUi((s) => s.navigate);
  const [tab, setTab] = useState<Tab>('contacts');
  const [chosen, setChosen] = useState<string | null>(null);
  const [unlocking, setUnlocking] = useState<BackupInfo | null>(null);
  const android = device.platform === 'android';

  const sources = (backups.data ?? [])
    .filter((b) => b.platform === 'ios' && b.state === 'complete')
    .sort((a, b) => Number(b.deviceId === device.id) - Number(a.deviceId === device.id) || (b.createdAt ?? '').localeCompare(a.createdAt ?? ''));
  const fallback = android ? null : (sources.find((b) => b.deviceId === device.id) ?? null);
  const backup = sources.find((b) => b.id === chosen) ?? fallback;

  useEffect(() => setChosen(null), [device.id]);

  const backUp = () => {
    useBackupUi.getState().openWizard(device.id);
    navigate('backup');
  };

  const picker =
    sources.length > 0 ? (
      <label className={styles.source}>
        <span className={styles.sourceLabel}>{t('contacts.source')}</span>
        <select className={styles.select} value={backup?.id ?? ''} onChange={(e) => setChosen(e.target.value || null)} data-testid="contacts-source">
          {backup ? null : <option value="">{t('contacts.sourceNone')}</option>}
          {sources.map((b) => (
            <option key={b.id} value={b.id}>
              {`${b.deviceName} · ${backupDate(b, i18n)}${b.encrypted ? ' 🔒' : ''}`}
            </option>
          ))}
        </select>
      </label>
    ) : null;

  let body;
  if (backups.error) {
    body = <ErrorBanner error={backups.error} onRetry={() => void backups.refetch()} />;
  } else if (backups.isPending) {
    body = <Skeleton height={240} />;
  } else if (!backup) {
    body = android ? (
      <Card className={styles.helper} data-testid="contacts-android">
        <EmptyState
          icon={<PeopleRegular />}
          title={t('contacts.android.title')}
          body={
            <>
              <p>{t('contacts.android.body')}</p>
              {sources.length > 0 ? <p>{t('contacts.android.iphone')}</p> : null}
            </>
          }
          action={picker ?? undefined}
        />
      </Card>
    ) : (
      <EmptyState
        icon={<ArchiveRegular />}
        title={t('contacts.noBackup.title')}
        body={t('contacts.noBackup.body', { device: device.name })}
        action={
          <div className={styles.toolbar}>
            {device.capabilities.includes('backup') ? (
              <Button variant="accent" onClick={backUp} data-testid="contacts-backup">
                {t('contacts.noBackup.action', { device: device.name })}
              </Button>
            ) : null}
            {picker}
          </div>
        }
      />
    );
  } else if (backup.locked) {
    body = (
      <EmptyState
        icon={<LockClosedRegular />}
        title={t('contacts.locked.title')}
        body={t('backup.unlock.body', { device: backup.deviceName, date: backupDate(backup, i18n) })}
        action={
          <Button variant="accent" onClick={() => setUnlocking(backup)} data-testid="contacts-unlock">
            {t('backup.unlock.submit')}
          </Button>
        }
      />
    );
  } else {
    body = tab === 'contacts' ? <ContactsPane backup={backup} /> : <MessagesPane backup={backup} />;
  }

  return (
    <ScreenLayout
      title={t('contacts.title')}
      subtitle={backup ? t('contacts.subtitle', { device: backup.deviceName, date: backupDate(backup, i18n) }) : ' '}
      testId="screen-contacts"
      fill
      actions={
        backup ? (
          <Segmented
            label={t('contacts.title')}
            value={tab}
            onChange={setTab}
            options={[
              { value: 'contacts', label: t('contacts.tab.contacts') },
              { value: 'messages', label: t('contacts.tab.messages') },
            ]}
          />
        ) : undefined
      }
      banners={
        android && backup ? <Banner tone="info" title={t('contacts.android.title')}>{t('contacts.android.short')}</Banner> : undefined
      }
    >
      <div className={styles.wrap}>
        {backup ? <div className={styles.toolbar}>{picker}</div> : null}
        {body}
      </div>
      <UnlockDialog backup={unlocking} onOpenChange={(open) => !open && setUnlocking(null)} onUnlocked={() => setUnlocking(null)} />
    </ScreenLayout>
  );
}
