// Photo grouping and grid layout, kept pure so they can be tested without a DOM.

import type { PhotoAsset } from '../../bridge/types';

export interface PhotoGroup {
  key: string;
  /** ISO date of the group's newest item, for its label. */
  labelDate: string;
  /** Index of the group's first item in the flat, newest-first list. */
  start: number;
  count: number;
}

export type GridRow =
  | { kind: 'header'; group: number; size: number }
  | { kind: 'tiles'; group: number; from: number; to: number; size: number };

/** Groups a newest-first list by month; `keyOf` decides the calendar (Gregorian or Solar Hijri). */
export function groupByMonth(assets: readonly PhotoAsset[], keyOf: (iso: string) => string): PhotoGroup[] {
  const groups: PhotoGroup[] = [];
  const cache = new Map<string, string>();
  let current: PhotoGroup | null = null;
  for (let i = 0; i < assets.length; i++) {
    const taken = assets[i]!.takenAt ?? '';
    // The month only depends on the local day, so the (slower) calendar lookup runs once per day.
    const date = taken ? new Date(taken) : null;
    const day = date ? `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}` : '';
    let key = cache.get(day);
    if (key === undefined) {
      key = taken ? keyOf(taken) : 'unknown';
      cache.set(day, key);
    }
    if (!current || current.key !== key) {
      current = { key, labelDate: taken, start: i, count: 0 };
      groups.push(current);
    }
    current.count++;
  }
  return groups;
}

export function columnsFor(width: number, tileTarget: number, gap: number): number {
  return Math.max(2, Math.floor((width + gap) / (tileTarget + gap)));
}

/** Header rows and tile rows for the virtual list. */
export function buildRows(groups: readonly PhotoGroup[], columns: number, tileSize: number, gap: number, headerSize: number): GridRow[] {
  const rows: GridRow[] = [];
  groups.forEach((group, g) => {
    rows.push({ kind: 'header', group: g, size: headerSize });
    for (let from = group.start; from < group.start + group.count; from += columns) {
      rows.push({ kind: 'tiles', group: g, from, to: Math.min(from + columns, group.start + group.count), size: tileSize + gap });
    }
  });
  return rows;
}

/** The row that holds a flat item index. */
export function rowOfItem(rows: readonly GridRow[], index: number): number {
  let lo = 0;
  let hi = rows.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const row = rows[mid]!;
    if (row.kind === 'tiles' && index >= row.from && index < row.to) return mid;
    const pivot = row.kind === 'tiles' ? row.from : firstItemAfterHeader(rows, mid);
    if (index < pivot) hi = mid - 1;
    else lo = mid + 1;
  }
  return -1;
}

function firstItemAfterHeader(rows: readonly GridRow[], headerRow: number): number {
  const next = rows[headerRow + 1];
  return next && next.kind === 'tiles' ? next.from : Number.MAX_SAFE_INTEGER;
}

/** Moves the keyboard focus inside the grid, respecting group rows. */
export function moveIndex(rows: readonly GridRow[], index: number, key: 'left' | 'right' | 'up' | 'down', total: number): number {
  if (total === 0) return 0;
  if (key === 'left') return Math.max(0, index - 1);
  if (key === 'right') return Math.min(total - 1, index + 1);
  const r = rowOfItem(rows, index);
  if (r < 0) return index;
  const row = rows[r] as Extract<GridRow, { kind: 'tiles' }>;
  const column = index - row.from;
  const step = key === 'down' ? 1 : -1;
  for (let i = r + step; i >= 0 && i < rows.length; i += step) {
    const candidate = rows[i]!;
    if (candidate.kind !== 'tiles') continue;
    return Math.min(candidate.from + column, candidate.to - 1);
  }
  return index;
}
