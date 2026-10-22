// Delete asks first and says exactly what goes: each item (a folder with what is inside it), the total the phone
// frees, and that it can't be undone. Folder contents are counted on the phone (appFiles.measure). v0.6: Delete hands
// the items, each with the size shown, to a job in the Transfers drawer.

import { DocumentRegular, FolderRegular } from '@fluentui/react-icons';
import type { FileDeleteItem } from '../../bridge/deleteTypes';
import type { DeviceSummary, FileEntry } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Spinner } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useFolderUsage } from '../appDocuments/appFiles';
import styles from './Files.module.css';

const SHOWN = 6;

export function DeleteDialog({
  device,
  root,
  entries,
  busy,
  onCancel,
  onConfirm,
}: {
  device: DeviceSummary;
  root: string;
  entries: FileEntry[] | null;
  busy: boolean;
  onCancel(): void;
  onConfirm(items: FileDeleteItem[]): void;
}) {
  const { t, bytes } = useI18n();
  const list = entries ?? [];
  const folders = list.filter((e) => e.isDirectory).map((e) => e.path);
  const usage = useFolderUsage(device, root, folders, entries !== null && folders.length > 0);
  const counted = folders.length === 0 || usage.data !== undefined;
  const fileBytes = list.reduce((n, e) => n + (e.isDirectory ? 0 : e.size), 0);
  const fileCount = list.filter((e) => !e.isDirectory).length;
  const total = counted ? { files: fileCount + (usage.data?.files ?? 0), bytes: fileBytes + (usage.data?.bytes ?? 0) } : null;
  const inside = (entry: FileEntry) => usage.data?.items.find((i) => i.path === entry.path);
  // A folder goes with what it holds: its size is what was counted inside it (0 when it couldn't be counted).
  const confirmed = (): FileDeleteItem[] =>
    list.map((e) => ({ root, path: e.path, size: e.isDirectory ? (inside(e)?.bytes ?? 0) : e.size, isDirectory: e.isDirectory }));

  return (
    <Dialog
      open={entries !== null}
      onOpenChange={(open) => (open ? undefined : onCancel())}
      title={t('files.delete.title', { count: list.length, device: device.name })}
      description={
        total
          ? `${t('appFiles.delete.frees', { files: total.files, size: bytes(total.bytes) })} ${t('files.delete.body')}`
          : usage.error
            ? `${t('appFiles.delete.unknown')} ${t('files.delete.body')}`
            : t('appFiles.delete.counting')
      }
      footer={
        <>
          <Button onClick={onCancel}>{t('common.cancel')}</Button>
          <Button variant="dangerSolid" onClick={() => onConfirm(confirmed())} loading={busy} disabled={!counted && !usage.error} autoFocus data-testid="delete-confirm">
            {t('common.delete')}
          </Button>
        </>
      }
    >
      <ul className={styles.deleteList} data-testid="delete-list">
        {list.slice(0, SHOWN).map((entry) => {
          const usageOf = inside(entry);
          return (
            <li key={entry.path} className={styles.deleteItem}>
              <span className={styles.fileIcon} data-kind={entry.isDirectory ? 'folder' : 'file'} aria-hidden>
                {entry.isDirectory ? <FolderRegular /> : <DocumentRegular />}
              </span>
              <span className={styles.name} title={entry.path}>
                {entry.name}
              </span>
              <span className={styles.deleteSize}>
                {!entry.isDirectory ? (
                  bytes(entry.size)
                ) : usageOf ? (
                  `${t('appFiles.files', { count: usageOf.files })} · ${bytes(usageOf.bytes)}`
                ) : usage.error ? (
                  '—'
                ) : (
                  <Spinner label={t('appFiles.counting')} />
                )}
              </span>
            </li>
          );
        })}
        {list.length > SHOWN ? <li className={styles.deleteMore}>{t('appFiles.delete.more', { count: list.length - SHOWN })}</li> : null}
      </ul>
    </Dialog>
  );
}
