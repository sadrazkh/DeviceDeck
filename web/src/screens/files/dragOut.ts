// Dragging phone files out to File Explorer. The host owns the drag (the desktop feature's shell.dragOut): the page
// cancels the browser's own drag on dragstart and asks the host to start a native one while the button is still
// down. Explorer gets virtual files, which a dragOut job in the transfer queue copies while Explorer reads them.

import type { BridgeClient } from '../../bridge';
import { startDragOut } from '../../shell/dragOut';

interface DragOutResult {
  /** "copy" when another app took the drop; "none" when it was cancelled or dropped back on the window. */
  effect: string;
  droppedInside: { x: number; y: number } | null;
}

/** Only the WebView2 host can drag files out; the mock in a plain browser can't. */
export const canDragOut = (client: BridgeClient) => client.kind === 'webview';

export async function dragOutFiles(client: BridgeClient, deviceId: string, root: string, paths: string[]): Promise<DragOutResult | null> {
  if (!canDragOut(client) || paths.length === 0) return null;
  // Through the shell's helper: drop zones ignore the drag while it runs, and a drop back on the window (the PC
  // pane) reaches the element under the pointer.
  return startDragOut(client, { source: 'files', deviceId, root, paths });
}
