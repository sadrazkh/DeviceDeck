// Files dragged from File Explorer onto the window. WinUI hosts WebView2 without the browser's own drop target, so the
// host takes the drag (src/DeviceDeck.Shell/Desktop/DropIn) and sends where the pointer is; this plays it here as
// HTML drag events at that point (dragenter, dragover, dragleave, drop, in the browser's order), so every drop zone
// (useFileDrop, the App documents cards, the nav rail, …) works as it does in a browser. After each move the page
// tells the host whether the element under the pointer takes files: File Explorer shows copy or no-drop from that.
// On drop the files are real (read-only handles from the host) and remember their PC paths for the bridge.

import { registerNativeFile } from '../bridge/nativeFiles';
import { getWebView, type WebViewBridge } from '../bridge/webview';

export interface NativeDragFile {
  name: string;
  path: string;
  directory: boolean;
  /** Its FileSystemFileHandle among the drop message's additional objects; -1 for a folder. */
  handle: number;
}

export interface NativeDragMessage {
  $dd: 'nativeDrag';
  phase: 'enter' | 'over' | 'leave' | 'drop';
  x?: number;
  y?: number;
  files?: NativeDragFile[];
}

type FileHandle = { getFile(): Promise<File> };

/** A file's stand-in while it is dragged (names only, as the host sends them), or a folder's at the drop. */
function standIn(file: NativeDragFile): File {
  return registerNativeFile(new File([], file.name), file.path);
}

async function realFile(file: NativeDragFile, handles: ArrayLike<unknown> | undefined): Promise<File> {
  const handle = file.handle >= 0 ? (handles?.[file.handle] as FileHandle | undefined) : undefined;
  if (handle && typeof handle.getFile === 'function') {
    try {
      return registerNativeFile(await handle.getFile(), file.path);
    } catch {
      // Gone or unreadable since the drop: the host still copies it by its path.
    }
  }
  return standIn(file);
}

function transferOf(files: readonly File[]): DataTransfer {
  const transfer = new DataTransfer();
  for (const file of files) transfer.items.add(file);
  // Outside a real drag the browser ignores writes to dropEffect; keep what handlers set, as a real drag does.
  let effect: DataTransfer['dropEffect'] = 'copy';
  Object.defineProperty(transfer, 'dropEffect', { get: () => effect, set: (value: DataTransfer['dropEffect']) => (effect = value), configurable: true });
  Object.defineProperty(transfer, 'effectAllowed', { get: () => 'copy', configurable: true });
  return transfer;
}

/** Plays the host's drag messages as HTML drag events in `doc`; `answer` tells the host whether the drop is taken. */
export class NativeDragPlayer {
  private transfer: DataTransfer | null = null;
  private under: Element | null = null;
  private x = 0;
  private y = 0;
  private queue: Promise<void> = Promise.resolve();

  constructor(
    private readonly answer: (accept: boolean) => void,
    private readonly doc: Document = document,
  ) {}

  /** Messages are played in order; a drop waits for its files. */
  play(message: NativeDragMessage, handles?: ArrayLike<unknown>): Promise<void> {
    this.queue = this.queue.then(() => this.step(message, handles)).catch((error) => console.warn('A drop from File Explorer failed', error));
    return this.queue;
  }

  private async step(message: NativeDragMessage, handles: ArrayLike<unknown> | undefined) {
    const x = message.x ?? this.x;
    const y = message.y ?? this.y;
    switch (message.phase) {
      case 'enter':
        this.reset();
        this.transfer = transferOf((message.files ?? []).map(standIn));
        this.move(x, y);
        break;
      case 'over':
        if (this.transfer) this.move(x, y);
        break;
      case 'leave':
        if (this.transfer && this.under) this.fire('dragleave', this.under, this.transfer, null);
        this.reset();
        break;
      case 'drop': {
        const files = await Promise.all((message.files ?? []).map((f) => realFile(f, handles)));
        if (!this.transfer) this.transfer = transferOf(files);
        const taken = this.move(x, y);
        const target = this.under;
        if (target && taken) this.fire('drop', target, transferOf(files), null);
        else if (target) this.fire('dragleave', target, this.transfer, null);
        this.reset();
        break;
      }
    }
  }

  /** Moves the drag to (x, y): enter and leave as the element under it changes, then dragover. True when it is taken there. */
  private move(x: number, y: number): boolean {
    const transfer = this.transfer!;
    this.x = x;
    this.y = y;
    const target = this.doc.elementFromPoint(x, y) ?? this.doc.documentElement;
    if (target !== this.under) {
      const before = this.under;
      this.fire('dragenter', target, transfer, before);
      if (before) this.fire('dragleave', before, transfer, target);
      this.under = target;
    }
    transfer.dropEffect = 'copy';
    const cancelled = !this.fire('dragover', target, transfer, null);
    // A handler may have set dropEffect to 'none' (a place that refuses files).
    const taken = cancelled && (transfer.dropEffect as string) !== 'none';
    this.answer(taken);
    return taken;
  }

  private fire(type: string, target: Element, transfer: DataTransfer, related: Element | null): boolean {
    const event = new DragEvent(type, {
      bubbles: true,
      cancelable: type !== 'dragleave',
      composed: true,
      clientX: this.x,
      clientY: this.y,
      dataTransfer: transfer,
      relatedTarget: related,
    });
    return target.dispatchEvent(event);
  }

  private reset() {
    this.transfer = null;
    this.under = null;
  }
}

let installed = false;

/** In WebView2: plays the host's drags of File Explorer files in the page. Once; nothing in a plain browser. */
export function installNativeDrop(webview: WebViewBridge | null = getWebView()): void {
  if (!webview || installed) return;
  installed = true;
  const player = new NativeDragPlayer((accept) => webview.postMessage({ $dd: 'nativeDragFeedback', accept }));
  webview.addEventListener('message', (event) => {
    const data = event.data as Partial<NativeDragMessage> | null;
    if (data?.$dd !== 'nativeDrag' || !data.phase) return;
    void player.play(data as NativeDragMessage, (event as { additionalObjects?: ArrayLike<unknown> }).additionalObjects);
  });
}
