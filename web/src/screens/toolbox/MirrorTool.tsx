import { PhoneDesktopRegular, WindowNewRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import { useActions } from '../../app/actions';
import { useBridge } from '../../app/bridgeContext';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Banner } from '../../components/ui';
import { useToolboxI18n } from './i18n';
import styles from './Toolbox.module.css';

export function MirrorTool({ device }: { device: DeviceSummary }) {
  const { client } = useBridge();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [opened, setOpened] = useState(false);
  const [busy, setBusy] = useState(false);
  const preview = client.kind === 'mock';

  const open = async () => {
    setBusy(true);
    try {
      await client.call('mirror.open', { deviceId: device.id });
      // In the browser preview there is no host to open a window: open the page ourselves.
      if (preview) window.open(`./mirror.html?device=${encodeURIComponent(device.id)}${location.search.replace('?', '&')}`, '_blank', 'popup,width=420,height=860');
      setOpened(true);
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(false);
    }
  };

  if (device.platform === 'ios') {
    return (
      <div className={styles.tool} data-testid="mirror-tool">
        <Banner tone="info">{tt('mirror.iphone')}</Banner>
      </div>
    );
  }

  return (
    <div className={styles.tool} data-testid="mirror-tool">
      <div className={cx(styles.panel, styles.mirrorHero)}>
        <div className={styles.phoneFrame} aria-hidden>
          <PhoneDesktopRegular />
        </div>
        <div className={styles.steps}>
          <span className={styles.sectionTitle}>{device.name}</span>
          <span>{tt('mirror.how')}</span>
          <span className={styles.muted}>{tt('mirror.needs')}</span>
          {preview ? <span className={cx(styles.muted, styles.small)}>{tt('mirror.mockNote')}</span> : null}
          <div className={styles.toolbar}>
            <Button variant="accent" size="large" icon={<WindowNewRegular />} loading={busy} onClick={() => void open()} data-testid="mirror-open">
              {tt('mirror.open')}
            </Button>
            {opened ? <span className={styles.muted}>{tt('mirror.opened')}</span> : null}
          </div>
        </div>
      </div>
    </div>
  );
}
