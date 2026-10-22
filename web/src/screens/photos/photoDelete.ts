// Deleting photos (round 4, photodelete): whether the phone lets the PC delete them (and why not), and the
// confirmed delete with its per-item results. Used by Photos and Clean-up.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { PhotoDeleteBlock, PhotoDeleteStarted, PhotoDeleteSupport } from '../../bridge/photoDeleteTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys } from '../../app/queries';
import type { MessageKey } from '../../i18n';
import { backupKeys } from './backupQueries';

export const photoDeleteKey = (deviceId: string) => ['photos.deleteSupport', deviceId] as const;

/** More than this many photos: "Copy to this PC first" starts checked. */
export const COPY_FIRST_ABOVE = 20;

export type BlockReason = Exclude<PhotoDeleteBlock, 'none'>;

export const SHORT_REASON: Record<BlockReason, MessageKey> = {
  iCloudPhotos: 'photoDelete.short.iCloudPhotos',
  readOnly: 'photoDelete.short.readOnly',
  locked: 'photoDelete.short.locked',
  notCamera: 'photoDelete.short.notCamera',
  converted: 'photoDelete.short.converted',
  unsupported: 'photoDelete.short.unsupported',
  checking: 'photoDelete.short.checking',
};

/** Whether the phone lets the PC delete its photos now; asked again when its photosDelete capability changes. */
export function usePhotoDeleteSupport(device: DeviceSummary) {
  const client = useClient();
  const can = device.capabilities.includes('photosDelete');
  return useQuery({
    queryKey: [...photoDeleteKey(device.id), can],
    queryFn: () => client.call('photos.deleteSupport', { deviceId: device.id }),
    enabled: device.state === 'ready' && device.capabilities.includes('photosRead'),
    staleTime: 30_000,
    // An iPhone that just connected is still being checked: ask again shortly.
    refetchInterval: (query) => (query.state.data?.reason === 'checking' ? 2000 : false),
  });
}

/** "Check again": asks the phone, then keeps the answer. */
export function useRecheckPhotoDelete(device: DeviceSummary) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  return useMutation({
    mutationFn: () => client.call('photos.deleteSupport', { deviceId: device.id, recheck: true }),
    onSuccess: (support: PhotoDeleteSupport) => {
      queryClient.setQueryData([...photoDeleteKey(device.id), support.canDelete], support);
      void queryClient.invalidateQueries({ queryKey: photoDeleteKey(device.id) });
      void queryClient.invalidateQueries({ queryKey: keys.devices });
    },
    onError: (error) => actions.fail(error),
  });
}

/** A delete's copy-first folder until its copy jobs exist (their rows then offer Show in Explorer). */
export const copyDestinations = new Map<string, string>();

/**
 * The confirmed delete (v0.4.1: a transfer job). Resolves as soon as the job is queued, or with null after showing
 * the error (a phone that can't delete, bad input). The Transfers drawer shows the job; it ends with a toast.
 */
export function useDeletePhotos(device: DeviceSummary) {
  const client = useClient();
  const actions = useActions();
  return async (items: { id: string; size: number }[], copyFirstTo: string | null): Promise<PhotoDeleteStarted | null> => {
    try {
      const expectedBytes = items.reduce((n, i) => n + i.size, 0);
      const started = await client.call('photos.deleteItems', { deviceId: device.id, items, expectedBytes, copyFirstTo });
      if (copyFirstTo && started.jobId) copyDestinations.set(started.jobId, copyFirstTo);
      return started;
    } catch (error) {
      actions.fail(error);
      return null;
    }
  };
}

/** After a delete the host rebuilt its photo index; the page reads the library, albums and storage again. */
export function refreshAfterDelete(queryClient: ReturnType<typeof useQueryClient>, deviceId: string) {
  void queryClient.invalidateQueries({ queryKey: ['photos.list', deviceId] });
  void queryClient.invalidateQueries({ queryKey: keys.albums(deviceId) });
  void queryClient.invalidateQueries({ queryKey: keys.storage(deviceId) });
  void queryClient.invalidateQueries({ queryKey: backupKeys.status(deviceId) });
  void queryClient.invalidateQueries({ queryKey: photoDeleteKey(deviceId) });
}
