// Dragging photos and files out of the window. The page cancels its own HTML drag and asks the host for a
// native one (shell.dragOut): File Explorer then receives virtual files, which the transfer queue copies as
// Explorer reads them. A drop back on the window comes back as a point; the element under it that registered
// with useInsideDrop handles it (phone items onto the PC pane, PC files onto the phone).

import { useCallback, useMemo, useRef, type DragEvent } from 'react';
import type { BridgeClient } from '../bridge';
import type { DeviceSummary, DragOutEntry, DragOutParams, DragOutResult, FileEntry, PhotoAsset } from '../bridge/types';
import { useClient } from '../app/bridgeContext';
import type { Selection } from '../state/selection';

type InsideDropHandler = (drop: DragOutParams, hit: Element) => void;

const targets = new Map<Element, InsideDropHandler>();
let active = 0;

/** True while a native drag out of the window runs: drop zones ignore it passing over them. */
export function isDraggingOut(): boolean {
  return active > 0;
}

export const photoEntry = (asset: PhotoAsset): DragOutEntry => ({
  id: asset.id,
  name: asset.fileName,
  size: asset.size,
  isDirectory: false,
  modified: asset.takenAt,
});

export const fileEntry = (entry: FileEntry): DragOutEntry => ({
  id: entry.path,
  name: entry.name,
  size: entry.size,
  isDirectory: entry.isDirectory,
  modified: entry.modified,
});

/** Starts the native drag; resolves when it ends. Null outside WebView2 or when the host refused. */
export async function startDragOut(client: BridgeClient, params: DragOutParams): Promise<DragOutResult | null> {
  if (client.kind !== 'webview') return null;
  active++;
  try {
    const result = await client.call('shell.dragOut', params);
    if (result.droppedInside) deliverInside(result.droppedInside, params);
    return result;
  } catch (error) {
    console.warn('Drag out failed', error);
    return null;
  } finally {
    active--;
  }
}

function deliverInside(point: { x: number; y: number }, drop: DragOutParams): void {
  const hit = document.elementFromPoint(point.x, point.y);
  for (let element: Element | null = hit; element; element = element.parentElement) {
    const handler = targets.get(element);
    if (handler) {
      handler(drop, hit!);
      return;
    }
  }
}

/**
 * Drag props for something the user can drag out of the window: the HTML drag is cancelled and a native one
 * starts with what params() returns (null: nothing to drag). Only in WebView2; in a browser nothing changes.
 */
export function dragOutProps(client: BridgeClient, params: () => DragOutParams | null) {
  if (client.kind !== 'webview') return {};
  return {
    draggable: true,
    onDragStart: (e: DragEvent) => {
      e.preventDefault();
      const p = params();
      if (p) void startDragOut(client, p);
    },
  };
}

/** A ref for an element that takes page items dropped back on the window. The handler gets the innermost element hit. */
export function useInsideDrop(handler: InsideDropHandler) {
  const latest = useRef(handler);
  latest.current = handler;
  const current = useRef<Element | null>(null);
  return useCallback((element: HTMLElement | null) => {
    if (current.current) targets.delete(current.current);
    current.current = element;
    if (element) targets.set(element, (drop, hit) => latest.current(drop, hit));
  }, []);
}

/** For the photo grid: drag a tile (the selection, when the tile is in it) out of the window. Undefined outside WebView2. */
export function usePhotoDragOut(device: DeviceSummary, assets: readonly PhotoAsset[], selection: Selection, selected: readonly PhotoAsset[]) {
  const client = useClient();
  const latest = useRef({ assets, selection, selected });
  latest.current = { assets, selection, selected };
  const deviceId = device.id;
  return useMemo(() => {
    if (client.kind !== 'webview') return undefined;
    return (index: number) => {
      const { assets, selection, selected } = latest.current;
      const asset = assets[index];
      if (!asset) return;
      // Photos kept only in iCloud can't come over USB.
      const items = (selection.ids.has(asset.id) ? selected : [asset]).filter((a) => !a.inCloudOnly);
      if (items.length > 0) void startDragOut(client, { source: 'photos', deviceId, items: items.map(photoEntry) });
    };
  }, [client, deviceId]);
}
