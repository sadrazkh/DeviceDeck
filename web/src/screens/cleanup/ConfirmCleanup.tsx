import { useEffect, useState } from 'react';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Switch } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './Cleanup.module.css';
import type { CleanupKind, CleanupRequest } from './useCleanup';

export interface PlanRow {
  key: string;
  label: string;
  detail?: string;
  size: number;
}

/** A clean-up the user is about to confirm: exactly these rows, this total. */
export interface CleanupPlan {
  kind: CleanupKind;
  title: string;
  rows: PlanRow[];
  /** Caches: the phone's last count, so the total is "about". */
  approximate?: boolean;
  /** Replaces the "Frees …" line (caches the phone hasn't counted). */
  description?: string;
  confirmLabel: string;
  notes: string[];
  /** Offer "copy to this PC first": the label and the folder it starts with. */
  copy: { label: MessageKey; folder: string } | null;
  request(copyFirstTo: string | null): CleanupRequest;
}

/**
 * The one door to every delete: lists what goes and what it frees, offers a copy to the PC first,
 * and only then runs. While it runs it can't be dismissed, so the result always lands here.
 */
export function ConfirmCleanup({
  plan,
  device,
  onClose,
  run,
}: {
  plan: CleanupPlan | null;
  device: DeviceSummary;
  onClose(): void;
  run(request: CleanupRequest): Promise<boolean>;
}) {
  const { t, bytes } = useI18n();
  const actions = useActions();
  const [copy, setCopy] = useState(true);
  const [folder, setFolder] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (plan) {
      setCopy(plan.copy !== null);
      setFolder(plan.copy?.folder ?? '');
      setBusy(false);
    }
  }, [plan]);

  if (!plan) return null;
  const total = plan.rows.reduce((n, r) => n + r.size, 0);
  const copying = plan.copy !== null && copy && folder.length > 0;

  const confirm = async () => {
    setBusy(true);
    const ok = await run(plan.request(copying ? folder : null));
    setBusy(false);
    if (ok) onClose();
  };

  return (
    <Dialog
      open
      wide
      dismissible={!busy}
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={plan.title}
      description={plan.description ?? t(plan.approximate ? 'cleanup.confirm.freesAbout' : 'cleanup.confirm.frees', { size: bytes(total) })}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </Button>
          <Button variant="dangerSolid" onClick={() => void confirm()} loading={busy} data-testid="cleanup-confirm" autoFocus>
            {busy ? t(copying ? 'cleanup.confirm.busyCopy' : 'cleanup.confirm.busy') : plan.confirmLabel}
          </Button>
        </>
      }
    >
      <div className={styles.confirmBody} data-testid="cleanup-confirm-dialog" data-device={device.id}>
        {plan.rows.length > 0 ? (
          <div>
            <ul className={styles.confirmList} aria-label={plan.title}>
              {plan.rows.map((row) => (
                <li key={row.key} className={styles.confirmRow} data-testid="cleanup-confirm-row">
                  <span className={styles.text}>
                    <span className={styles.name}>{row.label}</span>
                    {row.detail ? <span className={styles.detail}>{row.detail}</span> : null}
                  </span>
                  <span className={styles.size}>{bytes(row.size)}</span>
                </li>
              ))}
            </ul>
            <div className={styles.confirmTotal} data-testid="cleanup-confirm-total">
              <span>{t('common.items', { count: plan.rows.length })}</span>
              <span>{bytes(total)}</span>
            </div>
          </div>
        ) : null}

        {plan.copy ? (
          <div className={styles.copyBox}>
            <Switch checked={copy} onChange={setCopy} label={t(plan.copy.label)} />
            {copy ? (
              <>
                <div className={styles.copyFolder}>
                  <span>{t('cleanup.confirm.copyTo', { folder })}</span>
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
                <div className={styles.copyFolder}>{t('cleanup.confirm.copySafe')}</div>
              </>
            ) : null}
          </div>
        ) : null}

        {plan.notes.map((note) => (
          <p key={note} className={styles.hint}>
            {note}
          </p>
        ))}
        {plan.kind !== 'caches' ? <p className={styles.warn}>{t('cleanup.confirm.undo')}</p> : null}
      </div>
    </Dialog>
  );
}
