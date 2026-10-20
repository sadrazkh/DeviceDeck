// Dragging phone files out to File Explorer. The host owns the drag (the desktop feature's shell.dragOut): the page
// cancels the browser's own drag on dragstart and asks the host to start a native one while the button is still
// down. When Explorer takes the drop, the host copies through the transfer queue (a copyToPc job).

import type { BridgeClient } from '../../bridge';

interface DragOutResult {
  /** "copy" when another app took the drop; "none" when it was cancelled or dropped back on the window. */
  effect: string;
  droppedInside: { x: number; y: number } | null;
}

/** Only the WebView2 host can drag files out; the mock in a plain browser can't. */
export const canDragOut = (client: BridgeClient) => client.kind === 'webview';

export async function dragOutFiles(client: BridgeClient, deviceId: string, root: string, paths: string[]): Promise<DragOutResult | null> {
  if (!canDragOut(client) || paths.length === 0) return null;
  // shell.dragOut is the desktop feature's method; called by name so this compiles on its own branch as well.
  const call = client.call as unknown as (method: string, params: unknown) => Promise<DragOutResult>;
  return call.call(client, 'shell.dragOut', { source: 'files', deviceId, root, paths });
}
