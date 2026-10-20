// Desktop events and update status: a click on a Windows notification selects its phone; app.updateChanged
// keeps the Settings card's update status live.

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { DeviceSummary, UpdateStatus } from '../bridge/types';
import { useBridge, useClient } from '../app/bridgeContext';
import { keys } from '../app/queries';
import { useUi } from '../state/ui';

export const updateKey = ['app.updateStatus'] as const;

export function useUpdateStatus() {
  const client = useClient();
  return useQuery({ queryKey: updateKey, queryFn: () => client.call('app.updateStatus'), staleTime: Infinity, retry: false });
}

/** Call once (AppShell): notification clicks and update status changes from the host. */
export function useDesktopEvents() {
  const { client } = useBridge();
  const queryClient = useQueryClient();
  useEffect(() => {
    const offs = [
      client.on('notification.activated', ({ deviceId }) => {
        // The host has brought the window to the front; show the phone the notification was about.
        if (deviceId && queryClient.getQueryData<DeviceSummary[]>(keys.devices)?.some((d) => d.id === deviceId)) {
          useUi.getState().selectDevice(deviceId);
        }
      }),
      client.on('app.updateChanged', (status: UpdateStatus) => queryClient.setQueryData(updateKey, status)),
    ];
    return () => offs.forEach((off) => off());
  }, [client, queryClient]);
}
