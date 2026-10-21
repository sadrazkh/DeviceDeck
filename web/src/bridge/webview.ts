import { nativePathOf } from './nativeFiles';
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
 * the request's params.files is [] and the host fills it from each CoreWebView2File.Path. Files the host itself
 * handed over from a File Explorer drop (components/nativeDrop.ts) go as their paths in params.files.
 * host → page: PostWebMessageAsJson, read from the 'message' event's data.
 */
export function createWebViewTransport(webview: WebViewBridge): Transport {
  return {
    kind: 'webview',
    post(request, files) {
      const json = JSON.stringify(request);
      if (files && files.length > 0) {
        // Files of a File Explorer drop that the host played in the page go by their paths, like any path the page sends.
        const paths = files.map(nativePathOf);
        if (paths.every((p): p is string => p !== undefined)) {
          webview.postMessage(JSON.stringify({ ...request, params: { ...(request.params as object), files: paths } }));
          return;
        }
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
