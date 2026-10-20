// The apps that accept files (VLC, Documents, Infuse…): icon, name, item count and size, a copy in
// progress, and files dropped on a card go straight into that app. Used by App documents and Files.

import { useQueries } from '@tanstack/react-query';
import { useState, type DragEvent } from 'react';
import type { AppInfo, DeviceSummary, FileRoot, TransferJob } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys, useApps, useTransfers } from '../../app/queries';
import { LinearProgress } from '../../components/ui';
import { useI18n } from '../../i18n';
import { isFinished, jobFraction } from '../../state/transfers';
import { useUi } from '../../state/ui';
import { progressLine } from '../../shell/TransferDrawer';
import styles from './AppDocuments.module.css';

const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes('Files');

/**
 * Copies into an app: the ones this page started (it remembers their root), and the host's own
 * "<file> into <App>" titles until TransferJob carries a destination (proposed).
 */
export function copiesInto(jobs: readonly TransferJob[], deviceId: string, root: FileRoot, jobRoots: Record<string, string>): TransferJob[] {
  return jobs.filter(
    (j) =>
      j.deviceId === deviceId &&
      j.kind === 'copyToDevice' &&
      !isFinished(j.state) &&
      (jobRoots[j.id] === root.key || (jobRoots[j.id] === undefined && j.title.endsWith(` into ${root.label}`))),
  );
}

function AppCard({ device, root, app, count, copies, onOpen }: { device: DeviceSummary; root: FileRoot; app: AppInfo | undefined; count: number | undefined; copies: TransferJob[]; onOpen(): void }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const actions = useActions();
  const [over, setOver] = useState(false);
  const meta = [
    count === undefined ? null : count === 0 ? t('appDocs.empty') : t('appDocs.items', { count }),
    app && app.dataBytes > 0 ? t('appDocs.dataSize', { size: bytes(app.dataBytes) }) : null,
  ].filter(Boolean);
  const copy = copies[0];

  return (
    <button
      type="button"
      className={styles.card}
      onClick={onOpen}
      aria-label={t('appDocs.open', { app: root.label })}
      data-drop={over || undefined}
      data-testid={`app-card-${root.key}`}
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
        setOver(false);
        const files = Array.from(e.dataTransfer.files);
        if (files.length > 0) void actions.copyToDevice(device, root.key, '/', files);
      }}
    >
      <span className={styles.head}>
        {root.appId ? <img className={styles.icon} src={client.images.icon(device.id, root.appId)} alt="" /> : null}
        <span className={styles.text}>
          <span className={styles.name}>{root.label}</span>
          {over ? (
            <span className={styles.dropNote}>{t('appDocs.dropOn', { app: root.label })}</span>
          ) : (
            <span className={styles.meta}>{meta.join(' · ') || ' '}</span>
          )}
        </span>
      </span>
      {copy ? (
        <span className={styles.copy} data-testid="app-card-copy">
          <LinearProgress value={copy.state === 'queued' ? null : jobFraction(copy)} tone={copy.state === 'running' ? 'accent' : 'paused'} label={copy.title} />
          <span className={styles.copyLine} title={copy.title}>
            {copies.length > 1 ? `${copy.title} (+${i18n.n(copies.length - 1)})` : copy.title} · {progressLine(copy, i18n, device)}
          </span>
        </span>
      ) : null}
    </button>
  );
}

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

export function AppGrid({ device, roots, onOpen, showOthers = false }: { device: DeviceSummary; roots: FileRoot[]; onOpen(root: string): void; showOthers?: boolean }) {
  const { t } = useI18n();
  const client = useClient();
  const apps = useApps(device, false).data ?? [];
  const jobs = useTransfers().data ?? [];
  const jobRoots = useUi((s) => s.jobRoots);
  const sharing = roots.filter((r) => r.kind === 'appDocuments');
  const listings = useQueries({
    queries: sharing.map((root) => ({
      queryKey: keys.files(device.id, root.key, '/'),
      queryFn: () => client.call('files.list', { deviceId: device.id, root: root.key, path: '/' }),
      retry: false,
    })),
  });
  const others = showOthers ? apps.filter((a) => !a.fileSharing && !sharing.some((r) => r.appId === a.id)) : [];

  return (
    <>
      <div className={styles.grid} data-testid="app-grid">
        {sharing.map((root, i) => (
          <AppCard
            key={root.key}
            device={device}
            root={root}
            app={apps.find((a) => a.id === root.appId)}
            count={listings[i]?.data?.length}
            copies={copiesInto(jobs, device.id, root, jobRoots)}
            onOpen={() => onOpen(root.key)}
          />
        ))}
      </div>
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
    </>
  );
}
