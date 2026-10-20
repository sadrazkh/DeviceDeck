// Queries and actions of the photo backup (photos.backup*) and of adding photos to an iPhone app.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { FileSource } from '../../bridge';
import type { MethodParams } from '../../bridge/methods';
import type { DeviceSummary, PhotoBackupStatus } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';
import { useActions } from '../../app/actions';
import { useI18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { useUi } from '../../state/ui';

export const backupKeys = {
  status: (deviceId: string) => ['photos.backupStatus', deviceId] as const,
};

export function canBackUp(device: DeviceSummary | null | undefined): device is DeviceSummary {
  return device?.state === 'ready' && device.capabilities.includes('photosRead');
}

/** An iPhone takes photos only into an app that shares files (no supported way into Photos over USB). */
export function addsPhotosThroughApps(device: DeviceSummary): boolean {
  return device.platform === 'ios' && device.capabilities.includes('appDocuments') && !device.capabilities.includes('photosImport');
}

export function useBackupStatus(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: backupKeys.status(device?.id ?? ''),
    queryFn: () => client.call('photos.backupStatus', { deviceId: device!.id }),
    enabled: canBackUp(device),
    staleTime: 30_000,
  });
}

type ConfigurePatch = Omit<MethodParams<'photos.backupConfigure'>, 'deviceId'>;

export function useConfigureBackup(device: DeviceSummary) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  return useMutation({
    mutationFn: (patch: ConfigurePatch) => client.call('photos.backupConfigure', { deviceId: device.id, ...patch }),
    onMutate: (patch) => {
      const key = backupKeys.status(device.id);
      const previous = queryClient.getQueryData<PhotoBackupStatus>(key);
      if (previous && !patch.useDefaults) {
        const { useDefaults: _ignored, ...options } = patch;
        queryClient.setQueryData<PhotoBackupStatus>(key, { ...previous, options: { ...previous.options, ...options }, customized: true });
      }
      return { previous };
    },
    onError: (error, _patch, context) => {
      if (context?.previous) queryClient.setQueryData(backupKeys.status(device.id), context.previous);
      actions.fail(error);
    },
    onSuccess: (status) => queryClient.setQueryData(backupKeys.status(device.id), status),
  });
}

export function useBackupActions(device: DeviceSummary) {
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const { t } = useI18n();
  return useMemo(
    () => ({
      async start(destination: string) {
        try {
          const { jobId } = await client.call('photos.backup', { deviceId: device.id });
          useUi.getState().rememberDestination(jobId, destination);
          await queryClient.invalidateQueries({ queryKey: backupKeys.status(device.id) });
          toast({ tone: 'info', title: t('photos.backup.started', { device: device.name }), duration: 3000 });
          return jobId;
        } catch (error) {
          actions.fail(error);
          return null;
        }
      },
      async forget() {
        try {
          const { forgotten } = await client.call('photos.backupForget', { deviceId: device.id });
          await queryClient.invalidateQueries({ queryKey: backupKeys.status(device.id) });
          toast({ tone: 'success', title: t('photos.backup.forgotten', { count: forgotten }) });
          return true;
        } catch (error) {
          actions.fail(error);
          return false;
        }
      },
      async importToApp(appId: string, appName: string, files: FileSource) {
        try {
          const { jobId } = await client.callWithFiles('photos.importToApp', { deviceId: device.id, appId }, files);
          useUi.getState().rememberJobRoot(jobId, `app:${appId}`);
          toast({
            tone: 'info',
            title: t('photos.toApp.started', { app: appName, device: device.name }),
            body: t('photos.toApp.where', { app: appName }),
            duration: 9000,
          });
          return jobId;
        } catch (error) {
          actions.fail(error);
          return null;
        }
      },
    }),
    [actions, client, device, queryClient, t],
  );
}

/** The example folder a pattern makes for a date: "yyyy/MM" → "2026\10". */
export function exampleFolder(pattern: string, when: Date): string {
  if (pattern === '') return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return pattern
    .replace('yyyy', String(when.getFullYear()))
    .replace('MM', pad(when.getMonth() + 1))
    .replace('dd', pad(when.getDate()))
    .split('/')
    .join('\\');
}

export const PATTERN_PRESETS = ['yyyy/MM', 'yyyy/MM/dd', 'yyyy-MM', 'yyyy', ''] as const;

/** The app the user last chose on this phone for photos (per-viewer convenience only). */
export const lastApp = {
  get(deviceId: string): string | null {
    try {
      return localStorage.getItem(`devicedeck.photosToApp.${deviceId}`);
    } catch {
      return null;
    }
  },
  set(deviceId: string, appId: string) {
    try {
      localStorage.setItem(`devicedeck.photosToApp.${deviceId}`, appId);
    } catch {
      // Private window or blocked storage: the choice just isn't remembered.
    }
  },
};

/** Apps known to take photos well from Files sharing; listed first. */
export const SUGGESTED_APPS = ['com.readdle.ReaddleDocsIPad', 'org.localsend.localsendApp', 'org.videolan.vlc-ios'];
