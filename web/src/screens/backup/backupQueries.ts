// React Query hooks and live updates for backups, contacts and messages (round 2).

import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { create } from 'zustand';
import { useActions } from '../../app/actions';
import { useBridge, useClient } from '../../app/bridgeContext';
import type { BackupInfo, BackupJob, DeviceSummary, PrunePlan } from '../../bridge/types';
import { useI18n, type MessageKey } from '../../i18n';
import { toast } from '../../state/toasts';

export const backupKeys = {
  list: ['backup.list'] as const,
  device: (id: string) => ['backup.device', id] as const,
  jobs: ['backup.jobs'] as const,
  domains: (backupId: string) => ['backup.domains', backupId] as const,
  files: (backupId: string, domain: string, path: string) => ['backup.files', backupId, domain, path] as const,
  contacts: (backupId: string) => ['contacts.list', backupId] as const,
  conversations: (backupId: string) => ['messages.conversations', backupId] as const,
  messages: (backupId: string, conversationId: string) => ['messages.list', backupId, conversationId] as const,
};

export const ACTIVE_JOB_STATES = ['queued', 'running', 'waitingForDevice'] as const;

export function isActive(job: BackupJob): boolean {
  return (ACTIVE_JOB_STATES as readonly string[]).includes(job.state);
}

/** Every backup on the PC, newest first. */
export function useBackups() {
  const client = useClient();
  return useQuery({ queryKey: backupKeys.list, queryFn: () => client.call('backup.list', {}) });
}

export function useBackupDevice(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.device(device?.id ?? ''),
    queryFn: () => client.call('backup.device', { deviceId: device!.id }),
    enabled: device?.state === 'ready',
  });
}

export function useBackupJobs() {
  const client = useClient();
  return useQuery({ queryKey: backupKeys.jobs, queryFn: () => client.call('backup.jobs', {}), staleTime: Infinity });
}

/** The phone's backup, restore or encryption job that is still going, if any. */
export function useActiveDeviceJob(deviceId: string | undefined): BackupJob | undefined {
  const jobs = useBackupJobs().data ?? [];
  return jobs.find((j) => j.deviceId === deviceId && j.kind !== 'extract' && j.kind !== 'export' && isActive(j));
}

/** The latest state of one job (null until it is known). */
export function useBackupJob(jobId: string | null): BackupJob | null {
  const jobs = useBackupJobs().data ?? [];
  return jobId ? (jobs.find((j) => j.id === jobId) ?? null) : null;
}

export function useDomains(backup: BackupInfo | null) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.domains(backup?.id ?? ''),
    queryFn: () => client.call('backup.domains', { backupId: backup!.id }),
    enabled: backup !== null && !backup.locked && backup.state === 'complete',
    retry: false,
  });
}

export function useBackupFiles(backup: BackupInfo | null, domain: string | null, path: string) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.files(backup?.id ?? '', domain ?? '', path),
    queryFn: () => client.call('backup.files', { backupId: backup!.id, domain: domain!, path }),
    enabled: backup !== null && domain !== null && !backup.locked,
    retry: false,
  });
}

export function useContacts(backup: BackupInfo | null) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.contacts(backup?.id ?? ''),
    queryFn: () => client.call('contacts.list', { backupId: backup!.id }),
    enabled: backup !== null && !backup.locked && backup.platform === 'ios',
    retry: false,
  });
}

export function useConversations(backup: BackupInfo | null) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.conversations(backup?.id ?? ''),
    queryFn: () => client.call('messages.conversations', { backupId: backup!.id }),
    enabled: backup !== null && !backup.locked && backup.platform === 'ios',
    retry: false,
  });
}

/** Page-wide backup state that outlives one screen: older backups waiting for the user's decision. */
interface BackupUiState {
  /** deviceId → the plan a finished backup offered, until the user deletes or keeps them. */
  prune: Record<string, PrunePlan>;
  /** Set by Overview's "Back up now": the Backup screen opens its wizard for this phone. */
  wizardFor: string | null;
  setPrune(plan: PrunePlan | null, deviceId: string): void;
  openWizard(deviceId: string | null): void;
}

