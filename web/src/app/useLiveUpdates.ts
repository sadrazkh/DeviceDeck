import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import type { TransferJob } from '../bridge/types';
import { useI18n } from '../i18n';
import { toast } from '../state/toasts';
import { useUi } from '../state/ui';
import { useActions } from './actions';
import { useBridge } from './bridgeContext';
import { connectBridgeEvents, keys } from './queries';

/** What a finished job changed on the phone, so the right lists refresh. */
function invalidateAfter(queryClient: ReturnType<typeof useQueryClient>, job: TransferJob) {
  const id = job.deviceId;
  switch (job.kind) {
    case 'copyToDevice':
      void queryClient.invalidateQueries({ queryKey: ['files.list', id] });
      void queryClient.invalidateQueries({ queryKey: keys.storage(id) });
      break;
    case 'photosImport':
      void queryClient.invalidateQueries({ queryKey: ['photos.list', id] });
      void queryClient.invalidateQueries({ queryKey: keys.albums(id) });
      void queryClient.invalidateQueries({ queryKey: ['photos.backupStatus', id] });
      break;
    case 'photosBackup':
      void queryClient.invalidateQueries({ queryKey: ['photos.backupStatus', id] });
      break;
    case 'photosToApp':
      void queryClient.invalidateQueries({ queryKey: ['files.list', id] });
      break;
    case 'appInstall':
      void queryClient.invalidateQueries({ queryKey: ['apps.list', id] });
      void queryClient.invalidateQueries({ queryKey: keys.storage(id) });
      break;
    default:
      break;
  }
}

/** Keeps the caches live from host events and turns the important changes into toasts. */
export function useLiveUpdates() {
  const { client } = useBridge();
  const queryClient = useQueryClient();
  const i18n = useI18n();
  const actions = useActions();
  const latest = useRef({ i18n, actions });
  latest.current = { i18n, actions };
  const wasDisconnected = useRef(new Set<string>());

  useEffect(
    () =>
      connectBridgeEvents(client, queryClient, {
        onTransfer: ({ job, previous }) => {
          if (!previous || previous.state === job.state) return;
          const { t } = latest.current.i18n;
          if (job.state === 'done') {
            invalidateAfter(queryClient, job);
            const destination = useUi.getState().jobDestinations[job.id];
            toast({
              tone: 'success',
              title: t('transfers.doneToast', { title: job.title }),
              action: destination
                ? { label: t('common.showInExplorer'), run: () => void latest.current.actions.revealInExplorer(destination) }
                : undefined,
            });
          } else if (job.state === 'failed') {
            toast({
              tone: 'danger',
              title: t('transfers.failedToast', { title: job.title }),
              body: job.error?.message,
              action: { label: t('common.retry'), run: () => void latest.current.actions.transfer('resume', job.id) },
            });
          }
        },
        onDevice: ({ device, previous }) => {
          const { t } = latest.current.i18n;
          const ui = useUi.getState();
          if (!ui.selectedDeviceId) ui.selectDevice(device.id);
          if (!previous || previous.state === device.state) return;
          if (device.state === 'disconnected') {
            wasDisconnected.current.add(device.id);
            toast({ tone: 'warning', title: t('toast.disconnected', { device: device.name }) });
          } else if (device.state === 'ready') {
            if (wasDisconnected.current.delete(device.id)) {
              toast({ tone: 'success', title: t('toast.reconnected', { device: device.name }) });
            } else if (previous.state !== 'ready') {
              toast({ tone: 'success', title: t('toast.connected', { device: device.name }), duration: 3000 });
            }
          }
        },
        onDeviceRemoved: (deviceId) => {
          const ui = useUi.getState();
          if (ui.selectedDeviceId !== deviceId) return;
          const rest = queryClient.getQueryData<{ id: string }[]>(keys.devices) ?? [];
          ui.selectDevice(rest[0]?.id ?? null);
        },
      }),
    [client, queryClient],
  );
}
