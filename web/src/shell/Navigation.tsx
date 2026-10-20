import { useCallback, useState, type DragEvent } from 'react';
import { isAppPackage, useActions } from '../app/actions';
import { useSelectedDevice } from '../app/queries';
import { useMediaQuery } from '../app/useMediaQuery';
import { Tooltip } from '../components/ui';
import { useI18n } from '../i18n';
import { toast } from '../state/toasts';
import { useSection, useUi, type Section } from '../state/ui';
import { NAV, RAIL_COMPACT_QUERY, SETTINGS_NAV, navEnabled, type NavEntry } from './nav';
import styles from './Shell.module.css';

const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes('Files');

/** Files dropped on a nav item go there: Photos = the camera folder, Apps = install. */
function useNavDrop() {
  const device = useSelectedDevice();
  const actions = useActions();
  const { t } = useI18n();
  const [target, setTarget] = useState<Section | null>(null);

  const accepts = useCallback(
    (section: Section) => {
      if (!device || device.state !== 'ready') return false;
      if (section === 'photos') return device.capabilities.includes('photosImport');
      if (section === 'apps') return device.capabilities.includes('appsInstall');
      return false;
    },
    [device],
  );

  const bind = (section: Section) => ({
    onDragEnter: (e: DragEvent) => {
      if (hasFiles(e) && accepts(section)) {
        e.preventDefault();
        setTarget(section);
      }
    },
    onDragOver: (e: DragEvent) => {
      if (hasFiles(e) && accepts(section)) e.preventDefault();
    },
    onDragLeave: () => setTarget((s) => (s === section ? null : s)),
    onDrop: (e: DragEvent) => {
      if (!hasFiles(e) || !device || !accepts(section)) return;
      e.preventDefault();
      setTarget(null);
      const files = Array.from(e.dataTransfer.files);
      if (section === 'photos') void actions.importPhotos(device, files);
      if (section === 'apps') {
        const apps = files.filter((f) => isAppPackage(device, f.name));
        if (apps.length === 0) {
          toast({ tone: 'warning', title: t('apps.dropWrong', { device: device.name, types: device.platform === 'ios' ? '.ipa' : '.apk, .apks, .xapk' }) });
          return;
        }
        void actions.installApps(device, apps);
      }
    },
  });
  return { target, bind };
}

function useNavKeys() {
  const navigate = useUi((s) => s.navigate);
  return navigate;
}

export function NavRail() {
  const { t } = useI18n();
  const device = useSelectedDevice();
  const section = useSection();
  const navigate = useNavKeys();
  const compact = useMediaQuery(RAIL_COMPACT_QUERY);
  const drop = useNavDrop();

  const item = (entry: NavEntry) => {
    const current = entry.section === section && (device?.state === 'ready' || entry.section === 'settings');
    const Icon = current ? entry.iconSelected : entry.icon;
    const button = (
      <button
        key={entry.section}
        type="button"
        className={styles.navItem}
        aria-current={current ? 'page' : undefined}
        disabled={!navEnabled(entry, device)}
        onClick={() => navigate(entry.section)}
        data-drop={drop.target === entry.section || undefined}
        data-testid={`nav-${entry.section}`}
        {...drop.bind(entry.section)}
      >
        <span className={styles.navIcon} aria-hidden>
          <Icon />
        </span>
        <span className={styles.navLabel}>{t(entry.label)}</span>
      </button>
    );
    return compact ? (
      <Tooltip key={entry.section} content={t(entry.label)} side={document.documentElement.dir === 'rtl' ? 'left' : 'right'}>
        {button}
      </Tooltip>
    ) : (
      button
    );
  };

  return (
    <nav className={styles.rail} data-compact={compact || undefined} aria-label={t('shell.sections')}>
      {NAV.map(item)}
      <div className={styles.railSpacer} />
      {item(SETTINGS_NAV)}
    </nav>
  );
}

export function TopTabs() {
  const { t } = useI18n();
  const device = useSelectedDevice();
  const section = useSection();
  const navigate = useNavKeys();
  const drop = useNavDrop();
  return (
    <nav className={styles.tabs} aria-label={t('shell.sections')}>
      {[...NAV, SETTINGS_NAV].map((entry) => {
        const current = entry.section === section && (device?.state === 'ready' || entry.section === 'settings');
        const Icon = current ? entry.iconSelected : entry.icon;
        return (
          <button
            key={entry.section}
            type="button"
            className={styles.tab}
            aria-current={current ? 'page' : undefined}
            disabled={!navEnabled(entry, device)}
            onClick={() => navigate(entry.section)}
            data-testid={`nav-${entry.section}`}
            {...drop.bind(entry.section)}
          >
            <Icon aria-hidden />
            {t(entry.label)}
          </button>
        );
      })}
    </nav>
  );
}
