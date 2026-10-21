// The report's sections as cards. Each card keeps its own selection and asks for a CleanupPlan;
// nothing here deletes: the screen shows the plan in ConfirmCleanup and runs it on the user's OK.

import { ArrowDownloadRegular, DeleteRegular, DocumentRegular, DismissRegular, FolderRegular, VideoClipRegular } from '@fluentui/react-icons';
import { useId, useMemo, useState, type ReactNode } from 'react';
import type { AppFileHit, CleanupApps, CleanupAppFiles, CleanupCaches, CleanupDuplicates, CleanupMedia } from '../../bridge/cleanupTypes';
import type { AppInfo, DeviceSummary, PhotoAsset } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Badge, Banner, Card, Checkbox } from '../../components/ui';
import { useI18n, type I18n } from '../../i18n';
import styles from './Cleanup.module.css';
import type { CleanupPlan } from './ConfirmCleanup';
// Deleting an iPhone's photos too (round 4, photodelete).
import { whereTheyGoNotes } from '../photos/PhotoDeleteDialog';
import { usePhotoDeleteSupport } from '../photos/photoDelete';

const COLLAPSED = 8;
const GROUPS_COLLAPSED = 6;

export interface SectionProps {
  device: DeviceSummary;
  /** Opens the confirmation for a plan. */
  confirm(plan: CleanupPlan): void;
  /** Where copies go when the user keeps "copy to this PC first" on. */
  folders: { photos: string; files: string };
  /** iPhone photos: copy only. */
  copyMedia(assetIds: string[]): void;
  /** The analysis is still running: nothing can be acted on yet. */
  locked: boolean;
}

// ---------- Small pieces ----------

function useSelection(keys: readonly string[]) {
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  // Items the report no longer lists (deleted, or a new analysis) drop out of the selection.
  const selected = useMemo(() => new Set(keys.filter((k) => picked.has(k))), [keys, picked]);
  return {
    selected,
    has: (key: string) => selected.has(key),
    toggle: (key: string, on: boolean) =>
      setPicked((s) => {
        const next = new Set(s);
        if (on) next.add(key);
        else next.delete(key);
        return next;
      }),
    set: (list: Iterable<string>) => setPicked(new Set(list)),
    clear: () => setPicked(new Set()),
  };
}

function Row({
  checked,
  onToggle,
  label,
  media,
  name,
  detail,
  size,
  testId,
}: {
  checked?: boolean;
  onToggle?(on: boolean): void;
  label: string;
  media: ReactNode;
  name: ReactNode;
  detail?: ReactNode;
  size: string;
  testId?: string;
}) {
  return (
    <li className={cx(styles.row, !onToggle && styles.rowNoCheck)} aria-selected={onToggle ? checked : undefined} data-testid={testId}>
      {onToggle ? <Checkbox checked={!!checked} onChange={onToggle} label={label} /> : null}
      {media}
      <span className={styles.text}>
        <span className={styles.name}>{name}</span>
        {detail ? <span className={styles.detail}>{detail}</span> : null}
      </span>
      <span className={styles.size}>{size}</span>
    </li>
  );
}

function Expandable<T>({ items, render, testId }: { items: readonly T[]; render(item: T): ReactNode; testId?: string }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const shown = open ? items : items.slice(0, COLLAPSED);
  return (
    <>
      <ul className={styles.list} data-testid={testId}>
        {shown.map(render)}
      </ul>
      {items.length > COLLAPSED ? (
        <Button variant="subtle" size="small" className={styles.more} onClick={() => setOpen((v) => !v)}>
          {open ? t('cleanup.showFewer') : t('cleanup.showAll', { count: items.length })}
        </Button>
      ) : null}
    </>
  );
}

function SelectionBar({
  count,
  size,
  onClear,
  children,
}: {
  count: number;
  size: number;
  onClear(): void;
  children: ReactNode;
}) {
  const { t, bytes } = useI18n();
  if (count === 0) return null;
  return (
    <div className={styles.toolbar} role="toolbar" aria-label={t('cleanup.selected', { count, size: bytes(size) })}>
      <span className={styles.selection}>{t('cleanup.selected', { count, size: bytes(size) })}</span>
      <Button variant="subtle" size="small" icon={<DismissRegular />} onClick={onClear}>
        {t('cleanup.clear')}
      </Button>
      {children}
    </div>
  );
}

