import {
  AppsRegular,
  ArrowDownloadRegular,
  ArrowLeftRegular,
  DocumentRegular,
  FolderOpenRegular,
  FolderRegular,
  PhoneRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { BackupDomain, BackupEntry, BackupInfo, BackupItemRef } from '../../bridge/types';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Checkbox, EmptyState, Skeleton } from '../../components/ui';
import { useI18n, type I18n, type MessageKey } from '../../i18n';
import { toast } from '../../state/toasts';
import { ScreenLayout } from '../ScreenLayout';
import styles from './Backup.module.css';
import { backupDate } from './BackupBits';
import { rememberJob, useBackupFiles, useDomains } from './backupQueries';

/** A system domain's name in words when there is one ("HomeDomain" → "Home: messages, contacts…"). */
export function domainLabel(domain: BackupDomain, { t }: I18n): string {
  if (domain.kind === 'appGroup') return t('backup.browse.group', { app: domain.name });
  if (domain.kind === 'appPlugin') return t('backup.browse.plugin', { app: domain.name });
  if (domain.kind === 'app') return domain.name;
  const key = `backup.domain.${domain.domain}` as MessageKey;
  const label = t(key);
  return label === key ? domain.domain : label;
}

/** Folders and files of one backup, by app and by system domain; selected items extract to a folder on the PC. */
export function BackupBrowser({ backup, onBack }: { backup: BackupInfo; onBack(): void }) {
  const i18n = useI18n();
  const { t, bytes, date } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const domains = useDomains(backup);
  const [domain, setDomain] = useState<string | null>(null);
  const [path, setPath] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const files = useBackupFiles(backup, domain, path);
  const current = domains.data?.find((d) => d.domain === domain) ?? null;

  useEffect(() => {
    if (!domain && domains.data?.length) setDomain(domains.data[0]!.domain);
  }, [domain, domains.data]);
  useEffect(() => setSelected(new Set()), [domain, path]);

  const apps = useMemo(() => (domains.data ?? []).filter((d) => d.kind !== 'system'), [domains.data]);
  const system = useMemo(() => (domains.data ?? []).filter((d) => d.kind === 'system'), [domains.data]);
  const entries = files.data ?? [];
  const crumbs = path ? path.split('/') : [];

  const open = (d: BackupDomain) => {
    setDomain(d.domain);
    setPath('');
  };

  const extract = async () => {
    if (!domain) return;
    const items: BackupItemRef[] = selected.size > 0 ? [...selected].map((p) => ({ domain, path: p })) : [{ domain, path }];
    const destination = await actions.pickFolder(t('backup.browse.extractTo'));
    if (!destination) return;
    try {
      const job = await client.call('backup.extract', { backupId: backup.id, items, destination });
      rememberJob(queryClient, job);
      toast({ tone: 'info', title: t('backup.browse.extractStarted', { folder: destination }), duration: 3000 });
    } catch (e) {
      actions.fail(e);
    }
  };

  const toggle = (entry: BackupEntry) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(entry.path)) next.delete(entry.path);
      else next.add(entry.path);
      return next;
    });

  const allChecked: boolean | 'indeterminate' = entries.length > 0 && selected.size === entries.length ? true : selected.size > 0 ? 'indeterminate' : false;

  const domainButton = (d: BackupDomain) => (
    <button key={d.domain} type="button" className={styles.domain} aria-current={d.domain === domain} onClick={() => open(d)} data-testid="backup-domain" data-domain={d.domain}>
      {d.kind === 'system' ? <PhoneRegular aria-hidden /> : <AppsRegular aria-hidden />}
      <span className={styles.domainText}>
        <span className={styles.domainName}>{domainLabel(d, i18n)}</span>
        <span className={styles.domainMeta}>
          {bytes(d.bytes)} · {t('backup.browse.files', { count: d.files })}
        </span>
      </span>
    </button>
  );

  return (
    <ScreenLayout
      title={t('backup.browse.title', { device: backup.deviceName })}
      subtitle={`${backupDate(backup, i18n)} · ${bytes(backup.sizeBytes)}`}
      testId="backup-browser"
      fill
      actions={
        <>
          <Button icon={<ArrowLeftRegular />} onClick={onBack} data-testid="browser-back">
            {t('backup.browse.back')}
          </Button>
          <Button icon={<FolderOpenRegular />} onClick={() => void actions.revealInExplorer(backup.path)}>
            {t('common.showInExplorer')}
          </Button>
          <Button variant="accent" icon={<ArrowDownloadRegular />} disabled={!domain} onClick={() => void extract()} data-testid="backup-extract">
            {selected.size > 0 ? t('backup.browse.extractSelected', { count: selected.size }) : t('backup.browse.extract')}
          </Button>
        </>
      }
    >
      {domains.error ? <ErrorBanner error={domains.error} onRetry={() => void domains.refetch()} /> : null}
      <div className={styles.browser}>
        <nav className={styles.pane} aria-label={t('backup.browse.domains')}>
          {domains.isPending ? (
            <div style={{ padding: 12 }}>
              <Skeleton height={36} />
            </div>
          ) : (
            <>
              {apps.length ? <div className={styles.paneHeading}>{t('backup.browse.apps')}</div> : null}
              {apps.map(domainButton)}
              {system.length ? <div className={styles.paneHeading}>{t('backup.browse.system')}</div> : null}
              {system.map(domainButton)}
            </>
          )}
        </nav>
        <section className={styles.pane} aria-label={current ? domainLabel(current, i18n) : t('backup.browse.pick')}>
          {current ? (
            <nav className={styles.crumbs} aria-label={t('files.breadcrumb')}>
              <button type="button" className={styles.crumb} onClick={() => setPath('')}>
                {domainLabel(current, i18n)}
              </button>
              {crumbs.map((part, i) => (
                <span key={i} className={styles.inline}>
                  <span aria-hidden>›</span>
                  <button type="button" className={styles.crumb} onClick={() => setPath(crumbs.slice(0, i + 1).join('/'))}>
                    {part}
                  </button>
                </span>
              ))}
            </nav>
          ) : null}
          {files.error ? <ErrorBanner error={files.error} onRetry={() => void files.refetch()} /> : null}
          {!domain ? (
            <EmptyState title={t('backup.browse.pick')} />
          ) : files.isPending ? (
            <div style={{ padding: 12 }}>
              <Skeleton height={28} />
            </div>
          ) : entries.length === 0 ? (
            <EmptyState icon={<FolderRegular />} title={t('backup.browse.empty')} />
          ) : (
            <table className={styles.table} data-testid="backup-files">
              <thead>
                <tr>
                  <th style={{ width: 40 }}>
                    <Checkbox checked={allChecked} onChange={(v) => setSelected(v ? new Set(entries.map((e) => e.path)) : new Set())} label={t('common.selectAll')} />
                  </th>
                  <th>{t('backup.browse.column.name')}</th>
                  <th className={styles.num}>{t('backup.browse.column.size')}</th>
                  <th>{t('backup.browse.column.modified')}</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((entry) => (
                  <tr
                    key={entry.path}
                    aria-selected={selected.has(entry.path)}
                    onDoubleClick={() => entry.isDirectory && setPath(entry.path)}
                    data-testid="backup-file"
                  >
                    <td>
                      <Checkbox checked={selected.has(entry.path)} onChange={() => toggle(entry)} label={entry.name} />
                    </td>
                    <td>
                      {entry.isDirectory ? (
                        <button type="button" className={`${styles.crumb} ${styles.fileName}`} onClick={() => setPath(entry.path)}>
                          <FolderRegular aria-hidden />
                          {entry.name}
                        </button>
                      ) : (
                        <span className={styles.fileName}>
                          <DocumentRegular aria-hidden />
                          {entry.name}
                        </span>
                      )}
                    </td>
                    <td className={styles.num}>{entry.isDirectory ? '' : bytes(entry.size)}</td>
                    <td className={styles.num}>{entry.modified ? date(entry.modified) : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {entries.length > 0 ? <p className={styles.note} style={{ padding: '8px 12px' }}>{t('backup.browse.count', { count: entries.length })}</p> : null}
        </section>
      </div>
    </ScreenLayout>
  );
}
