import { ArrowClockwiseRegular, DeleteRegular, SaveRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { CrashReport, CrashReportKind } from '../../bridge/toolbox';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Dialog } from '../../components/Dialog';
import { ErrorBanner } from '../../components/ErrorState';
import { Badge, Banner, Checkbox, Spinner, type Tone } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n, type ToolboxKey } from './i18n';
import styles from './Toolbox.module.css';

const KIND_TONE: Record<CrashReportKind, Tone> = {
  crash: 'danger',
  hang: 'warning',
  memory: 'info',
  panic: 'danger',
  tombstone: 'danger',
  diagnostic: 'neutral',
};

export function CrashTool({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const { t, tt, bytes, date, n } = useToolboxI18n();
  const key = ['crash.list', device.id];
  const list = useQuery({ queryKey: key, queryFn: () => client.call('crash.list', { deviceId: device.id }), staleTime: 60_000 });
  const [open, setOpen] = useState<string | null>(null);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState<'save' | 'delete' | null>(null);

  const reports = list.data?.reports ?? [];
  const current = reports.find((r) => r.id === open) ?? null;
  const text = useQuery({
    queryKey: ['crash.read', device.id, open],
    queryFn: () => client.call('crash.read', { deviceId: device.id, id: open! }),
    enabled: current?.readable === true,
    retry: false,
    staleTime: Infinity,
  });

  const selection = useMemo(() => reports.filter((r) => checked.has(r.id)), [reports, checked]);
  const targets = selection.length > 0 ? selection : current ? [current] : [];
  const freed = targets.reduce((sum, r) => sum + r.size, 0);
  const allChecked = reports.length > 0 && selection.length === reports.length;

  const toggle = (id: string, on: boolean) =>
    setChecked((s) => {
      const next = new Set(s);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });

  const save = async () => {
    setBusy('save');
    try {
      const { folder, files } = await client.call('crash.export', { deviceId: device.id, ids: targets.map((r) => r.id) });
      toast({ tone: 'success', title: tt('crash.saved', { count: files.length }), body: folder, action: { label: tt('heic.showFiles'), run: () => void actions.revealInExplorer(folder) } });
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
    }
  };

  const remove = async () => {
    setBusy('delete');
    try {
      const { deleted } = await client.call('crash.delete', { deviceId: device.id, ids: targets.map((r) => r.id) });
      toast({ tone: 'success', title: tt('crash.deleted', { count: deleted }) });
      setChecked(new Set());
      if (open && targets.some((r) => r.id === open)) setOpen(null);
      await queryClient.invalidateQueries({ queryKey: key });
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
      setConfirm(false);
    }
  };

  const unreadable = list.data?.unreadable ?? [];
  return (
    <div className={styles.tool} data-testid="crash-tool">
      <div className={styles.toolbar}>
        <Button icon={<ArrowClockwiseRegular />} onClick={() => void list.refetch()} loading={list.isFetching}>
          {tt('crash.refresh')}
        </Button>
        <span className={styles.toolbarSpacer} />
        <Button icon={<SaveRegular />} disabled={targets.length === 0} loading={busy === 'save'} onClick={() => void save()} data-testid="crash-save">
          {selection.length > 0 ? tt('crash.exportSelected', { count: selection.length }) : tt('crash.export')}
        </Button>
        {list.data?.canDelete ? (
          <Button variant="danger" icon={<DeleteRegular />} disabled={targets.length === 0} onClick={() => setConfirm(true)} data-testid="crash-delete">
            {tt('crash.delete')}
          </Button>
        ) : null}
      </div>

      {list.isError ? <ErrorBanner error={list.error} onRetry={() => void list.refetch()} /> : null}
      {list.data && !list.data.canDelete && device.platform === 'android' ? (
        <Banner tone="info">
          {tt('crash.androidNoDelete')}
          {unreadable.length > 0
            ? ` ${tt('crash.unreadable', { places: unreadable.map((p) => tt(`crash.place.${p}` as ToolboxKey)).join('، ') })}`
            : ''}
        </Banner>
      ) : null}

      <div className={styles.split}>
        <div className={cx(styles.panel, styles.listPane)}>
          {list.isPending ? (
            <div className={styles.center}>
              <Spinner label={tt('crash.loading')} />
              <span>{tt('crash.loading')}</span>
            </div>
          ) : reports.length === 0 ? (
            <div className={styles.center}>{tt('crash.empty')}</div>
          ) : (
            <table className={styles.table} data-testid="crash-table">
              <thead>
                <tr>
                  <th style={{ width: 36 }}>
                    <Checkbox
                      checked={allChecked ? true : selection.length > 0 ? 'indeterminate' : false}
                      onChange={(on) => setChecked(on ? new Set(reports.map((r) => r.id)) : new Set())}
                      label={tt('crash.col.process')}
                    />
                  </th>
                  <th>{tt('crash.col.process')}</th>
                  <th>{tt('crash.col.kind')}</th>
                  <th>{tt('crash.col.time')}</th>
                  <th>{tt('crash.col.size')}</th>
                </tr>
              </thead>
              <tbody>
                {reports.map((report: CrashReport) => (
                  <tr key={report.id} aria-selected={open === report.id} onClick={() => setOpen(report.id)} data-testid="crash-row">
                    <td>
                      <Checkbox checked={checked.has(report.id)} onChange={(on) => toggle(report.id, on)} label={report.fileName} />
                    </td>
                    <td className={styles.nameCell} title={report.fileName}>
                      {report.process}
                    </td>
                    <td>
                      <Badge tone={KIND_TONE[report.kind]}>{tt(`crash.kind.${report.kind}` as ToolboxKey)}</Badge>
                    </td>
                    <td>{report.time ? date(report.time, 'medium') : ''}</td>
                    <td>{bytes(report.size)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className={cx(styles.panel, styles.viewer)} data-testid="crash-viewer">
          {current ? (
            <>
              <div className={styles.viewerHead}>
                <span className={styles.viewerTitle} title={current.id}>
                  {current.fileName}
                </span>
                <span className={styles.muted}>{bytes(current.size)}</span>
              </div>
              {!current.readable ? (
                <div className={styles.center}>{tt('crash.binary')}</div>
              ) : text.isPending ? (
                <div className={styles.center}>
                  <Spinner />
                </div>
              ) : text.isError ? (
                <ErrorBanner error={text.error} onRetry={() => void text.refetch()} />
              ) : (
                <>
                  {text.data.truncated ? <Banner tone="info">{tt('crash.truncated', { size: bytes(text.data.size) })}</Banner> : null}
                  <pre className={styles.viewerText}>{text.data.text}</pre>
                </>
              )}
            </>
          ) : (
            <div className={styles.center}>{reports.length > 0 ? tt('crash.select') : ''}</div>
          )}
        </div>
      </div>

      <Dialog
        open={confirm}
        onOpenChange={setConfirm}
        title={tt('crash.deleteTitle', { count: targets.length })}
        description={tt('crash.deleteBody', { size: bytes(freed) })}
        footer={
          <>
            <Button onClick={() => setConfirm(false)}>{t('common.cancel')}</Button>
            <Button variant="dangerSolid" loading={busy === 'delete'} onClick={() => void remove()} autoFocus data-testid="crash-delete-confirm">
              {tt('crash.delete').replace('…', '')}
            </Button>
          </>
        }
      >
        <ul className={styles.deleteList}>
          {targets.slice(0, 50).map((r) => (
            <li key={r.id}>
              <span className={styles.cell}>{r.fileName}</span>
              <span className={styles.muted}>{bytes(r.size)}</span>
            </li>
          ))}
          {targets.length > 50 ? <li>… {n(targets.length - 50)}</li> : null}
        </ul>
      </Dialog>
    </div>
  );
}
