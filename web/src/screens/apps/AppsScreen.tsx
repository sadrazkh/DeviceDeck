import {
  AddRegular,
  AppsRegular,
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  ArrowSortDownRegular,
  ArrowSortUpRegular,
  DeleteRegular,
  DismissRegular,
  FolderOpenRegular,
} from '@fluentui/react-icons';
import { AnimatePresence, motion } from 'framer-motion';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { AppInfo, DeviceSummary, TransferJob } from '../../bridge/types';
import { APP_EXTENSIONS, isAppPackage, useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useApps, useSettings, useTransfers } from '../../app/queries';
import { Button } from '../../components/Button';
import { ConfirmDialog } from '../../components/Dialog';
import { DropOverlay, useFileDrop } from '../../components/DropZone';
import { ErrorBanner } from '../../components/ErrorState';
import { Badge, Banner, Checkbox, EmptyState, LinearProgress, Segmented, Skeleton, Tooltip } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import { toast } from '../../state/toasts';
import { jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import { progressLine } from '../../shell/TransferDrawer';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Apps.module.css';

type Filter = 'user' | 'system' | 'all';
type SortKey = 'name' | 'size' | 'installed';

function sourceLabel(app: AppInfo, platform: DeviceSummary['platform']): MessageKey {
  if (app.source === 'store') return platform === 'ios' ? 'apps.source.store.ios' : 'apps.source.store.android';
  return app.source === 'sideloaded' ? 'apps.source.sideloaded' : 'apps.source.system';
}

function InstallRows({ jobs, device }: { jobs: TransferJob[]; device: DeviceSummary }) {
  const i18n = useI18n();
  const actions = useActions();
  return (
    <>
      {jobs.map((job) => (
        <tr key={job.id} className={styles.installRow} data-testid="install-row">
          <td className={styles.check} />
          <td>
            <div className={styles.app}>
              <span className={`${styles.icon} ${styles.iconPending}`} aria-hidden>
                <AppsRegular />
              </span>
              <div className={styles.appText}>
                <span className={styles.appName}>{job.title}</span>
              </div>
            </div>
          </td>
          <td colSpan={4}>
            <div className={styles.installMeta}>
              {job.state === 'failed' ? null : <LinearProgress value={job.state === 'queued' ? null : jobFraction(job)} label={job.title} />}
              <span className={`${styles.installLine} ${job.state === 'failed' ? styles.installLineError : ''}`}>
                {progressLine(job, i18n, device)}
              </span>
            </div>
          </td>
          <td>
            <div className={styles.actions}>
              {job.state === 'failed' ? (
                <Button size="small" icon={<ArrowClockwiseRegular />} onClick={() => void actions.transfer('resume', job.id)}>
                  {i18n.t('common.retry')}
                </Button>
              ) : (
                <Tooltip content={i18n.t('transfers.cancel')}>
                  <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={i18n.t('transfers.cancel')} onClick={() => void actions.transfer('cancel', job.id)} />
                </Tooltip>
              )}
            </div>
          </td>
        </tr>
      ))}
    </>
  );
}

export function AppsScreen({ device }: { device: DeviceSummary }) {
  const i18n = useI18n();
  const { t, bytes, date, digits, lang } = i18n;
  const client = useClient();
  const actions = useActions();
  const settings = useSettings().data;
  const [filter, setFilter] = useState<Filter>('user');
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'name', desc: false });
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [uninstall, setUninstall] = useState<AppInfo[] | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const apps = useApps(device, filter !== 'user');
  const caps = new Set(device.capabilities);
  const ios = device.platform === 'ios';
  const types = APP_EXTENSIONS[device.platform].join(', ');

  const jobs = (useTransfers().data ?? []).filter((j) => j.deviceId === device.id && j.kind === 'appInstall');
  const installing = jobs.filter((j) => ['queued', 'running', 'paused', 'waitingForDevice'].includes(j.state) || (j.state === 'failed' && Date.now() - Date.parse(j.finishedAt ?? j.createdAt) < 10 * 60_000));

  const collator = useMemo(() => new Intl.Collator(lang === 'fa' ? 'fa-IR' : 'en-GB', { sensitivity: 'base', numeric: true }), [lang]);
  const list = useMemo(() => {
    const all = (apps.data ?? []).filter((a) => (filter === 'system' ? a.source === 'system' : filter === 'user' ? a.source !== 'system' : true));
    const sign = sort.desc ? -1 : 1;
    return all.sort((a, b) => {
      let diff = 0;
      if (sort.key === 'size') diff = a.appBytes + a.dataBytes - (b.appBytes + b.dataBytes);
      else if (sort.key === 'installed') diff = (a.installedAt ?? '').localeCompare(b.installedAt ?? '');
      if (diff === 0) diff = collator.compare(a.name, b.name);
      return diff * sign;
    });
  }, [apps.data, filter, sort, collator]);

  useEffect(() => setSelected(new Set()), [filter, device.id]);

  const install = (files: File[]) => {
    const ok = files.filter((f) => isAppPackage(device, f.name));
    if (ok.length === 0) {
      toast({ tone: 'warning', title: t('apps.dropWrong', { device: device.name, types }) });
      return;
    }
    // One app per install job (split APKs of one app travel together).
    if (ios) ok.forEach((f) => void actions.installApps(device, [f]));
    else void actions.installApps(device, ok);
  };

  const drop = useFileDrop({
    verdict: () => (caps.has('appsInstall') ? { ok: true, message: t('apps.drop', { device: device.name }) } : { ok: false, message: t('error.next.not_supported') }),
    onDrop: install,
  });

  const header = (key: SortKey, label: MessageKey, className?: string) => (
    <th className={className} aria-sort={sort.key === key ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className={styles.sort} onClick={() => setSort((s) => ({ key, desc: s.key === key ? !s.desc : key !== 'name' }))}>
        {t(label)}
        {sort.key === key ? sort.desc ? <ArrowSortDownRegular aria-hidden /> : <ArrowSortUpRegular aria-hidden /> : null}
      </button>
    </th>
  );

  const removable = (a: AppInfo) => a.source !== 'system' && caps.has('appsUninstall');
  const selectedApps = list.filter((a) => selected.has(a.id));
  const allChecked: boolean | 'indeterminate' = list.length > 0 && selectedApps.length === list.length ? true : selectedApps.length > 0 ? 'indeterminate' : false;

  const exportApk = async (app: AppInfo) => {
    const destination = settings?.fileCopyFolder ?? (await actions.pickFolder());
    if (destination) void actions.exportApp(device, app.id, app.name, destination);
  };

  return (
    <ScreenLayout
      title={t('apps.title')}
      subtitle={apps.data ? t('apps.subtitle', { count: list.length }) : ' '}
      testId="screen-apps"
      fill
      actions={
        <>
          {!ios ? (
            <Segmented
              label={t('apps.includeSystem')}
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'user', label: t('apps.filter.user') },
                { value: 'system', label: t('apps.filter.system') },
                { value: 'all', label: t('apps.filter.all') },
              ]}
            />
          ) : null}
          {caps.has('appsInstall') ? (
            <Button variant="accent" icon={<AddRegular />} onClick={() => input.current?.click()}>
              {t('apps.install')}
            </Button>
          ) : null}
          <input
            ref={input}
            type="file"
            hidden
            multiple={!ios}
            accept={APP_EXTENSIONS[device.platform].join(',')}
            onChange={(e) => {
              const files = Array.from(e.target.files ?? []);
              e.target.value = '';
              if (files.length) install(files);
            }}
          />
        </>
      }
    >
      <div className={styles.wrap} {...drop.bind}>
        {apps.error ? <ErrorBanner error={apps.error} onRetry={() => void apps.refetch()} /> : null}
        {installing.some((j) => j.state === 'failed') ? (
          <Banner tone="danger" title={installing.find((j) => j.state === 'failed')?.error?.message}>
            {t('error.next.install_failed')}
          </Banner>
        ) : null}
        <div className={styles.table}>
          {apps.isPending ? (
            <div className={styles.skeletons}>
              {[0, 1, 2, 3, 4, 5].map((i) => (
                <Skeleton key={i} height={36} />
              ))}
            </div>
          ) : list.length === 0 && installing.length === 0 ? (
            <EmptyState icon={<AppsRegular />} title={t('apps.empty')} />
          ) : (
            <table className={styles.grid} data-testid="apps-table">
              <thead>
                <tr>
                  <th className={styles.check}>
                    <Checkbox
                      checked={allChecked}
                      onChange={(on) => setSelected(on ? new Set(list.map((a) => a.id)) : new Set())}
                      label={t('common.selectAll')}
                    />
                  </th>
                  {header('name', 'apps.column.name')}
                  <th>{t('apps.column.version')}</th>
                  {header('size', 'apps.column.size', styles.end)}
                  <th className={styles.hideNarrow}>{t('apps.column.source')}</th>
                  {header('installed', 'apps.column.installed', styles.hideNarrow)}
                  <th>
                    <span className="visually-hidden">{t('common.details')}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                <InstallRows jobs={installing} device={device} />
                {list.map((app) => (
                  <tr key={app.id} aria-selected={selected.has(app.id)} data-testid="app-row">
                    <td className={styles.check}>
                      <Checkbox
                        checked={selected.has(app.id)}
                        onChange={(on) =>
                          setSelected((s) => {
                            const next = new Set(s);
                            if (on) next.add(app.id);
                            else next.delete(app.id);
                            return next;
                          })
                        }
                        label={app.name}
                      />
                    </td>
                    <td>
                      <div className={styles.app}>
                        <img className={styles.icon} src={client.images.icon(device.id, app.id)} alt="" loading="lazy" />
                        <div className={styles.appText}>
                          <span className={styles.appName}>
                            {app.name}
                            {app.fileSharing ? <Badge tone="accent">{t('apps.fileSharing')}</Badge> : null}
                            {app.source === 'sideloaded' ? <Badge tone="warning">{t('apps.source.sideloaded')}</Badge> : null}
                            {app.source === 'system' ? <Badge>{t('apps.source.system')}</Badge> : null}
                          </span>
                          <span className={styles.appId}>{app.id}</span>
                        </div>
                      </div>
                    </td>
                    <td className={styles.num}>{digits(app.version)}</td>
                    <td className={`${styles.end} ${styles.size}`}>
                      <Tooltip content={t('apps.sizeDetail', { app: bytes(app.appBytes), data: bytes(app.dataBytes) })}>
                        <span>{bytes(app.appBytes + app.dataBytes)}</span>
                      </Tooltip>
                    </td>
                    <td className={`${styles.num} ${styles.hideNarrow}`}>{t(sourceLabel(app, device.platform))}</td>
                    <td className={`${styles.num} ${styles.hideNarrow}`}>{app.installedAt ? date(app.installedAt, 'short') : t('apps.never')}</td>
                    <td>
                      <div className={styles.actions}>
                        {ios && app.fileSharing && caps.has('appDocuments') ? (
                          <Tooltip content={t('apps.openDocuments')}>
                            <Button
                              variant="subtle"
                              size="small"
                              icon={<FolderOpenRegular />}
                              aria-label={`${t('apps.openDocuments')}: ${app.name}`}
                              onClick={() => {
                                useUi.getState().setAppDocsPlace({ root: `app:${app.id}`, path: '/' });
                                useUi.getState().navigate('appDocuments');
                              }}
                            />
                          </Tooltip>
                        ) : null}
                        {caps.has('appsExport') ? (
                          <Tooltip content={t('apps.exportApk')}>
                            <Button variant="subtle" size="small" icon={<ArrowDownloadRegular />} aria-label={`${t('apps.exportApk')}: ${app.name}`} onClick={() => void exportApk(app)} />
                          </Tooltip>
                        ) : null}
                        {removable(app) ? (
                          <Tooltip content={t('apps.uninstall')}>
                            <Button variant="subtle" size="small" icon={<DeleteRegular />} aria-label={`${t('apps.uninstall')}: ${app.name}`} onClick={() => setUninstall([app])} />
                          </Tooltip>
                        ) : null}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <AnimatePresence>
          {selectedApps.length > 0 ? (
            <motion.div
              className={styles.selectionBar}
              initial={{ opacity: 0, y: 16 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: 16 }}
              transition={{ duration: 0.2 }}
              role="toolbar"
              aria-label={t('common.selected', { count: selectedApps.length })}
            >
              <span className={styles.selectionCount}>{t('common.selected', { count: selectedApps.length })}</span>
              {caps.has('appsUninstall') ? (
                <Button variant="danger" icon={<DeleteRegular />} disabled={!selectedApps.some(removable)} onClick={() => setUninstall(selectedApps.filter(removable))}>
                  {t('apps.uninstall')}
                </Button>
              ) : null}
              <Button variant="subtle" icon={<DismissRegular />} aria-label={t('common.clearSelection')} onClick={() => setSelected(new Set())} />
            </motion.div>
          ) : null}
        </AnimatePresence>
        <DropOverlay visible={drop.over} verdict={caps.has('appsInstall') ? { ok: true, message: t('apps.drop', { device: device.name }) } : { ok: false, message: t('error.next.not_supported') }} />
      </div>

      <ConfirmDialog
        open={uninstall !== null}
        onOpenChange={(open) => (open ? undefined : setUninstall(null))}
        danger
        title={
          uninstall && uninstall.length === 1
            ? t('apps.uninstall.title', { app: uninstall[0]!.name, device: device.name })
            : t('apps.uninstall.titleMany', { count: uninstall?.length ?? 0, device: device.name })
        }
        body={t('apps.uninstall.body')}
        confirmLabel={t('apps.uninstall')}
        onConfirm={async () => {
          const targets = uninstall ?? [];
          setUninstall(null);
          for (const app of targets) await actions.uninstallApp(device, app.id, app.name);
          setSelected(new Set());
        }}
      />
    </ScreenLayout>
  );
}
