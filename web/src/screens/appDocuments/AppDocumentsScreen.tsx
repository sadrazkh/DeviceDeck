import { AppGenericRegular, ArrowClockwiseRegular, InfoRegular, LockClosedRegular, SearchRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { useApps } from '../../app/queries';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, EmptyState, Skeleton, Switch, Tooltip } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useDevicePlace, useUi } from '../../state/ui';
import { FileBrowser } from '../files/FileBrowser';
import { ScreenLayout } from '../ScreenLayout';
import { findFolder, isEmptyPlace, useAppFiles, type AppPlace } from './appFiles';
import { AppGrid, folderLabel, PlaceIcon, placeLabel } from './AppGrid';
import styles from './AppDocuments.module.css';

/** Matches a place by its name or id, ignoring case. */
export function matchesSearch(place: { id: string; label: string }, query: string): boolean {
  const q = query.trim().toLocaleLowerCase();
  return q.length === 0 || place.label.toLocaleLowerCase().includes(q) || place.id.toLocaleLowerCase().includes(q);
}

/** Screen 5 of the brief: the apps that take files, and each one's files. */
export function AppDocumentsScreen({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const queryClient = useQueryClient();
  const appFiles = useAppFiles(device);
  const { root, path } = useDevicePlace().appDocs;
  const setPlace = useUi((s) => s.setAppDocsPlace);
  const [showOthers, setShowOthers] = useState(false);
  const [search, setSearch] = useState('');
  const ios = device.platform === 'ios';
  const allApps = useApps(ios && showOthers ? device : null, false).data;
  const current = findFolder(appFiles.places, root);

  const refresh = () => {
    appFiles.refetch();
    void queryClient.invalidateQueries({ queryKey: ['appFiles.measure', device.id] });
    void queryClient.invalidateQueries({ queryKey: ['files.list', device.id] });
  };

  const visible = useMemo(() => appFiles.places.filter((p) => matchesSearch(p, search)), [appFiles.places, search]);
  const apps = visible.filter((p) => p.kind === 'app' && (ios || showOthers || !isEmptyPlace(p))).sort(byLabel);
  const shared = visible.filter((p) => p.kind === 'sharedFolder');
  const emptyHidden = ios ? 0 : visible.filter((p) => p.kind === 'app' && isEmptyPlace(p)).length;
  const others = ios && showOthers ? (allApps ?? []).filter((a) => !a.fileSharing && !appFiles.places.some((p) => p.appId === a.id) && matchesSearch({ id: a.id, label: a.name }, search)) : [];
  const sharingCount = appFiles.places.filter((p) => p.kind === 'app').length;

  if (current) {
    const { place, folder } = current;
    const label = placeLabel(place, t);
    return (
      <ScreenLayout title={t('appDocs.title')} subtitle={label} testId="screen-appDocuments" fill>
        <div className={styles.browserPane}>
          <FileBrowser
            device={device}
            root={folder.rootKey}
            path={path}
            rootLabel={place.folders.length > 1 ? `${label} · ${folderLabel(folder.label, t)}` : label}
            rootIcon={<PlaceIcon device={device} place={place} glyphClassName={styles.crumbGlyph} />}
            onNavigate={(p) => setPlace({ root: folder.rootKey, path: p })}
            exit={{ label: t('appDocs.title'), onExit: () => setPlace({ root: null, path: '/' }) }}
            folders={
              place.folders.length > 1
                ? { value: folder.rootKey, onChange: (key) => setPlace({ root: key, path: '/' }), options: place.folders.map((f) => ({ value: f.rootKey, label: folderLabel(f.label, t) })) }
                : undefined
            }
          />
        </div>
      </ScreenLayout>
    );
  }

  return (
    <ScreenLayout
      title={t('appDocs.title')}
      subtitle={appFiles.isPending ? ' ' : t('appDocs.subtitle', { count: sharingCount })}
      testId="screen-appDocuments"
      actions={
        <div className={styles.actions}>
          <label className={styles.search}>
            <SearchRegular aria-hidden />
            <input
              type="search"
              value={search}
              placeholder={t('appFiles.search')}
              aria-label={t('appFiles.search')}
              onChange={(e) => setSearch(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Escape') setSearch('');
              }}
              data-testid="app-search"
            />
          </label>
          <Switch checked={showOthers} onChange={setShowOthers} label={ios ? t('appDocs.showOthers') : t('appFiles.showEmpty')} />
          <Tooltip content={t('appFiles.refresh')}>
            <Button variant="subtle" icon={<ArrowClockwiseRegular />} aria-label={t('appFiles.refresh')} onClick={refresh} />
          </Tooltip>
        </div>
      }
    >
      <div className={styles.page}>
        {appFiles.error ? (
          <ErrorBanner error={appFiles.error} onRetry={appFiles.refetch} />
        ) : appFiles.isPending ? (
          <div className={styles.grid}>
            {[0, 1, 2].map((i) => (
              <Skeleton key={i} height={84} />
            ))}
          </div>
        ) : appFiles.places.length === 0 ? (
          <EmptyState icon={<AppGenericRegular />} title={t('appDocs.none.title')} body={ios ? t('appDocs.none.body') : t('appFiles.none.android')} />
        ) : (
          <>
            {appFiles.privateData === 'blocked' ? (
              <Banner tone="info" icon={<LockClosedRegular />} title={t('appFiles.android.blocked.title')}>
                {t('appFiles.android.blocked.body')}
              </Banner>
            ) : null}
            <p className={styles.hint}>
              <InfoRegular aria-hidden />
              {t('appFiles.hint')}
            </p>
            {apps.length === 0 && shared.length === 0 && others.length === 0 ? (
              <EmptyState icon={<SearchRegular />} title={t('appFiles.noMatch.title', { query: search.trim() })} body={t('appFiles.noMatch.body')} />
            ) : null}
            {apps.length > 0 || others.length > 0 ? (
              <AppGrid
                device={device}
                places={apps}
                others={others}
                onOpen={(key) => setPlace({ root: key, path: '/' })}
                label={t('appFiles.apps')}
                heading={!ios && shared.length > 0 ? <h2 className="t-subtitle">{t('appFiles.apps')}</h2> : undefined}
              />
            ) : null}
            {!ios && emptyHidden > 0 && !showOthers ? <p className="t-caption t-secondary">{t('appFiles.emptyHidden', { count: emptyHidden })}</p> : null}
            {shared.length > 0 ? (
              <AppGrid
                device={device}
                places={shared}
                onOpen={(key) => setPlace({ root: key, path: '/' })}
                label={t('appFiles.shared')}
                heading={<h2 className="t-subtitle">{t('appFiles.shared')}</h2>}
              />
            ) : null}
          </>
        )}
      </div>
    </ScreenLayout>
  );
}

const byLabel = (a: AppPlace, b: AppPlace) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' });
