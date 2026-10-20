import { AppGenericRegular, ArrowSwapRegular, PhoneRegular } from '@fluentui/react-icons';
import { useEffect, useState, type DragEvent } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useFileRoots } from '../../app/queries';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Segmented, Skeleton, Switch, Tooltip } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useDevicePlace, useUi } from '../../state/ui';
import { useInsideDrop } from '../../shell/dragOut';
import { noPhoneFolderToast, PcPane } from '../../shell/pc/PcPane';
import { findFolder, useAppFiles } from '../appDocuments/appFiles';
import { AppGrid, folderLabel, PlaceIcon, placeLabel } from '../appDocuments/AppGrid';
import { ScreenLayout } from '../ScreenLayout';
import { FileBrowser, RootGlyph } from './FileBrowser';
import styles from './Files.module.css';

// ---------------------------------------------------------------- screen

/** The App documents grid inside Files on Android (an iPhone uses root null for it, as it starts there). */
export const APP_GRID = '@appFiles';

export function FilesScreen({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const client = useClient();
  const actions = useActions();
  const { root, path } = useDevicePlace().files;
  const setPlace = useUi((s) => s.setFilesPlace);
  const roots = useFileRoots(device);
  const appFiles = useAppFiles(device);
  const [swapped, setSwapped] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [dropRoot, setDropRoot] = useState<string | null>(null);
  const rootList = roots.data ?? [];
  const ios = device.platform === 'ios';
  const showsApps = device.capabilities.includes('appDocuments');

  // Android has one obvious place to start; an iPhone starts at its App documents.
  useEffect(() => {
    if (root === null && !ios && rootList[0]) setPlace({ root: rootList[0].key, path: '/' });
  }, [root, ios, rootList, setPlace]);

  const gridRoot = ios ? null : APP_GRID;
  const app = findFolder(appFiles.places, root);
  // An iPhone lists its file-sharing apps as roots too; the app files list names and draws them.
  const storageRoots = rootList.filter((r) => r.kind !== 'appDocuments' && (r.kind !== 'media' || advanced || root === r.key));
  const appPlaces = ios ? appFiles.places : [];
  const currentRoot = rootList.find((r) => r.key === root && r.kind !== 'appDocuments') ?? null;
  const iconUrl = (appId: string) => client.images.icon(device.id, appId);
  const labelOf = (key: string) => {
    const r = rootList.find((x) => x.key === key);
    return r ? (r.kind === 'media' ? t('files.mediaFolder') : r.label) : key;
  };
  const open = (rootKey: string | null, at = '/') => setPlace({ root: rootKey, path: at });
  const dropProps = (key: string) => ({
    'data-drop': dropRoot === key || undefined,
    onDragOver: (e: DragEvent) => {
      if (Array.from(e.dataTransfer.types).includes('Files')) {
        e.preventDefault();
        e.stopPropagation();
        setDropRoot(key);
      }
    },
    onDragLeave: () => setDropRoot((k) => (k === key ? null : k)),
    onDrop: (e: DragEvent) => {
      if (!Array.from(e.dataTransfer.types).includes('Files')) return;
      e.preventDefault();
      e.stopPropagation();
      setDropRoot(null);
      void actions.copyToDevice(device, key, '/', Array.from(e.dataTransfer.files));
    },
  });
  // PC files dragged from the PC pane and dropped on the phone side: copy them into the open folder.
  const phoneDropRef = useInsideDrop((drop) => {
    if (drop.source !== 'pc') return;
    if (currentRoot) void actions.copyToDevice(device, currentRoot.key, path, drop.paths);
    else noPhoneFolderToast(t);
  });

  return (
    <ScreenLayout title={t('files.title')} testId="screen-files" fill>
      <div className={styles.panes} data-swapped={swapped || undefined}>
        <PcPane device={device} phoneTarget={currentRoot ? { root: currentRoot.key, path, label: labelOf(currentRoot.key) } : null} />
        <Tooltip content={t('files.swap')}>
          <Button className={styles.swap} variant="subtle" icon={<ArrowSwapRegular />} aria-label={t('files.swap')} onClick={() => setSwapped((s) => !s)} />
        </Tooltip>
        <section ref={phoneDropRef} className={`${styles.pane} ${styles.phonePane}`} aria-label={device.name}>
          <div className={styles.paneHeader}>
            <span className={styles.paneTitle}>
              <PhoneRegular aria-hidden />
              {device.name}
            </span>
          </div>
          <div className={styles.phoneBody}>
            <nav className={styles.places} aria-label={t('files.places')}>
              {!ios ? (
                <>
                  <div className={styles.placesTitle}>{t('files.places')}</div>
                  {storageRoots.map((r) => (
                    <button key={r.key} type="button" className={styles.place} aria-current={r.key === root} onClick={() => open(r.key)} {...dropProps(r.key)}>
                      <RootGlyph appId={r.appId} kind={r.kind} iconUrl={iconUrl} className={styles.placeIcon} glyphClass={styles.placeIconGlyph} />
                      <span className={styles.placeLabel}>{labelOf(r.key)}</span>
                    </button>
                  ))}
                </>
              ) : null}
              {showsApps ? (
                <>
                  <div className={styles.placesTitle}>{t('files.appDocuments')}</div>
                  <button type="button" className={styles.place} aria-current={root === gridRoot} onClick={() => open(gridRoot)} data-testid="place-app-documents">
                    <span className={styles.placeIconGlyph} aria-hidden>
                      <AppGenericRegular />
                    </span>
                    <span className={styles.placeLabel}>{t('files.appDocuments')}</span>
                  </button>
                  {appPlaces.map((place) => (
                    <button
                      key={place.id}
                      type="button"
                      className={styles.place}
                      aria-current={place.folders.some((f) => f.rootKey === root)}
                      onClick={() => open(place.folders[0]!.rootKey)}
                      {...dropProps(place.folders[0]!.rootKey)}
                    >
                      <PlaceIcon device={device} place={place} className={styles.placeIcon} glyphClassName={styles.placeIconGlyph} />
                      <span className={styles.placeLabel}>{placeLabel(place, t)}</span>
                    </button>
                  ))}
                </>
              ) : null}
              {ios
                ? storageRoots.map((r) => (
                    <button key={r.key} type="button" className={styles.place} aria-current={r.key === root} onClick={() => open(r.key)} {...dropProps(r.key)}>
                      <RootGlyph appId={r.appId} kind={r.kind} iconUrl={iconUrl} className={styles.placeIcon} glyphClass={styles.placeIconGlyph} />
                      <span className={styles.placeLabel}>{labelOf(r.key)}</span>
                    </button>
                  ))
                : null}
              {ios && rootList.some((r) => r.kind === 'media') ? (
                <div className={styles.advanced}>
                  <Switch checked={advanced} onChange={setAdvanced} label={t('files.showAdvanced')} />
                </div>
              ) : null}
            </nav>

            <div className={styles.main}>
              {roots.error ? (
                <div className={styles.banner}>
                  <ErrorBanner error={roots.error} onRetry={() => void roots.refetch()} />
                </div>
              ) : app ? (
                <FileBrowser
                  device={device}
                  root={app.folder.rootKey}
                  path={path}
                  rootLabel={app.place.folders.length > 1 ? `${placeLabel(app.place, t)} · ${folderLabel(app.folder.label, t)}` : placeLabel(app.place, t)}
                  rootIcon={<PlaceIcon device={device} place={app.place} glyphClassName={styles.crumbGlyph} />}
                  onNavigate={(p) => open(app.folder.rootKey, p)}
                  exit={{ label: t('files.appDocuments'), onExit: () => open(gridRoot) }}
                  folderSwitch={
                    app.place.folders.length > 1 ? (
                      <Segmented
                        label={t('appFiles.folders')}
                        value={app.folder.rootKey}
                        onChange={(key) => open(key)}
                        options={app.place.folders.map((f) => ({ value: f.rootKey, label: folderLabel(f.label, t) }))}
                      />
                    ) : null
                  }
                />
              ) : currentRoot ? (
                <FileBrowser
                  device={device}
                  root={currentRoot.key}
                  path={path}
                  rootLabel={labelOf(currentRoot.key)}
                  onNavigate={(p) => open(currentRoot.key, p)}
                  exit={ios ? { label: t('files.appDocuments'), onExit: () => open(null) } : undefined}
                  privateData={appFiles.privateData}
                />
              ) : roots.isPending || appFiles.isPending ? (
                <div className={styles.skeletons}>
                  {[0, 1, 2].map((i) => (
                    <Skeleton key={i} height={72} />
                  ))}
                </div>
              ) : (
                <div className={styles.chooser}>
                  <h2 className="t-subtitle">{showsApps ? t('files.appDocuments') : t('files.chooseRoot')}</h2>
                  {appFiles.error ? <ErrorBanner error={appFiles.error} onRetry={appFiles.refetch} /> : null}
                  {showsApps && !appFiles.error && appFiles.places.length === 0 ? <p className="t-secondary">{ios ? t('files.noSharingApps') : t('appFiles.none.android')}</p> : null}
                  <AppGrid device={device} places={appFiles.places.filter((p) => !p.folders.every((f) => f.empty) || ios)} onOpen={(key) => open(key)} />
                </div>
              )}
            </div>
          </div>
        </section>
      </div>
    </ScreenLayout>
  );
}
