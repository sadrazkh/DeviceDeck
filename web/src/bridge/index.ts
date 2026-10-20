import { BridgeClient } from './client';
import type { MockBackend, MockScenario } from './mock/backend';
import { createMockTransport, type MockTransportOptions } from './mock/transport';
import { createWebViewTransport, getWebView } from './webview';

export { BridgeClient, type FileSource } from './client';
export { BridgeError, isBridgeError } from './errors';
export * from './methods';
export type * from './types';

export interface Bridge {
  client: BridgeClient;
  /** Present only outside WebView2: lets the UI simulate a pulled cable, the Trust prompt and so on. */
  mock: MockBackend | null;
  dispose(): void;
}

const SCENARIOS: readonly MockScenario[] = ['ready', 'empty', 'trust', 'limited', 'driverMissing', 'iphoneOnly'];

/** Reads mock options from the page URL: ?mock=trust&latency=0&lang=fa&theme=dark&density=compact */
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
