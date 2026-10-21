// Why the PC can't delete this phone's photos, and the fastest way on the phone (round 4, photodelete).
// The Photos screen shows it behind "Why can't I delete?"; Clean-up shows it above the media sections.

import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import type { PhotoDeleteSupport } from '../../bridge/photoDeleteTypes';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Banner, Spinner } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './PhotoDelete.module.css';
import { useRecheckPhotoDelete, type BlockReason } from './photoDelete';

const TITLE: Record<BlockReason, MessageKey> = {
  iCloudPhotos: 'photoDelete.blocked.title.iCloudPhotos',
  readOnly: 'photoDelete.blocked.title.readOnly',
  locked: 'photoDelete.blocked.title.locked',
  notCamera: 'photoDelete.blocked.title.notCamera',
  converted: 'photoDelete.blocked.title.converted',
  unsupported: 'photoDelete.blocked.title.unsupported',
  checking: 'photoDelete.blocked.title.checking',
};

const BODY: Record<BlockReason, MessageKey> = {
  iCloudPhotos: 'photoDelete.blocked.body.iCloudPhotos',
  readOnly: 'photoDelete.blocked.body.readOnly',
  locked: 'photoDelete.blocked.body.locked',
  notCamera: 'photoDelete.blocked.body.notCamera',
  converted: 'photoDelete.blocked.body.converted',
  unsupported: 'photoDelete.blocked.body.unsupported',
  checking: 'photoDelete.blocked.body.checking',
};

export function reasonOf(support: PhotoDeleteSupport | undefined): BlockReason {
  return !support ? 'checking' : support.reason === 'none' ? 'checking' : support.reason;
}

/** The explanation itself: what's wrong, what to do about it, and the fastest way on the phone. */
function Explanation({ device, support }: { device: DeviceSummary; support: PhotoDeleteSupport | undefined }) {
  const { t } = useI18n();
  const reason = reasonOf(support);
  const ios = device.platform === 'ios';
  return (
    <div className={styles.notice}>
      <p>{t(BODY[reason])}</p>
      {ios && reason !== 'checking' ? (
        <div className={styles.fastest} data-testid="photo-delete-fastest">
          <span className={styles.fastestTitle}>{t('photoDelete.fastest.title')}</span>
          <span>{t('photoDelete.fastest.steps')}</span>
          <span className={styles.hint}>{t('photoDelete.fastest.duplicates')}</span>
          <span className={styles.hint}>{t('photoDelete.fastest.copy')}</span>
        </div>
      ) : null}
    </div>
  );
}

function RecheckButton({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const recheck = useRecheckPhotoDelete(device);
  return (
    <Button size="small" icon={<ArrowClockwiseRegular />} loading={recheck.isPending} onClick={() => recheck.mutate()} data-testid="photo-delete-recheck">
      {t('photoDelete.recheck')}
    </Button>
  );
}

/** A banner: the reason, the fastest way on the phone, "Check again" and what the check saw. */
export function PhotoDeleteNotice({ device, support }: { device: DeviceSummary; support: PhotoDeleteSupport | undefined }) {
  const { t } = useI18n();
  const reason = reasonOf(support);
  return (
    <Banner
      tone="info"
      icon={reason === 'checking' ? <Spinner /> : undefined}
      title={t(TITLE[reason])}
      details={support?.detail}
      actions={reason === 'checking' ? null : <RecheckButton device={device} />}
    >
      <div data-testid="photo-delete-notice" data-reason={reason}>
        <Explanation device={device} support={support} />
      </div>
    </Banner>
  );
}

/** The same explanation in a dialog, behind the selection bar's "Why can't I delete?". */
export function PhotoDeleteWhyDialog({
  device,
  support,
  open,
  onOpenChange,
}: {
  device: DeviceSummary;
  support: PhotoDeleteSupport | undefined;
  open: boolean;
  onOpenChange(open: boolean): void;
}) {
  const { t } = useI18n();
  const reason = reasonOf(support);
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={t(TITLE[reason])}
      footer={
        <>
          {reason === 'checking' ? null : <RecheckButton device={device} />}
          <Button variant="accent" onClick={() => onOpenChange(false)} autoFocus>
            {t('common.close')}
          </Button>
        </>
      }
    >
      <div data-testid="photo-delete-why" data-reason={reason}>
        <Explanation device={device} support={support} />
        {support?.detail ? (
          <p className={styles.hint} dir="auto">
            {support.detail}
          </p>
        ) : null}
      </div>
    </Dialog>
  );
}
