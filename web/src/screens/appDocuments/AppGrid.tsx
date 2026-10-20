// The apps that take files (VLC, Documents, Infuse; Android's app folders and Documents / Download): icon, name,
// file count and size, a copy in progress, and files or folders dropped on a card go straight into that app, or
// into one of its folders. Used by App documents and Files.

import {
  AppGenericRegular,
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  DocumentFolderRegular,
  FolderOpenRegular,
  FolderRegular,
} from '@fluentui/react-icons';
import { useQueries, useQueryClient } from '@tanstack/react-query';
import { useState, type DragEvent, type ReactNode } from 'react';
import type { AppInfo, DeviceSummary, TransferJob } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useFileList, useTransfers } from '../../app/queries';
import { ContextMenu } from '../../components/Menu';
import { LinearProgress } from '../../components/ui';
import { useI18n, type I18n, type MessageKey } from '../../i18n';
import { isFinished, jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import { progressLine } from '../../shell/TransferDrawer';
import { appFilesKeys, type AppPlace } from './appFiles';
import styles from './AppDocuments.module.css';

const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes('Files');

/** A folder's label in the page's language ("Media", "Data", "Documents", "Download"). */
export function folderLabel(label: string, t: I18n['t']): string {
  const key = `appFiles.folder.${label}` as MessageKey;
  const text = t(key);
  return text === key ? label : text;
}

/** The name to show for a place: an app's label, or a shared folder's name in the page's language. */
export function placeLabel(place: AppPlace, t: I18n['t']): string {
  return place.kind === 'sharedFolder' ? folderLabel(place.label, t) : place.label;
}

export function PlaceIcon({ device, place, className, glyphClassName }: { device: DeviceSummary; place: AppPlace; className?: string; glyphClassName?: string }) {
  const client = useClient();
  if (place.appId) return <img className={className} src={client.images.icon(device.id, place.appId)} alt="" loading="lazy" />;
  return (
    <span className={glyphClassName} aria-hidden>
      {place.id === 'shared:Download' ? <ArrowDownloadRegular /> : place.kind === 'sharedFolder' ? <DocumentFolderRegular /> : <AppGenericRegular />}
    </span>
  );
}

/**
 * Copies into a place: the ones this page started (it remembers their root), and the host's own
 * "… into <label>" titles until TransferJob carries a destination (proposed).
 */
export function copiesInto(jobs: readonly TransferJob[], deviceId: string, place: AppPlace, jobRoots: Record<string, string>): TransferJob[] {
  const roots = new Set(place.folders.map((f) => f.rootKey));
  return jobs.filter(
    (j) =>
      j.deviceId === deviceId &&
      j.kind === 'copyToDevice' &&
      !isFinished(j.state) &&
      (jobRoots[j.id] !== undefined ? roots.has(jobRoots[j.id]!) : j.title.endsWith(` into ${place.name}`) || place.folders.some((f) => j.title.endsWith(` into ${place.name} (${f.label})`))),
  );
}

/** Asks for a PC folder, then copies the whole place there (one job per folder). */
export function useExportPlace(device: DeviceSummary) {
  const { t } = useI18n();
  const actions = useActions();
  return async (place: AppPlace) => {
    const destination = await actions.pickFolder(t('appFiles.exportTitle'));
    if (!destination) return;
    for (const folder of place.folders) await actions.copyToPc(device, folder.rootKey, ['/'], destination);
  };
}

function SubfolderChips({ device, root, onDrop }: { device: DeviceSummary; root: string; onDrop(path: string, files: File[]): void }) {
  const { t } = useI18n();
  const list = useFileList(device, root, '/');
  const [over, setOver] = useState<string | null>(null);
  const folders = (list.data ?? []).filter((e) => e.isDirectory && !e.readOnly).slice(0, 4);
  if (folders.length === 0) return null;
  return (
    <span className={styles.chips}>
      <span className={styles.chipsLabel}>{t('appFiles.dropInto')}</span>
      {folders.map((folder) => (
        <span
          key={folder.path}
          className={styles.chip}
          data-over={over === folder.path || undefined}
          data-testid="subfolder-chip"
          onDragEnter={(e) => {
            e.preventDefault();
            e.stopPropagation();
            setOver(folder.path);
          }}
          onDragOver={(e) => {
            e.preventDefault();
            e.stopPropagation();
            e.dataTransfer.dropEffect = 'copy';
          }}
          onDragLeave={() => setOver((p) => (p === folder.path ? null : p))}
          onDrop={(e) => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            e.stopPropagation();
            onDrop(folder.path, Array.from(e.dataTransfer.files));
          }}
        >
          <FolderRegular aria-hidden />
          {folder.name}
        </span>
      ))}
    </span>
  );
}

