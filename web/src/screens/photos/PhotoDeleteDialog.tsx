// The Photos screen's delete (round 4, photodelete), in the style of Clean-up's confirmation: what goes and what it
// frees, "Copy to this PC first" (on by default for more than 20), where they go, then a result for each photo.

import { useEffect, useMemo, useState } from 'react';
import type { PhotoDeleteReport, PhotoDeleteSupport } from '../../bridge/photoDeleteTypes';
import type { DeviceSummary, PhotoAsset } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Switch } from '../../components/ui';
import { useI18n, type I18n } from '../../i18n';
import { toast } from '../../state/toasts';
import styles from './PhotoDelete.module.css';
import { PhotoDeleteResults } from './PhotoDeleteResults';
import { COPY_FIRST_ABOVE, useDeletePhotos } from './photoDelete';

/** Rows listed in the confirmation; the rest are counted. */
const LISTED = 200;

/** The same as notes for Clean-up's confirmation. */
export function whereTheyGoNotes(t: I18n['t'], device: DeviceSummary, support: PhotoDeleteSupport | undefined): string[] {
  const goesTo = support?.goesTo ?? 'unknown';
  const notes = [t(goesTo === 'recentlyDeleted' ? 'photoDelete.goes.recentlyDeleted' : goesTo === 'permanent' ? 'photoDelete.goes.permanent' : 'photoDelete.goes.unknown')];
  if (device.platform === 'ios' && support) notes.push(t(support.observed ? 'photoDelete.goes.seen' : 'photoDelete.goes.reported'));
  if (support?.iCloudPhotos) notes.push(t('photoDelete.icloud'));
  return notes;
}

/** "They're deleted for good" / "They go to Recently Deleted", and how DeviceDeck knows. */
export function WhereTheyGo({ device, support }: { device: DeviceSummary; support: PhotoDeleteSupport | undefined }) {
  const { t } = useI18n();
  const goesTo = support?.goesTo ?? 'unknown';
  const ios = device.platform === 'ios';
  return (
    <div className={styles.goes} data-testid="photo-delete-goes" data-goes={goesTo}>
      <span className={styles.goesTitle}>
        {t(goesTo === 'recentlyDeleted' ? 'photoDelete.goes.recentlyDeleted' : goesTo === 'permanent' ? 'photoDelete.goes.permanent' : 'photoDelete.goes.unknown')}
      </span>
      {ios && support ? <span className={styles.hint}>{t(support.observed ? 'photoDelete.goes.seen' : 'photoDelete.goes.reported')}</span> : null}
      {support?.iCloudPhotos ? <span className={styles.hint}>{t('photoDelete.icloud')}</span> : null}
    </div>
  );
}

