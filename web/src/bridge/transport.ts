import type { JsonRpcRequest } from './jsonrpc';

/** Builds image URLs. In WebView2 they point at the host's virtual host https://device.local/. */
export interface ImageUrls {
  thumb(deviceId: string, assetId: string, size?: number): string;
  photo(deviceId: string, assetId: string): string;
  icon(deviceId: string, appId: string): string;
}

/** Moves JSON-RPC messages between the page and a backend (the .NET host or the mock). */
export interface Transport {
  readonly kind: 'webview' | 'mock';
  /** Sends one request. `files` travel as additional objects (dropped or picked File objects). */
  post(request: JsonRpcRequest, files?: readonly File[]): void;
  /** Listens for responses and notifications. Returns an unsubscribe function. */
  subscribe(listener: (message: unknown) => void): () => void;
  readonly images: ImageUrls;
}

const enc = encodeURIComponent;

/** URL scheme served by the host's WebResourceRequested handler. */
export const deviceLocalImages: ImageUrls = {
  thumb: (deviceId, assetId, size = 256) => `https://device.local/thumb/${enc(deviceId)}/${enc(assetId)}?s=${size}`,
  photo: (deviceId, assetId) => `https://device.local/photo/${enc(deviceId)}/${enc(assetId)}`,
  icon: (deviceId, appId) => `https://device.local/icon/${enc(deviceId)}/${enc(appId)}`,
};
