import { AnimatePresence, motion } from 'framer-motion';
import { Direction, Tooltip as RadixTooltip } from 'radix-ui';
import type { CSSProperties } from 'react';
import { useSelectedDevice } from '../app/queries';
import { usePrefs } from '../state/prefs';
import { useSection, useUi } from '../state/ui';
import { AppDocumentsScreen } from '../screens/appDocuments/AppDocumentsScreen';
import { AppsScreen } from '../screens/apps/AppsScreen';
import { ComingNextScreen } from '../screens/ComingNextScreen';
import { FilesScreen } from '../screens/files/FilesScreen';
import { OverviewScreen } from '../screens/overview/OverviewScreen';
import { PhotosScreen } from '../screens/photos/PhotosScreen';
import { SettingsScreen } from '../screens/SettingsScreen';
import { WelcomeScreen } from '../screens/welcome/WelcomeScreen';
import { CommandPalette } from './CommandPalette';
import { useDesktopEvents } from './desktop';
import { ConflictDialog } from './ConflictDialog';
import { NAV, navEnabled } from './nav';
import { NavRail, TopTabs } from './Navigation';
import styles from './Shell.module.css';
import { TitleBar } from './TitleBar';
import { Toaster } from './Toaster';
import { TransferDrawer } from './TransferDrawer';

function Screen() {
  const device = useSelectedDevice();
  const section = useSection();
  const global = useUi((s) => s.globalSection);

  if (global === 'settings' || section === 'settings') return <SettingsScreen />;
  if (!device || device.state !== 'ready') return <WelcomeScreen device={device} />;

  const entry = NAV.find((n) => n.section === section);
  const usable = !entry || navEnabled(entry, device);
  switch (usable ? section : 'overview') {
    case 'overview':
      return <OverviewScreen device={device} />;
    case 'photos':
      return <PhotosScreen device={device} />;
    case 'files':
      return <FilesScreen device={device} />;
    case 'appDocuments':
      return <AppDocumentsScreen device={device} />;
    case 'apps':
      return <AppsScreen device={device} />;
    default:
      return <ComingNextScreen section={section} />;
  }
}

export function AppShell() {
  useDesktopEvents();
  const nav = usePrefs((s) => s.navLayout);
  const dir = usePrefs((s) => (s.language === 'fa' ? 'rtl' : 'ltr'));
  const device = useSelectedDevice();
  const section = useSection();
  const screenKey = `${device?.id ?? 'none'}:${device?.state === 'ready' ? section : 'welcome'}`;
  const style = (device?.colorHex ? { '--device-color': device.colorHex } : {}) as CSSProperties;

  return (
    <Direction.Provider dir={dir}>
      <RadixTooltip.Provider delayDuration={400}>
        <div className={styles.shell} style={style}>
          <TitleBar />
          {nav === 'tabs' ? <TopTabs /> : null}
          <div className={styles.body}>
            {nav === 'rail' ? <NavRail /> : null}
            <main className={styles.content} id="content" data-testid="content">
              <div className={styles.layer}>
                <AnimatePresence mode="wait" initial={false}>
                  <motion.div
                    key={screenKey}
                    className={styles.screen}
                    initial={{ opacity: 0, y: 6 }}
                    animate={{ opacity: 1, y: 0 }}
                    exit={{ opacity: 0 }}
                    transition={{ duration: 0.2, ease: [0.2, 0, 0, 1] }}
                  >
                    <Screen />
                  </motion.div>
                </AnimatePresence>
              </div>
            </main>
          </div>
          <TransferDrawer />
          <Toaster />
          <CommandPalette />
          <ConflictDialog />
        </div>
      </RadixTooltip.Provider>
    </Direction.Provider>
  );
}
