import { afterEach, describe, expect, it, vi } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { BridgeError } from '../../src/bridge/errors';
import type { JsonRpcRequest } from '../../src/bridge/jsonrpc';
import { deviceLocalImages, type Transport } from '../../src/bridge/transport';
import { createWebViewTransport, type WebViewBridge } from '../../src/bridge/webview';

/** A transport whose host side the test drives by hand. */
function fakeTransport() {
  const sent: { request: JsonRpcRequest; files?: readonly File[] }[] = [];
  const listeners = new Set<(m: unknown) => void>();
  const transport: Transport = {
    kind: 'webview',
    post: (request, files) => sent.push({ request, files }),
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    images: deviceLocalImages,
  };
  const host = (message: unknown) => listeners.forEach((l) => l(message));
  return { transport, sent, host, listeners };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('BridgeClient', () => {
  it('sends JSON-RPC 2.0 requests with increasing ids and resolves by id', async () => {
    const { transport, sent, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const first = client.call('device.list');
    const second = client.call('device.storage', { deviceId: 'abc' });
    expect(sent.map((s) => s.request)).toEqual([
      { jsonrpc: '2.0', id: 1, method: 'device.list', params: {} },
      { jsonrpc: '2.0', id: 2, method: 'device.storage', params: { deviceId: 'abc' } },
    ]);
    // Answers may arrive out of order.
    host({ jsonrpc: '2.0', id: 2, result: { volumes: [] } });
    host({ jsonrpc: '2.0', id: 1, result: [] });
    await expect(first).resolves.toEqual([]);
    await expect(second).resolves.toEqual({ volumes: [] });
  });

  it('maps a provider error to BridgeError with data.code, message and detail', async () => {
    const { transport, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const call = client.call('device.battery', { deviceId: 'x' });
    host({
      jsonrpc: '2.0',
      id: 1,
      error: { code: -32000, message: 'Unlock the phone to continue.', data: { code: 'device_locked', detail: 'lockdown: PasswordProtected' } },
    });
    const error = await call.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(BridgeError);
    expect(error).toMatchObject({
      code: 'device_locked',
      rpcCode: -32000,
      message: 'Unlock the phone to continue.',
      detail: 'lockdown: PasswordProtected',
      method: 'device.battery',
    });
  });

  it('maps standard JSON-RPC errors to client codes', async () => {
    const { transport, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const a = client.call('app.hello');
    const b = client.call('app.hello');
    host({ jsonrpc: '2.0', id: 1, error: { code: -32601, message: 'Method not found' } });
    host({ jsonrpc: '2.0', id: 2, error: { code: -32602, message: 'Invalid params' } });
    await expect(a).rejects.toMatchObject({ code: 'method_not_found' });
    await expect(b).rejects.toMatchObject({ code: 'invalid_params' });
  });

  it('accepts messages posted as JSON strings and ignores malformed ones', async () => {
    const { transport, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const call = client.call('app.hello');
    host('not json');
    host({ id: 1, result: 'missing jsonrpc' });
    host({ jsonrpc: '2.0', id: 99, result: 'unknown id' });
    host(JSON.stringify({ jsonrpc: '2.0', id: 1, result: { version: '1.0', isWebView: true } }));
    await expect(call).resolves.toEqual({ version: '1.0', isWebView: true });
  });

  it('delivers notifications to listeners until they unsubscribe', () => {
    const { transport, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const seen: unknown[] = [];
    const off = client.on('device.removed', (p) => seen.push(p));
    host({ jsonrpc: '2.0', method: 'device.removed', params: { deviceId: 'a' } });
    host({ jsonrpc: '2.0', method: 'device.added', params: { id: 'ignored' } });
    off();
    host({ jsonrpc: '2.0', method: 'device.removed', params: { deviceId: 'b' } });
    expect(seen).toEqual([{ deviceId: 'a' }]);
  });

  it('keeps delivering when one listener throws', () => {
    const { transport, host } = fakeTransport();
    const client = new BridgeClient(transport);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const seen: unknown[] = [];
    client.on('drivers.changed', () => {
      throw new Error('boom');
    });
    client.on('drivers.changed', (p) => seen.push(p));
    host({ jsonrpc: '2.0', method: 'drivers.changed', params: [] });
    expect(seen).toEqual([[]]);
    expect(spy).toHaveBeenCalledOnce();
  });

  it('times out a call the host never answers, but not the folder picker', async () => {
    vi.useFakeTimers();
    const { transport } = fakeTransport();
    const client = new BridgeClient(transport, { timeoutMs: 1000 });
    const call = client.call('device.list');
    const picker = client.call('shell.pickFolder', { title: 'Export to' });
    let pickerSettled = false;
    picker.finally(() => (pickerSettled = true)).catch(() => {});
    vi.advanceTimersByTime(1001);
    await expect(call).rejects.toMatchObject({ code: 'timeout', method: 'device.list' });
    expect(pickerSettled).toBe(false);
    client.dispose();
    await expect(picker).rejects.toMatchObject({ code: 'transport_closed' });
  });

  it('sends File objects as additional objects with params.files = []', () => {
    const { transport, sent } = fakeTransport();
    const client = new BridgeClient(transport);
    const file = new File(['x'], 'Persepolis.mkv');
    void client.callWithFiles('files.copyToDevice', { deviceId: 'd', root: 'app:vlc', path: '/' }, [file]);
    expect(sent[0]!.request.params).toEqual({ deviceId: 'd', root: 'app:vlc', path: '/', files: [] });
    expect(sent[0]!.files).toEqual([file]);
  });

  it('sends known paths inline', () => {
    const { transport, sent } = fakeTransport();
    const client = new BridgeClient(transport);
    void client.callWithFiles('apps.install', { deviceId: 'd' }, ['C:\\apps\\Snapseed.ipa']);
    expect(sent[0]!.request.params).toEqual({ deviceId: 'd', files: ['C:\\apps\\Snapseed.ipa'] });
    expect(sent[0]!.files).toBeUndefined();
  });

  it('rejects a call whose post throws, and every call after dispose', async () => {
    const { transport } = fakeTransport();
    transport.post = () => {
      throw new Error('gone');
    };
    const client = new BridgeClient(transport);
    await expect(client.call('app.hello')).rejects.toMatchObject({ code: 'transport_closed', detail: 'gone' });
    client.dispose();
    await expect(client.call('app.hello')).rejects.toMatchObject({ code: 'transport_closed' });
  });
});

describe('WebView2 transport', () => {
  function fakeWebView() {
    const handlers = new Set<(e: { data: unknown }) => void>();
    const webview: WebViewBridge & { posted: unknown[]; withObjects: [unknown, ArrayLike<unknown>][] } = {
      posted: [],
      withObjects: [],
      postMessage(message) {
        this.posted.push(message);
      },
      postMessageWithAdditionalObjects(message, objects) {
        this.withObjects.push([message, objects]);
      },
      addEventListener: (_type, listener) => handlers.add(listener),
      removeEventListener: (_type, listener) => handlers.delete(listener),
    };
    return { webview, fire: (data: unknown) => handlers.forEach((h) => h({ data })), handlers };
  }

  it('posts requests as JSON strings and reads e.data objects (PostWebMessageAsJson)', async () => {
    const { webview, fire } = fakeWebView();
    const client = new BridgeClient(createWebViewTransport(webview));
    const call = client.call('device.list');
    expect(webview.posted).toEqual([JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'device.list', params: {} })]);
    fire({ jsonrpc: '2.0', id: 1, result: [{ id: 'udid' }] });
    await expect(call).resolves.toEqual([{ id: 'udid' }]);
  });

  it('uses postMessageWithAdditionalObjects for dropped files', () => {
    const { webview } = fakeWebView();
    const client = new BridgeClient(createWebViewTransport(webview));
    const files = [new File(['a'], 'a.apk'), new File(['b'], 'b.apk')];
    void client.callWithFiles('apps.install', { deviceId: 'serial' }, files);
    expect(webview.posted).toEqual([]);
    const [json, objects] = webview.withObjects[0]!;
    expect(JSON.parse(json as string)).toEqual({
      jsonrpc: '2.0',
      id: 1,
      method: 'apps.install',
      params: { deviceId: 'serial', files: [] },
    });
    expect(Array.from(objects)).toEqual(files);
  });

  it('stops listening on dispose', () => {
    const { webview, handlers } = fakeWebView();
    const client = new BridgeClient(createWebViewTransport(webview));
    expect(handlers.size).toBe(1);
    client.dispose();
    expect(handlers.size).toBe(0);
  });

  it('builds URI-encoded image URLs on https://device.local/', () => {
    const client = new BridgeClient(createWebViewTransport(fakeWebView().webview));
    expect(client.images.thumb('00008150-001A', 'ab/c d', 256)).toBe('https://device.local/thumb/00008150-001A/ab%2Fc%20d?s=256');
    expect(client.images.photo('dev', 'x')).toBe('https://device.local/photo/dev/x');
    expect(client.images.icon('192.168.1.4:5555', 'com.whatsapp')).toBe('https://device.local/icon/192.168.1.4%3A5555/com.whatsapp');
  });
});