/** A card whose heading carries the section's one-line summary. */
function SectionCard({ title, summary, actions, testId, children }: { title: string; summary: string; actions?: ReactNode; testId: string; children: ReactNode }) {
  const id = useId();
  return (
    <Card aria-labelledby={id} data-testid={testId}>
      <div className={styles.cardHead}>
        <h2 id={id} className={styles.cardTitle}>
          <span className={styles.titleRow}>
            <span>{title}</span>
            <span className={styles.summary}>{summary}</span>
          </span>
        </h2>
        {actions ? <div className={styles.toolbar}>{actions}</div> : null}
      </div>
      {children}
    </Card>
  );
}

function when(i18n: I18n, iso: string | null): string {
  return iso ? i18n.date(iso, 'short') : '';
}

function mediaDetail(i18n: I18n, a: PhotoAsset): string {
  const parts = [when(i18n, a.takenAt)];
  if (a.durationSeconds) parts.push(i18n.duration(a.durationSeconds));
  if (a.width && a.height) parts.push(i18n.digits(`${a.width}×${a.height}`));
  return parts.filter(Boolean).join(' · ');
}

// ---------- Duplicates ----------

export function DuplicatesCard({ data, ...props }: SectionProps & { data: CleanupDuplicates }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const { device, confirm, folders, locked } = props;
  const keys = useMemo(() => data.groups.flatMap((g) => g.assets.map((a) => a.id)), [data]);
  const selection = useSelection(keys);
  const byId = useMemo(() => new Map(data.groups.flatMap((g) => g.assets.map((a) => [a.id, { asset: a, size: g.size }] as const))), [data]);
  const picked = [...selection.selected].map((id) => byId.get(id)!).filter(Boolean);
  const total = picked.reduce((n, p) => n + p.size, 0);
  const wholeGroup = data.groups.some((g) => g.assets.every((a) => selection.has(a.id)));
  const [allGroups, setAllGroups] = useState(false);
  const groups = allGroups ? data.groups : data.groups.slice(0, GROUPS_COLLAPSED);
  // Round 4: what the phone allows now (an iPhone through its camera connection), not what it allowed at the analysis.
  const canDelete = device.capabilities.includes('photosDelete');
  const support = usePhotoDeleteSupport(device).data;

  const plan = (): CleanupPlan => ({
    kind: 'media',
    title: t('cleanup.confirm.media', { count: picked.length, device: device.name }),
    rows: picked.map(({ asset, size }) => ({ key: asset.id, label: asset.fileName, detail: mediaDetail(i18n, asset), size })),
    confirmLabel: t('cleanup.action.delete'),
    notes: [t('cleanup.duplicates.keepHint'), ...whereTheyGoNotes(t, device, support)],
    reversible: support?.goesTo === 'recentlyDeleted',
    copy: { label: 'cleanup.confirm.copyFirst', folder: folders.photos },
    request: (copyFirstTo) => ({ kind: 'media', items: picked.map(({ asset, size }) => ({ id: asset.id, size })), copyFirstTo }),
  });

  return (
    <SectionCard
      title={t('cleanup.duplicates.title')}
      summary={t('cleanup.duplicates.summary', { count: data.groups.length, size: bytes(data.reclaimableBytes) })}
      actions={
        data.groups.length > 0 ? (
          <Button size="small" onClick={() => selection.set(data.groups.flatMap((g) => g.assets.slice(1).map((a) => a.id)))}>
            {t('cleanup.selectExtras')}
          </Button>
        ) : null
      }
      testId="cleanup-duplicates"
    >
      {data.groups.length === 0 ? (
        <p className={styles.hint}>{t('cleanup.duplicates.none')}</p>
      ) : (
        <>
          <p className={styles.hint}>{t('cleanup.duplicates.how')}</p>
          <div className={styles.groups}>
            {groups.map((group) => (
              <div key={group.hash} className={styles.group} data-testid="cleanup-duplicate-group">
                <img className={styles.groupThumb} src={client.images.thumb(device.id, group.assets[0]!.id, 256)} alt="" loading="lazy" />
                <div className={styles.text}>
                  <div className={styles.groupHead}>
                    <span>{t('cleanup.duplicates.copies', { count: group.assets.length })}</span>
                    <span className={styles.size}>{bytes(group.size)}</span>
                  </div>
                  <ul className={styles.copies}>
                    {group.assets.map((asset, index) => (
                      <li key={asset.id} className={styles.copy}>
                        <Checkbox checked={selection.has(asset.id)} onChange={(on) => selection.toggle(asset.id, on)} label={`${asset.fileName} ${when(i18n, asset.takenAt)}`} />
                        <span className={styles.name}>
                          {asset.fileName} <span className={styles.detail}>{when(i18n, asset.takenAt)}</span>
                        </span>
                        {index === 0 ? <Badge>{t('cleanup.duplicates.keep')}</Badge> : <span />}
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            ))}
          </div>
          {data.groups.length > GROUPS_COLLAPSED ? (
            <Button variant="subtle" size="small" className={styles.more} onClick={() => setAllGroups((v) => !v)}>
              {allGroups ? t('cleanup.showFewer') : t('cleanup.showAll', { count: data.groups.length })}
            </Button>
          ) : null}
          {wholeGroup ? <Banner tone="warning">{t('cleanup.duplicates.allSelected')}</Banner> : null}
          <SelectionBar count={picked.length} size={total} onClear={selection.clear}>
            <MediaAction {...props} canDelete={canDelete} disabled={locked || wholeGroup} onDelete={() => confirm(plan())} ids={picked.map((p) => p.asset.id)} />
          </SelectionBar>
        </>
      )}
    </SectionCard>
  );
}

/** Delete when the phone allows it (Android; an iPhone whose camera connection does); otherwise copy to the PC. */
function MediaAction({
  canDelete,
  disabled,
  onDelete,
  ids,
  copyMedia,
}: SectionProps & { canDelete: boolean; disabled: boolean; onDelete(): void; ids: string[] }) {
  const { t } = useI18n();
  return canDelete ? (
    <Button variant="danger" icon={<DeleteRegular />} disabled={disabled} onClick={onDelete} data-testid="cleanup-delete">
      {t('cleanup.action.delete')}
    </Button>
  ) : (
    <Button variant="accent" icon={<ArrowDownloadRegular />} disabled={disabled} onClick={() => copyMedia(ids)} data-testid="cleanup-copy">
      {t('cleanup.action.copy')}
    </Button>
  );
}

// ---------- Largest videos / photos ----------

export function MediaCard({ data, kind, ...props }: SectionProps & { data: CleanupMedia; kind: 'videos' | 'photos' }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const { device, confirm, folders, locked } = props;
  const list = kind === 'videos' ? data.largestVideos : data.largestPhotos;
  const keys = useMemo(() => list.map((a) => a.id), [list]);
  const selection = useSelection(keys);
  const picked = list.filter((a) => selection.has(a.id));
  const total = picked.reduce((n, a) => n + a.size, 0);
  // Round 4: what the phone allows now (an iPhone through its camera connection), not what it allowed at the analysis.
  const canDelete = device.capabilities.includes('photosDelete');
  const support = usePhotoDeleteSupport(device).data;

  const plan = (): CleanupPlan => ({
    kind: 'media',
    title: t('cleanup.confirm.media', { count: picked.length, device: device.name }),
    rows: picked.map((a) => ({ key: a.id, label: a.fileName, detail: mediaDetail(i18n, a), size: a.size })),
    confirmLabel: t('cleanup.action.delete'),
    notes: whereTheyGoNotes(t, device, support),
    reversible: support?.goesTo === 'recentlyDeleted',
    copy: { label: 'cleanup.confirm.copyFirst', folder: folders.photos },
    request: (copyFirstTo) => ({ kind: 'media', items: picked.map((a) => ({ id: a.id, size: a.size })), copyFirstTo }),
  });

  const summary =
    kind === 'videos'
      ? t('cleanup.videos.summary', { count: data.videoCount, size: bytes(data.videoBytes) })
      : t('cleanup.photos.summary', { count: data.photoCount, size: bytes(data.photoBytes) });

  return (
    <SectionCard title={t(kind === 'videos' ? 'cleanup.videos.title' : 'cleanup.photos.title')} summary={summary} testId={`cleanup-${kind}`}>
      <Expandable
        items={list}
        testId={`cleanup-${kind}-list`}
        render={(a) => (
          <Row
            key={a.id}
            checked={selection.has(a.id)}
            onToggle={(on) => selection.toggle(a.id, on)}
            label={a.fileName}
            media={<img className={styles.thumb} src={client.images.thumb(device.id, a.id, 128)} alt="" loading="lazy" />}
            name={a.fileName}
            detail={mediaDetail(i18n, a)}
            size={bytes(a.size)}
            testId="cleanup-media-row"
          />
        )}
      />
      {kind === 'photos' && data.cloudOnlyCount > 0 ? <p className={styles.hint}>{t('cleanup.media.cloudOnly', { count: data.cloudOnlyCount })}</p> : null}
      <SelectionBar count={picked.length} size={total} onClear={selection.clear}>
        <MediaAction {...props} canDelete={canDelete} disabled={locked} onDelete={() => confirm(plan())} ids={picked.map((a) => a.id)} />
      </SelectionBar>
    </SectionCard>
  );
}

// ---------- Apps ----------

export function AppsCard({ data, ...props }: SectionProps & { data: CleanupApps }) {
  const { t, bytes } = useI18n();
  const client = useClient();
  const { device, confirm, folders, locked } = props;
  const keys = useMemo(() => data.largest.map((a) => a.id), [data]);
  const selection = useSelection(keys);
  const size = (a: AppInfo) => a.appBytes + a.dataBytes;
  const picked = data.largest.filter((a) => selection.has(a.id));
  const total = picked.reduce((n, a) => n + size(a), 0);
  const ios = device.platform === 'ios';

  const plan = (): CleanupPlan => ({
    kind: 'apps',
    title: t('cleanup.confirm.apps', { count: picked.length, device: device.name }),
    rows: picked.map((a) => ({ key: a.id, label: a.name, detail: t('cleanup.apps.sizes', { app: bytes(a.appBytes), data: bytes(a.dataBytes) }), size: size(a) })),
    confirmLabel: t('cleanup.action.uninstall'),
    notes: [t('cleanup.confirm.appsData'), ...(ios ? [t('cleanup.apps.iphoneNote')] : [])],
    copy: data.canCopyFirst ? { label: 'cleanup.confirm.copyApk', folder: folders.files } : null,
    request: (copyFirstTo) => ({ kind: 'apps', items: picked.map((a) => ({ id: a.id, size: size(a) })), copyFirstTo }),
  });

  return (
    <SectionCard title={t('cleanup.apps.title')} summary={t('cleanup.apps.summary', { count: data.count, size: bytes(data.totalBytes) })} testId="cleanup-apps">
      <Expandable
        items={data.largest}
        testId="cleanup-apps-list"
        render={(a) => (
          <Row
            key={a.id}
            checked={selection.has(a.id)}
            onToggle={data.canUninstall ? (on) => selection.toggle(a.id, on) : undefined}
            label={a.name}
            media={<img className={styles.icon} src={client.images.icon(device.id, a.id)} alt="" loading="lazy" />}
            name={a.name}
            detail={t('cleanup.apps.sizes', { app: bytes(a.appBytes), data: bytes(a.dataBytes) })}
            size={bytes(size(a))}
            testId="cleanup-app-row"
          />
        )}
      />
      <SelectionBar count={picked.length} size={total} onClear={selection.clear}>
        <Button variant="danger" icon={<DeleteRegular />} disabled={locked} onClick={() => confirm(plan())} data-testid="cleanup-uninstall">
          {t('cleanup.action.uninstall')}
        </Button>
      </SelectionBar>
    </SectionCard>
  );
}

// ---------- Big files in apps ----------

const fileKey = (f: AppFileHit) => `${f.root}\n${f.path}`;

export function AppFilesCard({ data, ...props }: SectionProps & { data: CleanupAppFiles }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const { device, confirm, folders, locked } = props;
  const keys = useMemo(() => data.files.map(fileKey), [data]);
  const selection = useSelection(keys);
  const picked = data.files.filter((f) => selection.has(fileKey(f)));
  const total = picked.reduce((n, f) => n + f.size, 0);
  const owner = (f: AppFileHit) => (f.appId ? (data.appNames[f.appId] ?? f.appId) : (f.folder ?? f.root));
  const detail = (f: AppFileHit) => [owner(f), when(i18n, f.modified)].filter(Boolean).join(' · ');

  const plan = (): CleanupPlan => ({
    kind: 'files',
    title: t('cleanup.confirm.files', { count: picked.length, device: device.name }),
    rows: picked.map((f) => ({ key: fileKey(f), label: f.name, detail: detail(f), size: f.size })),
    confirmLabel: t('cleanup.action.delete'),
    notes: [],
    copy: { label: 'cleanup.confirm.copyFirst', folder: folders.files },
    request: (copyFirstTo) => ({ kind: 'files', items: picked.map((f) => ({ root: f.root, path: f.path, size: f.size })), copyFirstTo }),
  });

  return (
    <SectionCard
      title={t('cleanup.files.title')}
      summary={t('cleanup.files.summary', { count: data.files.length, min: bytes(data.minBytes), size: bytes(data.totalBytes) })}
      testId="cleanup-files"
    >
      {data.files.length === 0 ? (
        <p className={styles.hint}>{t('cleanup.files.none', { min: bytes(data.minBytes) })}</p>
      ) : (
        <Expandable
          items={data.files}
          testId="cleanup-files-list"
          render={(f) => (
            <Row
              key={fileKey(f)}
              checked={selection.has(fileKey(f))}
              onToggle={data.canDelete ? (on) => selection.toggle(fileKey(f), on) : undefined}
              label={f.name}
              media={
                <span className={styles.glyph} aria-hidden>
                  {/\.(mp4|mkv|mov|avi|m4v)$/i.test(f.name) ? <VideoClipRegular /> : f.appId ? <DocumentRegular /> : <FolderRegular />}
                </span>
              }
              name={f.name}
              detail={detail(f)}
              size={bytes(f.size)}
              testId="cleanup-file-row"
            />
          )}
        />
      )}
      <SelectionBar count={picked.length} size={total} onClear={selection.clear}>
        <Button variant="danger" icon={<DeleteRegular />} disabled={locked} onClick={() => confirm(plan())} data-testid="cleanup-delete-files">
          {t('cleanup.action.delete')}
        </Button>
      </SelectionBar>
    </SectionCard>
  );
}

// ---------- App caches (Android) ----------

export function CachesCard({ data, device, confirm, locked }: SectionProps & { data: CleanupCaches }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const shown = data.apps.slice(0, 8);
  const counted = data.totalBytes !== null;

  const plan = (): CleanupPlan => ({
    kind: 'caches',
    title: t('cleanup.confirm.caches', { device: device.name }),
    rows: data.apps.map((a) => ({ key: a.appId, label: a.name ?? a.appId, size: a.bytes })),
    approximate: true,
    description: counted ? undefined : t('cleanup.caches.notCounted'),
    confirmLabel: t('cleanup.caches.clear'),
    notes: [t('cleanup.confirm.cachesBody')],
    copy: null,
    request: () => ({ kind: 'caches' }),
  });

  return (
    <SectionCard
      title={t('cleanup.caches.title')}
      summary={counted ? t('cleanup.caches.summary', { size: bytes(data.totalBytes!) }) : t('cleanup.tile.notCounted')}
      actions={
        <Button variant="danger" size="small" icon={<DeleteRegular />} disabled={locked} onClick={() => confirm(plan())} data-testid="cleanup-trim">
          {t('cleanup.caches.clear')}
        </Button>
      }
      testId="cleanup-caches"
    >
      <p className={styles.hint}>{counted ? t('cleanup.caches.about') : t('cleanup.caches.notCounted')}</p>
      {data.trimmedAt !== null ? (
        <Banner tone="success">{t('cleanup.caches.trimmed', { when: i18n.date(data.trimmedAt), size: bytes(data.trimmedBytes ?? 0) })}</Banner>
      ) : null}
      {shown.length > 0 ? (
        <ul className={styles.caches}>
          {shown.map((a) => (
            <li key={a.appId} className={styles.cacheChip}>
              <span>{a.name ?? a.appId}</span>
              <span className={styles.cacheSize}>{bytes(a.bytes)}</span>
            </li>
          ))}
          {data.apps.length > shown.length ? <li className={styles.cacheChip}>{t('cleanup.caches.more', { count: data.apps.length - shown.length })}</li> : null}
        </ul>
      ) : null}
    </SectionCard>
  );
}
