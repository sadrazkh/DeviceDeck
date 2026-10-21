// The in-app banner (round 4): the first time an update is ready (or, for the zip, out on GitHub), once per version, in
// the toast stack under the title bar. It stays until it is closed or acted on; the title-bar button stays after it.

import { ArrowCircleDownRegular, ArrowSyncCircleFilled, DismissRegular } from '@fluentui/react-icons';
import { motion } from 'framer-motion';
import { useEffect, useState } from 'react';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useTransfers } from '../app/queries';
import { Button } from '../components/Button';
import { useI18n } from '../i18n';
import { overallProgress } from '../state/transfers';
import { useUpdateStatus } from './desktop';
import toastStyles from './Toaster.module.css';
import styles from './Update.module.css';
import { bannerSeen, markBannerSeen, offerOf, useWhatsNew } from './updates';

export function UpdateBanner() {
  const { t, digits } = useI18n();
  const client = useClient();
  const actions = useActions();
  const status = useUpdateStatus().data;
  const show = useWhatsNew((s) => s.show);
  const busy = overallProgress(useTransfers().data ?? []).active > 0;
  const [seen, setSeen] = useState(bannerSeen);
  const offer = offerOf(status);
  // What's new opened for this version (from the title bar, Settings or here): the update has been seen.
  const opened = useWhatsNew((s) => s.request?.version);
  const offered = offer?.version;
  useEffect(() => {
    if (!offered || opened !== offered) return;
    markBannerSeen(offered);
    setSeen(offered);
  }, [offered, opened]);
  if (!offer || seen === offer.version) return null;

  const version = digits(offer.version);
  const done = () => {
    markBannerSeen(offer.version);
    setSeen(offer.version);
  };
  const act = async () => {
    done();
    // With copies running, the dialog says what a restart would stop.
    if (offer.kind === 'ready' && busy) {
      show({ version: offer.version, reason: 'offer' });
      return;
    }
    try {
      if (offer.kind === 'ready') await client.call('app.applyUpdate');
      else await client.call('app.openReleasePage', { version: offer.version });
    } catch (error) {
      actions.fail(error);
    }
  };

  return (
    <motion.li
      layout
      className={`${toastStyles.toast} ${styles.banner}`}
      initial={{ opacity: 0, y: -12, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.2, ease: [0.2, 0, 0, 1] }}
      role="status"
      data-testid="update-banner"
    >
      <span className={styles.bannerIcon} aria-hidden>
        {offer.kind === 'ready' ? <ArrowSyncCircleFilled /> : <ArrowCircleDownRegular />}
      </span>
      <div className={toastStyles.body}>
        <div className={toastStyles.title}>
          {offer.kind === 'ready' ? t('update.banner.ready', { version }) : t('update.banner.available', { version })}
        </div>
        <div className={toastStyles.text}>{offer.kind === 'ready' ? t('update.banner.readyBody') : t('update.banner.availableBody')}</div>
        <div className={toastStyles.actions}>
          <Button
            size="small"
            onClick={() => {
              done();
              show({ version: offer.version, reason: 'offer' });
            }}
          >
            {t('update.banner.whatsNew')}
          </Button>
          <Button size="small" variant="accent" onClick={() => void act()} data-testid="update-banner-act">
            {offer.kind === 'ready' ? t('update.whatsNew.restart') : t('update.whatsNew.download')}
          </Button>
        </div>
      </div>
      <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={t('common.close')} onClick={done} />
    </motion.li>
  );
}
