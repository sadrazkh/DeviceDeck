import { beforeEach, describe, expect, it } from 'vitest';
import type { TransferJob } from '../../src/bridge/types';
import { clickSelect, emptySelection, pruneSelection, selectAll, toggleGroup, toggleSelect } from '../../src/state/selection';
import { groupOf, groupTransfers, jobFraction, overallProgress } from '../../src/state/transfers';
import { useToasts } from '../../src/state/toasts';
import { useUi } from '../../src/state/ui';
import { resolvedTheme } from '../../src/state/prefs';

const order = ['a', 'b', 'c', 'd', 'e', 'f'];

describe('selection', () => {
  it('a plain click selects one item and sets the anchor', () => {
    const s = clickSelect(emptySelection, 'c', {}, order);
    expect([...s.ids]).toEqual(['c']);
    expect(s.anchor).toBe('c');
  });

  it('Ctrl+click toggles without clearing', () => {
    let s = clickSelect(emptySelection, 'a', {}, order);
    s = clickSelect(s, 'd', { ctrl: true }, order);
    expect([...s.ids].sort()).toEqual(['a', 'd']);
    s = clickSelect(s, 'a', { ctrl: true }, order);
    expect([...s.ids]).toEqual(['d']);
  });

  it('Shift+click selects a range from the anchor, in either direction', () => {
    let s = clickSelect(emptySelection, 'b', {}, order);
    s = clickSelect(s, 'e', { shift: true }, order);
    expect([...s.ids]).toEqual(['b', 'c', 'd', 'e']);
    s = clickSelect(s, 'a', { shift: true }, order);
    expect([...s.ids]).toEqual(['a', 'b']);
    expect(s.anchor).toBe('b');
  });

  it('Ctrl+Shift adds a range to what is selected', () => {
    let s = clickSelect(emptySelection, 'f', {}, order);
    s = clickSelect(s, 'a', { ctrl: true }, order);
    s = clickSelect(s, 'c', { ctrl: true, shift: true }, order);
    expect([...s.ids].sort()).toEqual(['a', 'b', 'c', 'f']);
  });

  it('Ctrl+A, month toggles and pruning', () => {
    let s = selectAll(order);
    expect(s.ids.size).toBe(6);
    s = toggleGroup(s, ['a', 'b']);
    expect([...s.ids].sort()).toEqual(['c', 'd', 'e', 'f']);
    s = toggleGroup(s, ['a', 'b']);
    expect(s.ids.size).toBe(6);
    s = toggleSelect(s, 'c');
    expect(s.ids.has('c')).toBe(false);
    const pruned = pruneSelection(s, new Set(['a', 'd']));
    expect([...pruned.ids].sort()).toEqual(['a', 'd']);
    expect(pruneSelection(pruned, new Set(order))).toBe(pruned);
  });
});

function job(partial: Partial<TransferJob>): TransferJob {
  return {
    id: 'j',
    deviceId: 'd',
    kind: 'copyToPc',
    title: 't',
    state: 'running',
    totalItems: 1,
    doneItems: 0,
    failedItems: 0,
    totalBytes: 100,
    doneBytes: 0,
    bytesPerSecond: null,
    etaSeconds: null,
    conflict: null,
    error: null,
    createdAt: '2026-10-20T10:00:00Z',
    finishedAt: null,
    ...partial,
  };
}

