import { DismissRegular, FolderOpenRegular, ImageArrowForwardRegular, ImageRegular, OpenRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { ToolboxJob } from '../../bridge/toolbox';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { useFileDrop } from '../../components/DropZone';
import { Banner, LinearProgress, RadioCards } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n } from './i18n';
import styles from './Toolbox.module.css';

/** A HEIC source: a File from a drop or the picker (the host gets its path), or a folder path from the folder picker. */
type Source = { kind: 'file'; file: File } | { kind: 'path'; path: string };

const STORE_HEIF = 'https://apps.microsoft.com/detail/9pmmsr1cgpwg';

const nameOf = (s: Source) => (s.kind === 'file' ? s.file.name : s.path);

export function HeicTool() {
  const client = useClient();
  const actions = useActions();
  const { t, tt, n } = useToolboxI18n();
  const status = useQuery({ queryKey: ['heic.status'], queryFn: () => client.call('heic.status'), staleTime: 60_000 });
  const [sources, setSources] = useState<Source[]>([]);
  const [quality, setQuality] = useState(90);
  const [where, setWhere] = useState<'same' | 'folder'>('same');
  const [folder, setFolder] = useState<string | null>(null);
  // A conversion can be two jobs: dropped files (sent as objects) and picked folders (sent as paths).
  const [jobs, setJobs] = useState<Record<string, ToolboxJob>>({});
  const [starting, setStarting] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  useEffect(
    () =>
      client.on('toolbox.job', (update) => {
        setJobs((current) => (update.id in current ? { ...current, [update.id]: update } : current));
      }),
    [client],
  );

  const add = useCallback((more: Source[]) => setSources((s) => [...s, ...more.filter((m) => !s.some((x) => nameOf(x) === nameOf(m)))]), []);
  const drop = useFileDrop({
    verdict: () => ({ ok: true, message: tt('heic.dropVerdict') }),
    onDrop: (files) => add(files.map((file) => ({ kind: 'file', file }))),
  });

  const chooseFolder = async () => {
    const path = await actions.pickFolder();
    if (path) add([{ kind: 'path', path }]);
  };

  const chooseTarget = async (value: 'same' | 'folder') => {
    setWhere(value);
    if (value === 'folder' && !folder) {
      const path = await actions.pickFolder();
      if (path) setFolder(path);
      else setWhere('same');
    }
  };

  const job = combine(Object.values(jobs));
  const running = job?.state === 'running';
  const convert = async () => {
    setStarting(true);
    setJobs({});
    const started = (result: { jobId: string; total: number }) =>
      setJobs((current) => ({
        ...current,
        [result.jobId]: { id: result.jobId, kind: 'heic', state: 'running', total: result.total, done: 0, failed: 0, current: null, outputs: [], errors: [] },
      }));
    try {
      const files = sources.filter((s): s is { kind: 'file'; file: File } => s.kind === 'file').map((s) => s.file);
      const paths = sources.filter((s): s is { kind: 'path'; path: string } => s.kind === 'path').map((s) => s.path);
      const destination = where === 'folder' ? folder : null;
      // Files go as additional objects (the host reads their paths); folder paths are already known.
      if (files.length > 0) started(await client.callWithFiles('heic.convert', { quality, destination }, files));
      if (paths.length > 0) started(await client.call('heic.convert', { files: paths, quality, destination }));
    } catch (error) {
      actions.fail(error);
    } finally {
      setStarting(false);
    }
  };

  const cancel = () => {
    for (const j of Object.values(jobs)) {
      if (j.state === 'running') void client.call('toolbox.cancelJob', { jobId: j.id }).catch(() => undefined);
    }
  };

  useEffect(() => {
    if (!job || job.state === 'running') return;
    const first = job.outputs[0];
    if (job.state === 'done' && job.done > 0) {
      toast({
        tone: job.failed > 0 ? 'warning' : 'success',
        title: tt('heic.done', { count: job.done }),
        body: job.failed > 0 ? tt('heic.failed', { count: job.failed }) : undefined,
        action: first ? { label: tt('heic.showFiles'), run: () => void actions.revealInExplorer(first) } : undefined,
      });
    }
    // Once per finished conversion: only the state change matters here.
  }, [job?.state]);

  const missing = status.data && !status.data.available;
  return (
    <div className={styles.tool} data-testid="heic-tool" style={{ overflow: 'auto' }}>
      {missing ? (
        <Banner
          tone="warning"
          title={tt('heic.missing')}
          actions={
            <Button icon={<OpenRegular />} onClick={() => window.open(STORE_HEIF, '_blank', 'noopener')}>
              {tt('heic.getExtensions')}
            </Button>
          }
        >
          {tt('heic.missingBody')}
        </Banner>
      ) : null}

      <div className={styles.dropTarget} data-over={drop.over || undefined} {...drop.bind} data-testid="heic-drop">
        <ImageArrowForwardRegular className={styles.dropIcon} aria-hidden />
        <span className={styles.dropTitle}>{tt('heic.drop')}</span>
        <span className={styles.muted}>{tt('heic.dropHint')}</span>
        <div className={styles.toolbar}>
          <Button icon={<ImageRegular />} onClick={() => input.current?.click()}>
            {tt('heic.chooseFiles')}
          </Button>
          <Button icon={<FolderOpenRegular />} onClick={() => void chooseFolder()}>
            {tt('heic.chooseFolder')}
          </Button>
        </div>
        <input
          ref={input}
          type="file"
          multiple
          accept=".heic,.heif,.hif,image/heic,image/heif"
          hidden
          onChange={(e) => {
            add(Array.from(e.target.files ?? []).map((file) => ({ kind: 'file', file })));
            e.target.value = '';
          }}
        />
      </div>

      {sources.length > 0 ? (
        <div className={cx(styles.panel, styles.chosen)} data-testid="heic-sources">
          {sources.map((s) => (
            <div key={nameOf(s)} className={styles.deviceLine}>
              <span className={styles.cell} style={{ flex: 1 }}>
                {nameOf(s)}
              </span>
              <Button
                variant="subtle"
                size="small"
                icon={<DismissRegular />}
                aria-label={nameOf(s)}
                onClick={() => setSources((list) => list.filter((x) => x !== s))}
                disabled={running}
              />
            </div>
          ))}
        </div>
      ) : null}

      <div className={styles.formRow}>
        <label className={styles.optionGroup}>
          <span className={styles.optionLabel}>{tt('heic.quality', { value: n(quality) })}</span>
          <input className={styles.range} type="range" min={50} max={100} step={1} value={quality} onChange={(e) => setQuality(Number(e.target.value))} data-testid="heic-quality" />
          <span className={cx(styles.muted, styles.small)}>{tt('heic.qualityHint')}</span>
        </label>
        <div className={styles.optionGroup} style={{ minWidth: 280 }}>
          <span className={styles.optionLabel}>{tt('heic.where')}</span>
          <RadioCards
            value={where}
            onChange={(v) => void chooseTarget(v)}
            label={tt('heic.where')}
            options={[
              { value: 'same', label: tt('heic.whereSame') },
              { value: 'folder', label: tt('heic.whereFolder'), hint: folder ? tt('heic.pickedFolder', { folder }) : undefined },
            ]}
          />
        </div>
      </div>

      <div className={styles.toolbar}>
        <Button
          variant="accent"
          icon={<ImageArrowForwardRegular />}
          disabled={sources.length === 0 || running || missing === true}
          loading={starting}
          onClick={() => void convert()}
          data-testid="heic-convert"
        >
          {tt('heic.convert')}
        </Button>
        {running ? <Button onClick={cancel}>{t('common.cancel')}</Button> : null}
      </div>

      {job ? (
        <div className={cx(styles.panel, styles.section)} data-testid="heic-job">
          <div className={styles.status}>
            {job.state === 'running'
              ? tt('heic.running', { done: job.done + job.failed, total: job.total })
              : job.state === 'cancelled'
                ? tt('heic.cancelled', { count: job.done })
                : tt('heic.done', { count: job.done })}
            {job.current ? <span className={styles.cell}>{job.current}</span> : null}
          </div>
          <LinearProgress value={job.total > 0 ? (job.done + job.failed) / job.total : null} tone={job.failed > 0 && job.state !== 'running' ? 'danger' : 'accent'} />
          {job.failed > 0 ? (
            <>
              <span>{tt('heic.failed', { count: job.failed })}</span>
              <ul className={styles.errors}>
                {job.errors.map((e) => (
                  <li key={e}>{e}</li>
                ))}
              </ul>
            </>
          ) : null}
          {job.state !== 'running' && job.outputs[0] ? (
            <div>
              <Button icon={<FolderOpenRegular />} onClick={() => void actions.revealInExplorer(job.outputs[0]!)}>
                {tt('heic.showFiles')}
              </Button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

/** Several jobs as one progress line: running while any runs. */
export function combine(list: ToolboxJob[]): ToolboxJob | null {
  if (list.length === 0) return null;
  const state = list.some((j) => j.state === 'running')
    ? 'running'
    : list.some((j) => j.state === 'cancelled')
      ? 'cancelled'
      : list.every((j) => j.state === 'failed')
        ? 'failed'
        : 'done';
  return {
    id: list.map((j) => j.id).join('+'),
    kind: 'heic',
    state,
    total: list.reduce((s, j) => s + j.total, 0),
    done: list.reduce((s, j) => s + j.done, 0),
    failed: list.reduce((s, j) => s + j.failed, 0),
    current: list.find((j) => j.current)?.current ?? null,
    outputs: list.flatMap((j) => j.outputs),
    errors: list.flatMap((j) => j.errors),
  };
}
