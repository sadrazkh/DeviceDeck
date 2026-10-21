import { afterEach, describe, expect, it } from 'vitest';
import { mockOptionsFromUrl } from '../../src/bridge';
import { BridgeClient } from '../../src/bridge/client';
import { nativePathOf, registerNativeFile } from '../../src/bridge/nativeFiles';
import { LONG_ANDROID_APP, LONG_IPHONE_APP } from '../../src/bridge/mock/longNames';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { createWebViewTransport, type WebViewBridge } from '../../src/bridge/webview';
import { foldCrumbs } from '../../src/screens/files/Breadcrumbs';

// Round 3 (ui): the file browser's breadcrumbs, the long-name sample, and files dropped from File Explorer.

describe('breadcrumbs fold the middle first', () => {
  const crumbs = ['App documents', 'VLC', 'Films', 'Persian', 'Subtitles'];

  it('shows everything when nothing is folded', () => {
    expect(foldCrumbs(crumbs, 0)).toEqual({ before: [], hidden: [], after: crumbs });
  });

  it('folds the levels nearest the top first, keeping the first and the current folder', () => {
    expect(foldCrumbs(crumbs, 1)).toEqual({ before: ['App documents'], hidden: ['VLC'], after: ['Films', 'Persian', 'Subtitles'] });
    expect(foldCrumbs(crumbs, 3)).toEqual({ before: ['App documents'], hidden: ['VLC', 'Films', 'Persian'], after: ['Subtitles'] });
  });

  it('then folds the first one too: only the current folder stays', () => {
    expect(foldCrumbs(crumbs, 4)).toEqual({ before: [], hidden: ['App documents', 'VLC', 'Films', 'Persian'], after: ['Subtitles'] });
    expect(foldCrumbs(crumbs, 9).after).toEqual(['Subtitles']);
  });

  it('never folds a lone crumb', () => {
    expect(foldCrumbs(['Internal storage'], 3)).toEqual({ before: [], hidden: [], after: ['Internal storage'] });
  });
});

describe('the long-name sample (?names=long)', () => {
  let handle: MockHandle | null = null;
  afterEach(() => {
    handle?.dispose();
    handle = null;
  });
  const setup = (longNames: boolean) => {
    handle = createMockTransport({ latencyMs: 0, autoTick: false, seedQueue: false, longNames });
    return new BridgeClient(handle.transport, { timeoutMs: 0 });
  };

  it('is off by default, so other screens keep their sample data', async () => {
    const client = setup(false);
    const iphone = await client.call('appFiles.list', { deviceId: IPHONE_ID });
    expect(iphone.entries.map((e) => e.id)).not.toContain(LONG_IPHONE_APP.id);
    expect(mockOptionsFromUrl('?names=long').longNames).toBe(true);
    expect(mockOptionsFromUrl('').longNames).toBe(false);
  });

  it('adds a long-named app on each phone with folders three deep', async () => {
    const client = setup(true);
    const iphone = await client.call('appFiles.list', { deviceId: IPHONE_ID });
    expect(iphone.entries.find((e) => e.id === LONG_IPHONE_APP.id)?.name).toBe(LONG_IPHONE_APP.name);
    const deep = await client.call('files.list', {
      deviceId: IPHONE_ID,
      root: `app:${LONG_IPHONE_APP.id}`,
      path: '/Work Projects 2026 — Quarterly Reports and Board Presentations/گزارش‌های فصلی شرکت (نسخهٔ نهایی پس از بازبینی)/Q3 2026 – Final review drafts for the board meeting in Tehran',
    });
    expect(deep.map((e) => e.name)).toContain('گزارش Q3 2026 – Sales report (نسخهٔ ۲) final.xlsx');

    const android = await client.call('appFiles.list', { deviceId: ANDROID_ID });
    expect(android.entries.map((e) => e.id)).toContain(LONG_ANDROID_APP.id);
    const media = await client.call('files.list', { deviceId: ANDROID_ID, root: `appmedia:${LONG_ANDROID_APP.id}`, path: '/' });
    expect(media.find((e) => e.name === 'پوشهٔ خالی')?.isDirectory).toBe(true);
  });
});

describe('files dropped from File Explorer go back to the host as their paths', () => {
  function fakeWebView() {
    const sent: { message: unknown; objects?: ArrayLike<unknown> }[] = [];
    const webview: WebViewBridge = {
      postMessage: (message) => sent.push({ message }),
      postMessageWithAdditionalObjects: (message, objects) => sent.push({ message, objects }),
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    };
    return { webview, sent };
  }

  it('sends the paths of files the host handed over, with no additional objects', () => {
    const { webview, sent } = fakeWebView();
    const transport = createWebViewTransport(webview);
    const film = registerNativeFile(new File([], 'Persepolis.mkv'), 'E:\\Films\\Persepolis.mkv');
    const folder = registerNativeFile(new File([], 'زیرنویس'), 'E:\\Films\\زیرنویس');
    expect(nativePathOf(film)).toBe('E:\\Films\\Persepolis.mkv');
    transport.post({ jsonrpc: '2.0', id: 7, method: 'files.copyToDevice', params: { deviceId: 'x', root: 'app:vlc', path: '/', files: [] } }, [film, folder]);
    expect(sent).toHaveLength(1);
    expect(sent[0]!.objects).toBeUndefined();
    expect(JSON.parse(sent[0]!.message as string).params.files).toEqual(['E:\\Films\\Persepolis.mkv', 'E:\\Films\\زیرنویس']);
  });

  it('sends picked files as additional objects, as before', () => {
    const { webview, sent } = fakeWebView();
    const transport = createWebViewTransport(webview);
    const picked = new File(['x'], 'picked.txt');
    transport.post({ jsonrpc: '2.0', id: 8, method: 'files.copyToDevice', params: { files: [] } }, [picked]);
    expect(sent[0]!.objects).toEqual([picked]);
    expect(JSON.parse(sent[0]!.message as string).params.files).toEqual([]);
  });
});
