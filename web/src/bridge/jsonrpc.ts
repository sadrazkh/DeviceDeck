// JSON-RPC 2.0 message shapes used over WebView2 web messages (docs/bridge-contract.md).

export interface JsonRpcRequest {
  jsonrpc: '2.0';
  id: number;
  method: string;
  params: unknown;
}

export interface JsonRpcErrorObject {
  code: number;
  message: string;
  data?: { code?: string; detail?: string } | null;
}

export interface JsonRpcSuccess {
  jsonrpc: '2.0';
  id: number;
  result: unknown;
}

export interface JsonRpcFailure {
  jsonrpc: '2.0';
  id: number | null;
  error: JsonRpcErrorObject;
}

export interface JsonRpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: unknown;
}

export type JsonRpcIncoming = JsonRpcSuccess | JsonRpcFailure | JsonRpcNotification;

/** Application errors from a provider (DeviceException). */
export const RPC_DEVICE_ERROR = -32000;
export const RPC_INVALID_REQUEST = -32600;
export const RPC_METHOD_NOT_FOUND = -32601;
export const RPC_INVALID_PARAMS = -32602;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Accepts what `e.data` holds: an object (PostWebMessageAsJson) or a JSON string (PostWebMessageAsString). */
export function parseIncoming(raw: unknown): JsonRpcIncoming | null {
  let value = raw;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value);
    } catch {
      return null;
    }
  }
  if (!isRecord(value) || value.jsonrpc !== '2.0') return null;
  if ('result' in value || 'error' in value) {
    if (typeof value.id !== 'number' && value.id !== null) return null;
    if ('error' in value && !isRecord(value.error)) return null;
    return value as unknown as JsonRpcSuccess | JsonRpcFailure;
  }
  if (typeof value.method === 'string' && (value.id === undefined || value.id === null)) {
    return value as unknown as JsonRpcNotification;
  }
  return null;
}

export function isNotification(message: JsonRpcIncoming): message is JsonRpcNotification {
  return 'method' in message && !('result' in message) && !('error' in message);
}

export function isFailure(message: JsonRpcIncoming): message is JsonRpcFailure {
  return 'error' in message;
}
