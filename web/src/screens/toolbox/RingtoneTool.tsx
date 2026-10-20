import { MusicNote2Regular, PhoneRegular, PlayRegular, StopRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { useFileDrop } from '../../components/DropZone';
import { Banner, Checkbox, EmptyState, Spinner, TextField } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n } from './i18n';
import styles from './Toolbox.module.css';
import { clampSelection, computePeaks, formatSeconds, initialSelection, SUGGESTED_LENGTH } from './waveform';

interface Song {
  file: File;
  url: string;
  duration: number;
  peaks: number[];
}

const BUCKETS = 600;

export function RingtoneTool({ device }: { device: DeviceSummary }) {
  const { tt } = useToolboxI18n();
  if (device.platform === 'ios') {
    return (
      <div className={styles.tool} data-testid="ringtone-tool">
        <div className={styles.panel}>
          <EmptyState icon={<PhoneRegular />} title={tt('ringtone.iphoneTitle')} body={tt('ringtone.iphoneBody')} />
        </div>
      </div>
    );
  }
  return <RingtoneMaker device={device} />;
}

function RingtoneMaker({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const actions = useActions();
  const { tt, n, digits } = useToolboxI18n();
  const [song, setSong] = useState<Song | null>(null);
  const [decoding, setDecoding] = useState(false);
  const [error, setError] = useState(false);
  const [range, setRange] = useState({ start: 0, end: 0 });
  const [name, setName] = useState('');
  const [asDefault, setAsDefault] = useState(false);
  const [busy, setBusy] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [position, setPosition] = useState<number | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const audio = useRef<HTMLAudioElement | null>(null);
  const url = useRef<string | null>(null);

  const load = useCallback(async (file: File) => {
    setDecoding(true);
    setError(false);
    try {
      const context = new AudioContext();
      try {
        const buffer = await context.decodeAudioData(await file.arrayBuffer());
        const peaks = computePeaks(buffer.getChannelData(0), BUCKETS);
        if (url.current) URL.revokeObjectURL(url.current);
        url.current = URL.createObjectURL(file);
        setSong({ file, url: url.current, duration: buffer.duration, peaks });
        setRange(initialSelection(buffer.duration));
        setName(file.name.replace(/\.[^.]+$/, ''));
      } finally {
        void context.close();
      }
    } catch {
      setError(true);
    } finally {
      setDecoding(false);
    }
  }, []);

  useEffect(
    () => () => {
      audio.current?.pause();
      if (url.current) URL.revokeObjectURL(url.current);
    },
    [],
  );

  const drop = useFileDrop({
    verdict: () => ({ ok: true, message: tt('ringtone.dropVerdict') }),
    onDrop: (files) => void load(files[0]!),
  });

  const stop = () => {
    audio.current?.pause();
    setPlaying(false);
    setPosition(null);
  };

  const play = () => {
    if (!song) return;
    if (!audio.current) audio.current = new Audio();
    const el = audio.current;
    el.src = song.url;
    el.currentTime = range.start;
    el.ontimeupdate = () => {
      setPosition(el.currentTime);
      if (el.currentTime >= range.end) stop();
    };
    el.onended = stop;
    void el.play().then(() => setPlaying(true), stop);
  };

  const create = async () => {
    if (!song) return;
    setBusy(true);
    try {
      const result = await client.callWithFiles(
        'ringtone.create',
        { deviceId: device.id, startSeconds: range.start, endSeconds: range.end, name: name.trim() || null, setAsDefault: asDefault },
        [song.file],
      );
      const shown = result.devicePath.split('/').pop() ?? result.devicePath;
      toast({ tone: 'success', title: result.isDefault ? tt('ringtone.createdDefault', { name: shown }) : tt('ringtone.created', { name: shown }), body: result.devicePath });
    } catch (e) {
      actions.fail(e);
    } finally {
      setBusy(false);
    }
  };

  const length = Math.round((range.end - range.start) * 10) / 10;
  return (
    <div className={styles.tool} data-testid="ringtone-tool" style={{ overflow: 'auto' }}>
      {!song ? (
        <div className={styles.dropTarget} data-over={drop.over || undefined} {...drop.bind} data-testid="ringtone-drop">
          {decoding ? <Spinner large label={tt('ringtone.decoding')} /> : <MusicNote2Regular className={styles.dropIcon} aria-hidden />}
          <span className={styles.dropTitle}>{decoding ? tt('ringtone.decoding') : tt('ringtone.drop')}</span>
          <Button onClick={() => input.current?.click()} disabled={decoding}>
            {tt('ringtone.choose')}
          </Button>
        </div>
      ) : (
        <>
          <div className={styles.toolbar}>
            <span className={styles.sectionTitle} style={{ flex: 1 }}>
              {song.file.name}
            </span>
            <Button onClick={() => input.current?.click()}>{tt('ringtone.another')}</Button>
          </div>
          <Waveform song={song} range={range} onChange={(r) => setRange(clampSelection(r.start, r.end, song.duration))} position={position} />
          <div className={styles.times}>
            <label className={styles.checkRow}>
              {tt('ringtone.start')}
              <input
                className={styles.numberInput}
                type="number"
                step={0.1}
                min={0}
                value={range.start}
                onChange={(e) => setRange(clampSelection(Number(e.target.value), range.end, song.duration))}
              />
            </label>
            <label className={styles.checkRow}>
              {tt('ringtone.end')}
              <input
                className={styles.numberInput}
                type="number"
                step={0.1}
                min={0}
                value={range.end}
                onChange={(e) => setRange(clampSelection(range.start, Number(e.target.value), song.duration))}
              />
            </label>
            <span className={styles.muted}>
              {tt('ringtone.length', { seconds: n(length) })} · {digits(formatSeconds(range.start))} – {digits(formatSeconds(range.end))}
            </span>
            {playing ? (
              <Button icon={<StopRegular />} onClick={stop}>
                {tt('ringtone.stop')}
              </Button>
            ) : (
              <Button icon={<PlayRegular />} onClick={play} data-testid="ringtone-play">
                {tt('ringtone.play')}
              </Button>
            )}
          </div>
          {length > SUGGESTED_LENGTH ? <Banner tone="info">{tt('ringtone.lengthHint')}</Banner> : null}
          <div className={cx(styles.panel, styles.section)} style={{ maxWidth: 520 }}>
            <TextField label={tt('ringtone.name')} value={name} onChange={setName} />
            <label className={styles.checkRow}>
              <Checkbox checked={asDefault} onChange={setAsDefault} label={tt('ringtone.default')} />
              {tt('ringtone.default')}
            </label>
            <div>
              <Button variant="accent" icon={<PhoneRegular />} loading={busy} onClick={() => void create()} data-testid="ringtone-create">
                {tt('ringtone.create')}
              </Button>
            </div>
          </div>
        </>
      )}
      {error ? <Banner tone="danger">{tt('ringtone.cantRead')}</Banner> : null}
      <input
        ref={input}
        type="file"
        accept="audio/*,.mp3,.m4a,.aac,.wav,.flac,.ogg,.opus,.wma"
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) void load(file);
          e.target.value = '';
        }}
      />
    </div>
  );
}

