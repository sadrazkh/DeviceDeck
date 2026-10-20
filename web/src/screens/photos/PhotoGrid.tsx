import { CheckmarkRegular, CloudRegular, HeartFilled, PlayFilled } from '@fluentui/react-icons';
import { defaultRangeExtractor, useVirtualizer, type Range } from '@tanstack/react-virtual';
import { memo, useCallback, useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type Ref } from 'react';
import type { PhotoAsset } from '../../bridge/types';
import { useI18n } from '../../i18n';
import type { ClickModifiers, Selection } from '../../state/selection';
import { buildRows, columnsFor, moveIndex, rowOfItem, type PhotoGroup } from './grouping';
import styles from './Photos.module.css';

const HEADER_SIZE = 48;

export interface PhotoGridHandle {
  scrollToGroup(group: number): void;
}

interface TileProps {
  asset: PhotoAsset;
  index: number;
  src: string;
  selected: boolean;
  selecting: boolean;
  active: boolean;
  onClick(index: number, mods: ClickModifiers): void;
  onToggle(index: number): void;
  onOpen(index: number): void;
}

const Tile = memo(function Tile({ asset, index, src, selected, selecting, active, onClick, onToggle, onOpen }: TileProps) {
  const { t, date, duration } = useI18n();
  const [loaded, setLoaded] = useState(false);
  return (
    <div
      id={`photo-${asset.id}`}
      role="gridcell"
      aria-selected={selected}
      aria-label={t('photos.tile', { name: asset.fileName, date: asset.takenAt ? date(asset.takenAt) : '' })}
      className={styles.tile}
      data-selected={selected || undefined}
      data-selecting={selecting || undefined}
      data-active={active || undefined}
      data-cloud={asset.inCloudOnly || undefined}
      onClick={(e) => onClick(index, { ctrl: e.ctrlKey || e.metaKey, shift: e.shiftKey })}
      onDoubleClick={() => onOpen(index)}
    >
      <img src={src} alt="" loading="lazy" decoding="async" draggable={false} data-loaded={loaded || undefined} onLoad={() => setLoaded(true)} />
      <span
        className={styles.tick}
        aria-hidden
        onClick={(e) => {
          e.stopPropagation();
          onToggle(index);
        }}
      >
        {selected ? <CheckmarkRegular /> : null}
      </span>
      {asset.kind === 'video' && asset.durationSeconds !== null ? (
        <span className={`${styles.chip} ${styles.chipDuration}`}>
          <PlayFilled aria-hidden />
          {duration(asset.durationSeconds)}
        </span>
      ) : null}
      {asset.kind === 'livePhoto' ? <span className={`${styles.chip} ${styles.chipLive}`}>{t('photos.live')}</span> : null}
      {asset.inCloudOnly && asset.kind !== 'livePhoto' ? (
        <span className={`${styles.chip} ${styles.chipCloud}`} title={t('photos.inCloudHint')}>
          <CloudRegular aria-label={t('photos.inCloud')} />
        </span>
      ) : null}
      {asset.isFavorite ? (
        <span className={`${styles.chip} ${styles.chipFavorite}`}>
          <HeartFilled aria-label={t('photos.favorite')} />
        </span>
      ) : null}
    </div>
  );
});