export const useBackupUi = create<BackupUiState>((set) => ({
  prune: {},
  wizardFor: null,
  setPrune: (plan, deviceId) =>
    set((s) => {
      const next = { ...s.prune };
      if (plan && plan.backups.length > 0) next[deviceId] = plan;
      else delete next[deviceId];
      return { prune: next };
    }),
  openWizard: (wizardFor) => set({ wizardFor }),
}));

const DONE_TOAST: Record<BackupJob['kind'], MessageKey> = {
  backup: 'backup.toast.done.backup',
  restore: 'backup.toast.done.restore',
  encryption: 'backup.toast.done.encryption',
  extract: 'backup.toast.done.extract',
  export: 'backup.toast.done.export',
};

const FAILED_TOAST: Record<BackupJob['kind'], MessageKey> = {
  backup: 'backup.toast.failed.backup',
  restore: 'backup.toast.failed.restore',
  encryption: 'backup.toast.failed.encryption',
  extract: 'backup.toast.failed.extract',
  export: 'backup.toast.failed.export',
};

/** Puts a job the page just started into the cache (its events update it from there). */
export function rememberJob(queryClient: QueryClient, job: BackupJob): void {
  queryClient.setQueryData<BackupJob[]>(backupKeys.jobs, (list) => (list?.some((j) => j.id === job.id) ? list : upsert(list, job)));
}

function upsert(list: BackupJob[] | undefined, job: BackupJob): BackupJob[] {
  if (!list) return [job];
  const index = list.findIndex((j) => j.id === job.id);
  if (index < 0) return [...list, job];
  const next = list.slice();
  next[index] = job;
  return next;
}

/** Mounted once by the app: keeps the job list live, refreshes backups when jobs end, and says so in a toast. */
export function useBackupEvents() {
  const { client } = useBridge();
  const queryClient = useQueryClient();
  const i18n = useI18n();
  const actions = useActions();
  const latest = useRef({ i18n, actions });
  latest.current = { i18n, actions };

  useEffect(
    () =>
      client.on('backup.updated', (job) => {
        const previous = queryClient.getQueryData<BackupJob[]>(backupKeys.jobs)?.find((j) => j.id === job.id);
        queryClient.setQueryData<BackupJob[]>(backupKeys.jobs, (list) => upsert(list, job));
        if (previous?.state === job.state || !['done', 'failed'].includes(job.state)) return;

        if (job.kind === 'backup' || job.kind === 'restore' || job.kind === 'encryption') {
          void queryClient.invalidateQueries({ queryKey: backupKeys.list });
          if (job.deviceId) void queryClient.invalidateQueries({ queryKey: backupKeys.device(job.deviceId) });
        }
        if (job.kind === 'backup' && job.state === 'done' && job.deviceId) useBackupUi.getState().setPrune(job.prune, job.deviceId);

        const { t } = latest.current.i18n;
        const device = job.deviceName;
        if (job.state === 'done') {
          const destination = job.destination;
          toast({
            tone: job.error ? 'warning' : 'success',
            title: t(DONE_TOAST[job.kind], { device }),
            body: job.error?.message,
            action: destination ? { label: t('common.showInExplorer'), run: () => void latest.current.actions.revealInExplorer(destination) } : undefined,
          });
        } else {
          toast({ tone: 'danger', title: t(FAILED_TOAST[job.kind], { device }), body: job.error?.message });
        }
      }),
    [client, queryClient],
  );
}

/** 0..1 for a progress bar; null while the phone hasn't said how far it is. */
export function backupFraction(job: BackupJob): number | null {
  if (job.state === 'done') return 1;
  if (job.percent !== null) return job.percent / 100;
  return null;
}
