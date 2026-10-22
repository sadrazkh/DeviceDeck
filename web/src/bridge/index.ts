import { BridgeClient } from './client';
import type { MockBackend, MockScenario } from './mock/backend';
import { createMockTransport, type MockTransportOptions } from './mock/transport';
import { MOCK_LOCALSEND_SCENARIOS } from './mock/localSend';
import { MOCK_NOTES_SCENARIOS, MOCK_UPDATE_SCENARIOS } from './mock/updates';
import { MOCK_DELETE_SPEEDS, MOCK_PHOTODELETE_SCENARIOS, MOCK_REACH_SCENARIOS } from './mock/photoDelete';
import { MOCK_DRIVERS_SCENARIOS } from './mock/drivers';
import { createWebViewTransport, getWebView } from './webview';

export { BridgeClient, type FileSource } from './client';
export { BridgeError, isBridgeError } from './errors';
export * from './methods';
export type * from './types';
export type * from './drivers';

export interface Bridge {
  client: BridgeClient;
  /** Present only outside WebView2: lets the UI simulate a pulled cable, the Trust prompt and so on. */
  mock: MockBackend | null;
  dispose(): void;
}

const SCENARIOS: readonly MockScenario[] = ['ready', 'empty', 'trust', 'limited', 'driverMissing', 'iphoneOnly'];

/** Reads mock options from the page URL: ?mock=trust&latency=0&indexing=0&queue=0&lang=fa&theme=dark&density=compact&names=long */
export function mockOptionsFromUrl(search: string): MockTransportOptions {
  const query = new URLSearchParams(search);
  const scenario = query.get('mock');
  const latency = query.get('latency');
  const lang = query.get('lang');
  const theme = query.get('theme');
  const density = query.get('density');
  return {
    scenario: SCENARIOS.includes(scenario as MockScenario) ? (scenario as MockScenario) : 'ready',
    latencyMs: latency !== null && !Number.isNaN(Number(latency)) ? Number(latency) : undefined,
    indexing: query.get('indexing') !== '0',
    seedQueue: query.get('queue') !== '0',
    longNames: query.get('names') === 'long',
    // LocalSend on the phone (round 3): ?localsend=closed|opening|background|wifi|pin|reject|busy|quickSave|backgroundMidSend
    localSend: MOCK_LOCALSEND_SCENARIOS.find((s) => s === query.get('localsend')) ?? 'ready',
    // Updates (round 4): ?update=checking|downloading|downloadThenReady|ready|error|rateLimited|upToDate|portable|portableNew|whatsNew
    // and ?notes=english|hostile|missing|cache
    update: MOCK_UPDATE_SCENARIOS.find((s) => s === query.get('update')) ?? 'idle',
    notes: MOCK_NOTES_SCENARIOS.find((s) => s === query.get('notes')) ?? 'owner',
    // Deleting iPhone photos (round 4): ?photodelete=iCloudPhotos|readOnly|locked|notCamera|converted|recentlyDeleted|fails
    photoDelete: MOCK_PHOTODELETE_SCENARIOS.find((s) => s === query.get('photodelete')) ?? 'ok',
    // A delete's stages (v0.4.1): ?deletespeed=slow reads a slow camera roll, for watching them in the Transfers drawer.
    deleteSpeed: MOCK_DELETE_SPEEDS.find((s) => s === query.get('deletespeed')) ?? 'normal',
    // Items Windows doesn't list (v0.4.2): ?reach=some marks every fourth iPhone item "delete on the iPhone".
    reach: MOCK_REACH_SCENARIOS.find((s) => s === query.get('reach')) ?? 'all',
    // The Drivers card (v0.6): ?drivers=iosMissing|iosStopped|adbMissing|adbDownloading|adbDownloadFails|adbIdle|adbStarting|adbFailed|
    // androidNoDriver|androidUsbOff|androidUnauthorized|ready, and ?uac=decline for a declined administrator prompt.
    drivers: MOCK_DRIVERS_SCENARIOS.find((s) => s === query.get('drivers')),
    uacDeclined: query.get('uac') === 'decline',
    settings: {
      ...(lang === 'fa' || lang === 'en' ? { language: lang } : {}),
      ...(theme === 'light' || theme === 'dark' || theme === 'system' ? { theme } : {}),
      ...(density === 'compact' || density === 'comfortable' ? { density } : {}),
    },
  };
}

/** WebView2 when the page runs inside the host; the mock backend everywhere else. */
export function createBridge(search = typeof location === 'undefined' ? '' : location.search): Bridge {
  const webview = getWebView();
  if (webview) {
    const client = new BridgeClient(createWebViewTransport(webview));
    return { client, mock: null, dispose: () => client.dispose() };
  }
  const handle = createMockTransport(mockOptionsFromUrl(search));
  const client = new BridgeClient(handle.transport);
  return {
    client,
    mock: handle.backend,
    dispose: () => {
      client.dispose();
      handle.dispose();
    },
  };
}
