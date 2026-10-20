// The "This PC" side of the Files screen: browse the PC's folders, open files, make and rename folders,
// copy files to the phone, drag files out to File Explorer. Phone files dropped here are copied into the
// folder (or the folder row) under the pointer. The folder open here is where the phone's "Copy to PC" goes.

import {
  ArrowClockwiseRegular,
  ArrowUpRegular,
  ArrowUploadRegular,
  ChevronDownRegular,
  DesktopRegular,
  DocumentRegular,
  FolderAddRegular,
  FolderOpenRegular,
  FolderRegular,
  HardDriveRegular,
  HomeRegular,
  ImageRegular,
  LaptopRegular,
  MusicNote2Regular,
  OpenRegular,
  RenameRegular,
  ArrowDownloadRegular,
  UsbStickRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { isBridgeError } from '../../bridge/errors';
import type { DeviceSummary, PcEntry, PcRoot, PcRootKind } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useSettings, useUpdateSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { describeError, ErrorBanner } from '../../components/ErrorState';
import { ContextMenu, Menu, type MenuEntry } from '../../components/Menu';
import { EmptyState, Skeleton, TextField, Tooltip } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { usePrefs } from '../../state/prefs';
import { clickSelect, emptySelection, selectAll, type ClickModifiers, type Selection } from '../../state/selection';
import { toast } from '../../state/toasts';
import { kindOf, type FileKind } from '../../screens/files/fileKinds';
import filesStyles from '../../screens/files/Files.module.css';
import { dragOutProps, useInsideDrop } from '../dragOut';
import { pcJoin, pcKeys, pcName, pcParent, rootOf, usePcList, usePcRoots } from './pcQueries';
import styles from './PcPane.module.css';

const ROOT_ICON: Record<PcRootKind, typeof FolderRegular> = {
  home: HomeRegular,
  desktop: DesktopRegular,
  downloads: ArrowDownloadRegular,
  documents: DocumentRegular,
  pictures: ImageRegular,
  videos: VideoRegular,
  music: MusicNote2Regular,
  drive: HardDriveRegular,
  removableDrive: UsbStickRegular,
  networkDrive: HardDriveRegular,
  opticalDrive: HardDriveRegular,
};

const ROOT_LABEL: Partial<Record<PcRootKind, MessageKey>> = {
  home: 'pc.root.home',
  desktop: 'pc.root.desktop',
  downloads: 'pc.root.downloads',
  documents: 'pc.root.documents',
  pictures: 'pc.root.pictures',
  videos: 'pc.root.videos',
  music: 'pc.root.music',
};

const ENTRY_ICON: Partial<Record<FileKind, typeof FolderRegular>> = {
  folder: FolderRegular,
  image: ImageRegular,
  video: VideoRegular,
  audio: MusicNote2Regular,
};

/** Where "Copy to phone" goes: the phone pane's open folder. */
export interface PhoneTarget {
  root: string;
  path: string;
  label: string;
}

interface Editing {
  mode: 'rename' | 'mkdir';
  entry?: PcEntry;
  name: string;
  error: string | null;
  busy: boolean;
}

export function PcPane({ device, phoneTarget, className }: { device: DeviceSummary; phoneTarget: PhoneTarget | null; className?: string }) {
  const i18n = useI18n();
  const { t, bytes, date, lang } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const updateSettings = useUpdateSettings();
  const roots = usePcRoots();
  const [folder, setFolder] = useState<string | null>(null);
  const [selection, setSelection] = useState<Selection>(emptySelection);
  const [active, setActive] = useState(0);
  const [editing, setEditing] = useState<Editing | null>(null);
  const list = usePcList(folder);
  const density = usePrefs((s) => s.density);
  const rowHeight = density === 'compact' ? 30 : 36;
  const scroller = useRef<HTMLDivElement>(null);

  const rootList = roots.data ?? [];
  const labelOf = (root: PcRoot) => {
    const known = ROOT_LABEL[root.kind];
    if (known) return t(known);
    const letter = root.path.replace(/[\\/]+$/, '');
    return root.label ? `${root.label} (${letter})` : t('pc.root.drive', { letter });
  };

  // Start where copies go (settings.fileCopyFolder); a folder that isn't there yet falls back to its parent.
  useEffect(() => {
    if (folder === null && settings) setFolder(settings.fileCopyFolder);
  }, [folder, settings]);
  useEffect(() => {
    if (folder && isBridgeError(list.error) && list.error.code === 'path_not_found') {
      const parent = pcParent(folder);
      setFolder(parent ?? rootList.find((r) => r.kind === 'downloads')?.path ?? null);
    }
  }, [folder, list.error, rootList]);
  useEffect(() => {
    setSelection(emptySelection);
    setActive(0);
    scroller.current?.scrollTo({ top: 0 });
  }, [folder]);

  const collator = useMemo(() => new Intl.Collator(lang === 'fa' ? 'fa-IR' : 'en-GB', { numeric: true, sensitivity: 'base' }), [lang]);
  const entries = useMemo(
    () => [...(list.data ?? [])].sort((a, b) => (a.isDirectory !== b.isDirectory ? (a.isDirectory ? -1 : 1) : collator.compare(a.name, b.name))),
    [list.data, collator],
  );
  const order = useMemo(() => entries.map((e) => e.path), [entries]);
  const virtualizer = useVirtualizer({ count: entries.length, getScrollElement: () => scroller.current, estimateSize: () => rowHeight, overscan: 12 });
  useEffect(() => virtualizer.measure(), [rowHeight, virtualizer]);

  /** Navigates and makes this the folder "Copy to PC" uses. */
  const go = (path: string) => {
    setFolder(path);
    if (settings && path !== settings.fileCopyFolder) updateSettings.mutate({ fileCopyFolder: path });
  };
  const refresh = () => void queryClient.invalidateQueries({ queryKey: pcKeys.list(folder ?? '') });
  const parent = folder ? pcParent(folder) : null;
  const selectedPaths = () => (selection.ids.size > 0 ? [...selection.ids] : entries[active] ? [entries[active].path] : []);
  const open = async (entry: PcEntry) => {
    if (entry.isDirectory) {
      go(entry.path);
      return;
    }
    try {
      await client.call('shell.openFile', { path: entry.path });
    } catch (error) {
      actions.fail(error);
    }
  };
  const copyToPhone = (paths: string[]) => {
    if (phoneTarget && paths.length > 0) void actions.copyToDevice(device, phoneTarget.root, phoneTarget.path, paths);
  };
  const click = (index: number, mods: ClickModifiers) => {
    setActive(index);
    const id = order[index];
    if (id) setSelection((s) => clickSelect(s, id, mods, order));
  };

  // Phone files dragged out of the phone pane and dropped here: copy them into this folder, or the folder row hit.
  const dropRef = useInsideDrop((drop, hit) => {
    const target = hit.closest<HTMLElement>('[data-pc-folder]')?.dataset.pcFolder ?? folder;
    if (!target || drop.source !== 'files') return;
    void actions.copyToPc(device, drop.root, drop.items.map((i) => i.id), target);
  });

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const move = (to: number) => {
      const next = Math.max(0, Math.min(entries.length - 1, to));
      setActive(next);
      virtualizer.scrollToIndex(next, { align: 'auto' });
      const id = order[next];
      if (id) setSelection((s) => clickSelect(s, id, { shift: e.shiftKey }, order));
    };
    const ctrl = e.ctrlKey || e.metaKey;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      move(active + 1);
    } else if (e.key === 'ArrowUp' && !e.altKey) {
      e.preventDefault();
      move(active - 1);
    } else if (e.key === 'Enter' && entries[active]) {
      e.preventDefault();
      void open(entries[active]);
    } else if ((e.key === 'Backspace' || (e.key === 'ArrowUp' && e.altKey)) && parent) {
      e.preventDefault();
      go(parent);
    } else if (e.key === 'F2' && entries[active]) {
      e.preventDefault();
      setEditing({ mode: 'rename', entry: entries[active], name: entries[active].name, error: null, busy: false });
    } else if (ctrl && e.key.toLowerCase() === 'a') {
      e.preventDefault();
      setSelection(selectAll(order));
    } else if (e.key === 'Escape' && selection.ids.size > 0) {
      e.preventDefault();
      setSelection(emptySelection);
    }
  };

  const submitEdit = async () => {
    if (!editing || !folder) return;
    const name = editing.name.trim();
    if (!name) {
      setEditing({ ...editing, error: t('pc.nameEmpty') });
      return;
    }
    setEditing({ ...editing, busy: true, error: null });
    try {
      if (editing.mode === 'mkdir') await client.call('pc.mkdir', { path: pcJoin(folder, name) });
      else if (editing.entry) await client.call('pc.rename', { path: editing.entry.path, name });
      setEditing(null);
      refresh();
    } catch (error) {
      setEditing({ ...editing, busy: false, error: describeError(error, t).message });
    }
  };

  const menuFor = (entry: PcEntry): MenuEntry[] => {
    const paths = selection.ids.has(entry.path) ? [...selection.ids] : [entry.path];
    const files = paths.filter((p) => entries.find((e) => e.path === p && !e.isDirectory));
    return [
      { key: 'open', label: t('common.open'), icon: <FolderOpenRegular />, onSelect: () => void open(entry) },
      { key: 'phone', label: t('pc.copyToPhone'), icon: <ArrowUploadRegular />, disabled: !phoneTarget || files.length === 0, onSelect: () => copyToPhone(files) },
      { key: 'explorer', label: t('common.showInExplorer'), icon: <OpenRegular />, onSelect: () => void actions.revealInExplorer(entry.path) },
      { key: 'rename', label: t('common.rename'), icon: <RenameRegular />, hint: 'F2', disabled: paths.length > 1, onSelect: () => setEditing({ mode: 'rename', entry, name: entry.name, error: null, busy: false }) },
      'separator',
      { key: 'mkdir', label: t('pc.newFolder'), icon: <FolderAddRegular />, onSelect: () => setEditing({ mode: 'mkdir', name: t('pc.newFolderName'), error: null, busy: false }) },
    ];
  };

  const placeItems: MenuEntry[] = rootList.map((root) => {
    const Icon = ROOT_ICON[root.kind];
    return {
      key: root.path,
      label: labelOf(root),
      icon: <Icon />,
      hint: root.freeBytes !== null ? t('pc.free', { size: bytes(root.freeBytes) }) : undefined,
      onSelect: () => go(root.path),
    };
  });
  const here = folder ? rootOf(folder, rootList) : undefined;
  const title = folder ? (here && here.path.replace(/[\\/]+$/, '').toLowerCase() === folder.replace(/[\\/]+$/, '').toLowerCase() ? labelOf(here) : pcName(folder)) : t('files.pc');
  const files = selectedPaths().filter((p) => entries.find((e) => e.path === p && !e.isDirectory));

  return (
    <section
      ref={dropRef}
      className={`${filesStyles.pane} ${filesStyles.pcPane} ${className ?? ''}`}
      aria-label={t('files.pc')}
      data-testid="pc-pane"
    >
      <div className={filesStyles.paneHeader}>
        <span className={filesStyles.paneTitle}>
          <LaptopRegular aria-hidden />
          {t('files.pc')}
        </span>
        <Menu
          align="end"
          trigger={
            <Button size="small" variant="subtle" icon={<ChevronDownRegular />} data-testid="pc-places">
              {t('pc.places')}
            </Button>
          }
          items={placeItems}
        />
      </div>

      <div className={styles.toolbar}>
        <Tooltip content={t('pc.up')}>
          <Button variant="subtle" size="small" icon={<ArrowUpRegular />} aria-label={t('pc.up')} disabled={!parent} onClick={() => parent && go(parent)} />
        </Tooltip>
        <span className={styles.location} title={folder ?? ''} data-testid="pc-location">
          <span className={styles.locationName}>{title}</span>
          {folder ? <span className={styles.locationPath}>{folder}</span> : null}
        </span>
        <Tooltip content={t('pc.refresh')}>
          <Button variant="subtle" size="small" icon={<ArrowClockwiseRegular />} aria-label={t('pc.refresh')} onClick={refresh} disabled={!folder} />
        </Tooltip>
        <Tooltip content={t('pc.newFolder')}>
          <Button
            variant="subtle"
            size="small"
            icon={<FolderAddRegular />}
            aria-label={t('pc.newFolder')}
            disabled={!folder}
            onClick={() => setEditing({ mode: 'mkdir', name: t('pc.newFolderName'), error: null, busy: false })}
          />
        </Tooltip>
        <Tooltip content={t('files.pcOpen')}>
          <Button variant="subtle" size="small" icon={<OpenRegular />} aria-label={t('files.pcOpen')} disabled={!folder} onClick={() => folder && void actions.revealInExplorer(folder)} />
        </Tooltip>
      </div>

      <div className={styles.body}>
        {list.error && !(isBridgeError(list.error) && list.error.code === 'path_not_found') ? (
          <div className={styles.banner}>
            <ErrorBanner error={list.error} onRetry={refresh} />
          </div>
        ) : list.isPending || !folder ? (
          <div className={styles.skeletons}>
            {[0, 1, 2, 3, 4].map((i) => (
              <Skeleton key={i} height={rowHeight - 8} />
            ))}
          </div>
        ) : entries.length === 0 ? (
          <EmptyState icon={<FolderRegular />} title={t('pc.empty')} />
        ) : (
          <div
            className={styles.list}
            role="grid"
            aria-label={folder}
            aria-multiselectable
            aria-rowcount={entries.length}
            tabIndex={0}
            onKeyDown={onKeyDown}
            ref={scroller}
            data-testid="pc-list"
          >
            <div className={styles.inner} style={{ height: virtualizer.getTotalSize() }}>
              {virtualizer.getVirtualItems().map((item) => {
                const entry = entries[item.index]!;
                const Icon = ENTRY_ICON[kindOf(entry)] ?? DocumentRegular;
                const drag = dragOutProps(client, () => {
                  const paths = selection.ids.has(entry.path) ? [...selection.ids] : [entry.path];
                  return { source: 'pc', paths };
                });
                return (
                  <ContextMenu key={item.key} items={menuFor(entry)}>
                    <div
                      role="row"
                      aria-selected={selection.ids.has(entry.path)}
                      className={styles.row}
                      style={{ transform: `translateY(${item.start}px)`, height: item.size }}
                      data-active={item.index === active || undefined}
                      data-pc-folder={entry.isDirectory ? entry.path : undefined}
                      data-testid="pc-row"
                      onClick={(e) => click(item.index, { ctrl: e.ctrlKey || e.metaKey, shift: e.shiftKey })}
                      onDoubleClick={() => void open(entry)}
                      onContextMenu={() => {
                        if (!selection.ids.has(entry.path)) click(item.index, {});
                      }}
                      {...drag}
                    >
                      <span className={styles.icon} data-folder={entry.isDirectory || undefined} aria-hidden>
                        <Icon />
                      </span>
                      <span className={styles.name} role="gridcell" title={entry.name}>
                        {entry.name}
                      </span>
                      <span className={styles.meta} role="gridcell">
                        {entry.isDirectory ? (entry.modified ? date(entry.modified) : '') : bytes(entry.size)}
                      </span>
                    </div>
                  </ContextMenu>
                );
              })}
            </div>
          </div>
        )}
      </div>

      <div className={styles.footer}>
        <span className={styles.hint}>{t('pc.hint')}</span>
        <Button
          size="small"
          icon={<ArrowUploadRegular />}
          disabled={!phoneTarget || files.length === 0}
          onClick={() => copyToPhone(files)}
          data-testid="pc-copy-to-phone"
        >
          {phoneTarget ? t('pc.copyToPhoneNamed', { place: phoneTarget.label }) : t('pc.copyToPhone')}
        </Button>
      </div>

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => !open && setEditing(null)}
        title={editing?.mode === 'mkdir' ? t('pc.newFolder') : t('common.rename')}
        footer={
          <>
            <Button onClick={() => setEditing(null)}>{t('common.cancel')}</Button>
            <Button variant="accent" disabled={editing?.busy} onClick={() => void submitEdit()}>
              {editing?.mode === 'mkdir' ? t('pc.create') : t('common.rename')}
            </Button>
          </>
        }
      >
        {editing ? (
          <TextField
            label={t('pc.name')}
            value={editing.name}
            autoFocus
            error={editing.error}
            onChange={(name) => setEditing({ ...editing, name, error: null })}
            onEnter={() => void submitEdit()}
          />
        ) : null}
      </Dialog>
    </section>
  );
}

/** A toast for copies started from the PC pane is the copy action's own; this one is for refused drops. */
export function noPhoneFolderToast(t: ReturnType<typeof useI18n>['t']) {
  toast({ tone: 'warning', title: t('pc.choosePhoneFolder') });
}
