import { useEffect, useState } from 'react';
import type { CleanupResult } from '../../bridge/cleanupTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { Switch } from '../../components/ui';
import { useI18n, type MessageKey } from '../../i18n';
import styles from './Cleanup.module.css';
import type { CleanupKind, CleanupRequest } from './useCleanup';
import { LeftOutList, ReachChecking, usePhotoDeleteReach } from '../photos/DeleteReach';

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
  /** Round 4: the items can be brought back (an iPhone's Recently Deleted), so no "can't be undone". */
  reversible?: boolean;
  request(copyFirstTo: string | null): CleanupRequest;
  /**
   * v0.4.2, photos and videos: `rows` are everything selected. Those only the iPhone can delete (Windows lists them
   * nowhere) move to a list of their own as soon as that is known, even while the dialog is open, and the delete
   * leaves them out: the title and the request then come from what is left.
   */
  byReach?: { title(count: number): string; request(copyFirstTo: string | null, ids: ReadonlySet<string>): CleanupRequest };
}

/**
 * The one door to every delete: lists what goes and what it frees, offers a copy to the PC first,
 * and only then runs. While it runs it can't be dismissed. Photos and videos (v0.4.1) go to the Transfers drawer
 * as a delete job: the dialog closes as soon as it is queued, and the drawer shows each result.
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
  /** The result, "started" for a delete job in the drawer, or null after an error (already shown). */
  run(request: CleanupRequest): Promise<CleanupResult | 'started' | null>;
}) {
  const { t, bytes } = useI18n();
  const actions = useActions();
  const [copy, setCopy] = useState(true);
  const [folder, setFolder] = useState('');
  const [busy, setBusy] = useState(false);
  const reach = usePhotoDeleteReach(device, !!plan?.byReach);

  useEffect(() => {
    if (plan) {
      setCopy(plan.copy !== null);
      setFolder(plan.copy?.folder ?? '');
      setBusy(false);
    }
  }, [plan]);

  if (!plan) return null;
  const split = plan.byReach;
  const rows = split ? plan.rows.filter((r) => !reach.phoneOnly(r.key)) : plan.rows;
  const leftOut = split ? plan.rows.filter((r) => reach.phoneOnly(r.key)) : [];
  const title = split ? split.title(rows.length) : plan.title;
  const total = rows.reduce((n, r) => n + r.size, 0);
  const copying = plan.copy !== null && copy && folder.length > 0;

  const confirm = async () => {
    setBusy(true);
    const copyTo = copying ? folder : null;
    const result = await run(split ? split.request(copyTo, new Set(rows.map((r) => r.key))) : plan.request(copyTo));
    setBusy(false);
    if (result) onClose();
  };

  return (
    <Dialog
      open
      wide
      dismissible={!busy}
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={title}
      description={plan.description ?? t(plan.approximate ? 'cleanup.confirm.freesAbout' : 'cleanup.confirm.frees', { size: bytes(total) })}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="dangerSolid"
            onClick={() => void confirm()}
            loading={busy}
            disabled={!!split && rows.length === 0}
            data-testid="cleanup-confirm"
            autoFocus
          >
            {busy ? t(copying ? 'cleanup.confirm.busyCopy' : 'cleanup.confirm.busy') : plan.confirmLabel}
          </Button>
        </>
      }
    >
      <div className={styles.confirmBody} data-testid="cleanup-confirm-dialog" data-device={device.id}>
        {rows.length > 0 ? (
          <div>
            <ul className={styles.confirmList} aria-label={title}>
              {rows.map((row) => (
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
              <span>{t('common.items', { count: rows.length })}</span>
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

        {split && reach.checking ? <ReachChecking checking={reach.checking} /> : null}
        <LeftOutList rows={leftOut.map((row) => ({ key: row.key, label: row.label, detail: row.detail, size: bytes(row.size) }))} />

        {plan.notes.map((note) => (
          <p key={note} className={styles.hint}>
            {note}
          </p>
        ))}
        {plan.kind !== 'caches' && !plan.reversible ? <p className={styles.warn}>{t('cleanup.confirm.undo')}</p> : null}
      </div>
    </Dialog>
  );
}
