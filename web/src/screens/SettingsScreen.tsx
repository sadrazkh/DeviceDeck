import { useHello, useUpdateSettings } from '../app/queries';
import { Card, Segmented } from '../components/ui';
import { useI18n } from '../i18n';
import { usePrefs } from '../state/prefs';
import { BackupSettingsCard } from './backup/BackupSettingsCard';
import { PhotosBackupSettings } from './photos/PhotosBackupSettings';
import { ScreenLayout } from './ScreenLayout';
import styles from './Screen.module.css';

/** The basics only for now: language, theme, density and the layout switch. */
export function SettingsScreen() {
  const { t, digits } = useI18n();
  const prefs = usePrefs();
  const update = useUpdateSettings();
  const hello = useHello();
  return (
    <ScreenLayout title={t('settings.title')} subtitle={hello.data ? t('settings.about', { version: digits(hello.data.version) }) : undefined} testId="screen-settings">
      <div className={styles.settings}>
        <Card title={t('settings.general')}>
          <div className={styles.settingRow}>
            <span className={styles.settingLabel}>{t('settings.language')}</span>
            <Segmented
              label={t('settings.language')}
              value={prefs.language}
              onChange={(language) => update.mutate({ language })}
              options={[
                { value: 'en', label: 'English' },
                { value: 'fa', label: 'فارسی' },
              ]}
            />
          </div>
          <div className={styles.settingRow}>
            <span className={styles.settingLabel}>{t('settings.theme')}</span>
            <Segmented
              label={t('settings.theme')}
              value={prefs.theme}
              onChange={(theme) => update.mutate({ theme })}
              options={[
                { value: 'system', label: t('settings.theme.system') },
                { value: 'light', label: t('settings.theme.light') },
                { value: 'dark', label: t('settings.theme.dark') },
              ]}
            />
          </div>
          <div className={styles.settingRow}>
            <span className={styles.settingLabel}>{t('settings.density')}</span>
            <Segmented
              label={t('settings.density')}
              value={prefs.density}
              onChange={(density) => update.mutate({ density })}
              options={[
                { value: 'comfortable', label: t('settings.density.comfortable') },
                { value: 'compact', label: t('settings.density.compact') },
              ]}
            />
          </div>
          <div className={styles.settingRow}>
            <span className={styles.settingLabel}>{t('settings.navigation')}</span>
            <Segmented
              label={t('settings.navigation')}
              value={prefs.navLayout}
              onChange={prefs.setNavLayout}
              options={[
                { value: 'rail', label: t('settings.navigation.rail') },
                { value: 'tabs', label: t('settings.navigation.tabs') },
              ]}
            />
          </div>
        </Card>
        <PhotosBackupSettings />
        <BackupSettingsCard />
        <p className={styles.muted}>{t('settings.more')}</p>
      </div>
    </ScreenLayout>
  );
}
