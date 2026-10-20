import { FolderOpenRegular } from '@fluentui/react-icons';
import { useActions } from '../../app/actions';
import { useSettings, useUpdateSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { Card, Segmented, Switch } from '../../components/ui';
import { useI18n } from '../../i18n';
import screen from '../Screen.module.css';
import { PATTERN_PRESETS, exampleFolder } from './backupQueries';
import styles from './Backup.module.css';

/** Settings › Photo backup: the defaults a phone uses until it has options of its own. */
export function PhotosBackupSettings() {
  const { t } = useI18n();
  const settings = useSettings().data;
  const update = useUpdateSettings();
  const actions = useActions();
  if (!settings) return null;
  const today = new Date();
  const pattern = settings.photoBackupPattern;
  const presets: string[] = PATTERN_PRESETS.includes(pattern as (typeof PATTERN_PRESETS)[number]) ? [...PATTERN_PRESETS] : [...PATTERN_PRESETS, pattern];

  return (
    <Card title={t('settings.photoBackup')} data-testid="settings-photo-backup">
      <div className={screen.settingRow}>
        <span className={screen.settingLabel}>{t('settings.photoBackup.folder')}</span>
        <div className={styles.destination}>
          <span className={styles.path} title={settings.photoBackupFolder} data-testid="settings-backup-folder">
            {settings.photoBackupFolder}
          </span>
          <Button
            icon={<FolderOpenRegular />}
            onClick={async () => {
              const picked = await actions.pickFolder(t('settings.photoBackup.folder'));
              if (picked) update.mutate({ photoBackupFolder: picked });
            }}
          >
            {t('common.change')}
          </Button>
        </div>
      </div>
      <div className={screen.settingRow}>
        <span className={screen.settingLabel}>{t('photos.backup.folders')}</span>
        <Segmented
          label={t('photos.backup.folders')}
          value={pattern === '' ? 'none' : pattern}
          onChange={(v) => update.mutate({ photoBackupPattern: v === 'none' ? '' : v })}
          options={presets.map((p) => ({
            value: p === '' ? 'none' : p,
            label: p === '' ? t('photos.backup.oneFolder') : <span className="num">{`⁦${exampleFolder(p, today)}⁩`}</span>,
          }))}
        />
      </div>
      <div className={screen.settingRow}>
        <span className={screen.settingLabel}>{t('settings.photoBackup.format')}</span>
        <Switch
          checked={settings.photoBackupConvertHeic}
          onChange={(photoBackupConvertHeic) => update.mutate({ photoBackupConvertHeic })}
          label={t('photos.backup.convertHeic')}
        />
      </div>
      <p className={styles.fine}>{t('settings.photoBackup.note')}</p>
    </Card>
  );
}