function AppCard({ device, place, copies, onOpen }: { device: DeviceSummary; place: AppPlace; copies: TransferJob[]; onOpen(root?: string): void }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const exportPlace = useExportPlace(device);
  const [over, setOver] = useState(false);
  const label = placeLabel(place, t);
  const target = place.folders[0]!;
  const usages = useQueries({
    queries: place.folders.map((folder) => ({
      queryKey: appFilesKeys.measure(device.id, folder.rootKey, ['/']),
      queryFn: () => client.call('appFiles.measure', { deviceId: device.id, root: folder.rootKey, paths: ['/'] }),
      retry: false,
      staleTime: 30_000,
    })),
  });
  const measured = usages.every((u) => u.data);
  const files = usages.reduce((n, u) => n + (u.data?.files ?? 0), 0);
  const folders = usages.reduce((n, u) => n + (u.data?.folders ?? 0), 0);
  const size = usages.reduce((n, u) => n + (u.data?.bytes ?? 0), 0);
  const meta = !measured
    ? usages.some((u) => u.error)
      ? ' '
      : t('appFiles.counting')
    : files === 0 && folders === 0
      ? t('appDocs.empty')
      : `${t('appFiles.files', { count: files })} · ${bytes(size)}`;
  const copy = copies[0];
  const drop = (path: string, dropped: File[]) => {
    setOver(false);
    if (dropped.length > 0) void actions.copyToDevice(device, target.rootKey, path, dropped);
  };
  const menu = [
    { key: 'open', label: t('common.open'), icon: <FolderOpenRegular />, onSelect: () => onOpen() },
    ...(place.folders.length > 1
      ? place.folders.map((f) => ({ key: `open-${f.rootKey}`, label: t('appFiles.openFolder', { folder: folderLabel(f.label, t) }), icon: <FolderRegular />, onSelect: () => onOpen(f.rootKey) }))
      : []),
    { key: 'export', label: t('appFiles.exportApp'), icon: <ArrowDownloadRegular />, hint: place.folders.length > 1 ? folderLabels(place, t) : undefined, onSelect: () => void exportPlace(place) },
    'separator' as const,
    {
      key: 'refresh',
      label: t('appFiles.refresh'),
      icon: <ArrowClockwiseRegular />,
      onSelect: () => {
        void queryClient.invalidateQueries({ queryKey: ['appFiles.measure', device.id] });
        void queryClient.invalidateQueries({ queryKey: ['files.list', device.id] });
      },
    },
  ];

  return (
    <ContextMenu items={menu}>
      <button
        type="button"
        className={styles.card}
        onClick={() => onOpen()}
        aria-label={t('appDocs.open', { app: label })}
        data-drop={over || undefined}
        data-testid={`app-card-${target.rootKey}`}
        onDragEnter={(e) => {
          if (!hasFiles(e)) return;
          e.preventDefault();
          e.stopPropagation();
          setOver(true);
        }}
        onDragOver={(e) => {
          if (!hasFiles(e)) return;
          e.preventDefault();
          e.stopPropagation();
          e.dataTransfer.dropEffect = 'copy';
        }}
        onDragLeave={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false);
        }}
        onDrop={(e) => {
          if (!hasFiles(e)) return;
          e.preventDefault();
          e.stopPropagation();
          drop('/', Array.from(e.dataTransfer.files));
        }}
      >
        <span className={styles.head}>
          <PlaceIcon device={device} place={place} className={styles.icon} glyphClassName={styles.iconGlyph} />
          <span className={styles.text}>
            <span className={styles.name} title={place.kind === 'app' && place.label !== place.id ? `${place.label} (${place.id})` : label}>
              {label}
            </span>
            {over ? (
              <span className={styles.dropNote}>{t('appDocs.dropOn', { app: label })}</span>
            ) : (
              <span className={styles.meta} data-testid="app-card-meta">
                {meta}
              </span>
            )}
          </span>
        </span>
        {place.folders.length > 1 && !over ? <span className={styles.folders}>{folderLabels(place, t)}</span> : null}
        {over ? <SubfolderChips device={device} root={target.rootKey} onDrop={drop} /> : null}
        {copy ? (
          <span className={styles.copy} data-testid="app-card-copy">
            <LinearProgress value={copy.state === 'queued' ? null : jobFraction(copy)} tone={copy.state === 'running' ? 'accent' : 'paused'} label={copy.title} />
            <span className={styles.copyLine} title={copy.title}>
              {copies.length > 1 ? `${copy.title} (+${i18n.n(copies.length - 1)})` : copy.title} · {progressLine(copy, i18n, device)}
            </span>
          </span>
        ) : null}
      </button>
    </ContextMenu>
  );
}

