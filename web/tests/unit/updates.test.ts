// Updates you can see (round 4): the mock's update states, release notes and the new methods, as the page uses them.

import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { mockOptionsFromUrl } from '../../src/bridge';
import { METHOD_NAMES } from '../../src/bridge/methods';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import type { MockNotesScenario, MockUpdateScenario } from '../../src/bridge/mock/updates';
import type { UpdateStatus } from '../../src/bridge/types';
import { offerOf, setupFileName } from '../../src/shell/updates';

let handle: MockHandle | null = null;

function setup(update: MockUpdateScenario = 'idle', notes: MockNotesScenario = 'owner') {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, update, notes, now: () => Date.UTC(2026, 10, 3, 9) });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock update states', () => {
  it('lists the round-4 methods and reads ?update= and ?notes=', () => {
    expect(METHOD_NAMES).toEqual(expect.arrayContaining(['app.releaseNotes', 'app.openReleasePage', 'app.whatsNewShown']));
    expect(mockOptionsFromUrl('?update=portableNew&notes=hostile')).toMatchObject({ update: 'portableNew', notes: 'hostile' });
    expect(mockOptionsFromUrl('?update=nonsense')).toMatchObject({ update: 'idle', notes: 'owner' });
  });

  it('has each state the page shows', async () => {
    const states: Record<string, Partial<UpdateStatus>> = {
      checking: { state: 'checking' },
      downloading: { state: 'downloading', percent: 42, newVersion: '0.4.0' },
      ready: { state: 'ready', newVersion: '0.4.0', install: 'setup' },
      error: { state: 'error', lastResult: 'error', retryAt: null },
      rateLimited: { state: 'error', lastResult: 'error' },
      upToDate: { state: 'upToDate', lastResult: 'upToDate' },
      portable: { state: 'upToDate', install: 'portable' },
      portableNew: { state: 'available', newVersion: '0.4.0', install: 'portable' },
      whatsNew: { state: 'idle', currentVersion: '0.4.0', whatsNew: '0.4.0' },
    };
    for (const [scenario, expected] of Object.entries(states)) {
      const { client } = setup(scenario as MockUpdateScenario);
      expect(await client.call('app.updateStatus'), scenario).toMatchObject(expected);
      handle!.dispose();
    }
    const { client } = setup('rateLimited');
    expect((await client.call('app.updateStatus')).retryAt).toBe('2026-11-03T09:40:00.000Z');
  });

  it('downloads to ready by itself in downloadThenReady', async () => {
    const { client, backend } = setup('downloadThenReady');
    const seen: string[] = [];
    client.on('app.updateChanged', (s) => seen.push(`${s.state}:${s.percent ?? ''}`));
    for (let i = 0; i < 5; i++) backend.tick(600);
    await Promise.resolve();
    expect(seen).toEqual(['downloading:62', 'downloading:82', 'ready:']);
    expect((await client.call('app.updateStatus')).state).toBe('ready');
  });

  it('restarts only when ready, and opens release pages only for versions', async () => {
    const ready = setup('ready');
    await ready.client.call('app.applyUpdate');
    expect(ready.backend.updates.applied).toBe(1);
    handle!.dispose();

    const portable = setup('portableNew');
    await expect(portable.client.call('app.applyUpdate')).rejects.toMatchObject({ code: 'internal' });
    await portable.client.call('app.openReleasePage', { version: '0.4.0' });
    await portable.client.call('app.openReleasePage', {});
    await expect(portable.client.call('app.openReleasePage', { version: 'https://evil.example' })).rejects.toBeTruthy();
    expect(portable.backend.updates.opened).toEqual(['0.4.0', null]);
    // Check now keeps the version on offer.
    expect(await portable.client.call('app.checkForUpdates')).toMatchObject({ state: 'available', newVersion: '0.4.0' });
  });

  it('What’s new is marked shown once', async () => {
    const { client, backend } = setup('whatsNew');
    const seen: (string | null)[] = [];
    client.on('app.updateChanged', (s) => seen.push(s.whatsNew));
    await client.call('app.whatsNewShown', { version: '0.4.0' });
    expect(backend.updates.whatsNewShown).toEqual(['0.4.0']);
    expect(seen).toEqual([null]);
    expect((await client.call('app.updateStatus')).whatsNew).toBeNull();
  });

  it('serves release notes in each shape', async () => {
    for (const [notes, source, hasMarkdown] of [
      ['owner', 'github', true],
      ['cache', 'cache', true],
      ['missing', 'none', false],
    ] as const) {
      const { client } = setup('ready', notes);
      const result = await client.call('app.releaseNotes', { version: '0.4.0' });
      expect(result.url).toBe('https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0');
      expect(result.source).toBe(source);
      expect(result.markdown !== null).toBe(hasMarkdown);
      handle!.dispose();
    }
    const { client } = setup();
    await expect(client.call('app.releaseNotes', { version: '../x' })).rejects.toBeTruthy();
  });
});

describe('the offer', () => {
  it('is the downloaded version or the one on GitHub, nothing else', () => {
    const base = { currentVersion: '0.3.0', newVersion: '0.4.0' } as UpdateStatus;
    expect(offerOf({ ...base, state: 'ready' })).toEqual({ version: '0.4.0', kind: 'ready' });
    expect(offerOf({ ...base, state: 'available' })).toEqual({ version: '0.4.0', kind: 'available' });
    expect(offerOf({ ...base, state: 'downloading' })).toBeNull();
    expect(offerOf({ ...base, state: 'error' })).toBeNull();
    expect(offerOf(undefined)).toBeNull();
    expect(setupFileName('0.4.0', 'arm64')).toBe('DeviceDeck-0.4.0-win-arm64-Setup.exe');
  });
});
