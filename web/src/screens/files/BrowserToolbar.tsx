// The file browser's toolbar. It fits the pane it is in (Files shows two panes, so the window's width says little),
// stepping tighter while the current folder's name has too little room:
//   0: New folder, Add and Export with their labels, Delete, Refresh, a wide search box
//   1: the same as icons with tooltips, a narrower search box
//   2: Add and Export, the rest (and an app's folder switch) in a "More" menu, a small search box
//   3: the search box folds into a button too; it opens over the breadcrumbs (Ctrl+F as well)

import {
  AddRegular,
  ArrowClockwiseRegular,
  ArrowExportRegular,
  ArrowUpRegular,
  CheckmarkRegular,
  DeleteRegular,
  DismissRegular,
  DocumentAddRegular,
  FolderAddRegular,
  FolderArrowUpRegular,
  MoreHorizontalRegular,
  SearchRegular,
} from '@fluentui/react-icons';
import { useEffect, useRef, type RefObject } from 'react';
import { Menu, type MenuEntry } from '../../components/Menu';
import { Segmented } from '../../components/ui';
import { useI18n } from '../../i18n';
import { Breadcrumbs, type Crumb } from './Breadcrumbs';
import { useFitLevel } from './fit';
import { TipButton } from './TipButton';
import styles from './Files.module.css';

/** An app's folders to switch between (Android: Media / Data). */
export interface FolderSwitch {
  value: string;
  options: { value: string; label: string }[];
  onChange(value: string): void;
}

export interface ToolbarProps {
  crumbs: Crumb[];
  canGoUp: boolean;
  onUp(): void;
  folders?: FolderSwitch;
  search: string;
  onSearch(value: string): void;
  searchBox: RefObject<HTMLInputElement | null>;
  /** The search box is wanted open (Ctrl+F) even where it is folded into a button. */
  searchOpen: boolean;
  onSearchOpen(open: boolean): void;
  writable: boolean;
  onNewFolder(): void;
  onAddFiles(): void;
  onAddFolder(): void;
  exportLabel: string;
  canExport: boolean;
  onExport(): void;
  canDelete: boolean;
  onDelete(): void;
  onRefresh(): void;
}

const MAX_LEVEL = 3;
/** Room the current folder's name gets before the commands step tighter (px, about 20 characters). */
const NAME_ROOM = 150;

