import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import type { TransferJob } from '../bridge/types';
import { useI18n } from '../i18n';
import { usePhotoBackupEvents } from '../screens/photos/backupQueries';
import { copyDestinations, refreshAfterDelete } from '../screens/photos/photoDelete';
import { announceDeleteFinished, deleteJobKey } from '../shell/DeleteJobRow';
import type { DeviceSummary } from '../bridge/types';
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
      // App files: card sizes and Android's "empty folder" flags.
      void queryClient.invalidateQueries({ queryKey: ['appFiles.measure', id] });
      void queryClient.invalidateQueries({ queryKey: ['appFiles.list', id] });
      break;
    case 'photosImport':
      void queryClient.invalidateQueries({ queryKey: ['photos.list', id] });
      void queryClient.invalidateQueries({ queryKey: keys.albums(id) });
      void queryClient.invalidateQueries({ queryKey: ['photos.backupStatus', id] });
      break;
    case 'photosBackup':
      // The badge on Photos › Back up comes from the status: nothing new any more (round 3: also after an automatic run).
      void queryClient.invalidateQueries({ queryKey: ['photos.backupStatus', id] });
      void queryClient.invalidateQueries({ queryKey: ['photos.backupSpace'] });
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
  usePhotoBackupEvents();

  useEffect(
    () =>
      connectBridgeEvents(client, queryClient, {
        onTransfer: ({ job, previous }) => {
          // A backup the host started by itself (the phone connected, a notification): the panel shows it at once.
          if (!previous && job.kind === 'photosBackup') void queryClient.invalidateQueries({ queryKey: ['photos.backupStatus', job.deviceId] });
          // A delete that copies first (v0.4.1): once its copy jobs exist, their rows can show the folder.
          const copyFolder = job.kind === 'photosDelete' && job.stage?.name === 'copying' ? copyDestinations.get(job.id) : undefined;
          if (copyFolder) {
            copyDestinations.delete(job.id);
            void client.call('photos.deleteJob', { jobId: job.id }).then(
              (status) => status.copyJobIds.forEach((id) => useUi.getState().rememberDestination(id, copyFolder)),
              () => undefined,
            );
          }
          if (!previous || previous.state === job.state) return;
          const { t } = latest.current.i18n;
          if (job.kind === 'photosDelete') {
            // v0.4.1: a delete ended: the photo lists, albums, storage and its results are read again.
            if (job.state === 'done' || job.state === 'failed' || job.state === 'cancelled') {
              refreshAfterDelete(queryClient, job.deviceId);
              void queryClient.invalidateQueries({ queryKey: deleteJobKey(job.id) });
              const device = queryClient.getQueryData<DeviceSummary[]>(keys.devices)?.find((d) => d.id === job.deviceId);
              announceDeleteFinished(latest.current.i18n, job, device);
            }
            return;
          }
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
