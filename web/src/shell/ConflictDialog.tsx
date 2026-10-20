import { useState } from 'react';
import { create } from 'zustand';
import type { ConflictChoice } from '../bridge/types';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useTransfers } from '../app/queries';
import { Button } from '../components/Button';
import { Dialog } from '../components/Dialog';
import { Checkbox } from '../components/ui';
import { useI18n } from '../i18n';
import styles from './Conflict.module.css';

/** Which conflict the user is looking at; "Later" hides one until they open it from the drawer. */
const useConflictState = create<{ current: string | null; later: Set<string>; open(id: string): void; postpone(id: string): void; clear(): void }>(
  (set) => ({
    current: null,
    later: new Set(),
    open: (id) => set((s) => ({ current: id, later: new Set([...s.later].filter((x) => x !== id)) })),
    postpone: (id) => set((s) => ({ current: null, later: new Set([...s.later, id]) })),
    clear: () => set({ current: null }),
  }),
);

export function useConflictQueue() {
  return useConflictState();
}

/** Asked once per transfer, with "Do this for every conflict in this transfer". */
export function ConflictDialog() {
  const { t, bytes } = useI18n();
  const client = useClient();
  const actions = useActions();
  const jobs = useTransfers().data ?? [];
  const { current, later, postpone, clear } = useConflictState();
  const [applyToAll, setApplyToAll] = useState(true);
  const [busy, setBusy] = useState(false);

  const deciding = jobs.filter((j) => j.state === 'needsDecision' && j.conflict);
  const job = deciding.find((j) => j.id === current) ?? deciding.find((j) => !later.has(j.id)) ?? null;
  if (!job || !job.conflict) return null;

  const choose = async (choice: ConflictChoice) => {
    setBusy(true);
    try {
      await client.call('transfers.resolveConflict', { jobId: job.id, choice, applyToAll });
      clear();
    } catch (error) {
      actions.fail(error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : postpone(job.id))}
      title={t('conflict.title', { name: job.conflict.itemName })}
      description={t('conflict.body', { existing: bytes(job.conflict.existingSize), incoming: bytes(job.conflict.newSize) })}
      footerStart={
        <label className={styles.applyAll}>
          <Checkbox checked={applyToAll} onChange={setApplyToAll} label={t('conflict.applyToAll')} />
          <span>{t('conflict.applyToAll')}</span>
        </label>
      }
      footer={
        <>
          <Button onClick={() => void choose('skip')} disabled={busy}>
            {t('conflict.skip')}
          </Button>
          <Button onClick={() => void choose('keepBoth')} disabled={busy}>
            {t('conflict.keepBoth')}
          </Button>
          <Button variant="accent" onClick={() => void choose('replace')} disabled={busy} autoFocus>
            {t('conflict.replace')}
          </Button>
        </>
      }
    >
      <p className={styles.job}>{job.title}</p>
    </Dialog>
  );
}
