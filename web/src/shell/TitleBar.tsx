import { ChevronDownRegular, DismissRegular, MaximizeRegular, PhoneRegular, SearchRegular, SubtractRegular } from '@fluentui/react-icons';
import { useBridge } from '../app/bridgeContext';
import { useDevices, useSelectedDevice } from '../app/queries';
import { ConnectionBadge, DeviceStateDot, modelName } from '../components/DeviceBits';
import { Menu } from '../components/Menu';
import { Kbd } from '../components/ui';
import { useI18n } from '../i18n';
import { useUi } from '../state/ui';
import styles from './Shell.module.css';
import { TransferPill } from './TransferPill';
import { UpdateButton } from './UpdateButton';

export function TitleBar() {
  const { t } = useI18n();
  const { client } = useBridge();
  const devices = useDevices().data ?? [];
  const selected = useSelectedDevice();
  const selectDevice = useUi((s) => s.selectDevice);
  const openPalette = useUi((s) => s.setPaletteOpen);

  return (
    <header className={`${styles.titlebar} drag-region`}>
      <span className={styles.appMark} aria-hidden>
        <PhoneRegular />
      </span>
      <span className="visually-hidden">{t('app.name')}</span>

      <Menu
        trigger={
          <button type="button" className={`${styles.switcher} no-drag`} aria-label={t('shell.chooseDevice')} data-testid="device-switcher">
            {selected ? <DeviceStateDot device={selected} /> : null}
            <span className={styles.switcherName}>{selected ? selected.name : t('shell.noDevice')}</span>
            {selected ? <ConnectionBadge device={selected} /> : null}
            <span className={styles.switcherChevron} aria-hidden>
              <ChevronDownRegular />
            </span>
          </button>
        }
        items={
          devices.length === 0
            ? [{ key: 'none', label: t('shell.noDevice'), disabled: true, onSelect: () => {} }]
            : devices.map((d) => ({
                key: d.id,
                label: d.name,
                hint: `${modelName(d)} · ${t(`device.state.${d.state}`)}`,
                icon: <DeviceStateDot device={d} />,
                onSelect: () => selectDevice(d.id),
              }))
        }
      />

      <div className={styles.titleSpacer} />
      <button type="button" className={`${styles.search} no-drag`} onClick={() => openPalette(true)} aria-keyshortcuts="Control+K" title={t('shell.search')}>
        <span className={styles.searchIcon} aria-hidden>
          <SearchRegular />
        </span>
        <span className={styles.searchText}>{t('shell.search')}</span>
        <Kbd>{t('shell.searchShortcut')}</Kbd>
      </button>
      <div className={styles.titleSpacer} />

      <UpdateButton />
      <TransferPill />

      {client.kind === 'mock' ? (
        <div className={styles.captions} aria-hidden>
          <span className={styles.caption}>
            <SubtractRegular />
          </span>
          <span className={styles.caption}>
            <MaximizeRegular />
          </span>
          <span className={styles.caption}>
            <DismissRegular />
          </span>
        </div>
      ) : null}
    </header>
  );
}