function Waveform({
  song,
  range,
  onChange,
  position,
}: {
  song: Song;
  range: { start: number; end: number };
  onChange(range: { start: number; end: number }): void;
  position: number | null;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const box = useRef<HTMLDivElement>(null);
  const dragging = useRef<'start' | 'end' | null>(null);

  const colours = useMemo(() => {
    if (typeof getComputedStyle === 'undefined') return { wave: '#5b5bf6', dim: '#a1a7b3' };
    const css = getComputedStyle(document.documentElement);
    return { wave: css.getPropertyValue('--color-accent').trim() || '#5b5bf6', dim: css.getPropertyValue('--mist-300').trim() || '#a1a7b3' };
  }, []);

  useEffect(() => {
    const el = canvas.current;
    if (!el) return;
    const width = el.clientWidth * devicePixelRatio;
    const height = el.clientHeight * devicePixelRatio;
    el.width = width;
    el.height = height;
    const ctx = el.getContext('2d');
    if (!ctx) return;
    ctx.clearRect(0, 0, width, height);
    const bar = width / song.peaks.length;
    song.peaks.forEach((peak, i) => {
      const t = (i / song.peaks.length) * song.duration;
      ctx.fillStyle = t >= range.start && t <= range.end ? colours.wave : colours.dim;
      const h = Math.max(1, peak * height * 0.9);
      ctx.fillRect(i * bar, (height - h) / 2, Math.max(1, bar - 1), h);
    });
  }, [song, range, colours]);

  const at = (e: ReactPointerEvent) => {
    const rect = box.current!.getBoundingClientRect();
    return ((e.clientX - rect.left) / rect.width) * song.duration;
  };

  const pct = (t: number) => `${(t / song.duration) * 100}%`;
  return (
    <div
      ref={box}
      className={cx(styles.panel, styles.wave)}
      data-testid="ringtone-wave"
      onPointerDown={(e) => {
        const t = at(e);
        dragging.current = Math.abs(t - range.start) <= Math.abs(t - range.end) ? 'start' : 'end';
        (e.target as Element).setPointerCapture?.(e.pointerId);
        onChange(dragging.current === 'start' ? { start: t, end: range.end } : { start: range.start, end: t });
      }}
      onPointerMove={(e) => {
        if (!dragging.current) return;
        const t = at(e);
        onChange(dragging.current === 'start' ? { start: t, end: range.end } : { start: range.start, end: t });
      }}
      onPointerUp={() => (dragging.current = null)}
      onPointerCancel={() => (dragging.current = null)}
    >
      <canvas ref={canvas} aria-hidden />
      <div className={styles.selection} style={{ left: pct(range.start), width: pct(range.end - range.start) }} />
      <div className={styles.handle} style={{ left: pct(range.start) }} aria-hidden />
      <div className={styles.handle} style={{ left: pct(range.end) }} aria-hidden />
      {position !== null ? <div className={styles.playhead} style={{ left: pct(position) }} /> : null}
    </div>
  );
}