export function BrowserToolbar(props: ToolbarProps) {
  const { t, lang } = useI18n();
  const bar = useRef<HTMLDivElement>(null);
  const crumbsBox = useRef<HTMLDivElement>(null);
  const { crumbs, folders, search, searchOpen } = props;
  const current = crumbs[crumbs.length - 1];

  const fits = () => {
    const box = crumbsBox.current;
    const toolbar = bar.current!;
    if (toolbar.scrollWidth > toolbar.clientWidth + 1) return false;
    if (!box) return true; // the search box covers the breadcrumbs
    const label = box.querySelector('[data-testid=crumb-current] [dir]');
    const button = label?.parentElement;
    if (!label || !button) return true;
    // The current folder's whole name, up to NAME_ROOM, plus its button and the "…" before it.
    const padding = button.offsetWidth - label.clientWidth;
    const need = Math.min(label.scrollWidth, NAME_ROOM) + padding + (crumbs.length > 1 ? 44 : 0);
    return box.clientWidth >= need;
  };
  const level = useFitLevel(bar, MAX_LEVEL, fits, `${lang}|${folders ? folders.options.length : 0}|${current?.label ?? ''}`);
  const searchFolded = level >= 3;
  const searchShown = !searchFolded || searchOpen || search !== '';

  useEffect(() => {
    if (searchOpen) props.searchBox.current?.focus();
  }, [searchOpen, props.searchBox]);

  const addTip = t('files.addTip');
  const more: MenuEntry[] = [
    ...(folders
      ? [
          { key: 'folders', label: t('appFiles.folders'), heading: true as const },
          ...folders.options.map((o) => ({
            key: `folder-${o.value}`,
            label: o.label,
            icon: o.value === folders.value ? <CheckmarkRegular /> : <span />,
            onSelect: () => folders.onChange(o.value),
          })),
          'separator' as const,
        ]
      : []),
    { key: 'mkdir', label: t('files.newFolder'), icon: <FolderAddRegular />, disabled: !props.writable, onSelect: props.onNewFolder },
    { key: 'delete', label: t('common.delete'), icon: <DeleteRegular />, hint: 'Del', danger: true, disabled: !props.canDelete, onSelect: props.onDelete },
    'separator',
    { key: 'refresh', label: t('appFiles.refresh'), icon: <ArrowClockwiseRegular />, onSelect: props.onRefresh },
  ];

  return (
    <div ref={bar} className={styles.toolbar} data-level={level} data-testid="browser-toolbar">
      <TipButton variant="subtle" size="small" icon={<ArrowUpRegular />} tip={t('common.back')} shortcut="Alt ↑" aria-label={t('common.back')} onClick={props.onUp} disabled={!props.canGoUp} data-testid="browser-up" />
      {searchFolded && searchShown ? null : (
        <div ref={crumbsBox} className={styles.crumbsBox}>
          <Breadcrumbs items={crumbs} label={t('files.breadcrumb')} />
        </div>
      )}
      {folders && level < 2 ? (
        <div className={styles.folderSwitch}>
          <Segmented label={t('appFiles.folders')} value={folders.value} onChange={folders.onChange} options={folders.options} />
        </div>
      ) : null}
      {searchShown ? (
        <label className={styles.search} data-filled={search ? true : undefined} data-open={searchFolded || undefined}>
          <SearchRegular aria-hidden />
          <input
            ref={props.searchBox}
            type="search"
            value={search}
            placeholder={t('appFiles.searchFolder')}
            aria-label={t('appFiles.searchFolder')}
            onChange={(e) => props.onSearch(e.target.value)}
            onBlur={() => {
              if (!search) props.onSearchOpen(false);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Escape' && (search || searchFolded)) {
                e.stopPropagation();
                props.onSearch('');
                props.onSearchOpen(false);
              }
            }}
            data-testid="file-search"
          />
          {search ? (
            <button
              type="button"
              className={styles.searchClear}
              aria-label={t('appFiles.clearSearch')}
              onClick={() => {
                props.onSearch('');
                props.onSearchOpen(false);
              }}
            >
              <DismissRegular />
            </button>
          ) : null}
        </label>
      ) : (
        <TipButton
          variant="subtle"
          size="small"
          icon={<SearchRegular />}
          tip={t('appFiles.searchFolder')}
          shortcut="Ctrl+F"
          aria-label={t('appFiles.searchFolder')}
          onClick={() => props.onSearchOpen(true)}
          data-testid="file-search-open"
        />
      )}
      <span className={styles.toolbarGap} aria-hidden />
      {level < 2 ? (
        <TipButton variant="subtle" size="small" icon={<FolderAddRegular />} tip={t('files.newFolder')} aria-label={t('files.newFolder')} disabled={!props.writable} onClick={props.onNewFolder} data-testid="new-folder" />
      ) : null}
      <Menu
        align="end"
        trigger={
          <TipButton variant="subtle" size="small" icon={<AddRegular />} tip={addTip} aria-label={t('appFiles.add')} disabled={!props.writable} data-testid="add-menu">
            {level === 0 ? t('appFiles.add') : null}
          </TipButton>
        }
        items={[
          { key: 'files', label: t('appFiles.addFiles'), icon: <DocumentAddRegular />, onSelect: props.onAddFiles },
          { key: 'folder', label: t('appFiles.addFolder'), icon: <FolderArrowUpRegular />, onSelect: props.onAddFolder },
        ]}
      />
      <TipButton
        variant="subtle"
        size="small"
        icon={<ArrowExportRegular className="flip-rtl" />}
        tip={props.exportLabel}
        shortcut="Ctrl+E"
        aria-label={props.exportLabel}
        disabled={!props.canExport}
        onClick={props.onExport}
        data-testid="export-button"
      >
        {level === 0 ? t('common.export') : null}
      </TipButton>
      {level < 2 ? (
        <>
          <TipButton variant="subtle" size="small" icon={<DeleteRegular />} tip={t('common.delete')} shortcut="Del" aria-label={t('common.delete')} disabled={!props.canDelete} onClick={props.onDelete} data-testid="delete-button" />
          <TipButton variant="subtle" size="small" icon={<ArrowClockwiseRegular />} tip={t('appFiles.refresh')} aria-label={t('appFiles.refresh')} onClick={props.onRefresh} />
        </>
      ) : (
        <Menu
          align="end"
          trigger={<TipButton variant="subtle" size="small" icon={<MoreHorizontalRegular />} tip={t('files.more')} aria-label={t('files.more')} data-testid="toolbar-more" />}
          items={more}
        />
      )}
    </div>
  );
}