export function PhotoGrid({
  assets,
  groups,
  selection,
  tileTarget,
  thumbUrl,
  onClickTile,
  onToggleTile,
  onOpen,
  onToggleGroup,
  onSelectAll,
  onClear,
  onDelete,
  onExport,
  onVisibleGroup,
  handleRef,
}: {
  assets: readonly PhotoAsset[];
  groups: readonly PhotoGroup[];
  selection: Selection;
  tileTarget: number;
  thumbUrl(asset: PhotoAsset): string;
  onClickTile(index: number, mods: ClickModifiers): void;
  onToggleTile(index: number): void;
  onOpen(index: number): void;
  onToggleGroup(group: number): void;
  onSelectAll(): void;
  onClear(): void;
  onDelete(): void;
  onExport(): void;
  onVisibleGroup(group: number): void;
  handleRef?: Ref<PhotoGridHandle>;
}) {
  const { t, month, n } = useI18n();
  const scroller = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [active, setActive] = useState(0);
  const gap = 4;

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const observer = new ResizeObserver(() => setWidth(el.clientWidth));
    observer.observe(el);
    setWidth(el.clientWidth);
    return () => observer.disconnect();
  }, []);

  const columns = width > 0 ? columnsFor(width, tileTarget, gap) : 6;
  const tileSize = width > 0 ? (width - gap * (columns - 1)) / columns : tileTarget;
  const rows = useMemo(() => buildRows(groups, columns, tileSize, gap, HEADER_SIZE), [groups, columns, tileSize]);
  const headerRows = useMemo(() => rows.flatMap((r, i) => (r.kind === 'header' ? [i] : [])), [rows]);
  const activeHeader = useRef(0);

  const rangeExtractor = useCallback(
    (range: Range) => {
      // Keep the current month's header rendered so it can stick to the top.
      let sticky = 0;
      for (const h of headerRows) {
        if (h <= range.startIndex) sticky = h;
        else break;
      }
      activeHeader.current = sticky;
      const next = new Set([sticky, ...defaultRangeExtractor(range)]);
      return [...next].sort((a, b) => a - b);
    },
    [headerRows],
  );

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scroller.current,
    estimateSize: (i) => rows[i]?.size ?? HEADER_SIZE,
    overscan: 4,
    rangeExtractor,
  });

  useEffect(() => {
    virtualizer.measure();
  }, [rows, virtualizer]);

  const items = virtualizer.getVirtualItems();
  const stickyRow = activeHeader.current;
  const stickyGroup = rows[stickyRow]?.group ?? 0;
  useEffect(() => onVisibleGroup(stickyGroup), [stickyGroup, onVisibleGroup]);

  useImperativeHandle(
    handleRef,
    () => ({
      scrollToGroup(group: number) {
        const row = rows.findIndex((r) => r.kind === 'header' && r.group === group);
        if (row >= 0) virtualizer.scrollToIndex(row, { align: 'start' });
      },
    }),
    [rows, virtualizer],
  );

  useEffect(() => {
    if (active >= assets.length) setActive(Math.max(0, assets.length - 1));
  }, [assets.length, active]);

  const focusItem = (index: number) => {
    setActive(index);
    const row = rowOfItem(rows, index);
    if (row >= 0) virtualizer.scrollToIndex(row, { align: 'auto' });
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const rtl = document.documentElement.dir === 'rtl';
    const ctrl = e.ctrlKey || e.metaKey;
    const arrows: Record<string, 'left' | 'right' | 'up' | 'down'> = {
      ArrowLeft: rtl ? 'right' : 'left',
      ArrowRight: rtl ? 'left' : 'right',
      ArrowUp: 'up',
      ArrowDown: 'down',
    };
    const direction = arrows[e.key];
    if (direction) {
      e.preventDefault();
      const next = moveIndex(rows, active, direction, assets.length);
      focusItem(next);
      if (e.shiftKey) onClickTile(next, { shift: true, ctrl });
      return;
    }
    if (e.key === 'Home') {
      e.preventDefault();
      focusItem(0);
    } else if (e.key === 'End') {
      e.preventDefault();
      focusItem(assets.length - 1);
    } else if (e.key === ' ') {
      e.preventDefault();
      if (ctrl) onToggleTile(active);
      else onOpen(active);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      onOpen(active);
    } else if (ctrl && e.key.toLowerCase() === 'a') {
      e.preventDefault();
      onSelectAll();
    } else if (ctrl && e.key.toLowerCase() === 'e') {
      e.preventDefault();
      onExport();
    } else if (e.key === 'Escape') {
      if (selection.ids.size > 0) {
        e.preventDefault();
        onClear();
      }
    } else if (e.key === 'Delete') {
      e.preventDefault();
      onDelete();
    }
  };

  const clickTile = useCallback(
    (index: number, mods: ClickModifiers) => {
      setActive(index);
      onClickTile(index, mods);
    },
    [onClickTile],
  );

  const selecting = selection.ids.size > 0;
  const activeAsset = assets[active];

  return (
    <div
      ref={scroller}
      className={styles.grid}
      role="grid"
      aria-label={t('photos.grid')}
      aria-multiselectable
      aria-rowcount={rows.length}
      tabIndex={0}
      aria-activedescendant={activeAsset ? `photo-${activeAsset.id}` : undefined}
      onKeyDown={onKeyDown}
      data-testid="photo-grid"
    >
      <div className={styles.inner} style={{ height: virtualizer.getTotalSize() }}>
        {items.map((item) => {
          const row = rows[item.index]!;
          const isSticky = item.index === stickyRow;
          const position = isSticky
            ? { position: 'sticky' as const, height: item.size }
            : { position: 'absolute' as const, transform: `translateY(${item.start}px)`, height: item.size };
          if (row.kind === 'header') {
            const group = groups[row.group]!;
            const ids = assets.slice(group.start, group.start + group.count);
            const allSelected = ids.length > 0 && ids.every((a) => selection.ids.has(a.id));
            return (
              <div key={item.key} role="row" className={`${styles.row} ${styles.monthHeader}`} style={position} data-index={item.index}>
                <h2 className={styles.monthTitle} role="rowheader">
                  {group.labelDate ? month(group.labelDate) : t('common.unknown')}
                  <span className={styles.monthCount}>{n(group.count)}</span>
                </h2>
                <button type="button" className={styles.monthAction} onClick={() => onToggleGroup(row.group)}>
                  {allSelected ? t('photos.clearMonth') : t('photos.selectMonth')}
                </button>
              </div>
            );
          }
          return (
            <div
              key={item.key}
              role="row"
              className={`${styles.row} ${styles.tiles}`}
              style={{ ...position, gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`, paddingBottom: gap }}
              data-index={item.index}
            >
              {assets.slice(row.from, row.to).map((asset, i) => {
                const index = row.from + i;
                return (
                  <Tile
                    key={asset.id}
                    asset={asset}
                    index={index}
                    src={thumbUrl(asset)}
                    selected={selection.ids.has(asset.id)}
                    selecting={selecting}
                    active={index === active}
                    onClick={clickTile}
                    onToggle={onToggleTile}
                    onOpen={onOpen}
                  />
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
}
