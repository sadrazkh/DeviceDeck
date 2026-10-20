import {
  AppGenericRegular,
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  ArrowSortDownRegular,
  ArrowSortUpRegular,
  ArrowSwapRegular,
  ArrowUpRegular,
  ChevronRightRegular,
  ClosedCaptionRegular,
  DeleteRegular,
  DocumentRegular,
  FolderAddRegular,
  FolderOpenRegular,
  FolderRegular,
  FolderZipRegular,
  ImageRegular,
  InfoRegular,
  LaptopRegular,
  LockClosedRegular,
  MusicNote2Regular,
  OpenRegular,
  PhoneRegular,
  RenameRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { isBridgeError } from '../../bridge/errors';
import type { DeviceSummary, FileEntry, FileRoot } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys, useApps, useFileList, useFileRoots, useSettings, useTransfers, useUpdateSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { ConfirmDialog, Dialog } from '../../components/Dialog';
import { DropOverlay, useFileDrop, type DropVerdict } from '../../components/DropZone';
import { describeError, ErrorBanner } from '../../components/ErrorState';
import { ContextMenu, type MenuEntry } from '../../components/Menu';
import { Banner, EmptyState, LinearProgress, Skeleton, Switch, TextField, Tooltip } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { usePrefs } from '../../state/prefs';
import { clickSelect, emptySelection, selectAll, type ClickModifiers, type Selection } from '../../state/selection';
import { jobFraction } from '../../state/transfers';
import { useDevicePlace, useUi } from '../../state/ui';
import { progressLine } from '../../shell/TransferDrawer';
import { ScreenLayout } from '../ScreenLayout';
import { KIND_LABEL, extensionOf, isAndroidBlocked, joinPath, kindOf, parentPath, sortEntries, type FileKind, type SortKey } from './fileKinds';
import styles from './Files.module.css';

const KIND_ICON: Record<FileKind, typeof FolderRegular> = {
  folder: FolderRegular,
  video: VideoRegular,
  image: ImageRegular,
  audio: MusicNote2Regular,
  document: DocumentRegular,
  archive: FolderZipRegular,
  app: AppGenericRegular,
  subtitle: ClosedCaptionRegular,
  file: DocumentRegular,
};

// ---------------------------------------------------------------- PC pane

function PcPane({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t } = i18n;
  const settings = useSettings().data;
  const updateSettings = useUpdateSettings();
  const actions = useActions();
  const folder = settings?.fileCopyFolder ?? '';
  const jobs = (useTransfers().data ?? [])
    .filter((j) => j.deviceId === device.id && (j.kind === 'copyToPc' || j.kind === 'copyToDevice'))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
    .slice(0, 6);
  return (
    <section className={`${styles.pane} ${styles.pcPane}`} aria-label={t('files.pc')}>
      <div className={styles.paneHeader}>
        <span className={styles.paneTitle}>
          <LaptopRegular aria-hidden />
          {t('files.pc')}
        </span>
      </div>
      <div className={styles.pcBody}>
        <div>
          <div className={styles.pcLabel}>{t('files.pcDestination')}</div>
          <div className={styles.path}>{folder}</div>
        </div>
        <div className={styles.pcActions}>
          <Button
            size="small"
            icon={<FolderOpenRegular />}
            onClick={async () => {
              const picked = await actions.pickFolder(t('files.pcDestination'));
              if (picked) updateSettings.mutate({ fileCopyFolder: picked });
            }}
          >
            {t('common.change')}
          </Button>
          <Button size="small" icon={<OpenRegular />} onClick={() => void actions.revealInExplorer(folder)} disabled={!folder}>
            {t('files.pcOpen')}
          </Button>
        </div>
        <p className={styles.hint}>
          <InfoRegular aria-hidden />
          <span>{t('files.pcHint')}</span>
        </p>
        <div>
          <div className={styles.pcLabel}>{t('files.pcRecent')}</div>
          {jobs.length === 0 ? (
            <p className="t-caption t-secondary">{t('files.pcRecentEmpty')}</p>
          ) : (
            <ul className={styles.recent}>
              {jobs.map((job) => (
                <li key={job.id} className={styles.recentItem}>
                  <span className={styles.recentTitle} title={job.title}>
                    {job.title}
                  </span>
                  <LinearProgress value={jobFraction(job)} tone={job.state === 'done' ? 'success' : job.state === 'failed' ? 'danger' : job.state === 'running' ? 'accent' : 'paused'} label={job.title} />
                  <span className="t-secondary">{progressLine(job, i18n, device)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
        <p className={styles.hint}>
          <InfoRegular aria-hidden />
          <span>{t('files.pcBrowseLater')}</span>
        </p>
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- places and chooser

function rootIcon(root: FileRoot, iconUrl: (appId: string) => string, className?: string, glyphClass?: string) {
  if (root.appId) return <img className={className} src={iconUrl(root.appId)} alt="" />;
  return (
    <span className={glyphClass} aria-hidden>
      {root.kind === 'media' ? <ImageRegular /> : <PhoneRegular />}
    </span>
  );
}

function RootChooser({ device, roots, onOpen, onDropFiles }: { device: DeviceSummary; roots: FileRoot[]; onOpen(root: string): void; onDropFiles(root: string, files: File[]): void }) {
  const { t, bytes } = useI18n();
  const client = useClient();
  const apps = useApps(device, false).data ?? [];
  const [dropOn, setDropOn] = useState<string | null>(null);
  const sharing = roots.filter((r) => r.kind === 'appDocuments');
  return (
    <div className={styles.chooser}>
      <h2 className="t-subtitle">{device.platform === 'ios' ? t('files.appDocuments') : t('files.chooseRoot')}</h2>
      {device.platform === 'ios' && sharing.length === 0 ? <p className="t-secondary">{t('files.noSharingApps')}</p> : null}
      <div className={styles.appGrid}>
        {(device.platform === 'ios' ? sharing : roots).map((root) => {
          const app = apps.find((a) => a.id === root.appId);
          return (
            <button
              key={root.key}
              type="button"
              className={styles.appCard}
              onClick={() => onOpen(root.key)}
              data-drop={dropOn === root.key || undefined}
              data-testid={`root-${root.key}`}
              onDragOver={(e) => {
                if (Array.from(e.dataTransfer.types).includes('Files')) {
                  e.preventDefault();
                  e.stopPropagation();
                  setDropOn(root.key);
                }
              }}
              onDragLeave={() => setDropOn((k) => (k === root.key ? null : k))}
              onDrop={(e) => {
                if (!Array.from(e.dataTransfer.types).includes('Files')) return;
                e.preventDefault();
                e.stopPropagation();
                setDropOn(null);
                onDropFiles(root.key, Array.from(e.dataTransfer.files));
              }}
            >
              {rootIcon(root, (id) => client.images.icon(device.id, id), styles.appIcon, styles.placeIconGlyph)}
              <span className={styles.appText}>
                <span className="t-strong">{root.label}</span>
                {app ? <span className={styles.appMeta}>{bytes(app.dataBytes)}</span> : null}
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- the phone's file list

interface Editing {
  mode: 'rename' | 'mkdir';
  entry?: FileEntry;
  name: string;
  error: string | null;
  busy: boolean;
}

function FileTable({
  device,
  root,
  path,
  entries,
  onOpenFolder,
  onUp,
  selection,
  setSelection,
  onRename,
  onDelete,
  onCopyToPc,
  onNewFolder,
  readOnly,
}: {
  device: DeviceSummary;
  root: string;
  path: string;
  entries: FileEntry[];
  onOpenFolder(path: string): void;
  onUp(): void;
  selection: Selection;
  setSelection(s: Selection | ((s: Selection) => Selection)): void;
  onRename(entry: FileEntry): void;
  onDelete(paths: string[]): void;
  onCopyToPc(paths: string[]): void;
  onNewFolder(): void;
  readOnly: boolean;
}) {
  const { t, bytes, date, lang } = useI18n();
  const density = usePrefs((s) => s.density);
  const rowHeight = density === 'compact' ? 32 : 40;
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'name', desc: false });
  const [active, setActive] = useState(0);
  const scroller = useRef<HTMLDivElement>(null);
  const collator = useMemo(() => new Intl.Collator(lang === 'fa' ? 'fa-IR' : 'en-GB', { numeric: true, sensitivity: 'base' }), [lang]);
  const sorted = useMemo(() => sortEntries(entries, sort.key, sort.desc, collator), [entries, sort, collator]);
  const order = useMemo(() => sorted.map((e) => e.path), [sorted]);
  const android = device.platform === 'android';

  const virtualizer = useVirtualizer({ count: sorted.length, getScrollElement: () => scroller.current, estimateSize: () => rowHeight, overscan: 12 });
  useEffect(() => virtualizer.measure(), [rowHeight, virtualizer]);
  useEffect(() => {
    setActive(0);
    virtualizer.scrollToOffset(0);
  }, [root, path, virtualizer]);

  const blocked = (e: FileEntry) => android && e.isDirectory && isAndroidBlocked(e.path);
  const open = (e: FileEntry) => {
    if (e.isDirectory && !blocked(e)) onOpenFolder(e.path);
  };
  const selectedPaths = () => (selection.ids.size > 0 ? [...selection.ids] : sorted[active] ? [sorted[active].path] : []);

  const click = (index: number, mods: ClickModifiers) => {
    setActive(index);
    const id = order[index];
    if (id) setSelection((s) => clickSelect(s, id, mods, order));
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const ctrl = e.ctrlKey || e.metaKey;
    const move = (to: number) => {
      const next = Math.max(0, Math.min(sorted.length - 1, to));
      setActive(next);
      virtualizer.scrollToIndex(next, { align: 'auto' });
      const id = order[next];
      if (id) setSelection((s) => clickSelect(s, id, { shift: e.shiftKey, ctrl: e.shiftKey && ctrl }, order));
    };
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      move(active + 1);
    } else if (e.key === 'ArrowUp' && !e.altKey) {
      e.preventDefault();
      move(active - 1);
    } else if (e.key === 'Home') {
      e.preventDefault();
      move(0);
    } else if (e.key === 'End') {
      e.preventDefault();
      move(sorted.length - 1);
    } else if (e.key === 'Enter') {
      const entry = sorted[active];
      if (entry) open(entry);
    } else if (e.key === 'Backspace' || (e.altKey && e.key === 'ArrowUp')) {
      e.preventDefault();
      onUp();
    } else if (e.key === 'F2') {
      e.preventDefault();
      const entry = sorted[active];
      if (entry && !readOnly && !entry.readOnly) onRename(entry);
    } else if (e.key === 'Delete') {
      e.preventDefault();
      if (!readOnly) onDelete(selectedPaths());
    } else if (ctrl && e.key.toLowerCase() === 'a') {
      e.preventDefault();
      setSelection(selectAll(order));
    } else if (e.key === 'Escape' && selection.ids.size > 0) {
      e.preventDefault();
      setSelection(emptySelection);
    }
  };

  const header = (key: SortKey, label: MessageKey, className?: string) => (
    <span className={className} role="columnheader" aria-sort={sort.key === key ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className={styles.sort} onClick={() => setSort((s) => ({ key, desc: s.key === key ? !s.desc : false }))}>
        {t(label)}
        {sort.key === key ? sort.desc ? <ArrowSortDownRegular aria-hidden /> : <ArrowSortUpRegular aria-hidden /> : null}
      </button>
    </span>
  );

  const menuFor = (entry: FileEntry): MenuEntry[] => {
    const paths = selection.ids.has(entry.path) ? [...selection.ids] : [entry.path];
    return [
      ...(entry.isDirectory ? [{ key: 'open', label: t('common.open'), icon: <FolderOpenRegular />, disabled: blocked(entry), onSelect: () => open(entry) }] : []),
      { key: 'copy', label: t('files.copyToPc'), icon: <ArrowDownloadRegular />, disabled: blocked(entry), onSelect: () => onCopyToPc(paths) },
      { key: 'rename', label: t('common.rename'), icon: <RenameRegular />, hint: 'F2', disabled: readOnly || entry.readOnly || paths.length > 1, onSelect: () => onRename(entry) },
      { key: 'delete', label: t('common.delete'), icon: <DeleteRegular />, hint: 'Del', danger: true, disabled: readOnly || entry.readOnly, onSelect: () => onDelete(paths) },
      'separator',
      { key: 'mkdir', label: t('files.newFolder'), icon: <FolderAddRegular />, disabled: readOnly, onSelect: onNewFolder },
    ];
  };

  return (
    <div
      className={styles.table}
      role="grid"
      aria-label={path}
      aria-multiselectable
      aria-rowcount={sorted.length}
      tabIndex={0}
      onKeyDown={onKeyDown}
      data-testid="file-table"
      aria-activedescendant={sorted[active] ? `file-${active}` : undefined}
    >
      <div className={styles.columns} role="row">
        {header('name', 'files.column.name')}
        {header('size', 'files.column.size', styles.alignEnd)}
        {header('kind', 'files.column.kind', styles.kindColumn)}
        {header('modified', 'files.column.modified')}
      </div>
      <div className={styles.scroll} ref={scroller}>
        <div className={styles.inner} style={{ height: virtualizer.getTotalSize() }}>
          {virtualizer.getVirtualItems().map((item) => {
            const entry = sorted[item.index]!;
            const kind = kindOf(entry);
            const Icon = KIND_ICON[kind];
            const isBlocked = blocked(entry);
            const row = (
              <div
                id={`file-${item.index}`}
                role="row"
                aria-selected={selection.ids.has(entry.path)}
                className={styles.row}
                style={{ transform: `translateY(${item.start}px)`, height: item.size }}
                data-active={item.index === active || undefined}
                data-blocked={isBlocked || undefined}
                onClick={(e) => click(item.index, { ctrl: e.ctrlKey || e.metaKey, shift: e.shiftKey })}
                onDoubleClick={() => open(entry)}
                onContextMenu={() => {
                  if (!selection.ids.has(entry.path)) click(item.index, {});
                }}
              >
                <span className={styles.nameCell} role="gridcell">
                  <span className={styles.fileIcon} data-kind={kind} aria-hidden>
                    <Icon />
                  </span>
                  <span className={styles.name} title={entry.name}>
                    {entry.name}
                  </span>
                  {entry.readOnly ? <LockClosedRegular className={styles.lockedIcon} aria-label={t('files.readOnly')} /> : null}
                  {isBlocked ? <span className={styles.blockedTag}>{t('files.blocked')}</span> : null}
                </span>
                <span className={`${styles.cell} ${styles.alignEnd}`} role="gridcell">
                  {entry.isDirectory ? '—' : bytes(entry.size)}
                </span>
                <span className={`${styles.cell} ${styles.kindColumn}`} role="gridcell">
                  {kind === 'file' ? t('files.kind.file', { ext: extensionOf(entry.name).toUpperCase() || '—' }) : t(KIND_LABEL[kind])}
                </span>
                <span className={styles.cell} role="gridcell">
                  {entry.modified ? date(entry.modified) : '—'}
                </span>
              </div>
            );
            return isBlocked ? (
              <Tooltip key={item.key} content={t('files.denied.android')}>
                {row}
              </Tooltip>
            ) : (
              <ContextMenu key={item.key} items={menuFor(entry)}>
                {row}
              </ContextMenu>
            );
          })}
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- screen

export function FilesScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, n, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const { root, path } = useDevicePlace().files;
  const setPlace = useUi((s) => s.setFilesPlace);
  const roots = useFileRoots(device);
  const [swapped, setSwapped] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [selection, setSelection] = useState<Selection>(emptySelection);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [deleting, setDeleting] = useState<string[] | null>(null);
  const [dropRoot, setDropRoot] = useState<string | null>(null);
  const list = useFileList(device, root, path);
  const rootList = roots.data ?? [];
  const ios = device.platform === 'ios';

  // Android has one obvious place to start; an iPhone starts at its App documents.
  useEffect(() => {
    if (root === null && !ios && rootList[0]) setPlace({ root: rootList[0].key, path: '/' });
  }, [root, ios, rootList, setPlace]);
  useEffect(() => setSelection(emptySelection), [root, path, device.id]);

  const currentRoot = rootList.find((r) => r.key === root) ?? null;
  const visibleRoots = rootList.filter((r) => r.kind !== 'media' || advanced || root === r.key);
  const parentListing = root && path !== '/' ? queryClient.getQueryData<FileEntry[]>(keys.files(device.id, root, parentPath(path))) : undefined;
  const folderReadOnly = parentListing?.find((e) => e.path === path)?.readOnly ?? false;
  const denied = isBridgeError(list.error, 'permission_denied');
  const entries = list.data ?? [];
  const totalSize = entries.reduce((sum, e) => sum + (e.isDirectory ? 0 : e.size), 0);
  const placeName = currentRoot ? (path === '/' ? currentRoot.label : `${currentRoot.label}${path}`) : '';

  const open = (rootKey: string, at = '/') => setPlace({ root: rootKey, path: at });
  const up = () => {
    if (root && path !== '/') setPlace({ root, path: parentPath(path) });
    else if (ios && root) setPlace({ root: null, path: '/' });
  };

  const verdict = (): DropVerdict => {
    if (!root || !currentRoot) return { ok: false, message: t('files.chooseRoot') };
    if (folderReadOnly || denied) return { ok: false, message: t('files.dropReadOnly') };
    return { ok: true, message: t('files.drop', { place: placeName }) };
  };
  const drop = useFileDrop({
    verdict,
    onDrop: (files) => {
      if (root) void actions.copyToDevice(device, root, path, files);
    },
    disabled: !root,
  });

  const copyToPc = (paths: string[]) => {
    if (root && paths.length > 0 && settings) void actions.copyToPc(device, root, paths, settings.fileCopyFolder);
  };

  const submitEdit = async () => {
    if (!editing || !root) return;
    const name = editing.name.trim();
    if (!name || /[\\/:*?"<>|]/.test(name)) {
      setEditing({ ...editing, error: t('error.next.already_exists') });
      return;
    }
    setEditing({ ...editing, busy: true, error: null });
    try {
      if (editing.mode === 'mkdir') await client.call('files.mkdir', { deviceId: device.id, root, path: joinPath(path, name) });
      else if (editing.entry) await client.call('files.rename', { deviceId: device.id, root, from: editing.entry.path, to: joinPath(parentPath(editing.entry.path), name) });
      await queryClient.invalidateQueries({ queryKey: keys.files(device.id, root, path) });
      setEditing(null);
    } catch (error) {
      const { message, next } = describeError(error, t);
      setEditing({ ...editing, busy: false, error: isBridgeError(error, 'already_exists') ? `${message} ${next}` : message });
    }
  };

  const confirmDelete = async () => {
    if (!deleting || !root) return;
    const paths = deleting;
    setDeleting(null);
    try {
      await client.call('files.delete', { deviceId: device.id, root, paths, recursive: true });
      await queryClient.invalidateQueries({ queryKey: keys.files(device.id, root, path) });
      setSelection(emptySelection);
    } catch (error) {
      actions.fail(error);
    }
  };

  const crumbs = path.split('/').filter(Boolean);

  return (
    <ScreenLayout title={t('files.title')} testId="screen-files" fill>
      <div className={styles.panes} data-swapped={swapped || undefined}>
        <PcPane device={device} />
        <Tooltip content={t('files.swap')}>
          <Button className={styles.swap} variant="subtle" icon={<ArrowSwapRegular />} aria-label={t('files.swap')} onClick={() => setSwapped((s) => !s)} />
        </Tooltip>
        <section className={`${styles.pane} ${styles.phonePane}`} aria-label={device.name}>
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
                  {rootIcon(r, (id) => client.images.icon(device.id, id), styles.placeIcon, styles.placeIconGlyph)}
                  <span className={styles.placeLabel}>{r.kind === 'media' ? t('files.mediaFolder') : r.label}</span>
                </button>
              ))}
              {ios && rootList.some((r) => r.kind === 'media') ? (
                <div className={styles.advanced}>
                  <Switch checked={advanced} onChange={setAdvanced} label={t('files.showAdvanced')} />
                </div>
              ) : null}
            </nav>

            <div className={styles.main} {...drop.bind}>
              {roots.error ? (
                <div className={styles.banner}>
                  <ErrorBanner error={roots.error} onRetry={() => void roots.refetch()} />
                </div>
              ) : root === null ? (
                roots.isPending ? (
                  <div className={styles.skeletons}>
                    {[0, 1, 2].map((i) => (
                      <Skeleton key={i} height={72} />
                    ))}
                  </div>
                ) : (
                  <RootChooser
                    device={device}
                    roots={rootList}
                    onOpen={(key) => open(key)}
                    onDropFiles={(key, files) => void actions.copyToDevice(device, key, '/', files)}
                  />
                )
              ) : (
                <>
                  <div className={styles.toolbar}>
                    <Tooltip content={t('common.back')} shortcut="Alt ↑">
                      <Button variant="subtle" size="small" icon={<ArrowUpRegular />} aria-label={t('common.back')} onClick={up} disabled={!ios && path === '/'} />
                    </Tooltip>
                    <ol className={styles.crumbs} aria-label={t('files.breadcrumb')}>
                      <li className={styles.crumb}>
                        <button type="button" className={styles.crumbButton} onClick={() => open(root)}>
                          {currentRoot?.kind === 'media' ? t('files.mediaFolder') : (currentRoot?.label ?? root)}
                        </button>
                      </li>
                      {crumbs.map((part, i) => (
                        <li key={i} className={styles.crumb}>
                          <ChevronRightRegular className="flip-rtl" aria-hidden />
                          <button type="button" className={styles.crumbButton} onClick={() => open(root, '/' + crumbs.slice(0, i + 1).join('/'))}>
                            {part}
                          </button>
                        </li>
                      ))}
                    </ol>
                    <Tooltip content={t('files.newFolder')}>
                      <Button
                        variant="subtle"
                        size="small"
                        icon={<FolderAddRegular />}
                        aria-label={t('files.newFolder')}
                        disabled={folderReadOnly || denied}
                        onClick={() => setEditing({ mode: 'mkdir', name: t('files.newFolderName'), error: null, busy: false })}
                      />
                    </Tooltip>
                    <Tooltip content={t('files.copyToPc')}>
                      <Button variant="subtle" size="small" icon={<ArrowDownloadRegular />} aria-label={t('files.copyToPc')} disabled={selection.ids.size === 0} onClick={() => copyToPc([...selection.ids])} />
                    </Tooltip>
                    <Tooltip content={t('common.delete')} shortcut="Del">
                      <Button
                        variant="subtle"
                        size="small"
                        icon={<DeleteRegular />}
                        aria-label={t('common.delete')}
                        disabled={selection.ids.size === 0 || folderReadOnly}
                        onClick={() => setDeleting([...selection.ids])}
                      />
                    </Tooltip>
                    <Tooltip content={t('common.retry')}>
                      <Button variant="subtle" size="small" icon={<ArrowClockwiseRegular />} aria-label={t('common.retry')} onClick={() => void list.refetch()} />
                    </Tooltip>
                  </div>
                  {folderReadOnly ? (
                    <div className={styles.banner}>
                      <Banner tone="info" icon={<LockClosedRegular />}>
                        {t('files.readOnly')}
                      </Banner>
                    </div>
                  ) : null}
                  {denied ? (
                    <EmptyState icon={<LockClosedRegular />} title={t('files.denied.title')} body={ios ? t('files.denied.ios') : t('files.denied.android')} />
                  ) : list.error ? (
                    <div className={styles.banner}>
                      <ErrorBanner error={list.error} onRetry={() => void list.refetch()} />
                    </div>
                  ) : list.isPending ? (
                    <div className={styles.skeletons}>
                      {[0, 1, 2, 3, 4, 5].map((i) => (
                        <Skeleton key={i} height={20} />
                      ))}
                    </div>
                  ) : entries.length === 0 ? (
                    <EmptyState icon={<FolderRegular />} title={t('files.empty.title')} body={folderReadOnly ? t('files.readOnly') : t('files.empty.body')} />
                  ) : (
                    <FileTable
                      device={device}
                      root={root}
                      path={path}
                      entries={entries}
                      onOpenFolder={(p) => open(root, p)}
                      onUp={up}
                      selection={selection}
                      setSelection={setSelection}
                      onRename={(entry) => setEditing({ mode: 'rename', entry, name: entry.name, error: null, busy: false })}
                      onDelete={(paths) => paths.length > 0 && setDeleting(paths)}
                      onCopyToPc={copyToPc}
                      onNewFolder={() => setEditing({ mode: 'mkdir', name: t('files.newFolderName'), error: null, busy: false })}
                      readOnly={folderReadOnly}
                    />
                  )}
                  <div className={styles.status}>
                    <span>{t('files.count', { count: entries.length })}</span>
                    <span>{selection.ids.size > 0 ? t('common.selected', { count: selection.ids.size }) : totalSize > 0 ? bytes(totalSize) : null}</span>
                  </div>
                </>
              )}
              <DropOverlay visible={drop.over} verdict={verdict()} />
            </div>
          </div>
        </section>
      </div>

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => (open ? undefined : setEditing(null))}
        title={editing?.mode === 'mkdir' ? t('files.newFolder') : t('files.renameTitle')}
        footer={
          <>
            <Button onClick={() => setEditing(null)}>{t('common.cancel')}</Button>
            <Button variant="accent" onClick={() => void submitEdit()} loading={editing?.busy}>
              {t('common.ok')}
            </Button>
          </>
        }
      >
        {editing ? (
          <TextField label={t('files.nameLabel')} value={editing.name} onChange={(name) => setEditing({ ...editing, name, error: null })} error={editing.error} autoFocus onEnter={() => void submitEdit()} />
        ) : null}
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => (open ? undefined : setDeleting(null))}
        danger
        title={t('files.delete.title', { count: deleting?.length ?? 0, device: device.name })}
        body={t('files.delete.body')}
        confirmLabel={t('common.delete')}
        onConfirm={() => void confirmDelete()}
      />
      <span className="visually-hidden" aria-live="polite">
        {n(entries.length)}
      </span>
    </ScreenLayout>
  );
}
