// Queries and actions of photos straight into Photos with LocalSend (round 3): localsend.*.

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import type { FileSource } from '../../bridge';
import type { LocalSendJobStatus, LocalSendTarget } from '../../bridge/localSend';
import type { DeviceSummary, TransferJob } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useTransfers } from '../../app/queries';
import { isFinished } from '../../state/transfers';

export const localSendKeys = {
  discover: (deviceId: string, wifi: boolean) => ['localsend.discover', deviceId, wifi] as const,
  job: (jobId: string) => ['localsend.job', jobId] as const,
};

/**
 * Looks for LocalSend on the phone while the dialog is open, and keeps looking: the person may open LocalSend (or send
 * it to the background) at any moment. Quicker while nothing is found or a Wi-Fi search is running.
 */
export function useLocalSendDiscovery(device: DeviceSummary, options: { enabled: boolean; wifi: boolean }) {
  const client = useClient();
  return useQuery({
    queryKey: localSendKeys.discover(device.id, options.wifi),
    queryFn: () => client.call('localsend.discover', { deviceId: device.id, wifi: options.wifi }),
    enabled: options.enabled,
    refetchInterval: (query) => {
      const found = query.state.data;
      if (!found) return 1500;
      if (found.wifiSearching) return 700;
      return found.usb === 'found' ? 5000 : 2000;
    },
    staleTime: 0,
    retry: false,
  });
}

/** One send: its transfer (progress, state) and LocalSend's phase and per-file results, kept fresh by localsend.updated. */
export function useLocalSendJob(jobId: string | null): { job: TransferJob | null; status: LocalSendJobStatus | null } {
  const client = useClient();
  const queryClient = useQueryClient();
  const transfers = useTransfers();
  const job = useMemo(() => transfers.data?.find((j) => j.id === jobId) ?? null, [transfers.data, jobId]);
  const status = useQuery({
    queryKey: localSendKeys.job(jobId ?? ''),
    queryFn: () => client.call('localsend.job', { jobId: jobId! }),
    enabled: jobId !== null,
    staleTime: Infinity,
  });

  useEffect(() => {
    if (!jobId) return;
    return client.on('localsend.updated', (update) => {
      if (update.jobId !== jobId) return;
      queryClient.setQueryData<LocalSendJobStatus>(localSendKeys.job(jobId), (old) => ({ ...update, files: old?.files ?? null }));
    });
  }, [client, queryClient, jobId]);

  // Each file's result once the send has stopped (done, failed or cancelled), and again after a retry starts.
  const finished = job ? isFinished(job.state) : false;
  useEffect(() => {
    if (jobId) void queryClient.invalidateQueries({ queryKey: localSendKeys.job(jobId) });
  }, [finished, jobId, queryClient]);

  return { job, status: status.data ?? null };
}

export function useLocalSendActions(device: DeviceSummary) {
  const client = useClient();
  const actions = useActions();
  return useMemo(
    () => ({
      async send(target: LocalSendTarget, files: FileSource): Promise<string | null> {
        try {
          const { jobId } = await client.callWithFiles('localsend.send', { deviceId: device.id, target }, files);
          return jobId;
        } catch (error) {
          actions.fail(error);
          return null;
        }
      },
      async retry(jobId: string, pin?: string): Promise<boolean> {
        try {
          await client.call('localsend.retry', pin === undefined ? { jobId } : { jobId, pin });
          return true;
        } catch (error) {
          actions.fail(error);
          return false;
        }
      },
      cancel(jobId: string) {
        void actions.transfer('cancel', jobId);
      },
    }),
    [actions, client, device.id],
  );
}
