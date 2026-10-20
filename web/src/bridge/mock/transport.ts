import type { JsonRpcRequest } from '../jsonrpc';
import type { Transport } from '../transport';
import { MockBackend, RpcFault, type MockOptions } from './backend';

export interface MockTransportOptions extends MockOptions {
  /** Simulated round-trip time. Default 40 ms; 0 answers on the next task. */
  latencyMs?: number;
  /** Advance simulated time on a real timer. Default true; tests turn it off and call backend.tick. */
  autoTick?: boolean;
  tickMs?: number;
}

export interface MockHandle {
  transport: Transport;
  backend: MockBackend;
  dispose(): void;
}

/**
 * Speaks JSON-RPC to the mock backend exactly as the host would: requests in, results, errors
 * and notifications out, so the same client code runs against both.
 */
export function createMockTransport(options: MockTransportOptions = {}): MockHandle {
  const listeners = new Set<(message: unknown) => void>();
  const deliver = (message: unknown) => {
    for (const listener of [...listeners]) listener(message);
  };
  const backend = new MockBackend((method, params) => deliver({ jsonrpc: '2.0', method, params }), options);
  const latency = options.latencyMs ?? 40;
  const timers = new Set<ReturnType<typeof setTimeout>>();
  let interval: ReturnType<typeof setInterval> | null = null;
  if (options.autoTick ?? true) {
    const step = options.tickMs ?? 250;
    interval = setInterval(() => backend.tick(step), step);
  }

  const respond = (request: JsonRpcRequest, files?: readonly File[]) => {
    let params = request.params;
    if (files && files.length > 0 && params && typeof params === 'object') {
      // The host fills params.files from the additional objects; the mock gives them made-up paths.
      params = { ...(params as object), files: backend.registerFiles(files) };
    }
    try {
      const result = backend.handle(request.method, params);
      // A round trip through JSON, like the real bridge: no shared references, no undefined.
      deliver(JSON.parse(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: result ?? null })));
    } catch (error) {
      if (error instanceof RpcFault) {
        deliver({ jsonrpc: '2.0', id: request.id, error: error.error });
      } else {
        deliver({
          jsonrpc: '2.0',
          id: request.id,
          error: {
            code: -32000,
            message: 'Something went wrong in the app.',
            data: { code: 'internal', detail: error instanceof Error ? error.message : String(error) },
          },
        });
      }
    }
  };

  const transport: Transport = {
    kind: 'mock',
    post(request, files) {
      // Jitter so skeletons and spinners get exercised, like a real host.
      const delay = latency === 0 ? 0 : latency * (0.6 + Math.random() * 0.8);
      const timer = setTimeout(() => {
        timers.delete(timer);
        respond(request, files);
      }, delay);
      timers.add(timer);
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    images: {
      thumb: (deviceId, assetId) => backend.thumbUrl(deviceId, assetId),
      photo: (deviceId, assetId) => backend.photoUrl(deviceId, assetId),
      icon: (deviceId, appId) => backend.iconUrl(deviceId, appId),
    },
  };

  return {
    transport,
    backend,
    dispose() {
      if (interval) clearInterval(interval);
      for (const timer of timers) clearTimeout(timer);
      timers.clear();
      listeners.clear();
    },
  };
}