const folderLabels = (place: AppPlace, t: I18n['t']) => place.folders.map((f) => folderLabel(f.label, t)).join(' · ');

/** An app that does not take files: greyed, with the one-line reason; a drop on it is refused. */
function OtherAppCard({ device, app }: { device: DeviceSummary; app: AppInfo }) {
  const { t } = useI18n();
  const client = useClient();
  const [refuse, setRefuse] = useState(false);
  return (
    <div
      className={styles.card}
      data-disabled
      data-refuse={refuse || undefined}
      aria-disabled
      onDragOver={(e) => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        e.stopPropagation();
        e.dataTransfer.dropEffect = 'none';
        setRefuse(true);
      }}
      onDragLeave={() => setRefuse(false)}
      onDrop={(e) => {
        e.preventDefault();
        e.stopPropagation();
        setRefuse(false);
      }}
    >
      <span className={styles.head}>
        <img className={styles.icon} src={client.images.icon(device.id, app.id)} alt="" loading="lazy" />
        <span className={styles.text}>
          <span className={styles.name}>{app.name}</span>
          <span className={styles.reason}>{t('appDocs.notSharing')}</span>
        </span>
      </span>
    </div>
  );
}

export function AppGrid({
  device,
  places,
  onOpen,
  others = [],
  label,
  heading,
}: {
  device: DeviceSummary;
  places: AppPlace[];
  /** Opens a place at one of its folders (the first when not given). */
  onOpen(root: string): void;
  /** Apps that don't share files, shown greyed (iPhone). */
  others?: AppInfo[];
  label?: string;
  heading?: ReactNode;
}) {
  const { t } = useI18n();
  const jobs = useTransfers().data ?? [];
  const jobRoots = useUi((s) => s.jobRoots);

  return (
    <section className={styles.section} aria-label={label}>
      {heading}
      {places.length > 0 ? (
        <div className={styles.grid} data-testid="app-grid">
          {places.map((place) => (
            <AppCard
              key={place.id}
              device={device}
              place={place}
              copies={copiesInto(jobs, device.id, place, jobRoots)}
              onOpen={(root) => onOpen(root ?? place.folders[0]!.rootKey)}
            />
          ))}
        </div>
      ) : null}
      {others.length > 0 ? (
        <section className={styles.section} aria-label={t('appDocs.others')}>
          <h2 className="t-subtitle">{t('appDocs.others')}</h2>
          <div className={styles.grid}>
            {others.map((app) => (
              <OtherAppCard key={app.id} device={device} app={app} />
            ))}
          </div>
        </section>
      ) : null}
    </section>
  );
}
