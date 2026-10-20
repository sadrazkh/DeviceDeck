import { deviceLocalImages, type Transport } from './transport';

/** The slice of `window.chrome.webview` the page uses. */
export interface WebViewBridge {
  postMessage(message: unknown): void;
  postMessageWithAdditionalObjects?(message: unknown, additionalObjects: ArrayLike<unknown>): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

export function getWebView(): WebViewBridge | null {
  if (typeof window === 'undefined') return null;
  return window.chrome?.webview ?? null;
}

/**
 * page → host: postMessage(JSON string). Dropped files go with postMessageWithAdditionalObjects;
 * the request's params.files is [] and the host fills it from each CoreWebView2File.Path.
 * host → page: PostWebMessageAsJson, read from the 'message' event's data.
 */
export function createWebViewTransport(webview: WebViewBridge): Transport {
  return {
    kind: 'webview',
    post(request, files) {
      const json = JSON.stringify(request);
      if (files && files.length > 0) {
        if (!webview.postMessageWithAdditionalObjects) {
          throw new Error('This WebView2 runtime cannot pass files to the host.');
        }
        webview.postMessageWithAdditionalObjects(json, files);
        return;
      }
      webview.postMessage(json);
    },
    subscribe(listener) {
      const handler = (event: { data: unknown }) => listener(event.data);
      webview.addEventListener('message', handler);
      return () => webview.removeEventListener('message', handler);
    },
    images: deviceLocalImages,
  };
}
