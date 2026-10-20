// The clean-up status of one phone, kept live from cleanup.updated, and the confirmed actions with their toasts.

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import type { CleanupFileItem, CleanupItem, CleanupResult, CleanupStatus } from '../../bridge/cleanupTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { keys } from '../../app/queries';
import { useI18n, type MessageKey } from '../../i18n';
import { toast } from '../../state/toasts';
import { useUi } from '../../state/ui';

export const cleanupKey = (deviceId: string) => ['cleanup.status', deviceId] as const;

export function useCleanupStatus(device: DeviceSummary) {
  const client = useClient();
  const queryClient = useQueryClient();
  useEffect(
    () => client.on('cleanup.updated', (status) => queryClient.setQueryData(cleanupKey(status.deviceId), status)),
    [client, queryClient],
  );
  return useQuery({
    queryKey: cleanupKey(device.id),
    queryFn: () => client.call('cleanup.status', { deviceId: device.id }),
    staleTime: Infinity,
    refetchOnMount: 'always',
  });
}

export type CleanupKind = 'media' | 'files' | 'apps' | 'caches';

/** What a confirmed clean-up sends: the items exactly as the dialog listed them. */
export type CleanupRequest =
  | { kind: 'media'; items: CleanupItem[]; copyFirstTo: string | null }
  | { kind: 'files'; items: CleanupFileItem[]; copyFirstTo: string | null }
  | { kind: 'apps'; items: CleanupItem[]; copyFirstTo: string | null }
  | { kind: 'caches' };

const DONE: Record<CleanupKind, MessageKey> = {
  media: 'cleanup.done.media',
  files: 'cleanup.done.files',
  apps: 'cleanup.done.apps',
  caches: 'cleanup.done.caches',
};

export function useCleanupActions(device: DeviceSummary) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const { t, bytes } = useI18n();

  return useMemo(() => {
    const deviceId = device.id;
    const refresh = (kind: CleanupKind) => {
      void queryClient.invalidateQueries({ queryKey: keys.storage(deviceId) });
      if (kind === 'media') {
        void queryClient.invalidateQueries({ queryKey: ['photos.list', deviceId] });
        void queryClient.invalidateQueries({ queryKey: keys.albums(deviceId) });
      } else if (kind === 'files') {
        void queryClient.invalidateQueries({ queryKey: ['files.list', deviceId] });
      } else if (kind === 'apps') {
        void queryClient.invalidateQueries({ queryKey: ['apps.list', deviceId] });
        void queryClient.invalidateQueries({ queryKey: keys.roots(deviceId) });
      }
    };

    const report = (kind: CleanupKind, result: CleanupResult, copyFirstTo: string | null) => {
      for (const jobId of result.copyJobIds) if (copyFirstTo) useUi.getState().rememberDestination(jobId, copyFirstTo);
      if (result.copyIncomplete) {
        toast({ tone: 'warning', title: t('cleanup.done.copyIncomplete'), body: t('cleanup.done.copyIncompleteBody'), duration: 0 });
        return;
      }
      toast({
        tone: 'success',
        title: t(DONE[kind], { count: result.deleted, size: bytes(result.freedBytes) }),
        body: result.skipped > 0 ? t('cleanup.done.skipped', { count: result.skipped }) : undefined,
        action: copyFirstTo && result.copyJobIds.length > 0 ? { label: t('common.showInExplorer'), run: () => void actions.revealInExplorer(copyFirstTo) } : undefined,
      });
    };

    return {
      async analyze() {
        try {
          queryClient.setQueryData(cleanupKey(deviceId), await client.call('cleanup.analyze', { deviceId }));
        } catch (error) {
          actions.fail(error);
        }
      },

      async cancel() {
        try {
          await client.call('cleanup.cancel', { deviceId });
        } catch (error) {
          actions.fail(error);
        }
      },

      /** Runs a confirmed clean-up. Resolves true when it finished (even with nothing deleted), false on an error. */
      async run(request: CleanupRequest): Promise<boolean> {
        try {
          let result: CleanupResult;
          let copyFirstTo: string | null = null;
          if (request.kind === 'caches') {
            result = await client.call('cleanup.trimCaches', { deviceId });
          } else {
            copyFirstTo = request.copyFirstTo;
            const expectedBytes = request.items.reduce((n, i) => n + i.size, 0);
            const common = { deviceId, expectedBytes, copyFirstTo };
            result =
              request.kind === 'media'
                ? await client.call('cleanup.deleteMedia', { ...common, items: request.items })
                : request.kind === 'files'
                  ? await client.call('cleanup.deleteFiles', { ...common, items: request.items })
                  : await client.call('cleanup.uninstallApps', { ...common, items: request.items });
          }
          refresh(request.kind);
          report(request.kind, result, copyFirstTo);
          return true;
        } catch (error) {
          actions.fail(error);
          return false;
        }
      },

      /** iPhone photos: copy only (the user deletes them in Photos). */
      async copyMedia(assetIds: string[], destination: string, folderPattern: 'yyyy/MM' | '') {
        const jobId = await actions.exportPhotos(device, assetIds, destination, false, folderPattern);
        if (jobId) toast({ tone: 'info', title: t('cleanup.copyStarted'), duration: 3000 });
      },
    };
  }, [client, queryClient, actions, device, t, bytes]);
}

/** The bytes and count of a selection. */
export function totalOf<T extends { size: number }>(items: readonly T[]): number {
  return items.reduce((n, i) => n + i.size, 0);
}

export type { CleanupStatus };
