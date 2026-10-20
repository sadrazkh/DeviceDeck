import type { JsonRpcErrorObject } from './jsonrpc';
import { RPC_DEVICE_ERROR } from './jsonrpc';

/** Client-side codes for failures that never reached a provider. */
export type ClientErrorCode = 'timeout' | 'transport_closed' | 'invalid_request' | 'method_not_found' | 'invalid_params';

/**
 * A failed bridge call. `code` is the contract's `data.code` (a DeviceErrorCodes value or "internal"),
 * or a client code; `message` is the host's one plain sentence; `detail` is the technical text.
 */
export class BridgeError extends Error {
  readonly code: string;
  readonly rpcCode: number;
  readonly detail: string | null;
  readonly method: string;

  constructor(options: { message: string; code: string; rpcCode: number; detail?: string | null; method: string }) {
    super(options.message);
    this.name = 'BridgeError';
    this.code = options.code;
    this.rpcCode = options.rpcCode;
    this.detail = options.detail ?? null;
    this.method = options.method;
  }

  static fromRpc(method: string, error: JsonRpcErrorObject): BridgeError {
    let code = error.data?.code;
    if (!code) {
      code =
        error.code === -32600
          ? 'invalid_request'
          : error.code === -32601
            ? 'method_not_found'
            : error.code === -32602
              ? 'invalid_params'
              : 'internal';
    }
    return new BridgeError({
      message: error.message,
      code,
      rpcCode: error.code,
      detail: error.data?.detail ?? null,
      method,
    });
  }
}

export function isBridgeError(error: unknown, code?: string): error is BridgeError {
  return error instanceof BridgeError && (code === undefined || error.code === code);
}

export function deviceError(code: string, message: string, detail?: string): JsonRpcErrorObject {
  return { code: RPC_DEVICE_ERROR, message, data: { code, detail: detail ?? '' } };
}
