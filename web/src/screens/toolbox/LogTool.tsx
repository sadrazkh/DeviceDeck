import { ArrowClockwiseRegular, EraserRegular, PauseRegular, PlayRegular, SaveRegular } from '@fluentui/react-icons';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { LogLevel, LogLine, ToolboxError } from '../../bridge/toolbox';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Banner } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n, type ToolboxKey } from './i18n';
import { LEVEL_LETTER, LogBuffer, logTime } from './logBuffer';
import styles from './Toolbox.module.css';

const ROW = 22;
const LEVEL_CHOICES: { level: LogLevel; label: ToolboxKey; ios?: boolean }[] = [
  { level: 'verbose', label: 'log.level.verbose' },
  { level: 'debug', label: 'log.level.debug' },
  { level: 'info', label: 'log.level.info' },
  { level: 'notice', label: 'log.level.notice', ios: true },
  { level: 'warning', label: 'log.level.warning' },
  { level: 'error', label: 'log.level.error' },
];

export function LogTool({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const actions = useActions();
  const { tt, n } = useToolboxI18n();
  const buffer = useRef(new LogBuffer());
  const [view, setView] = useState<readonly LogLine[]>([]);
  const [frozen, setFrozen] = useState<readonly LogLine[] | null>(null);
  const [waiting, setWaiting] = useState(0);
  const [minLevel, setMinLevel] = useState<LogLevel>('verbose');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<number | null>(null);
  const [session, setSession] = useState<string | null>(null);
  const [ended, setEnded] = useState<ToolboxError | null | undefined>(undefined);
  const [dropped, setDropped] = useState(0);
  const [run, setRun] = useState(0);
  const [saving, setSaving] = useState(false);
  const paused = frozen !== null;
  const pausedRef = useRef(false);
  pausedRef.current = paused;
  const failRef = useRef(actions.fail);
  failRef.current = actions.fail;

  // One session per mount (and per "Start again"); the host stops it with the page too.
  useEffect(() => {
    let active = true;
    let id: string | null = null;
    buffer.current.clear();
    setView([]);
    setEnded(undefined);
    setDropped(0);
    const offLines = client.on('log.lines', (e) => {
      if (e.sessionId !== id) return;
      buffer.current.push(e.lines);
      if (e.dropped > 0) setDropped((d) => d + e.dropped);
      if (pausedRef.current) setWaiting((w) => w + e.lines.length);
      else setView(buffer.current.view);
    });
    const offEnded = client.on('log.ended', (e) => {
      if (e.sessionId === id) setEnded(e.error);
    });
    client
      .call('log.start', { deviceId: device.id })
      .then(({ sessionId }) => {
        if (!active) {
          void client.call('log.stop', { sessionId }).catch(() => undefined);
          return;
        }
        id = sessionId;
        setSession(sessionId);
      })
      .catch((error: unknown) => {
        if (active) {
          failRef.current(error);
          setEnded({ code: 'internal', message: error instanceof Error ? error.message : String(error) });
        }
      });
    return () => {
      active = false;
      offLines();
      offEnded();
      if (id) void client.call('log.stop', { sessionId: id }).catch(() => undefined);
      setSession(null);
    };
  }, [client, device.id, run]);

  useEffect(() => {
    buffer.current.setFilter({ minLevel, search });
    setView(buffer.current.view);
    setSelected(null);
  }, [minLevel, search]);

  const lines = frozen ?? view;

  const scroller = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const virtualizer = useVirtualizer({
    count: lines.length,
    getScrollElement: () => scroller.current,
    estimateSize: () => ROW,
    overscan: 24,
  });

  // Follow new lines while the view is scrolled to the end.
  useLayoutEffect(() => {
    if (atBottom.current && lines.length > 0) virtualizer.scrollToIndex(lines.length - 1, { align: 'end' });
  }, [lines.length, virtualizer]);

  const onScroll = useCallback(() => {
    const el = scroller.current;
    if (el) atBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < ROW * 2;
  }, []);

  const togglePause = () => {
    if (paused) {
      setFrozen(null);
      setWaiting(0);
      setView(buffer.current.view);
    } else {
      setFrozen(buffer.current.view);
    }
  };

  const clear = () => {
    buffer.current.clear();
    setView([]);
    setFrozen((f) => (f ? [] : f));
    setWaiting(0);
    setSelected(null);
    setDropped(0);
    if (session) void client.call('log.clear', { sessionId: session }).catch(() => undefined);
  };

  const save = async () => {
    if (!session) return;
    setSaving(true);
    try {
      const { path, count } = await client.call('log.export', { sessionId: session, minLevel, search: search.trim() || null });
      toast({ tone: 'success', title: tt('log.exported', { count }), body: path, action: { label: tt('heic.showFiles'), run: () => void actions.revealInExplorer(path) } });
    } catch (error) {
      actions.fail(error);
    } finally {
      setSaving(false);
    }
  };

  const selectedLine = useMemo(() => (selected === null ? null : (lines.find((l) => l.seq === selected) ?? null)), [lines, selected]);

  return (
    <div className={styles.tool} data-testid="log-tool">
      <div className={styles.toolbar}>
        <input
          className={cx(styles.search, 'no-drag')}
          type="search"
          placeholder={tt('log.search')}
          aria-label={tt('log.search')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          spellCheck={false}
          data-testid="log-search"
        />
        <select
          className={styles.select}
          aria-label={tt('log.level')}
          value={minLevel}
          onChange={(e) => setMinLevel(e.target.value as LogLevel)}
          data-testid="log-level"
        >
          {LEVEL_CHOICES.filter((c) => !c.ios || device.platform === 'ios').map((c) => (
            <option key={c.level} value={c.level}>
              {tt(c.label)}
            </option>
          ))}
        </select>
        <Button icon={paused ? <PlayRegular /> : <PauseRegular />} onClick={togglePause} data-testid="log-pause" aria-pressed={paused}>
          {paused ? tt('log.resume') : tt('log.pause')}
        </Button>
        <Button icon={<EraserRegular />} onClick={clear}>
          {tt('log.clear')}
        </Button>
        <span className={styles.toolbarSpacer} />
        <Button icon={<SaveRegular />} onClick={() => void save()} loading={saving} disabled={!session} data-testid="log-save">
          {tt('log.export')}
        </Button>
      </div>

      {ended !== undefined ? (
        <Banner
          tone={ended ? 'danger' : 'info'}
          title={tt('log.ended')}
          actions={
            <Button icon={<ArrowClockwiseRegular />} onClick={() => setRun((r) => r + 1)}>
              {tt('log.restart')}
            </Button>
          }
        >
          {ended?.message}
        </Banner>
      ) : null}
      {dropped > 0 ? <Banner tone="warning">{tt('log.dropped', { count: dropped })}</Banner> : null}

      <div className={cx(styles.panel, styles.logList)} ref={scroller} onScroll={onScroll} role="grid" aria-rowcount={lines.length} data-testid="log-list">
        <div className={styles.logHeader} role="row">
          <span role="columnheader">{tt('log.col.time')}</span>
          <span role="columnheader" aria-label={tt('log.col.level')} />
          <span role="columnheader">{tt('log.col.process')}</span>
          <span role="columnheader">{tt('log.col.tag')}</span>
          <span role="columnheader">{tt('log.col.message')}</span>
        </div>
        {lines.length === 0 ? (
          <div className={styles.center}>{buffer.current.total === 0 ? tt('log.empty') : tt('log.noMatch')}</div>
        ) : (
          <div style={{ position: 'relative', height: virtualizer.getTotalSize() }}>
            {virtualizer.getVirtualItems().map((item) => {
              const line = lines[item.index]!;
              return (
                <div
                  key={line.seq}
                  role="row"
                  aria-selected={selected === line.seq}
                  className={cx(styles.logRow, styles[`row-${line.level}`])}
                  style={{ transform: `translateY(${item.start}px)` }}
                  onClick={() => setSelected(line.seq)}
                  data-testid="log-row"
                >
                  <span className={styles.cell}>{logTime(line.time)}</span>
                  <span>
                    <span className={cx(styles.level, styles[`level-${line.level}`])} title={line.level}>
                      {LEVEL_LETTER[line.level]}
                    </span>
                  </span>
                  <span className={styles.cell} title={line.process}>
                    {line.process}
                    {line.pid !== null ? `[${line.pid}]` : ''}
                  </span>
                  <span className={styles.cell} title={line.tag ?? undefined}>
                    {line.tag}
                  </span>
                  <span className={cx(styles.cell, styles.message)} title={line.message}>
                    {line.message}
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {selectedLine ? (
        <div className={styles.panel} aria-label={tt('log.details')}>
          <div className={styles.logDetails}>
            {selectedLine.time ?? ''} {selectedLine.level.toUpperCase()} {selectedLine.process}
            {selectedLine.pid !== null ? `[${selectedLine.pid}]` : ''} {selectedLine.tag ?? ''}
            {'\n'}
            {selectedLine.message}
          </div>
        </div>
      ) : null}

      <div className={styles.status} data-testid="log-status">
        <span>{tt('log.count', { shown: lines.length, total: n(buffer.current.total) })}</span>
        {paused ? <span>{tt('log.paused', { count: waiting })}</span> : null}
      </div>
    </div>
  );
}
