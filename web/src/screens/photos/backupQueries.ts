// Queries and actions of the photo backup (photos.backup*) and of adding photos to an iPhone app.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import { create } from 'zustand';
import type { FileSource } from '../../bridge';
import type { MethodParams } from '../../bridge/methods';
import type { DeviceSummary, PhotoBackupRange, PhotoBackupStatus } from '../../bridge/types';
import { useBridge, useClient } from '../../app/bridgeContext';
import { useActions } from '../../app/actions';
import { useI18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { useUi } from '../../state/ui';

export const backupKeys = {
  status: (deviceId: string) => ['photos.backupStatus', deviceId] as const,
};

/** A request to open a phone's Back up panel (from a notification); the Photos screen takes it. */
export const useBackupPanelRequest = create<{ deviceId: string | null; open(deviceId: string): void; clear(): void }>((set) => ({
  deviceId: null,
  open: (deviceId) => set({ deviceId }),
  clear: () => set({ deviceId: null }),
}));

/**
 * Clicks on the photo backup's Windows notifications (round 3). The host starts the backup for "Back up now" and
 * opens Explorer for "Open folder"; here the page follows: the phone, its Photos screen, the panel or the transfers.
 */
export function usePhotoBackupEvents() {
  const { client } = useBridge();
  useEffect(
    () =>
      client.on('notification.activated', ({ command, deviceId }) => {
        if (!deviceId || !command.startsWith('photos.')) return;
        const ui = useUi.getState();
        if (command === 'photos.openBackup') {
          ui.selectDevice(deviceId);
          ui.navigate('photos');
          useBackupPanelRequest.getState().open(deviceId);
        } else if (command === 'photos.backupNow') {
          ui.setDrawerOpen(true);
        }
      }),
    [client],
  );
}

export function canBackUp(device: DeviceSummary | null | undefined): device is DeviceSummary {
  return device?.state === 'ready' && device.capabilities.includes('photosRead');
}

/** An iPhone takes photos only into an app that shares files (no supported way into Photos over USB). */
export function addsPhotosThroughApps(device: DeviceSummary): boolean {
  return device.platform === 'ios' && device.capabilities.includes('appDocuments') && !device.capabilities.includes('photosImport');
}

/** Room on the drive of a backup folder (Settings' default folder; a phone's own comes with its status). */
export function useBackupSpace(folder: string | undefined) {
  const client = useClient();
  return useQuery({
    queryKey: ['photos.backupSpace', folder ?? ''],
    queryFn: () => client.call('photos.backupSpace', { folder: folder! }),
    enabled: !!folder,
    staleTime: 60_000,
  });
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

export type ConfigurePatch = Omit<MethodParams<'photos.backupConfigure'>, 'deviceId'>;

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
        const { useDefaults: _ignored, onConnect, ...options } = patch;
        queryClient.setQueryData<PhotoBackupStatus>(key, {
          ...previous,
          options: { ...previous.options, ...options },
          customized: Object.keys(options).length > 0 || previous.customized,
          onConnect: onConnect ?? previous.onConnect,
        });
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
          // Refused for room or a missing drive: the panel shows why, with "Choose another folder".
          await queryClient.invalidateQueries({ queryKey: backupKeys.status(device.id) });
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

/** A local calendar day as "yyyy-MM-dd" (what range.since and <input type="date"> use). */
export function isoDay(when: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${when.getFullYear()}-${pad(when.getMonth() + 1)}-${pad(when.getDate())}`;
}

/** The range choices of the panel; "since" starts 30 days back until the user picks a day. */
export const RANGE_DAYS = 30;

export function rangeFor(choice: 'all' | 'lastDays' | 'since', now: Date, since?: string | null): PhotoBackupRange {
  if (choice === 'lastDays') return { kind: 'lastDays', days: RANGE_DAYS, since: null };
  if (choice === 'since') return { kind: 'since', days: null, since: since ?? isoDay(new Date(now.getTime() - RANGE_DAYS * 24 * 3600 * 1000)) };
  return { kind: 'all', days: null, since: null };
}

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
