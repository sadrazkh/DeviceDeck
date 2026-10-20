import { describe, expect, it } from 'vitest';
import type { FileEntry, PhotoAsset } from '../../src/bridge/types';
import { buildRows, columnsFor, groupByMonth, moveIndex, rowOfItem } from '../../src/screens/photos/grouping';
import { isAndroidBlocked, kindOf, parentPath, sortEntries } from '../../src/screens/files/fileKinds';

function asset(id: string, takenAt: string): PhotoAsset {
  return { id, fileName: `${id}.HEIC`, kind: 'photo', takenAt, size: 1, width: 1, height: 1, durationSeconds: null, isFavorite: false, inCloudOnly: false, livePairId: null };
}

const assets = [
  asset('a', '2026-10-19T10:00:00'),
  asset('b', '2026-10-02T10:00:00'),
  asset('c', '2026-10-01T10:00:00'),
  asset('d', '2026-09-30T10:00:00'),
  asset('e', '2026-09-01T10:00:00'),
  asset('f', '2026-07-12T10:00:00'),
];
const keyOf = (iso: string) => iso.slice(0, 7);

describe('photo grid layout', () => {
  it('groups a newest-first list by month', () => {
    const groups = groupByMonth(assets, keyOf);
    expect(groups.map((g) => [g.key, g.start, g.count])).toEqual([
      ['2026-10', 0, 3],
      ['2026-09', 3, 2],
      ['2026-07', 5, 1],
    ]);
  });

  it('fits as many columns as the tile size allows, at least two', () => {
    expect(columnsFor(1000, 140, 4)).toBe(6);
    expect(columnsFor(100, 140, 4)).toBe(2);
  });

  it('lays out a header row plus tile rows per month, and finds items in them', () => {
    const rows = buildRows(groupByMonth(assets, keyOf), 2, 100, 4, 48);
    expect(rows.map((r) => (r.kind === 'header' ? 'H' : `${r.from}-${r.to}`))).toEqual(['H', '0-2', '2-3', 'H', '3-5', 'H', '5-6']);
    expect(rowOfItem(rows, 0)).toBe(1);
    expect(rowOfItem(rows, 2)).toBe(2);
    expect(rowOfItem(rows, 4)).toBe(4);
    expect(rowOfItem(rows, 5)).toBe(6);
  });

  it('moves the keyboard focus across rows and month breaks', () => {
    const rows = buildRows(groupByMonth(assets, keyOf), 2, 100, 4, 48);
    expect(moveIndex(rows, 1, 'down', 6)).toBe(2); // column 1 → the shorter row's last item
    expect(moveIndex(rows, 2, 'down', 6)).toBe(3);
    expect(moveIndex(rows, 4, 'up', 6)).toBe(2);
    expect(moveIndex(rows, 0, 'up', 6)).toBe(0);
    expect(moveIndex(rows, 5, 'right', 6)).toBe(5);
  });
});

function entry(name: string, isDirectory = false, size = 0, modified = '2026-01-01T00:00:00Z'): FileEntry {
  return { path: `/${name}`, name, isDirectory, size, modified, readOnly: false };
}

describe('file list', () => {
  const collator = new Intl.Collator('en', { numeric: true, sensitivity: 'base' });
  const list = [entry('b.mkv', false, 5), entry('Zeta', true), entry('a10.pdf', false, 1), entry('a2.pdf', false, 9), entry('Alpha', true)];

  it('sorts folders first, names naturally', () => {
    expect(sortEntries(list, 'name', false, collator).map((e) => e.name)).toEqual(['Alpha', 'Zeta', 'a2.pdf', 'a10.pdf', 'b.mkv']);
    expect(sortEntries(list, 'size', true, collator).map((e) => e.name)).toEqual(['Zeta', 'Alpha', 'a2.pdf', 'b.mkv', 'a10.pdf']);
  });

  it('knows kinds, parents and the folders Android blocks', () => {
    expect(kindOf(entry('Persepolis.mkv'))).toBe('video');
    expect(kindOf(entry('x.srt'))).toBe('subtitle');
    expect(kindOf(entry('README'))).toBe('file');
    expect(kindOf(entry('Movies', true))).toBe('folder');
    expect(parentPath('/a/b/c')).toBe('/a/b');
    expect(parentPath('/a')).toBe('/');
    expect(isAndroidBlocked('/Android/data')).toBe(true);
    expect(isAndroidBlocked('/Android/obb/com.game')).toBe(true);
    expect(isAndroidBlocked('/Android/media')).toBe(false);
  });
});
