import { ArrowClockwiseRegular, PhoneArrowRightRegular, SaveRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { ProfileMode } from '../../bridge/toolbox';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { Banner, Checkbox, RadioCards, Spinner, TextField } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n, type ToolboxKey } from './i18n';
import styles from './Toolbox.module.css';

const CARRIERS = { mci: 'mcinet', irancell: 'mtnirancell', rightel: 'rightel' } as const;

type Carrier = keyof typeof CARRIERS | 'custom';

const APN_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$/;

/** Steps the user takes on the phone; a phone that isn't supervised won't install a profile by itself. */
const STEPS: ToolboxKey[] = ['profile.step1', 'profile.step2', 'profile.step3', 'profile.step4'];

/** Must match CellularProfile.Identifier on the host: it marks the profile this tool made in the phone's list. */
const OUR_PROFILE = 'local.ipv6-profile.cellular';

export function ProfileTool({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const { tt } = useToolboxI18n();
  const [carrier, setCarrier] = useState<Carrier>('mci');
  const [customApn, setCustomApn] = useState('');
  const [mode, setMode] = useState<ProfileMode>('both');
  const [attachApn, setAttachApn] = useState(true);
  const [xlat, setXlat] = useState(false);
  const [busy, setBusy] = useState<'send' | 'save' | null>(null);
  const [sent, setSent] = useState(false);

  const apn = carrier === 'custom' ? customApn.trim() : CARRIERS[carrier];
  const valid = APN_PATTERN.test(apn);
  const options = { apn, mode, attachApn, xlat464: mode === 'ipv6' && xlat };

  const listKey = ['profile.list', device.id];
  const installed = useQuery({ queryKey: listKey, queryFn: () => client.call('profile.list', { deviceId: device.id }) });
  const preview = useQuery({
    queryKey: ['profile.preview', options],
    queryFn: () => client.call('profile.preview', options),
    enabled: valid,
  });

  const send = async () => {
    setBusy('send');
    setSent(false);
    try {
      await client.call('profile.send', { deviceId: device.id, ...options });
      setSent(true);
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
    }
  };

  const save = async () => {
    setBusy('save');
    try {
      const { path } = await client.call('profile.save', options);
      toast({ tone: 'success', title: tt('profile.saved'), action: { label: tt('profile.showFile'), run: () => void actions.revealInExplorer(path) } });
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={styles.tool} data-testid="profile-tool" style={{ overflow: 'auto' }}>
      <span className={styles.muted}>{tt('profile.intro')}</span>

      <div className={styles.formRow}>
        <div className={styles.optionGroup} style={{ flex: '1 1 280px' }}>
          <span className={styles.optionLabel}>{tt('profile.carrier')}</span>
          <RadioCards
            value={carrier}
            onChange={setCarrier}
            label={tt('profile.carrier')}
            options={[
              { value: 'mci', label: tt('profile.carrier.mci'), hint: CARRIERS.mci },
              { value: 'irancell', label: tt('profile.carrier.irancell'), hint: CARRIERS.irancell },
              { value: 'rightel', label: tt('profile.carrier.rightel'), hint: CARRIERS.rightel },
              { value: 'custom', label: tt('profile.carrier.custom') },
            ]}
          />
          {carrier === 'custom' ? (
            <TextField
              label={tt('profile.apn')}
              value={customApn}
              onChange={setCustomApn}
              error={customApn.trim() !== '' && !valid ? tt('profile.badApn') : null}
            />
          ) : null}
        </div>
        <div className={styles.optionGroup} style={{ flex: '1 1 280px' }}>
          <span className={styles.optionLabel}>{tt('profile.mode')}</span>
          <RadioCards
            value={mode}
            onChange={setMode}
            label={tt('profile.mode')}
            options={[
              { value: 'both', label: tt('profile.mode.both'), hint: tt('profile.mode.bothHint') },
              { value: 'ipv6', label: tt('profile.mode.ipv6'), hint: tt('profile.mode.ipv6Hint') },
            ]}
          />
          <label className={styles.checkRow}>
            <Checkbox checked={attachApn} onChange={setAttachApn} label={tt('profile.attach')} />
            <span>{tt('profile.attach')}</span>
          </label>
          {mode === 'ipv6' ? (
            <label className={styles.checkRow}>
              <Checkbox checked={xlat} onChange={setXlat} label={tt('profile.xlat')} />
              <span>{tt('profile.xlat')}</span>
            </label>
          ) : null}
        </div>
      </div>

      <div className={styles.toolbar}>
        <Button variant="accent" icon={<PhoneArrowRightRegular />} disabled={!valid || busy !== null} loading={busy === 'send'} onClick={() => void send()} data-testid="profile-send">
          {tt('profile.send')}
        </Button>
        <Button icon={<SaveRegular />} disabled={!valid || busy !== null} loading={busy === 'save'} onClick={() => void save()} data-testid="profile-save">
          {tt('profile.save')}
        </Button>
      </div>

      {sent ? (
        <Banner tone="success" title={tt('profile.sent')}>
          {tt('profile.sentBody')}
        </Banner>
      ) : null}

      <div className={cx(styles.panel, styles.section)}>
        <span className={styles.sectionTitle}>{tt('profile.steps')}</span>
        <ol className={styles.steps}>
          {STEPS.map((step) => (
            <li key={step}>{tt(step)}</li>
          ))}
        </ol>
        <span className={cx(styles.muted, styles.small)}>{tt('profile.caveats')}</span>
      </div>

      <div className={cx(styles.panel, styles.section)} data-testid="profile-installed">
        <div className={styles.toolbar}>
          <span className={styles.sectionTitle}>{tt('profile.installed')}</span>
          <span className={styles.toolbarSpacer} />
          <Button icon={<ArrowClockwiseRegular />} disabled={installed.isFetching} onClick={() => void queryClient.invalidateQueries({ queryKey: listKey })} data-testid="profile-refresh">
            {tt('profile.refresh')}
          </Button>
        </div>
        {installed.isPending ? (
          <Spinner />
        ) : installed.isError ? (
          <span className={styles.muted}>{tt('profile.listFailed')}</span>
        ) : installed.data.profiles.length === 0 ? (
          <span className={styles.muted}>{tt('profile.none')}</span>
        ) : (
          installed.data.profiles.map((profile) => (
            <div key={profile.identifier} className={styles.deviceLine} data-testid="profile-row">
              <span className={styles.cell} style={{ flex: 1 }}>
                {profile.name}
              </span>
              <span className={cx(styles.muted, styles.small)}>{profile.identifier === OUR_PROFILE ? tt('profile.ours') : profile.organization}</span>
            </div>
          ))
        )}
        <span className={cx(styles.muted, styles.small)}>{tt('profile.oneOnly')}</span>
      </div>

      <details className={cx(styles.panel, styles.fold)}>
        <summary>{tt('profile.preview')}</summary>
        <pre className={styles.profileText} dir="ltr" data-testid="profile-preview">
          {valid ? preview.data?.text : tt('profile.badApn')}
        </pre>
      </details>
    </div>
  );
}