export function PhotoDeleteDialog({
  device,
  assets,
  support,
  onClose,
  onDeleted,
}: {
  device: DeviceSummary;
  assets: PhotoAsset[];
  support: PhotoDeleteSupport | undefined;
  onClose(): void;
  /** Called once with the ids that went. */
  onDeleted(ids: string[]): void;
}) {
  const { t, bytes, date } = useI18n();
  const actions = useActions();
  const settings = useSettings().data;
  const deletePhotos = useDeletePhotos(device);
  // Originals in iCloud only aren't on the phone: nothing of theirs to delete, so they stay out.
  const cloudOnly = assets.filter((a) => a.inCloudOnly);
  const items = useMemo(() => assets.filter((a) => !a.inCloudOnly), [assets]);
  const [copy, setCopy] = useState(items.length > COPY_FIRST_ABOVE);
  const [folder, setFolder] = useState('');
  const [busy, setBusy] = useState(false);
  const [report, setReport] = useState<PhotoDeleteReport | null>(null);

  useEffect(() => {
    if (!folder && settings?.photoExportFolder) setFolder(settings.photoExportFolder);
  }, [folder, settings?.photoExportFolder]);

  const total = items.reduce((n, a) => n + a.size, 0);
  const live = items.some((a) => a.kind === 'livePhoto');
  const copying = copy && folder.length > 0;
  const permanent = (support?.goesTo ?? 'permanent') !== 'recentlyDeleted';

  const run = async () => {
    setBusy(true);
    const result = await deletePhotos(
      items.map((a) => ({ id: a.id, size: a.size })),
      copying ? folder : null,
    );
    setBusy(false);
    if (!result) return;
    setReport(result);
    const gone = result.items.filter((r) => r.outcome === 'deleted').map((r) => r.id);
    if (gone.length > 0) {
      onDeleted(gone);
      toast({ tone: 'success', title: t('photoDelete.toast', { count: gone.length, size: bytes(result.freedBytes) }), duration: 4000 });
    }
  };

  if (report) {
    return (
      <Dialog
        open
        wide
        onOpenChange={(open) => (open ? undefined : onClose())}
        title={t('photoDelete.result.title')}
        footer={
          <Button variant="accent" onClick={onClose} autoFocus data-testid="photo-delete-done">
            {t('photoDelete.done')}
          </Button>
        }
      >
        <PhotoDeleteResults
          items={report.items}
          deleted={report.deleted}
          freedBytes={report.freedBytes}
          goesTo={report.goesTo}
          copyIncomplete={report.copyIncomplete}
        />
      </Dialog>
    );
  }

  return (
    <Dialog
      open
      wide
      dismissible={!busy}
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={t('photoDelete.title', { count: items.length, device: device.name })}
      description={t(live ? 'photoDelete.freesLive' : 'photoDelete.frees', { size: bytes(total) })}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="dangerSolid"
            onClick={() => void run()}
            loading={busy}
            disabled={items.length === 0}
            data-testid="photo-delete-confirm"
            autoFocus
          >
            {busy ? t(copying ? 'photoDelete.busyCopy' : 'photoDelete.busy') : t('photoDelete.confirm', { count: items.length })}
          </Button>
        </>
      }
    >
      <div className={styles.body} data-testid="photo-delete-dialog" data-device={device.id}>
        <div>
          <ul className={styles.list} aria-label={t('photoDelete.title', { count: items.length, device: device.name })}>
            {items.slice(0, LISTED).map((a) => (
              <li key={a.id} className={`${styles.row} ${styles.rowPlain}`} data-testid="photo-delete-row">
                <span className={styles.text}>
                  <span className={styles.name}>{a.fileName}</span>
                  {a.takenAt ? <span className={styles.detail}>{date(a.takenAt, 'short')}</span> : null}
                </span>
                <span className={styles.size}>{bytes(a.size)}</span>
              </li>
            ))}
          </ul>
          <div className={styles.total} data-testid="photo-delete-total">
            <span>{items.length > LISTED ? t('photoDelete.more', { count: items.length - LISTED }) : t('common.items', { count: items.length })}</span>
            <span>{bytes(total)}</span>
          </div>
        </div>

        {cloudOnly.length > 0 ? <p className={styles.hint}>{t('photoDelete.cloudOnly', { count: cloudOnly.length })}</p> : null}

        <WhereTheyGo device={device} support={support} />

        <div className={styles.copyBox}>
          <Switch checked={copy} onChange={setCopy} label={t('photoDelete.copyFirst')} />
          {copy ? (
            <>
              <div className={styles.copyFolder}>
                <span>{t('photoDelete.copyTo', { folder })}</span>
                <Button
                  variant="link"
                  size="small"
                  disabled={busy}
                  onClick={async () => {
                    const picked = await actions.pickFolder();
                    if (picked) setFolder(picked);
                  }}
                >
                  {t('common.change')}
                </Button>
              </div>
              <div className={styles.copyFolder}>{t('photoDelete.copySafe')}</div>
            </>
          ) : null}
        </div>

        <p className={styles.hint}>{t('photoDelete.batches')}</p>
        {permanent ? <p className={styles.warn}>{t('photoDelete.undo')}</p> : null}
      </div>
    </Dialog>
  );
}
