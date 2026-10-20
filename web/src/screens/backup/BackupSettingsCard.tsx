import { FolderOpenRegular } from '@fluentui/react-icons';
import { useActions } from '../../app/actions';
import { useSettings, useUpdateSettings } from '../../app/queries';
import type { BackupDeviceInfo, DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Card } from '../../components/ui';
import { useI18n } from '../../i18n';
import styles from './Backup.module.css';

export type EncryptionMode = 'on' | 'off' | 'change';

const KEEP_CHOICES = [1, 2, 3, 5, 10, 20];

/**
 * Where backups go and how many of each phone to keep; on the Backup screen also the iPhone's encryption.
 * Settings shows it without a phone.
 */
export function BackupSettingsCard({
  device,
  info,
  onEncryption,
}: {
  device?: DeviceSummary | null;
  info?: BackupDeviceInfo;
  onEncryption?(mode: EncryptionMode): void;
}) {
  const { t, bytes } = useI18n();
  const settings = useSettings().data;
  const update = useUpdateSettings();
  const actions = useActions();
  const ios = device?.platform === 'ios';
  const keep = settings?.backupKeep ?? 3;
  const choices = KEEP_CHOICES.includes(keep) ? KEEP_CHOICES : [...KEEP_CHOICES, keep].sort((a, b) => a - b);

  const change = async () => {
    const folder = await actions.pickFolder(t('backup.settings.chooseFolder'));
    if (folder) update.mutate({ backupFolder: folder });
  };

  return (
    <Card title={t('backup.settings.title')} data-testid="backup-settings">
      <div className={styles.settingsRow}>
        <span className={styles.settingsLabel}>{t('backup.settings.folder')}</span>
        <span className={`${styles.path} ${styles.note}`} data-testid="backup-folder">
          {settings?.backupFolder ?? ''}
        </span>
        {info?.freeBytes != null ? <span className={styles.note}>{t('backup.settings.free', { free: bytes(info.freeBytes) })}</span> : null}
        <div className={styles.inline}>
          <Button size="small" onClick={() => void change()}>
            {t('common.change')}
          </Button>
          {settings ? (
            <Button size="small" variant="subtle" icon={<FolderOpenRegular />} onClick={() => void actions.revealInExplorer(settings.backupFolder)}>
              {t('backup.settings.open')}
            </Button>
          ) : null}
        </div>
      </div>
      <div className={styles.settingsRow}>
        <label className={styles.settingsLabel} htmlFor="backup-keep">
          {t('backup.settings.keep')}
        </label>
        <select
          id="backup-keep"
          className={styles.select}
          value={keep}
          onChange={(e) => update.mutate({ backupKeep: Number(e.target.value) })}
          data-testid="backup-keep"
        >
          {choices.map((c) => (
            <option key={c} value={c}>
              {t('backup.settings.keepValue', { count: c })}
            </option>
          ))}
        </select>
        <span className={styles.note}>{t('backup.settings.keepHint')}</span>
      </div>
      {ios && info && onEncryption ? (
        <div className={styles.settingsRow} data-testid="backup-encryption">
          <span className={styles.settingsLabel}>{t('backup.encryption.title')}</span>
          <span className={styles.note}>
            {info.encrypted === null ? t('common.unknown') : t(info.encrypted ? 'backup.encryption.on' : 'backup.encryption.off')}
          </span>
          {info.cloudBackupEnabled ? <span className={styles.note}>{t('backup.cloud.on')}</span> : null}
          <div className={styles.inline}>
            {info.encrypted ? (
              <>
                <Button size="small" onClick={() => onEncryption('change')}>
                  {t('backup.encryption.change')}
                </Button>
                <Button size="small" variant="subtle" onClick={() => onEncryption('off')} data-testid="encryption-off">
                  {t('backup.encryption.turnOff')}
                </Button>
              </>
            ) : (
              <Button size="small" onClick={() => onEncryption('on')} disabled={info.encrypted === null} data-testid="encryption-on">
                {t('backup.encryption.turnOn')}
              </Button>
            )}
          </div>
        </div>
      ) : null}
    </Card>
  );
}
