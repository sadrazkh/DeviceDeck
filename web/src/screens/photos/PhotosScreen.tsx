import {
  ArrowExportRegular,
  ArrowImportRegular,
  DeleteRegular,
  DesktopArrowDownRegular,
  DismissRegular,
  ImageAddRegular,
  EyeRegular,
  ImageMultipleRegular,
  ImageRegular,
  LockClosedRegular,
  LockOpenRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'framer-motion';
import { Slider } from 'radix-ui';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AlbumKind, DeviceSummary, PhotoAlbum, PhotoAsset } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys, useAlbums, usePhotoLibrary } from '../../app/queries';
import { Button } from '../../components/Button';
import { ConfirmDialog } from '../../components/Dialog';
import { DropOverlay, useFileDrop } from '../../components/DropZone';
import { ErrorBanner } from '../../components/ErrorState';
import { Banner, EmptyState, LinearProgress, Segmented, Skeleton, Spinner } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { clickSelect, emptySelection, pruneSelection, selectAll, toggleGroup, toggleSelect, type ClickModifiers, type Selection } from '../../state/selection';
import { useDevicePlace, useUi } from '../../state/ui';
import { ScreenLayout } from '../ScreenLayout';
import { BackupPanel } from './BackupPanel';
import { addsPhotosThroughApps, canBackUp, useBackupPanelRequest, useBackupStatus } from './backupQueries';
import backupStyles from './Backup.module.css';
import { ExportDialog } from './ExportDialog';
import { ImportToAppDialog } from './ImportToAppDialog';
import { PhotoJobBanner, useActivePhotoJob } from './PhotoJobBanner';
import { groupByMonth } from './grouping';
import { usePhotoDragOut } from '../../shell/dragOut';
import { Lightbox } from './Lightbox';
import { PhotoGrid, type PhotoGridHandle } from './PhotoGrid';
import styles from './Photos.module.css';

type Filter = 'all' | 'photos' | 'videos' | 'live' | 'favorites';

const FILTERS: Record<Filter, (a: PhotoAsset) => boolean> = {
  all: () => true,
  photos: (a) => a.kind !== 'video',
  videos: (a) => a.kind === 'video',
  live: (a) => a.kind === 'livePhoto',
  favorites: (a) => a.isFavorite,
};

const SMART_ALBUM_NAMES: Partial<Record<AlbumKind, MessageKey>> = {
  all: 'photos.album.all',
  camera: 'photos.album.camera',
  screenshots: 'photos.album.screenshots',
  videos: 'photos.album.videos',
  live: 'photos.album.live',
  favorites: 'photos.album.favorites',
};

/** The iPhone's Hidden album (id "hidden"; a "hidden" album kind is proposed for the contract). */
export function isHiddenAlbum(album: Pick<PhotoAlbum, 'id' | 'kind'>): boolean {
  return album.id === 'hidden' || (album.kind as string) === 'hidden';
}

