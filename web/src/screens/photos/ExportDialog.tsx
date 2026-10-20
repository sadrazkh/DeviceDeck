import { FolderOpenRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { DeviceSummary, FolderPattern, PhotoAsset } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useSettings, useUpdateSettings } from '../../app/queries';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Banner, RadioCards } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useUi } from '../../state/ui';
import styles from './Photos.module.css';

/** Asks "Keep HEIC or convert to JPG", the folder pattern and where to; then starts the export job. */
export function ExportDialog({ device, assets, onClose, onStarted }: { device: DeviceSummary; assets: PhotoAsset[]; onClose(): void; onStarted(): void }) {
  const { t, bytes } = useI18n();
  const settings = useSettings().data;
  const updateSettings = useUpdateSettings();
  const actions = useActions();
  const [convert, setConvert] = useState<'heic' | 'jpg'>(settings?.convertHeic ? 'jpg' : 'heic');
  const [pattern, setPattern] = useState<FolderPattern>(settings?.photoFolderPattern ?? 'yyyy/MM');
  const [destination, setDestination] = useState(settings?.photoExportFolder ?? 'D:\\Photos');
  const [busy, setBusy] = useState(false);

  const exportable = assets.filter((a) => !a.inCloudOnly);
  const skipped = assets.length - exportable.length;
  const hasHeic = exportable.some((a) => /\.heic$/i.test(a.fileName));
  const size = exportable.reduce((n, a) => n + (convert === 'jpg' && /\.heic$/i.test(a.fileName) ? a.size * 1.6 : a.size), 0);
  const newest = exportable[0]?.takenAt ? new Date(exportable[0].takenAt) : new Date();
  const example = `${newest.getFullYear()}/${String(newest.getMonth() + 1).padStart(2, '0')}`;

  const start = async () => {
    setBusy(true);
    const convertHeic = hasHeic && convert === 'jpg';
    const jobId = await actions.exportPhotos(device, exportable.map((a) => a.id), destination, convertHeic, pattern);
    setBusy(false);
    if (!jobId) return;
    updateSettings.mutate({ photoExportFolder: destination, photoFolderPattern: pattern, convertHeic });
    useUi.getState().setDrawerOpen(true);
    onStarted();
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={t('photos.export.title', { count: exportable.length })}
      description={<span className={styles.exportSummary}>{t('photos.export.size', { size: bytes(size) })}</span>}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void start()} loading={busy} disabled={exportable.length === 0} data-testid="export-start">
            {t('photos.export.start')}
          </Button>
        </>
      }
    >
      {skipped > 0 ? <Banner tone="info">{t('photos.export.cloudSkipped', { count: skipped })}</Banner> : null}
      {hasHeic ? (
        <div className={styles.exportSection}>
          <span className={styles.exportLabel}>{t('photos.export.format')}</span>
          <RadioCards
            label={t('photos.export.format')}
            value={convert}
            onChange={setConvert}
            options={[
              { value: 'heic', label: t('photos.export.keepHeic'), hint: t('photos.export.keepHeicHint') },
              { value: 'jpg', label: t('photos.export.toJpg'), hint: t('photos.export.toJpgHint') },
            ]}
          />
        </div>
      ) : null}
      <div className={styles.exportSection}>
        <span className={styles.exportLabel}>{t('photos.export.folders')}</span>
        <RadioCards
          label={t('photos.export.folders')}
          value={pattern === '' ? 'single' : 'month'}
          onChange={(v) => setPattern(v === 'single' ? '' : 'yyyy/MM')}
          options={[
            { value: 'month', label: t('photos.export.byMonth', { example: `\u2066${example}\u2069` }) },
            { value: 'single', label: t('photos.export.single') },
          ]}
        />
      </div>
      <div className={styles.exportSection}>
        <span className={styles.exportLabel}>{t('photos.export.destination')}</span>
        <div className={styles.destination}>
          <span className={styles.path} title={destination}>
            {destination}
          </span>
          <Button
            icon={<FolderOpenRegular />}
            onClick={async () => {
              const picked = await actions.pickFolder(t('photos.export.destination'));
              if (picked) setDestination(picked);
            }}
          >
            {t('common.change')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
