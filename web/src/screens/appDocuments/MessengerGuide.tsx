import { ChatRegular, DismissRegular, InfoRegular } from '@fluentui/react-icons';
import { create } from 'zustand';
import { useApps } from '../../app/queries';
import { useClient } from '../../app/bridgeContext';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Tooltip } from '../../components/ui';
import { useI18n } from '../../i18n';
import { useUi } from '../../state/ui';
import { useContactsTab } from '../contacts/contactsTab';
import styles from './MessengerGuide.module.css';

const TELEGRAM = 'ph.telegra.Telegraph';
const WHATSAPP = ['net.whatsapp.WhatsApp', 'net.whatsapp.WhatsAppSMB'];

/** Hidden for the rest of the session once the user closes it. */
const useGuide = create<{ hidden: boolean; hide(): void }>((set) => ({ hidden: false, hide: () => set({ hidden: true }) }));

/**
 * Round 4: Telegram and WhatsApp don't share their files with a computer and iOS keeps each app's files sealed, so
 * App documents on an iPhone says how to get them instead (nothing here gets around that). Shown when either app is
 * installed; as a plain tip for both when the apps list can't be read. Android shows Telegram's folders already.
 */
export function MessengerGuide({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const client = useClient();
  const apps = useApps(device.platform === 'ios' ? device : null, false);
  const hidden = useGuide((s) => s.hidden);
  const navigate = useUi((s) => s.navigate);
  if (device.platform !== 'ios' || hidden || apps.isLoading) return null;

  const ids = new Set((apps.data ?? []).map((a) => a.id));
  const known = apps.data !== undefined;
  const telegram = !known || ids.has(TELEGRAM);
  const whatsApp = !known || WHATSAPP.some((id) => ids.has(id));
  if (!telegram && !whatsApp) return null;

  const icon = (appId: string) => (known && ids.has(appId) ? <img className={styles.appIcon} src={client.images.icon(device.id, appId)} alt="" loading="lazy" /> : <ChatRegular className={styles.glyph} aria-hidden />);
  const openWhatsApp = () => {
    useContactsTab.getState().request('whatsapp');
    navigate('contacts');
  };

  return (
    <section className={styles.guide} aria-labelledby="messenger-guide-title" data-testid="messenger-guide">
      <div className={styles.head}>
        <InfoRegular className={styles.headIcon} aria-hidden />
        <h2 id="messenger-guide-title" className={styles.title}>
          {t('appDocs.guide.title')}
        </h2>
        <Tooltip content={t('appDocs.guide.dismiss')}>
          <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={t('appDocs.guide.dismiss')} onClick={() => useGuide.getState().hide()} />
        </Tooltip>
      </div>
      <p className={styles.intro}>{t('appDocs.guide.intro')}</p>
      <div className={styles.apps}>
        {telegram ? (
          <div className={styles.app} data-testid="messenger-guide-telegram">
            <div className={styles.appHead}>
              {icon(TELEGRAM)}
              <h3 className={styles.appName}>{t('appDocs.guide.telegram')}</h3>
            </div>
            <ul className={styles.steps}>
              <li>{t('appDocs.guide.telegram.all')}</li>
              <li>{t('appDocs.guide.telegram.some')}</li>
            </ul>
          </div>
        ) : null}
        {whatsApp ? (
          <div className={styles.app} data-testid="messenger-guide-whatsapp">
            <div className={styles.appHead}>
              {icon(WHATSAPP.find((id) => ids.has(id)) ?? WHATSAPP[0]!)}
              <h3 className={styles.appName}>{t('appDocs.guide.whatsapp')}</h3>
            </div>
            <p className={styles.body}>{t('appDocs.guide.whatsapp.body')}</p>
            <div>
              <Button size="small" onClick={openWhatsApp} data-testid="messenger-guide-open">
                {t('appDocs.guide.whatsapp.open')}
              </Button>
            </div>
          </div>
        ) : null}
      </div>
    </section>
  );
}