function AlbumList({
  albums,
  current,
  onSelect,
  android,
  hiddenRevealed,
}: {
  albums: PhotoAlbum[];
  current: string;
  onSelect(id: string): void;
  android: boolean;
  hiddenRevealed: boolean;
}) {
  const { t, n } = useI18n();
  const hidden = albums.filter(isHiddenAlbum);
  const smart = albums.filter((a) => a.kind !== 'user' && a.kind !== 'folder' && !isHiddenAlbum(a));
  const own = albums.filter((a) => (a.kind === 'user' || a.kind === 'folder') && !isHiddenAlbum(a));
  const item = (album: PhotoAlbum) => {
    const label = SMART_ALBUM_NAMES[album.kind];
    return (
      <button
        key={album.id}
        type="button"
        className={styles.album}
        aria-current={album.id === current}
        onClick={() => onSelect(album.id)}
        data-testid={`album-${album.id}`}
      >
        <span className={styles.albumName}>{label ? t(label) : album.name}</span>
        <span className={styles.albumCount}>{n(album.count)}</span>
      </button>
    );
  };
  // Collapsed and locked by default: no count until the user reveals it.
  const hiddenItem = (album: PhotoAlbum) => (
    <button
      key={album.id}
      type="button"
      className={styles.album}
      aria-current={album.id === current}
      onClick={() => onSelect(album.id)}
      data-testid="album-hidden"
      data-locked={!hiddenRevealed || undefined}
    >
      <span className={styles.albumIcon} aria-hidden>
        {hiddenRevealed ? <LockOpenRegular /> : <LockClosedRegular />}
      </span>
      <span className={styles.albumName}>{t('photos.hidden')}</span>
      {hiddenRevealed ? (
        <span className={styles.albumCount}>{n(album.count)}</span>
      ) : (
        <span className="visually-hidden">{t('photos.hidden.locked')}</span>
      )}
    </button>
  );
  return (
    <nav className={styles.albums} aria-label={t(android ? 'photos.folders' : 'photos.albums')}>
      {smart.map(item)}
      {own.length > 0 ? <div className={styles.albumsTitle}>{t(android ? 'photos.folders' : 'photos.myAlbums')}</div> : null}
      {own.map(item)}
      {hidden.length > 0 ? <div className={styles.albumsDivider} role="separator" /> : null}
      {hidden.map(hiddenItem)}
    </nav>
  );
}

function Scrubber({ labels, current, onJump }: { labels: { group: number; label: string }[]; current: number; onJump(group: number): void }) {
  const { t } = useI18n();
  const currentLabel = [...labels].reverse().find((l) => l.group <= current)?.group;
  return (
    <nav className={styles.scrubber} aria-label={t('photos.scrubber')}>
      {labels.map((l) => (
        <button key={l.group} type="button" className={styles.scrub} data-current={l.group === currentLabel || undefined} onClick={() => onJump(l.group)}>
          {l.label}
        </button>
      ))}
    </nav>
  );
}

