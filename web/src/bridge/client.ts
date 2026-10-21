import { BridgeError } from './errors';
import { isFailure, isNotification, parseIncoming, type JsonRpcRequest } from './jsonrpc';
import type {
  BridgeEvents,
  EventName,
  FileMethodName,
  MethodName,
  MethodParams,
  MethodResult,
} from './methods';
import type { ImageUrls, Transport } from './transport';

/** Methods with no required params can be called without an argument. */
type ParamsArg<M extends MethodName> = {} extends MethodParams<M> ? [params?: MethodParams<M>] : [params: MethodParams<M>];

/** Files for a file-taking method: paths the host already knows, or File objects from a drop or picker. */
export type FileSource = readonly string[] | readonly File[];

interface Pending {
  method: string;
  resolve(value: unknown): void;
  reject(error: unknown): void;
  timer: ReturnType<typeof setTimeout> | null;
}

export interface BridgeClientOptions {
  /** Default 30 s. Methods that wait for the user (the folder picker) or for copies never time out. */
  timeoutMs?: number;
}

// Calls that may legitimately run long: a folder picker waits for the user; clean-up deletes may first wait for
// copies to the PC to finish and a cache trim takes a while on the phone; listing crash reports and saving a
// device report read a lot from the phone; a drag out lasts as long as the pointer is down; an update check may download.
const NO_TIMEOUT: ReadonlySet<string> = new Set([
  'shell.pickFolder',
  'cleanup.deleteMedia',
  'cleanup.deleteFiles',
  'cleanup.uninstallApps',
  'cleanup.trimCaches',
  'crash.list',
  'report.save',
  'shell.dragOut',
  'app.checkForUpdates',
  // WhatsApp (round 4): the first call opens WhatsApp's database, which may be gigabytes, from an encrypted backup.
  'whatsapp.status',
  'whatsapp.chats',
  // Deleting photos (round 4): may copy them to the PC first.
  'photos.deleteItems',
]);

export class BridgeClient {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Map<string, Set<(params: unknown) => void>>();
  private readonly unsubscribe: () => void;
  private readonly timeoutMs: number;
  private disposed = false;

  constructor(
    private readonly transport: Transport,
    options: BridgeClientOptions = {},
  ) {
    this.timeoutMs = options.timeoutMs ?? 30_000;
    this.unsubscribe = transport.subscribe((message) => this.receive(message));
  }

  get kind(): Transport['kind'] {
    return this.transport.kind;
  }

  get images(): ImageUrls {
    return this.transport.images;
  }

  call<M extends MethodName>(method: M, ...args: ParamsArg<M>): Promise<MethodResult<M>> {
    return this.send(method, args[0] ?? {}, undefined) as Promise<MethodResult<M>>;
  }

  /**
   * Calls a method that takes local files. File objects go to the host as additional objects and
   * `params.files` is sent as [] for the host to fill; plain paths are sent as they are.
   */
  callWithFiles<M extends FileMethodName>(
    method: M,
    params: Omit<MethodParams<M>, 'files'>,
    files: FileSource,
  ): Promise<MethodResult<M>> {
    const objects = files.filter((f): f is File => typeof f !== 'string');
    if (objects.length > 0) {
      return this.send(method, { ...params, files: [] }, objects) as Promise<MethodResult<M>>;
    }
    return this.send(method, { ...params, files: [...(files as readonly string[])] }, undefined) as Promise<
      MethodResult<M>
    >;
  }

  on<E extends EventName>(event: E, handler: (params: BridgeEvents[E]) => void): () => void {
    let set = this.listeners.get(event);
    if (!set) {
      set = new Set();
      this.listeners.set(event, set);
    }
    const listener = handler as (params: unknown) => void;
    set.add(listener);
    return () => {
      set.delete(listener);
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribe();
    for (const [id, pending] of this.pending) {
      if (pending.timer) clearTimeout(pending.timer);
      pending.reject(
        new BridgeError({ message: 'The connection to the app closed.', code: 'transport_closed', rpcCode: 0, method: pending.method }),
      );
      this.pending.delete(id);
    }
    this.listeners.clear();
  }

  private send(method: string, params: unknown, files: readonly File[] | undefined): Promise<unknown> {
    if (this.disposed) {
      return Promise.reject(
        new BridgeError({ message: 'The connection to the app closed.', code: 'transport_closed', rpcCode: 0, method }),
      );
    }
    const id = this.nextId++;
    const request: JsonRpcRequest = { jsonrpc: '2.0', id, method, params };
    return new Promise((resolve, reject) => {
      const timer =
        NO_TIMEOUT.has(method) || this.timeoutMs <= 0
          ? null
          : setTimeout(() => {
              this.pending.delete(id);
              reject(
                new BridgeError({
                  message: 'The app took too long to answer.',
                  code: 'timeout',
                  rpcCode: 0,
                  method,
                  detail: `${method} timed out after ${this.timeoutMs} ms`,
                }),
              );
            }, this.timeoutMs);
      this.pending.set(id, { method, resolve, reject, timer });
      try {
        this.transport.post(request, files);
      } catch (error) {
        if (timer) clearTimeout(timer);
        this.pending.delete(id);
        reject(
          new BridgeError({
            message: 'The request could not be sent to the app.',
            code: 'transport_closed',
            rpcCode: 0,
            method,
            detail: error instanceof Error ? error.message : String(error),
          }),
        );
      }
    });
  }

  private receive(raw: unknown): void {
    const message = parseIncoming(raw);
    if (!message) return;

    if (isNotification(message)) {
      const set = this.listeners.get(message.method);
      if (!set) return;
      for (const listener of [...set]) {
        try {
          listener(message.params);
        } catch (error) {
          console.error(`Bridge event handler for ${message.method} failed`, error);
        }
      }
      return;
    }

    if (message.id === null) return;
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    if (pending.timer) clearTimeout(pending.timer);
    if (isFailure(message)) {
      pending.reject(BridgeError.fromRpc(pending.method, message.error));
    } else {
      pending.resolve(message.result);
    }
  }
}
