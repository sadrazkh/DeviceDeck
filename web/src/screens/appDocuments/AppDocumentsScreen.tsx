import { AppGenericRegular, InfoRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';
import { useFileRoots } from '../../app/queries';
import { ErrorBanner } from '../../components/ErrorState';
import { EmptyState, Skeleton, Switch } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useDevicePlace, useUi } from '../../state/ui';
import { FileBrowser } from '../files/FileBrowser';
import { ScreenLayout } from '../ScreenLayout';
import { AppGrid } from './AppGrid';
import styles from './AppDocuments.module.css';

/** Screen 5 of the brief: the apps that accept files, and each one's files. */
export function AppDocumentsScreen({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const client = useClient();
  const roots = useFileRoots(device);
  const { root, path } = useDevicePlace().appDocs;
  const setPlace = useUi((s) => s.setAppDocsPlace);
  const [showOthers, setShowOthers] = useState(false);
  const sharing = (roots.data ?? []).filter((r) => r.kind === 'appDocuments');
  const current = sharing.find((r) => r.key === root) ?? null;

  if (current) {
    return (
      <ScreenLayout title={t('appDocs.title')} subtitle={current.label} testId="screen-appDocuments" fill>
        <div className={styles.browserPane}>
          <FileBrowser
            device={device}
            root={current.key}
            path={path}
            rootLabel={current.label}
            rootIcon={current.appId ? <img src={client.images.icon(device.id, current.appId)} alt="" /> : <AppGenericRegular />}
            onNavigate={(p) => setPlace({ root: current.key, path: p })}
            exit={{ label: t('appDocs.title'), onExit: () => setPlace({ root: null, path: '/' }) }}
          />
        </div>
      </ScreenLayout>
    );
  }

  return (
    <ScreenLayout
      title={t('appDocs.title')}
      subtitle={roots.data ? t('appDocs.subtitle', { count: sharing.length }) : ' '}
      testId="screen-appDocuments"
      actions={<Switch checked={showOthers} onChange={setShowOthers} label={t('appDocs.showOthers')} />}
    >
      <div className={styles.page}>
        {roots.error ? (
          <ErrorBanner error={roots.error} onRetry={() => void roots.refetch()} />
        ) : roots.isPending ? (
          <div className={styles.grid}>
            {[0, 1, 2].map((i) => (
              <Skeleton key={i} height={84} />
            ))}
          </div>
        ) : sharing.length === 0 ? (
          <EmptyState icon={<AppGenericRegular />} title={t('appDocs.none.title')} body={t('appDocs.none.body')} />
        ) : (
          <>
            <p className={styles.hint}>
              <InfoRegular aria-hidden />
              {t('appDocs.hint')}
            </p>
            <AppGrid device={device} roots={sharing} onOpen={(key) => setPlace({ root: key, path: '/' })} showOthers={showOthers} />
          </>
        )}
      </div>
    </ScreenLayout>
  );
}