export function PhotosScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, n, bytes } = i18n;
  const client = useClient();
  const actions = useActions();
  const albumId = useDevicePlace().albumId;
  const setAlbum = useUi((s) => s.setAlbum);
  const indexProgress = useUi((s) => s.indexProgress[device.id]);
  const albums = useAlbums(device);
  const queryClient = useQueryClient();
  const hiddenRevealed = useUi((s) => s.hiddenRevealed[device.id] ?? false);
  const setHiddenRevealed = useUi((s) => s.setHiddenRevealed);
  const selectedAlbum = (albums.data ?? []).find((a) => a.id === albumId);
  const hiddenSelected = isHiddenAlbum(selectedAlbum ?? { id: albumId, kind: 'user' });
  const locked = hiddenSelected && !hiddenRevealed;
  // A locked Hidden album requests nothing from the phone: no items, no thumbnails.
  const library = usePhotoLibrary(device, albumId, { enabled: !locked });
  const [filter, setFilter] = useState<Filter>('all');
  const [tileTarget, setTileTarget] = useState(136);
  const [selection, setSelection] = useState<Selection>(emptySelection);
  const [viewer, setViewer] = useState<number | null>(null);
  const [exporting, setExporting] = useState<PhotoAsset[] | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [visibleGroup, setVisibleGroup] = useState(0);
  const grid = useRef<PhotoGridHandle>(null);
  const importInput = useRef<HTMLInputElement>(null);
  // Photos backup and adding photos to an iPhone app (round 2).
  const [backupOpen, setBackupOpen] = useState(false);
  // A notification asked for this phone's Back up panel (round 3).
  const panelRequest = useBackupPanelRequest((s) => s.deviceId);
  useEffect(() => {
    if (panelRequest !== device.id) return;
    useBackupPanelRequest.getState().clear();
    setBackupOpen(true);
  }, [panelRequest, device.id]);
  const [toAppFiles, setToAppFiles] = useState<File[] | null>(null);
  const backupStatus = useBackupStatus(device);
  const newCount = backupStatus.data?.count.newCount ?? 0;
  const photoJob = useActivePhotoJob(device.id);
  const throughApps = addsPhotosThroughApps(device);

  const assets = useMemo(() => (filter === 'all' ? library.assets : library.assets.filter(FILTERS[filter])), [library.assets, filter]);
  const groups = useMemo(() => groupByMonth(assets, i18n.monthKey), [assets, i18n.monthKey]);
  const byId = useMemo(() => new Map(library.assets.map((a) => [a.id, a])), [library.assets]);
  const order = useMemo(() => assets.map((a) => a.id), [assets]);

  // A new album, filter or phone starts with nothing selected; deleted items drop out.
  useEffect(() => setSelection(emptySelection), [albumId, filter, device.id]);
  useEffect(() => setSelection((s) => pruneSelection(s, new Set(byId.keys()))), [byId]);

  const selectedAssets = useMemo(() => [...selection.ids].map((id) => byId.get(id)).filter((a): a is PhotoAsset => !!a), [selection, byId]);
  const selectedSize = selectedAssets.reduce((sum, a) => sum + a.size, 0);
  const dragOutTile = usePhotoDragOut(device, assets, selection, selectedAssets);

  const onClickTile = useCallback(
    (index: number, mods: ClickModifiers) => {
      const id = order[index];
      if (id) setSelection((s) => (s.ids.size > 0 && !mods.ctrl && !mods.shift ? toggleSelect(s, id) : clickSelect(s, id, mods, order)));
    },
    [order],
  );
  const onToggleTile = useCallback(
    (index: number) => {
      const id = order[index];
      if (id) setSelection((s) => toggleSelect(s, id));
    },
    [order],
  );
  const onOpen = useCallback((index: number) => setViewer(index), []);
  const onToggleGroup = useCallback(
    (g: number) => {
      const group = groups[g];
      if (group) setSelection((s) => toggleGroup(s, order.slice(group.start, group.start + group.count)));
    },
    [groups, order],
  );

  const scrubLabels = useMemo(() => {
    // The newest months by name, then one label per year.
    const labels: { group: number; label: string }[] = [];
    let lastYear = '';
    groups.forEach((g, index) => {
      if (!g.labelDate) return;
      const year = i18n.year(g.labelDate);
      if (index < 6) labels.push({ group: index, label: i18n.monthShort(g.labelDate) });
      else if (year !== lastYear) labels.push({ group: index, label: year });
      lastYear = year;
    });
    return labels.slice(0, 18);
  }, [groups, i18n]);

  const canImport = device.capabilities.includes('photosImport');
  const dropVerdict = canImport
    ? { ok: true, message: t('photos.dropAdd') }
    : throughApps
      ? { ok: true, message: t('photos.toApp.drop') }
      : { ok: false, message: t('photos.dropIphone') };
  const drop = useFileDrop({
    verdict: () => dropVerdict,
    onDrop: (files) => (canImport ? void actions.importPhotos(device, files) : throughApps ? setToAppFiles(files) : undefined),
  });

  const albumList = albums.data ?? [];
  const all = albumList.find((a) => a.kind === 'all');
  const videos = albumList.find((a) => a.kind === 'videos');
  const indexing = indexProgress && indexProgress.done < indexProgress.total ? indexProgress : null;
  const loading = library.isPending;
  const loadingMore = !loading && library.assets.length < library.total;

  const startExport = (list: PhotoAsset[]) => {
    if (list.length > 0) setExporting(list);
  };

  /** Locks the Hidden album again and forgets what was read from it. */
  const lockHidden = () => {
    setHiddenRevealed(device.id, false);
    setViewer(null);
    setSelection(emptySelection);
    queryClient.removeQueries({ queryKey: keys.photos(device.id, albumId) });
  };

  return (
    <ScreenLayout
      title={t('photos.title')}
      testId="screen-photos"
      subtitle={all && videos ? t('photos.subtitle', { photos: all.count - videos.count, videos: videos.count }) : ' '}
      actions={
        <>
          {hiddenSelected && hiddenRevealed ? (
            <Button icon={<LockClosedRegular />} onClick={lockHidden} data-testid="hidden-lock">
              {t('photos.hidden.lock')}
            </Button>
          ) : null}
          <Segmented
            label={t('photos.filter')}
            value={filter}
            onChange={setFilter}
            options={(['all', 'photos', 'videos', ...(device.platform === 'ios' ? (['live'] as const) : []), 'favorites'] as Filter[]).map((f) => ({
              value: f,
              label: t(`photos.filter.${f}`),
            }))}
          />
          <label className={styles.size}>
            <ImageRegular aria-hidden />
            <Slider.Root className={styles.slider} min={96} max={260} step={4} value={[tileTarget]} onValueChange={([v]) => v && setTileTarget(v)} aria-label={t('photos.gridSize')}>
              <Slider.Track className={styles.sliderTrack}>
                <Slider.Range className={styles.sliderRange} />
              </Slider.Track>
              <Slider.Thumb className={styles.sliderThumb} aria-label={t('photos.gridSize')} />
            </Slider.Root>
            <ImageMultipleRegular aria-hidden />
          </label>
          {canBackUp(device) ? (
            <Button icon={<DesktopArrowDownRegular />} onClick={() => setBackupOpen(true)} data-testid="backup-open">
              {t('photos.backup.button')}
              {newCount > 0 ? (
                <span className={backupStyles.badge} aria-label={t('photos.backup.badge', { count: newCount })} data-testid="backup-badge">
                  {n(newCount)}
                </span>
              ) : null}
            </Button>
          ) : null}
          {throughApps ? (
            <Button icon={<ImageAddRegular />} onClick={() => setToAppFiles([])} data-testid="to-app-open">
              {t('photos.toApp.button')}
            </Button>
          ) : null}
          {canImport ? (
            <>
              <Button icon={<ArrowImportRegular />} onClick={() => importInput.current?.click()}>
                {t('photos.import')}
              </Button>
              <input
                ref={importInput}
                type="file"
                multiple
                hidden
                accept="image/*,video/*"
                onChange={(e) => {
                  const files = Array.from(e.target.files ?? []);
                  e.target.value = '';
                  if (files.length) void actions.importPhotos(device, files);
                }}
              />
            </>
          ) : null}
        </>
      }
      banners={
        indexing ? (
          <Banner icon={<Spinner />} title={t('photos.indexing', { total: indexing.total })}>
            <div className={styles.indexing}>
              <LinearProgress value={indexing.done / indexing.total} label={t('photos.indexing', { total: indexing.total })} />
              <span className="t-caption t-secondary num">{t('photos.indexingDetail', { done: n(indexing.done), total: n(indexing.total) })}</span>
            </div>
          </Banner>
        ) : library.error ? (
          <ErrorBanner error={library.error} onRetry={() => void library.refetch()} />
        ) : photoJob ? (
          <PhotoJobBanner job={photoJob} device={device} />
        ) : null
      }
      fill
    >
      <div className={styles.layout}>
        <AlbumList albums={albumList} current={albumId} onSelect={setAlbum} android={device.platform === 'android'} hiddenRevealed={hiddenRevealed} />
        <div className={styles.gridWrap} {...drop.bind}>
          {locked ? (
            <div className={styles.lockedPanel} data-testid="hidden-locked">
              <EmptyState
                icon={<LockClosedRegular />}
                title={t('photos.hidden.lockedTitle')}
                body={t('photos.hidden.lockedBody')}
                action={
                  <Button variant="accent" icon={<EyeRegular />} onClick={() => setHiddenRevealed(device.id, true)} data-testid="hidden-reveal">
                    {t('photos.hidden.reveal')}
                  </Button>
                }
              />
            </div>
          ) : loading ? (
            <div className={styles.skeletonGrid} aria-busy>
              {Array.from({ length: 24 }, (_, i) => (
                <Skeleton key={i} height="auto" radius="var(--radius-tile)" className={styles.skeletonTile} />
              ))}
            </div>
          ) : assets.length === 0 ? (
            <EmptyState
              icon={<ImageMultipleRegular />}
              title={t('photos.empty.title')}
              body={library.assets.length > 0 ? t('photos.emptyFilter.body') : t('photos.empty.body', { device: device.name })}
            />
          ) : (
            <PhotoGrid
              handleRef={grid}
              assets={assets}
              groups={groups}
              selection={selection}
              tileTarget={tileTarget}
              thumbUrl={(a) => client.images.thumb(device.id, a.id, 256)}
              onClickTile={onClickTile}
              onToggleTile={onToggleTile}
              onOpen={onOpen}
              onToggleGroup={onToggleGroup}
              onSelectAll={() => setSelection(selectAll(order))}
              onClear={() => setSelection(emptySelection)}
              onDelete={() => selection.ids.size > 0 && device.capabilities.includes('photosDelete') && setConfirmDelete(true)}
              onExport={() => startExport(selectedAssets)}
              onVisibleGroup={setVisibleGroup}
              onDragOut={dragOutTile}
            />
          )}
          {loadingMore ? (
            <span className={styles.loadingMore}>
              <Spinner />
              {t('photos.loading', { loaded: n(library.assets.length), total: n(library.total) })}
            </span>
          ) : null}

          <AnimatePresence>
            {selection.ids.size > 0 ? (
              <motion.div
                className={styles.selectionBar}
                initial={{ opacity: 0, y: 16 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: 16 }}
                transition={{ duration: 0.2, ease: [0.2, 0, 0, 1] }}
                role="toolbar"
                aria-label={t('common.selected', { count: selection.ids.size })}
                data-testid="selection-bar"
              >
                <span className={styles.selectionCount}>{t('common.selected', { count: selection.ids.size })}</span>
                <span className={styles.selectionSize}>{bytes(selectedSize)}</span>
                <Button variant="accent" icon={<ArrowExportRegular />} onClick={() => startExport(selectedAssets)} data-testid="export-selected">
                  {t('photos.exportToPc')}
                </Button>
                {device.capabilities.includes('photosDelete') ? (
                  <Button variant="danger" icon={<DeleteRegular />} onClick={() => setConfirmDelete(true)}>
                    {t('common.delete')}
                  </Button>
                ) : null}
                <Button variant="subtle" icon={<DismissRegular />} aria-label={t('common.clearSelection')} onClick={() => setSelection(emptySelection)} />
              </motion.div>
            ) : null}
          </AnimatePresence>
          <DropOverlay visible={drop.over} verdict={dropVerdict} />
        </div>
        {groups.length > 1 ? <Scrubber labels={scrubLabels} current={visibleGroup} onJump={(g) => grid.current?.scrollToGroup(g)} /> : null}
      </div>

      {viewer !== null ? (
        <Lightbox
          assets={assets}
          index={viewer}
          onIndex={setViewer}
          onClose={() => setViewer(null)}
          photoUrl={(a) => (a.kind === 'video' ? client.images.video(device.id, a.id, a.size) : client.images.photo(device.id, a.id))}
          thumbUrl={(a) => client.images.thumb(device.id, a.id, 256)}
          onExport={(a) => startExport([a])}
          isWebView={client.kind === 'webview'}
        />
      ) : null}
      {exporting ? (
        <ExportDialog
          device={device}
          assets={exporting}
          onClose={() => setExporting(null)}
          onStarted={() => {
            setExporting(null);
            setViewer(null);
            setSelection(emptySelection);
          }}
        />
      ) : null}
      {backupOpen ? <BackupPanel device={device} open onOpenChange={setBackupOpen} /> : null}
      {toAppFiles ? (
        <ImportToAppDialog device={device} open initialFiles={toAppFiles} onOpenChange={(open) => (open ? undefined : setToAppFiles(null))} />
      ) : null}
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        danger
        title={t('photos.delete.title', { count: selection.ids.size, device: device.name })}
        body={t('photos.delete.body')}
        confirmLabel={t('photos.delete.confirm', { count: selection.ids.size })}
        onConfirm={async () => {
          setConfirmDelete(false);
          const ok = await actions.deletePhotos(device, [...selection.ids]);
          if (ok) setSelection(emptySelection);
        }}
      />
    </ScreenLayout>
  );
}
