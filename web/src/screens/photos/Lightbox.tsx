import { ArrowExportRegular, ChevronLeftRegular, ChevronRightRegular, CloudRegular, DismissRegular, InfoRegular, PlayFilled } from '@fluentui/react-icons';
import { Dialog as RadixDialog } from 'radix-ui';
import { useEffect, useRef, useState } from 'react';
import type { PhotoAsset } from '../../bridge/types';
import { useI18n } from '../../i18n';
import styles from './Lightbox.module.css';

const STRIP_RADIUS = 24;

/** Full-window viewer with a filmstrip and an info panel. ←/→ move (mirrored in RTL), I toggles info. */
export function Lightbox({
  assets,
  index,
  onIndex,
  onClose,
  photoUrl,
  thumbUrl,
  onExport,
  isWebView,
}: {
  assets: readonly PhotoAsset[];
  index: number;
  onIndex(index: number): void;
  onClose(): void;
  photoUrl(asset: PhotoAsset): string;
  thumbUrl(asset: PhotoAsset): string;
  onExport(asset: PhotoAsset): void;
  isWebView: boolean;
}) {
  const { t, date, bytes, n, duration, digits, dir } = useI18n();
  const [info, setInfo] = useState(true);
  const strip = useRef<HTMLDivElement>(null);
  const asset = assets[index];

  useEffect(() => {
    strip.current?.querySelector('[aria-current="true"]')?.scrollIntoView({ inline: 'center', block: 'nearest' });
  }, [index]);

  if (!asset) return null;
  const go = (delta: number) => onIndex(Math.max(0, Math.min(assets.length - 1, index + delta)));
  const from = Math.max(0, index - STRIP_RADIUS);
  const to = Math.min(assets.length, index + STRIP_RADIUS + 1);
  const PrevIcon = dir === 'rtl' ? ChevronRightRegular : ChevronLeftRegular;
  const NextIcon = dir === 'rtl' ? ChevronLeftRegular : ChevronRightRegular;
  const megapixels = asset.width && asset.height ? n((asset.width * asset.height) / 1e6, { maximumFractionDigits: 0 }) : null;

  return (
    <RadixDialog.Root open onOpenChange={(open) => (open ? undefined : onClose())}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className={styles.overlay} />
        <RadixDialog.Content
          className={styles.viewer}
          aria-label={t('photos.viewer')}
          data-testid="lightbox"
          onKeyDown={(e) => {
            if (e.key === 'ArrowLeft') go(dir === 'rtl' ? 1 : -1);
            else if (e.key === 'ArrowRight') go(dir === 'rtl' ? -1 : 1);
            else if (e.key === 'Home') onIndex(0);
            else if (e.key === 'End') onIndex(assets.length - 1);
            else if (e.key.toLowerCase() === 'i') setInfo((v) => !v);
            else if (e.key === ' ') {
              e.preventDefault();
              onClose();
            }
          }}
        >
          <RadixDialog.Title className="visually-hidden">{asset.fileName}</RadixDialog.Title>
          <RadixDialog.Description className="visually-hidden">{t('photos.viewer')}</RadixDialog.Description>
          <div className={styles.bar}>
            <div className={styles.barTitle}>
              <span className={styles.fileName}>{asset.fileName}</span>
              <span className={styles.fileMeta}>
                {asset.takenAt ? date(asset.takenAt, 'long') : ''} · {n(index + 1)} / {n(assets.length)}
              </span>
            </div>
            <button type="button" className={styles.mediaButton} onClick={() => onExport(asset)} disabled={asset.inCloudOnly}>
              <ArrowExportRegular aria-hidden /> {t('common.export')}
            </button>
            <button type="button" className={styles.mediaButton} aria-pressed={info} aria-label={t('photos.viewer.info')} onClick={() => setInfo((v) => !v)}>
              <InfoRegular aria-hidden />
            </button>
            <RadixDialog.Close asChild>
              <button type="button" className={styles.mediaButton} aria-label={t('common.close')}>
                <DismissRegular aria-hidden />
              </button>
            </RadixDialog.Close>
          </div>

          <div className={styles.stage}>
            {asset.kind === 'video' && isWebView ? (
              <video key={asset.id} className={`${styles.image} no-mirror`} src={photoUrl(asset)} poster={thumbUrl(asset)} controls />
            ) : (
              <>
                <img key={asset.id} className={`${styles.image} no-mirror`} src={photoUrl(asset)} alt={asset.fileName} />
                {asset.kind === 'video' ? (
                  <span className={styles.play} aria-label={t('photos.viewer.play')}>
                    <PlayFilled />
                  </span>
                ) : null}
              </>
            )}
            {asset.kind === 'livePhoto' ? <span className={styles.liveBadge}>{t('photos.live')}</span> : null}
            <button type="button" className={`${styles.mediaButton} ${styles.nav} ${styles.prev}`} aria-label={t('photos.viewer.previous')} onClick={() => go(-1)} disabled={index === 0}>
              <PrevIcon aria-hidden />
            </button>
            <button
              type="button"
              className={`${styles.mediaButton} ${styles.nav} ${styles.next}`}
              aria-label={t('photos.viewer.next')}
              onClick={() => go(1)}
              disabled={index === assets.length - 1}
            >
              <NextIcon aria-hidden />
            </button>
          </div>

          {info ? (
            <aside className={styles.info} aria-label={t('photos.viewer.info')}>
              <div className={styles.infoTitle}>{t('photos.viewer.info')}</div>
              <dl className={styles.facts}>
                <div>
                  <dt>{t('photos.viewer.file')}</dt>
                  <dd className="no-mirror">{asset.fileName}</dd>
                </div>
                <div>
                  <dt>{t('photos.viewer.kind')}</dt>
                  <dd>{t(`photos.kind.${asset.kind}`)}</dd>
                </div>
                {asset.takenAt ? (
                  <div>
                    <dt>{t('photos.viewer.date')}</dt>
                    <dd>{date(asset.takenAt, 'long')}</dd>
                  </div>
                ) : null}
                <div>
                  <dt>{t('photos.viewer.size')}</dt>
                  <dd>{bytes(asset.size)}</dd>
                </div>
                {asset.width && asset.height ? (
                  <div>
                    <dt>{t('photos.viewer.dimensions')}</dt>
                    <dd>{t('photos.megapixels', { w: digits(String(asset.width)), h: digits(String(asset.height)), mp: megapixels ?? '' })}</dd>
                  </div>
                ) : null}
                {asset.durationSeconds !== null ? (
                  <div>
                    <dt>{t('photos.viewer.duration')}</dt>
                    <dd className="no-mirror">{duration(asset.durationSeconds)}</dd>
                  </div>
                ) : null}
                {asset.inCloudOnly ? (
                  <div className={styles.cloud}>
                    <CloudRegular aria-hidden />
                    <span>{t('photos.inCloudHint')}</span>
                  </div>
                ) : null}
              </dl>
            </aside>
          ) : null}

          <div className={styles.strip} ref={strip}>
            {assets.slice(from, to).map((a, i) => (
              <button
                key={a.id}
                type="button"
                className={styles.thumb}
                aria-current={from + i === index}
                aria-label={a.fileName}
                onClick={() => onIndex(from + i)}
              >
                <img src={thumbUrl(a)} alt="" loading="lazy" />
              </button>
            ))}
          </div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
