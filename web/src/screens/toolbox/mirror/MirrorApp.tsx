import {
  AlertRegular,
  ArrowRotateClockwiseRegular,
  ArrowUndoRegular,
  CameraRegular,
  HomeRegular,
  PowerRegular,
  Speaker1Regular,
  Speaker2Regular,
  SquareMultipleRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactElement } from 'react';
import { useBridge } from '../../../app/bridgeContext';
import { useDocumentState } from '../../../app/useDocumentState';
import type { MirrorCommand, MirrorInput, MirrorTouchAction } from '../../../bridge/toolbox';
import { MOUSE_POINTER_ID } from '../../../bridge/toolbox';
import { Button } from '../../../components/Button';
import { Spinner } from '../../../components/ui';
import { usePrefs } from '../../../state/prefs';
import { useToolboxI18n } from '../i18n';
import { FrameAssembler, fromBase64 } from './h264';
import { fitSize, KEY, keyAction, toVideoPoint, wheelToScroll } from './input';
import styles from './Mirror.module.css';

type Status = { kind: 'connecting' } | { kind: 'running' } | { kind: 'ended'; message: string | null } | { kind: 'preview' };

/** The decoder's queue length at which delta frames are dropped until the next key frame. */
const MAX_QUEUE = 6;

export function MirrorApp() {
  const { client } = useBridge();
  const { tt } = useToolboxI18n();
  const deviceId = new URLSearchParams(location.search).get('device') ?? '';
  const [status, setStatus] = useState<Status>({ kind: 'connecting' });
  const [session, setSession] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [video, setVideo] = useState({ width: 0, height: 0 });
  const [shown, setShown] = useState({ width: 0, height: 0 });
  const [note, setNote] = useState<string | null>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const sessionRef = useRef<string | null>(null);
  const videoRef = useRef(video);
  videoRef.current = video;

  useDocumentState(client.kind === 'webview' ? 'webview' : 'browser');
  useEffect(() => {
    void client.call('settings.get').then((settings) => usePrefs.getState().apply(settings), () => undefined);
  }, [client]);

  useEffect(() => {
    document.title = name ? `${name} – DeviceDeck` : 'DeviceDeck';
  }, [name]);

  const send = useCallback(
    (input: MirrorInput) => {
      const id = sessionRef.current;
      if (id) void client.call('mirror.input', { sessionId: id, ...input }).catch(() => undefined);
    },
    [client],
  );
  const command = useCallback((c: MirrorCommand) => send({ kind: 'command', command: c }), [send]);
  const key = useCallback(
    (keyCode: number) => {
      send({ kind: 'key', down: true, keyCode });
      send({ kind: 'key', down: false, keyCode });
    },
    [send],
  );

  // Session and decoding.
  useEffect(() => {
    let active = true;
    let id: string | null = null;
    const assembler = new FrameAssembler();
    let decoder: VideoDecoder | null = null;

    const draw = (frame: VideoFrame) => {
      const el = canvas.current;
      if (el) {
        if (el.width !== frame.displayWidth || el.height !== frame.displayHeight) {
          el.width = frame.displayWidth;
          el.height = frame.displayHeight;
          setVideo({ width: frame.displayWidth, height: frame.displayHeight });
        }
        el.getContext('2d')?.drawImage(frame, 0, 0);
      }
      frame.close();
    };
    const makeDecoder = () =>
      new VideoDecoder({
        output: draw,
        error: () => {
          // A broken stream: start again from the next key frame.
          assembler.reset();
          decoder = null;
          command('resetVideo');
        },
      });

    const offVideo = client.on('mirror.video', (e) => {
      if (e.sessionId !== id) return;
      const out = assembler.push({ kind: e.kind, pts: e.pts, key: e.key, width: e.width, height: e.height, data: e.data ? fromBase64(e.data) : new Uint8Array() });
      if (!out) return;
      if (out.type === 'session') {
        setVideo({ width: out.width, height: out.height });
        return;
      }
      try {
        if (out.type === 'configure') {
          decoder ??= makeDecoder();
          decoder.configure({ codec: out.codec, optimizeForLatency: true, hardwareAcceleration: 'no-preference' });
        }
        if (!decoder || decoder.state !== 'configured') return;
        if (!out.chunk.key && decoder.decodeQueueSize > MAX_QUEUE) {
          assembler.skipToKeyFrame();
          command('resetVideo');
          return;
        }
        decoder.decode(new EncodedVideoChunk({ type: out.chunk.key ? 'key' : 'delta', timestamp: out.chunk.timestamp, data: out.chunk.data }));
      } catch {
        assembler.reset();
        command('resetVideo');
      }
    });
    const offEnded = client.on('mirror.ended', (e) => {
      if (e.sessionId === id) setStatus({ kind: 'ended', message: e.error?.message ?? null });
    });

    if (client.kind !== 'webview') {
      // The browser preview has no phone video; it still exercises the window and its buttons.
      void client.call('mirror.start', { deviceId }).then((r) => {
        if (!active) return;
        id = r.sessionId;
        sessionRef.current = r.sessionId;
        setSession(r.sessionId);
        setName(r.deviceName);
        setStatus({ kind: 'preview' });
      }, (error: unknown) => active && setStatus({ kind: 'ended', message: error instanceof Error ? error.message : String(error) }));
    } else if (typeof VideoDecoder === 'undefined') {
      setStatus({ kind: 'ended', message: 'This WebView2 runtime has no WebCodecs video decoder.' });
    } else {
      client
        .call('mirror.start', { deviceId })
        .then((r) => {
          if (!active) {
            void client.call('mirror.stop', { sessionId: r.sessionId }).catch(() => undefined);
            return;
          }
          id = r.sessionId;
          sessionRef.current = r.sessionId;
          setSession(r.sessionId);
          setName(r.deviceName);
          setStatus({ kind: 'running' });
        })
        .catch((error: unknown) => active && setStatus({ kind: 'ended', message: error instanceof Error ? error.message : String(error) }));
    }

    return () => {
      active = false;
      offVideo();
      offEnded();
      try {
        decoder?.close();
      } catch {
        /* already closed */
      }
      if (id) void client.call('mirror.stop', { sessionId: id }).catch(() => undefined);
      sessionRef.current = null;
    };
  }, [client, deviceId, command]);

  // Fit the video to the window.
  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const fit = () => setShown(fitSize({ width: el.clientWidth - 16, height: el.clientHeight - 16 }, videoRef.current));
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(el);
    return () => observer.disconnect();
  }, [video]);

  // Pointer → touches. The mouse is one finger; real touches keep their own pointer ids.
  const touch = (e: ReactPointerEvent<HTMLCanvasElement>, action: MirrorTouchAction) => {
    const v = videoRef.current;
    const rect = e.currentTarget.getBoundingClientRect();
    const point = toVideoPoint(e.clientX, e.clientY, rect, v);
    if (!point) return;
    send({
      kind: 'touch',
      action,
      pointerId: e.pointerType === 'mouse' ? MOUSE_POINTER_ID : e.pointerId,
      x: point.x,
      y: point.y,
      width: v.width,
      height: v.height,
      pressure: action === 'up' ? 0 : e.pressure || 1,
    });
  };

  const screenshot = async () => {
    try {
      const { path } = await client.call('mirror.screenshot', { deviceId });
      setNote(tt('mirror.saved', { path }));
      setTimeout(() => setNote(null), 6000);
    } catch (error) {
      setNote(error instanceof Error ? error.message : String(error));
    }
  };

  const buttons: { label: string; icon: ReactElement; run(): void; testId: string }[] = [
    { label: tt('mirror.back'), icon: <ArrowUndoRegular />, run: () => command('backOrScreenOn'), testId: 'mirror-back' },
    { label: tt('mirror.home'), icon: <HomeRegular />, run: () => key(KEY.home), testId: 'mirror-home' },
    { label: tt('mirror.recents'), icon: <SquareMultipleRegular />, run: () => key(KEY.appSwitch), testId: 'mirror-recents' },
    { label: tt('mirror.notifications'), icon: <AlertRegular />, run: () => command('expandNotifications'), testId: 'mirror-notifications' },
    { label: tt('mirror.rotate'), icon: <ArrowRotateClockwiseRegular />, run: () => command('rotate'), testId: 'mirror-rotate' },
    { label: tt('mirror.volumeDown'), icon: <Speaker1Regular />, run: () => key(KEY.volumeDown), testId: 'mirror-volume-down' },
    { label: tt('mirror.volumeUp'), icon: <Speaker2Regular />, run: () => key(KEY.volumeUp), testId: 'mirror-volume-up' },
    { label: tt('mirror.power'), icon: <PowerRegular />, run: () => key(KEY.power), testId: 'mirror-power' },
    { label: tt('mirror.screenshot'), icon: <CameraRegular />, run: () => void screenshot(), testId: 'mirror-screenshot' },
  ];

  return (
    <div className={styles.app} data-testid="mirror-app" dir="ltr">
      <div className={styles.toolbar} role="toolbar" aria-label={tt('mirror.title')}>
        {buttons.map((b) => (
          <Button key={b.testId} variant="subtle" icon={b.icon} aria-label={b.label} title={b.label} onClick={b.run} disabled={!session} data-testid={b.testId} />
        ))}
      </div>
      <div className={styles.stage} ref={stage}>
        <canvas
          ref={canvas}
          className={styles.screen}
          tabIndex={0}
          style={{ width: shown.width, height: shown.height, visibility: status.kind === 'running' && video.width > 0 ? 'visible' : 'hidden' }}
          onPointerDown={(e) => {
            if (e.button === 2) return; // right-click is Back, on pointerup
            if (e.button === 1) {
              key(KEY.home);
              return;
            }
            e.currentTarget.setPointerCapture(e.pointerId);
            e.currentTarget.focus();
            touch(e, 'down');
          }}
          onPointerMove={(e) => {
            if (e.buttons & 1 || e.pointerType !== 'mouse') touch(e, 'move');
          }}
          onPointerUp={(e) => {
            if (e.button === 2) {
              command('backOrScreenOn');
              return;
            }
            if (e.button === 0) touch(e, 'up');
          }}
          onPointerCancel={(e) => touch(e, 'cancel')}
          onContextMenu={(e) => e.preventDefault()}
          onWheel={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const point = toVideoPoint(e.clientX, e.clientY, rect, videoRef.current);
            if (!point) return;
            send({ kind: 'scroll', ...point, ...videoRef.current, ...wheelToScroll(e.deltaX, e.deltaY, e.deltaMode) });
          }}
          onKeyDown={(e) => {
            const action = keyAction(e);
            if (!action) return;
            e.preventDefault();
            if (action.kind === 'key') key(action.keyCode);
            else send(action);
          }}
          data-testid="mirror-screen"
        />
        {status.kind === 'connecting' || (status.kind === 'running' && video.width === 0) ? (
          <div className={styles.overlay}>
            <Spinner large />
          </div>
        ) : status.kind === 'preview' ? (
          <div className={styles.overlay} data-testid="mirror-preview">
            <div className={styles.placeholder}>
              {new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
              <small>{name}</small>
            </div>
            <span>{tt('mirror.mockNote')}</span>
          </div>
        ) : status.kind === 'ended' ? (
          <div className={styles.overlay} data-testid="mirror-ended">
            <strong>{name || tt('mirror.title')}</strong>
            <span>{status.message ?? tt('mirror.ended')}</span>
          </div>
        ) : null}
      </div>
      <div className={styles.status} aria-live="polite">
        {note ?? (video.width > 0 ? `${video.width} × ${video.height}` : '')}
      </div>
    </div>
  );
}
