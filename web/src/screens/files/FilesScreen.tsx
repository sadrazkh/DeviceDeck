import { AppGenericRegular, ArrowSwapRegular, PhoneRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useFileRoots } from '../../app/queries';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Skeleton, Switch, Tooltip } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useDevicePlace, useUi } from '../../state/ui';
import { useInsideDrop } from '../../shell/dragOut';
import { noPhoneFolderToast, PcPane } from '../../shell/pc/PcPane';
import { AppGrid } from '../appDocuments/AppGrid';
import { ScreenLayout } from '../ScreenLayout';
import { FileBrowser, RootGlyph } from './FileBrowser';
import styles from './Files.module.css';

// ---------------------------------------------------------------- screen

export function FilesScreen({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const client = useClient();
  const actions = useActions();
  const { root, path } = useDevicePlace().files;
  const setPlace = useUi((s) => s.setFilesPlace);
  const roots = useFileRoots(device);
  const [swapped, setSwapped] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [dropRoot, setDropRoot] = useState<string | null>(null);
  const rootList = roots.data ?? [];
  const ios = device.platform === 'ios';

  // Android has one obvious place to start; an iPhone starts at its App documents.
  useEffect(() => {
    if (root === null && !ios && rootList[0]) setPlace({ root: rootList[0].key, path: '/' });
  }, [root, ios, rootList, setPlace]);

  const currentRoot = rootList.find((r) => r.key === root) ?? null;
  const visibleRoots = rootList.filter((r) => r.kind !== 'media' || advanced || root === r.key);
  const iconUrl = (appId: string) => client.images.icon(device.id, appId);
  const labelOf = (key: string) => {
    const r = rootList.find((x) => x.key === key);
    return r ? (r.kind === 'media' ? t('files.mediaFolder') : r.label) : key;
  };
  const open = (rootKey: string, at = '/') => setPlace({ root: rootKey, path: at });
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
              {ios ? (
                <>
                  <div className={styles.placesTitle}>{t('files.appDocuments')}</div>
                  <button type="button" className={styles.place} aria-current={root === null} onClick={() => setPlace({ root: null, path: '/' })}>
                    <span className={styles.placeIconGlyph} aria-hidden>
                      <AppGenericRegular />
                    </span>
                    <span className={styles.placeLabel}>{t('files.appDocuments')}</span>
                  </button>
                </>
              ) : (
                <div className={styles.placesTitle}>{t('files.places')}</div>
              )}
              {visibleRoots.map((r) => (
                <button
                  key={r.key}
                  type="button"
                  className={styles.place}
                  aria-current={r.key === root}
                  data-drop={dropRoot === r.key || undefined}
                  onClick={() => open(r.key)}
                  onDragOver={(e) => {
                    if (Array.from(e.dataTransfer.types).includes('Files')) {
                      e.preventDefault();
                      e.stopPropagation();
                      setDropRoot(r.key);
                    }
                  }}
                  onDragLeave={() => setDropRoot((k) => (k === r.key ? null : k))}
                  onDrop={(e) => {
                    if (!Array.from(e.dataTransfer.types).includes('Files')) return;
                    e.preventDefault();
                    e.stopPropagation();
                    setDropRoot(null);
                    void actions.copyToDevice(device, r.key, '/', Array.from(e.dataTransfer.files));
                  }}
                >
                  <RootGlyph appId={r.appId} kind={r.kind} iconUrl={iconUrl} className={styles.placeIcon} glyphClass={styles.placeIconGlyph} />
                  <span className={styles.placeLabel}>{labelOf(r.key)}</span>
                </button>
              ))}
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
              ) : root === null || !currentRoot ? (
                roots.isPending ? (
                  <div className={styles.skeletons}>
                    {[0, 1, 2].map((i) => (
                      <Skeleton key={i} height={72} />
                    ))}
                  </div>
                ) : (
                  <div className={styles.chooser}>
                    <h2 className="t-subtitle">{ios ? t('files.appDocuments') : t('files.chooseRoot')}</h2>
                    {ios && !rootList.some((r) => r.kind === 'appDocuments') ? <p className="t-secondary">{t('files.noSharingApps')}</p> : null}
                    <AppGrid device={device} roots={rootList} onOpen={(key) => open(key)} />
                  </div>
                )
              ) : (
                <FileBrowser
                  device={device}
                  root={currentRoot.key}
                  path={path}
                  rootLabel={labelOf(currentRoot.key)}
                  onNavigate={(p) => open(currentRoot.key, p)}
                  exit={ios ? { label: t('files.appDocuments'), onExit: () => setPlace({ root: null, path: '/' }) } : undefined}
                />
              )}
            </div>
          </div>
        </section>
      </div>
    </ScreenLayout>
  );
}
