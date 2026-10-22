// Queries and actions of the Drivers card (v0.6). The host also pushes drivers.changed (see app/queries.ts) and
// drivers.download; the card asks again every 30 s while it is on screen, and on "Check again".

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { DriverDownloadStatus, DriverLink, DriverStatus } from '../../bridge';
import { keys } from '../../app/queries';
import { useClient } from '../../app/bridgeContext';

export const DRIVERS_REFRESH_MS = 30_000;

export const downloadKey = (providerId: string) => ['drivers.downloadStatus', providerId] as const;

/** drivers.status, asked again every 30 s while a card using it is mounted and the window is visible. */
export function useDriversLive() {
  const client = useClient();
  return useQuery({
    queryKey: keys.drivers,
    queryFn: () => client.call('drivers.status'),
    refetchInterval: DRIVERS_REFRESH_MS,
    refetchIntervalInBackground: false,
  });
}

/** Android's download: read once when it matters, then kept current by drivers.download events. */
export function useDriverDownload(providerId: string, enabled: boolean) {
  const client = useClient();
  const queryClient = useQueryClient();
  useEffect(
    () =>
      client.on('drivers.download', (status) => {
        queryClient.setQueryData<DriverDownloadStatus>(downloadKey(status.providerId), status);
        // Installed: the row turns into "starting", then "ready", through drivers.changed; ask now too.
        if (status.state === 'done') void queryClient.invalidateQueries({ queryKey: keys.drivers });
      }),
    [client, queryClient],
  );
  return useQuery({
    queryKey: downloadKey(providerId),
    queryFn: () => client.call('drivers.downloadStatus', { providerId }),
    enabled,
  });
}

export function useDriverActions() {
  const client = useClient();
  const queryClient = useQueryClient();
  const start = useMutation({
    mutationFn: (providerId: string) => client.call('drivers.start', { providerId }),
    onSuccess: (list: DriverStatus[]) => queryClient.setQueryData(keys.drivers, list),
  });
  const download = useMutation({
    mutationFn: (providerId: string) => client.call('drivers.download', { providerId }),
    onSuccess: (status: DriverDownloadStatus) => queryClient.setQueryData(downloadKey(status.providerId), status),
  });
  const cancel = useMutation({
    mutationFn: (providerId: string) => client.call('drivers.cancelDownload', { providerId }),
    onSettled: (_data, _error, providerId) => queryClient.invalidateQueries({ queryKey: downloadKey(providerId) }),
  });
  const openLink = useMutation({ mutationFn: (link: DriverLink) => client.call('drivers.openLink', { link }) });
  return { start, download, cancel, openLink };
}