describe('transfer helpers', () => {
  it('groups states the way the drawer shows them', () => {
    expect(groupOf('running')).toBe('active');
    expect(groupOf('waitingForDevice')).toBe('active');
    expect(groupOf('needsDecision')).toBe('active');
    expect(groupOf('queued')).toBe('queued');
    expect(groupOf('cancelled')).toBe('done');
    expect(groupOf('failed')).toBe('failed');
    const groups = groupTransfers([
      job({ id: '1', state: 'done', finishedAt: '2026-10-20T09:00:00Z' }),
      job({ id: '2', state: 'done', finishedAt: '2026-10-20T11:00:00Z' }),
      job({ id: '3', state: 'queued' }),
    ]);
    expect(groups.done.map((j) => j.id)).toEqual(['2', '1']);
    expect(groups.queued).toHaveLength(1);
  });

  it('computes the pill’s overall progress from unfinished work only', () => {
    const progress = overallProgress([
      job({ id: '1', totalBytes: 100, doneBytes: 50 }),
      job({ id: '2', state: 'queued', totalBytes: 100, doneBytes: 0 }),
      job({ id: '3', state: 'done', totalBytes: 1000, doneBytes: 1000 }),
      job({ id: '4', state: 'failed' }),
    ]);
    expect(progress).toEqual({ active: 2, fraction: 0.25, failed: 1 });
    expect(overallProgress([]).fraction).toBeNull();
    expect(jobFraction(job({ state: 'done', doneBytes: 0 }))).toBe(1);
    expect(jobFraction(job({ totalBytes: 0, totalItems: 4, doneItems: 1 }))).toBe(0.25);
  });
});

describe('ui store', () => {
  beforeEach(() => {
    useUi.setState({ selectedDeviceId: null, places: {}, globalSection: null });
  });

  it('keeps each phone’s place when switching between them', () => {
    const ui = useUi.getState();
    ui.selectDevice('iphone');
    useUi.getState().navigate('photos');
    useUi.getState().setAlbum('videos');
    useUi.getState().selectDevice('android');
    useUi.getState().navigate('files');
    useUi.getState().selectDevice('iphone');
    const state = useUi.getState();
    expect(state.places.iphone).toMatchObject({ section: 'photos', albumId: 'videos' });
    expect(state.places.android).toMatchObject({ section: 'files' });
  });

  it('goes back and forward through sections', () => {
    useUi.getState().selectDevice('iphone');
    useUi.getState().navigate('photos');
    useUi.getState().navigate('apps');
    useUi.getState().goBack();
    expect(useUi.getState().places.iphone!.section).toBe('photos');
    useUi.getState().goBack();
    expect(useUi.getState().places.iphone!.section).toBe('overview');
    useUi.getState().goForward();
    expect(useUi.getState().places.iphone!.section).toBe('photos');
    useUi.getState().navigate('files');
    expect(useUi.getState().places.iphone!.forward).toEqual([]);
  });

  it('remembers a revealed Hidden album per phone, in memory only', () => {
    useUi.setState({ hiddenRevealed: {} });
    expect(useUi.getState().hiddenRevealed.iphone).toBeUndefined();
    useUi.getState().setHiddenRevealed('iphone', true);
    expect(useUi.getState().hiddenRevealed).toEqual({ iphone: true });
    useUi.getState().setHiddenRevealed('iphone', false);
    expect(useUi.getState().hiddenRevealed.iphone).toBe(false);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('opens Settings with no phone', () => {
    useUi.getState().navigate('settings');
    expect(useUi.getState().globalSection).toBe('settings');
  });
});

describe('toasts and theme', () => {
  it('keeps at most four toasts and dismisses by id', () => {
    useToasts.getState().clear();
    for (let i = 0; i < 6; i++) useToasts.getState().push({ tone: 'info', title: `t${i}` });
    const toasts = useToasts.getState().toasts;
    expect(toasts.map((t) => t.title)).toEqual(['t2', 't3', 't4', 't5']);
    useToasts.getState().dismiss(toasts[0]!.id);
    expect(useToasts.getState().toasts).toHaveLength(3);
  });

  it('resolves the system theme against Windows’ preference', () => {
    expect(resolvedTheme({ theme: 'system', systemDark: true })).toBe('dark');
    expect(resolvedTheme({ theme: 'system', systemDark: false })).toBe('light');
    expect(resolvedTheme({ theme: 'dark', systemDark: false })).toBe('dark');
  });
});
