// The phone's folder browser: toolbar, breadcrumbs, search, virtual file table, rename / new folder / delete,
// export to the PC, and files or folders dropped from Explorer (onto the folder, or onto a folder row).
// Used by Files and by App documents.

import {
  AppGenericRegular,
  ArrowDownloadRegular,
  ArrowExportRegular,
  ArrowSortDownRegular,
  ArrowSortUpRegular,
  ClosedCaptionRegular,
  DeleteRegular,
  DocumentAddRegular,
  DocumentRegular,
  FolderAddRegular,
  FolderArrowUpRegular,
  FolderOpenRegular,
  FolderRegular,
  FolderZipRegular,
  ImageRegular,
  LockClosedRegular,
  MusicNote2Regular,
  RenameRegular,
  SearchRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import type { AppDataAccess } from '../../bridge/appFiles';
import type { FileDeleteItem } from '../../bridge/deleteTypes';
import { isBridgeError } from '../../bridge/errors';
import type { DeviceSummary, FileEntry } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys, useFileList, useSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { DropOverlay, useFileDrop, type DropVerdict } from '../../components/DropZone';
import { describeError, ErrorBanner } from '../../components/ErrorState';
import { ContextMenu, type MenuEntry } from '../../components/Menu';
import { Banner, EmptyState, Skeleton, TextField, Tooltip } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { usePrefs } from '../../state/prefs';
import { clickSelect, emptySelection, selectAll, type ClickModifiers, type Selection } from '../../state/selection';
import { toast } from '../../state/toasts';
import { announceDeleteStarted } from '../../shell/DeleteJobRow';
import type { Crumb } from './Breadcrumbs';
import { BrowserToolbar, type FolderSwitch } from './BrowserToolbar';
import { DeleteDialog } from './DeleteDialog';
import { canDragOut, dragOutFiles } from './dragOut';
import { useWidth } from './fit';
import { KIND_LABEL, extensionOf, isAndroidBlocked, joinPath, kindOf, matchesName, parentPath, sortEntries, type FileKind, type SortKey } from './fileKinds';
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

/** At or below this width (px) the table drops the time from dates (Files.module.css narrows the columns there too). */
const COMPACT_TABLE = 560;

const hasFiles = (types: readonly string[] | DOMStringList | undefined) => Array.from(types ?? []).includes('Files');

/** A folder row under files dragged from Explorer: they go into it, or it can't take them and says why. */
export interface RowDrop {
  path: string;
  name: string;
  refuse?: 'readOnly' | 'blocked';
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
  onExport,
  onNewFolder,
  readOnly,
  privateData,
  dropTarget,
  onDropTarget,
  canDrag,
  onDragOut,
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
  onExport(paths: string[]): void;
  onNewFolder(): void;
  readOnly: boolean;
  privateData: AppDataAccess | undefined;
  /** The folder row under files dragged from Explorer, or null for the open folder. */
  dropTarget: RowDrop | null;
  onDropTarget(row: RowDrop | null): void;
  /** Rows can be dragged out to File Explorer (the WebView2 host only). */
  canDrag: boolean;
  onDragOut(paths: string[]): void;
}) {
  const { t, bytes, date, lang } = useI18n();
  const density = usePrefs((s) => s.density);
  const rowHeight = density === 'compact' ? 32 : 40;
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'name', desc: false });
  const [active, setActive] = useState(0);
  const scroller = useRef<HTMLDivElement>(null);
  const grid = useRef<HTMLDivElement>(null);
  // A narrow table (Files beside the PC pane) shows dates without the time; the tooltip has both.
  const width = useWidth(grid);
  const compactDates = width > 0 && width <= COMPACT_TABLE;
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

  // Android/data and Android/obb of a storage root: greyed with the reason, unless this phone lets adb in.
  const blocked = (e: FileEntry) => android && e.isDirectory && privateData !== 'allowed' && isAndroidBlocked(e.path);
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
      { key: 'export', label: t('appFiles.exportTo'), icon: <ArrowExportRegular className="flip-rtl" />, hint: 'Ctrl+E', disabled: blocked(entry), onSelect: () => onExport(paths) },
      { key: 'copy', label: t('files.copyToPc'), icon: <ArrowDownloadRegular />, disabled: blocked(entry), onSelect: () => onCopyToPc(paths) },
      'separator',
      { key: 'rename', label: t('common.rename'), icon: <RenameRegular />, hint: 'F2', disabled: readOnly || entry.readOnly || paths.length > 1, onSelect: () => onRename(entry) },
      { key: 'delete', label: t('common.delete'), icon: <DeleteRegular />, hint: 'Del', danger: true, disabled: readOnly || entry.readOnly, onSelect: () => onDelete(paths) },
      'separator',
      { key: 'mkdir', label: t('files.newFolder'), icon: <FolderAddRegular />, disabled: readOnly, onSelect: onNewFolder },
    ];
  };

  return (
    <div
      ref={grid}
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
      <div
        className={styles.scroll}
        ref={scroller}
        onDragOver={(e) => {
          // Below the last row: the open folder again.
          if (!(e.target as Element).closest('[role=row]')) onDropTarget(null);
        }}
        onDragLeave={(e) => (e.currentTarget.contains(e.relatedTarget as Node | null) ? undefined : onDropTarget(null))}
      >
        <div className={styles.inner} style={{ height: virtualizer.getTotalSize() }}>
          {virtualizer.getVirtualItems().map((item) => {
            const entry = sorted[item.index]!;
            const kind = kindOf(entry);
            const Icon = KIND_ICON[kind];
            const sizeText = entry.isDirectory ? '—' : bytes(entry.size);
            const kindText = kind === 'file' ? t('files.kind.file', { ext: extensionOf(entry.name).toUpperCase() || '—' }) : t(KIND_LABEL[kind]);
            const dateText = entry.modified ? date(entry.modified, compactDates ? 'short' : 'medium') : '—';
            const dateTitle = entry.modified ? date(entry.modified) : '—';
            const isBlocked = blocked(entry);
            const rowDrop: RowDrop | null = !entry.isDirectory
              ? null
              : { path: entry.path, name: entry.name, ...(isBlocked ? { refuse: 'blocked' as const } : entry.readOnly || readOnly ? { refuse: 'readOnly' as const } : {}) };
            const row = (
              <div
                id={`file-${item.index}`}
                role="row"
                aria-selected={selection.ids.has(entry.path)}
                className={styles.row}
                style={{ transform: `translateY(${item.start}px)`, height: item.size }}
                data-active={item.index === active || undefined}
                data-blocked={isBlocked || undefined}
                data-drop={(dropTarget?.path === entry.path && !dropTarget.refuse) || undefined}
                data-refuse={(dropTarget?.path === entry.path && !!dropTarget.refuse) || undefined}
                draggable={canDrag && !isBlocked}
                onDragStart={(e) => {
                  // The host runs the drag (a native one Explorer understands); the browser's own is cancelled.
                  e.preventDefault();
                  const paths = selection.ids.has(entry.path) ? [...selection.ids] : [entry.path];
                  if (!selection.ids.has(entry.path)) click(item.index, {});
                  onDragOut(paths);
                }}
                onClick={(e) => click(item.index, { ctrl: e.ctrlKey || e.metaKey, shift: e.shiftKey })}
                onDoubleClick={() => open(entry)}
                onContextMenu={() => {
                  if (!selection.ids.has(entry.path)) click(item.index, {});
                }}
                onDragOver={(e) => {
                  if (!hasFiles(e.dataTransfer?.types)) return;
                  if (rowDrop?.path !== dropTarget?.path || rowDrop?.refuse !== dropTarget?.refuse) onDropTarget(rowDrop);
                }}
              >
                <span className={styles.nameCell} role="gridcell">
                  <span className={styles.fileIcon} data-kind={kind} aria-hidden>
                    <Icon />
                  </span>
                  <span className={styles.name} title={entry.name} dir="auto">
                    {entry.name}
                  </span>
                  {entry.readOnly ? <LockClosedRegular className={styles.lockedIcon} aria-label={t('files.readOnly')} /> : null}
                  {isBlocked ? <span className={styles.blockedTag}>{t('files.blocked')}</span> : null}
                </span>
                <span className={`${styles.cell} ${styles.alignEnd}`} role="gridcell" title={sizeText}>
                  {sizeText}
                </span>
                <span className={`${styles.cell} ${styles.kindColumn}`} role="gridcell" title={kindText}>
                  {kindText}
                </span>
                <span className={styles.cell} role="gridcell" title={dateTitle}>
                  {dateText}
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

// ---------------------------------------------------------------- browser

export interface BrowserExit {
  /** The crumb before the root ("App documents"); Up at the root goes there. */
  label: string;
  onExit(): void;
}

export function FileBrowser({
  device,
  root,
  path,
  rootLabel,
  rootIcon,
  onNavigate,
  exit,
  folders,
  privateData,
}: {
  device: DeviceSummary;
  root: string;
  path: string;
  rootLabel: string;
  rootIcon?: ReactNode;
  onNavigate(path: string): void;
  exit?: BrowserExit;
  /** Switches between an app's folders (Android: Media / Data), in the toolbar. */
  folders?: FolderSwitch;
  /** Whether this Android phone lets adb into Android/data (from appFiles.list); greys those folders otherwise. */
  privateData?: AppDataAccess;
}) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const list = useFileList(device, root, path);
  const [selection, setSelection] = useState<Selection>(emptySelection);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [deleting, setDeleting] = useState<FileEntry[] | null>(null);
  const [deleteBusy, setDeleteBusy] = useState(false);
  const [search, setSearch] = useState('');
  const [searchOpen, setSearchOpen] = useState(false);
  const [dropTarget, setDropTarget] = useState<RowDrop | null>(null);
  const searchBox = useRef<HTMLInputElement>(null);
  const filePicker = useRef<HTMLInputElement>(null);
  const ios = device.platform === 'ios';

  useEffect(() => {
    setSelection(emptySelection);
    setSearch('');
    setSearchOpen(false);
  }, [root, path, device.id]);

  const parentListing = path !== '/' ? queryClient.getQueryData<FileEntry[]>(keys.files(device.id, root, parentPath(path))) : undefined;
  const folderReadOnly = parentListing?.find((e) => e.path === path)?.readOnly ?? false;
  const denied = isBridgeError(list.error, 'permission_denied');
  const entries = list.data ?? [];
  const shown = useMemo(() => (search.trim() ? entries.filter((e) => matchesName(e.name, search)) : entries), [entries, search]);
  const selected = entries.filter((e) => selection.ids.has(e.path));
  const selectedBytes = selected.reduce((sum, e) => sum + (e.isDirectory ? 0 : e.size), 0);
  const totalSize = entries.reduce((sum, e) => sum + (e.isDirectory ? 0 : e.size), 0);
  const parts = path.split('/').filter(Boolean);
  const crumbs: Crumb[] = [
    ...(exit ? [{ key: 'exit', label: exit.label, onSelect: exit.onExit, testId: 'crumb-exit' }] : []),
    { key: 'root', label: rootLabel, icon: rootIcon, onSelect: () => onNavigate('/'), testId: 'crumb-root' },
    ...parts.map((part, i) => ({ key: `${i}:${part}`, label: part, onSelect: () => onNavigate('/' + parts.slice(0, i + 1).join('/')) })),
  ];
  const placeName = (folder: string) => (folder === '/' ? rootLabel : `${rootLabel}${folder}`);
  const writable = !folderReadOnly && !denied;

  const up = () => {
    if (path !== '/') onNavigate(parentPath(path));
    else exit?.onExit();
  };

  const refreshAfterChange = async () => {
    await queryClient.invalidateQueries({ queryKey: keys.files(device.id, root, path) });
    void queryClient.invalidateQueries({ queryKey: ['appFiles.measure', device.id] });
  };

  // The row under the pointer, as of the event being handled: the row's dragover runs just before the browser's,
  // which answers with the verdict, so it can't wait for the state to render.
  const dropRow = useRef<RowDrop | null>(null);
  const pointRow = (row: RowDrop | null) => {
    dropRow.current = row;
    setDropTarget(row);
  };
  const verdict = (): DropVerdict => {
    const row = dropRow.current;
    if (row?.refuse === 'blocked') return { ok: false, message: t('files.dropBlocked', { name: row.name }) };
    if (row?.refuse === 'readOnly') return { ok: false, message: t('files.dropRowReadOnly', { name: row.name }) };
    if (denied) return { ok: false, message: t('files.dropDenied') };
    if (!writable) return { ok: false, message: t('files.dropReadOnly') };
    return { ok: true, message: t('files.drop', { place: placeName(row?.path ?? path) }) };
  };
  const drop = useFileDrop({
    verdict,
    onDrop: (files) => {
      const into = dropRow.current?.path ?? path;
      pointRow(null);
      void actions.copyToDevice(device, root, into, files);
    },
  });
  useEffect(() => {
    if (!drop.over) pointRow(null);
  }, [drop.over]);

  const copyToPc = (paths: string[]) => {
    if (paths.length > 0 && settings) void actions.copyToPc(device, root, paths, settings.fileCopyFolder);
  };

  const exportTo = async (paths: string[]) => {
    if (paths.length === 0) return;
    const destination = await actions.pickFolder(t('appFiles.exportTitle'));
    if (destination) await actions.copyToPc(device, root, paths, destination);
  };
  const exportPaths = () => (selection.ids.size > 0 ? [...selection.ids] : [path]);
  const exportLabel = selection.ids.size > 0 ? t('appFiles.exportSelected', { count: selection.ids.size }) : path === '/' ? t('appFiles.exportAll', { place: rootLabel }) : t('appFiles.exportFolder');

  const dragOut = async (paths: string[]) => {
    try {
      const result = await dragOutFiles(client, device.id, root, paths);
      if (result?.effect === 'copy') toast({ tone: 'info', title: t('files.copyStarted'), duration: 3000 });
    } catch (error) {
      // A host without the desktop feature's drag out: the drag just doesn't start.
      if (!isBridgeError(error, 'method_not_found')) actions.fail(error);
    }
  };

  const addFolder = async () => {
    const folder = await actions.pickFolder(t('appFiles.addFolderTitle'));
    if (folder) await actions.copyToDevice(device, root, path, [folder]);
  };

  const submitEdit = async () => {
    if (!editing) return;
    const name = editing.name.trim();
    if (!name || /[\\/:*?"<>|]/.test(name)) {
      setEditing({ ...editing, error: t('appFiles.badName') });
      return;
    }
    setEditing({ ...editing, busy: true, error: null });
    try {
      if (editing.mode === 'mkdir') await client.call('files.mkdir', { deviceId: device.id, root, path: joinPath(path, name) });
      else if (editing.entry) await client.call('files.rename', { deviceId: device.id, root, from: editing.entry.path, to: joinPath(parentPath(editing.entry.path), name) });
      await refreshAfterChange();
      setEditing(null);
    } catch (error) {
      const { message, next } = describeError(error, t);
      setEditing({ ...editing, busy: false, error: isBridgeError(error, 'already_exists') ? `${message} ${next}` : message });
    }
  };

  const askDelete = (paths: string[]) => {
    const chosen = entries.filter((e) => paths.includes(e.path));
    if (chosen.length > 0) setDeleting(chosen);
  };

  // v0.6: the delete goes to the Transfers drawer as a job; the dialog closes as soon as it is queued and the list
  // is read again when the job ends.
  const confirmDelete = async (items: FileDeleteItem[]) => {
    if (!deleting) return;
    setDeleteBusy(true);
    try {
      const expectedBytes = items.reduce((n, i) => n + i.size, 0);
      const started = await client.call('files.deleteItems', { deviceId: device.id, items, expectedBytes });
      setDeleting(null);
      setSelection(emptySelection);
      announceDeleteStarted(i18n, started, 'filesDelete');
    } catch (error) {
      setDeleting(null);
      actions.fail(error);
    } finally {
      setDeleteBusy(false);
    }
  };

  const newFolder = () => setEditing({ mode: 'mkdir', name: t('files.newFolderName'), error: null, busy: false });

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const ctrl = (e.ctrlKey || e.metaKey) && !e.altKey;
    if (ctrl && e.key.toLowerCase() === 'f') {
      e.preventDefault();
      e.stopPropagation();
      setSearchOpen(true);
      searchBox.current?.focus();
      searchBox.current?.select();
    } else if (ctrl && e.key.toLowerCase() === 'e') {
      e.preventDefault();
      e.stopPropagation();
      void exportTo(exportPaths());
    }
  };

  return (
    <div className={styles.browser} {...drop.bind} onKeyDown={onKeyDown} data-testid="file-browser" data-path={path}>
      <BrowserToolbar
        crumbs={crumbs}
        canGoUp={!!exit || path !== '/'}
        onUp={up}
        folders={folders}
        search={search}
        onSearch={setSearch}
        searchBox={searchBox}
        searchOpen={searchOpen}
        onSearchOpen={setSearchOpen}
        writable={writable}
        onNewFolder={newFolder}
        onAddFiles={() => filePicker.current?.click()}
        onAddFolder={() => void addFolder()}
        exportLabel={exportLabel}
        canExport={!denied && !list.error}
        onExport={() => void exportTo(exportPaths())}
        canDelete={selection.ids.size > 0 && !folderReadOnly}
        onDelete={() => askDelete([...selection.ids])}
        onRefresh={() => void refreshAfterChange()}
      />
      <input
        ref={filePicker}
        type="file"
        multiple
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (files.length > 0) void actions.copyToDevice(device, root, path, files);
        }}
      />
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
        <div className={styles.empty} data-testid="empty-folder">
          <EmptyState
            icon={<FolderRegular />}
            title={exit && path === '/' ? t('files.emptyApp.title', { app: rootLabel }) : t('files.empty.title')}
            body={folderReadOnly ? t('files.readOnly') : t('files.empty.drop')}
            action={
              writable ? (
                <span className={styles.emptyActions}>
                  <Button icon={<DocumentAddRegular />} onClick={() => filePicker.current?.click()} data-testid="empty-add-files">
                    {t('files.addFilesButton')}
                  </Button>
                  <Button variant="subtle" icon={<FolderArrowUpRegular />} onClick={() => void addFolder()}>
                    {t('files.addFolderButton')}
                  </Button>
                </span>
              ) : null
            }
          />
        </div>
      ) : shown.length === 0 ? (
        <EmptyState
          icon={<SearchRegular />}
          title={t('appFiles.noMatch.title', { query: search.trim() })}
          body={t('appFiles.noMatch.folder')}
          action={<Button onClick={() => setSearch('')}>{t('appFiles.clearSearch')}</Button>}
        />
      ) : (
        <FileTable
          device={device}
          root={root}
          path={path}
          entries={shown}
          onOpenFolder={onNavigate}
          onUp={up}
          selection={selection}
          setSelection={setSelection}
          onRename={(entry) => setEditing({ mode: 'rename', entry, name: entry.name, error: null, busy: false })}
          onDelete={askDelete}
          onCopyToPc={copyToPc}
          onExport={(paths) => void exportTo(paths)}
          onNewFolder={newFolder}
          readOnly={folderReadOnly}
          privateData={privateData}
          dropTarget={drop.over ? dropTarget : null}
          onDropTarget={pointRow}
          canDrag={canDragOut(client)}
          onDragOut={(paths) => void dragOut(paths)}
        />
      )}
      <div className={styles.status}>
        <span aria-live="polite">{search.trim() ? t('appFiles.filtered', { shown: shown.length, count: entries.length }) : t('files.count', { count: entries.length })}</span>
        <span data-testid="file-status">
          {selection.ids.size > 0
            ? `${t('common.selected', { count: selection.ids.size })}${selectedBytes > 0 ? ` · ${bytes(selectedBytes)}` : ''}`
            : totalSize > 0
              ? bytes(totalSize)
              : null}
        </span>
      </div>
      <DropOverlay visible={drop.over} verdict={verdict()} />

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => (open ? undefined : setEditing(null))}
        title={editing?.mode === 'mkdir' ? t('files.newFolder') : t('files.renameTitle')}
        footer={
          <>
            <Button onClick={() => setEditing(null)}>{t('common.cancel')}</Button>
            <Button variant="accent" onClick={() => void submitEdit()} loading={editing?.busy} data-testid="edit-ok">
              {t('common.ok')}
            </Button>
          </>
        }
      >
        {editing ? (
          <TextField label={t('files.nameLabel')} value={editing.name} onChange={(name) => setEditing({ ...editing, name, error: null })} error={editing.error} autoFocus onEnter={() => void submitEdit()} />
        ) : null}
      </Dialog>

      <DeleteDialog device={device} root={root} entries={deleting} busy={deleteBusy} onCancel={() => setDeleting(null)} onConfirm={(items) => void confirmDelete(items)} />
    </div>
  );
}

/** A file-sharing app's icon, or a glyph for the media folder and Android storage. */
export function RootGlyph({ appId, kind, iconUrl, className, glyphClass }: { appId: string | null; kind: string; iconUrl(appId: string): string; className?: string; glyphClass?: string }) {
  if (appId) return <img className={className} src={iconUrl(appId)} alt="" />;
  return (
    <span className={glyphClass} aria-hidden>
      {kind === 'media' ? <ImageRegular /> : kind === 'appDocuments' ? <AppGenericRegular /> : <FolderOpenRegular />}
    </span>
  );
}
