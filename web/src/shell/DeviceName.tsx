// The phone's name on Overview, editable when the phone allows it (device.rename).

import { CheckmarkRegular, DismissRegular, EditRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { DeviceSummary } from '../bridge/types';
import { useClient } from '../app/bridgeContext';
import { Button } from '../components/Button';
import { describeError } from '../components/ErrorState';
import { Tooltip } from '../components/ui';
import { useI18n } from '../i18n';
import styles from './DeviceName.module.css';

const MAX = 64;

export function DeviceName({ device }: { device: DeviceSummary }) {
  const { t } = useI18n();
  const client = useClient();
  const [draft, setDraft] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const canRename = device.capabilities.includes('rename') && device.state === 'ready';

  const save = async () => {
    const name = (draft ?? '').trim();
    if (!name || name.length > MAX) {
      setError(t('device.rename.invalid', { max: MAX }));
      return;
    }
    if (name === device.name) {
      setDraft(null);
      return;
    }
    setBusy(true);
    try {
      // The host announces the new name with device.changed; the list follows from there.
      await client.call('device.rename', { deviceId: device.id, name });
      setDraft(null);
      setError(null);
    } catch (e) {
      setError(describeError(e, t).message);
    } finally {
      setBusy(false);
    }
  };

  if (draft === null) {
    return (
      <span className={styles.name}>
        <span className="t-title" data-testid="device-name">
          {device.name}
        </span>
        {canRename ? (
          <Tooltip content={t('device.rename')}>
            <Button
              variant="subtle"
              size="small"
              icon={<EditRegular />}
              aria-label={t('device.rename')}
              onClick={() => {
                setDraft(device.name);
                setError(null);
              }}
              data-testid="rename-device"
            />
          </Tooltip>
        ) : null}
      </span>
    );
  }

  return (
    <span className={styles.editor}>
      <span className={styles.row}>
        <input
          className={styles.input}
          value={draft}
          maxLength={MAX}
          autoFocus
          aria-label={t('device.rename')}
          aria-invalid={error ? true : undefined}
          spellCheck={false}
          onChange={(e) => {
            setDraft(e.target.value);
            setError(null);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') void save();
            if (e.key === 'Escape') setDraft(null);
          }}
          data-testid="rename-input"
        />
        <Button variant="accent" size="small" icon={<CheckmarkRegular />} aria-label={t('device.rename.save')} disabled={busy} onClick={() => void save()} />
        <Button variant="subtle" size="small" icon={<DismissRegular />} aria-label={t('common.cancel')} onClick={() => setDraft(null)} />
      </span>
      {error ? <span className={styles.error}>{error}</span> : <span className={styles.hint}>{t('device.rename.hint')}</span>}
    </span>
  );
}
