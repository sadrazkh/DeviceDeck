// Actions used from several places (screens, nav drops, the command palette), with their toasts.

import { useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { FileSource } from '../bridge';
import type { DeviceSummary, FolderPattern } from '../bridge/types';
import { describeError } from '../components/ErrorState';
import { useI18n } from '../i18n';
import { toast } from '../state/toasts';
import { useUi } from '../state/ui';
import { useClient } from './bridgeContext';
import { keys } from './queries';

export const APP_EXTENSIONS = { ios: ['.ipa'], android: ['.apk', '.apks', '.xapk'] } as const;

export function isAppPackage(device: DeviceSummary, name: string): boolean {
  const lower = name.toLowerCase();
  return APP_EXTENSIONS[device.platform].some((ext) => lower.endsWith(ext));
}

export function useActions() {
  const client = useClient();
  const queryClient = useQueryClient();
  const { t } = useI18n();

  return useMemo(() => {
    const fail = (error: unknown) => {
      const { message, next } = describeError(error, t);
      toast({ tone: 'danger', title: message, body: next });
    };
    const remember = (jobId: string, destination: string) => useUi.getState().rememberDestination(jobId, destination);

    return {
      fail,

      async revealInExplorer(path: string) {
        try {
          await client.call('shell.revealInExplorer', { path });
        } catch (error) {
          fail(error);
        }
      },

      async pickFolder(title?: string): Promise<string | null> {
        try {
          const { path } = await client.call('shell.pickFolder', title ? { title } : {});
          return path;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async exportPhotos(device: DeviceSummary, assetIds: string[], destination: string, convertHeic: boolean, folderPattern: FolderPattern) {
        try {
          const { jobId } = await client.call('photos.export', { deviceId: device.id, assetIds, destination, convertHeic, folderPattern });
          remember(jobId, destination);
          toast({ tone: 'info', title: t('photos.export.started'), duration: 3000 });
          return jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async deletePhotos(device: DeviceSummary, assetIds: string[]) {
        try {
          const { deleted } = await client.call('photos.delete', { deviceId: device.id, assetIds });
          await queryClient.invalidateQueries({ queryKey: ['photos.list', device.id] });
          await queryClient.invalidateQueries({ queryKey: keys.albums(device.id) });
          toast({ tone: 'success', title: t('photos.deleted', { count: deleted }) });
          return true;
        } catch (error) {
          fail(error);
          return false;
        }
      },

      async importPhotos(device: DeviceSummary, files: FileSource) {
        try {
          return (await client.callWithFiles('photos.import', { deviceId: device.id }, files)).jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async installApps(device: DeviceSummary, files: FileSource) {
        try {
          const { jobId } = await client.callWithFiles('apps.install', { deviceId: device.id }, files);
          const first = files[0];
          const name = typeof first === 'string' ? (first.split(/[\\/]/).pop() ?? first) : (first?.name ?? '');
          toast({ tone: 'info', title: t('apps.installStarted', { name }), duration: 3000 });
          return jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async uninstallApp(device: DeviceSummary, appId: string, name: string) {
        try {
          await client.call('apps.uninstall', { deviceId: device.id, appId });
          await queryClient.invalidateQueries({ queryKey: ['apps.list', device.id] });
          toast({ tone: 'success', title: t('apps.uninstalled', { name }) });
          return true;
        } catch (error) {
          fail(error);
          return false;
        }
      },

      async exportApp(device: DeviceSummary, appId: string, name: string, destination: string) {
        try {
          const { jobId } = await client.call('apps.export', { deviceId: device.id, appId, destination });
          remember(jobId, destination);
          toast({ tone: 'info', title: t('apps.exportStarted', { name }), duration: 3000 });
          return jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async copyToDevice(device: DeviceSummary, root: string, path: string, files: FileSource) {
        try {
          const { jobId } = await client.callWithFiles('files.copyToDevice', { deviceId: device.id, root, path }, files);
          useUi.getState().rememberJobRoot(jobId, root);
          toast({ tone: 'info', title: t('files.copyStarted'), duration: 3000 });
          return jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async copyToPc(device: DeviceSummary, root: string, paths: string[], destination: string) {
        try {
          const { jobId } = await client.call('files.copyToPc', { deviceId: device.id, root, paths, destination });
          remember(jobId, destination);
          toast({ tone: 'info', title: t('files.copyStarted'), duration: 3000 });
          return jobId;
        } catch (error) {
          fail(error);
          return null;
        }
      },

      async transfer(action: 'pause' | 'resume' | 'cancel', jobId: string) {
        try {
          await client.call(`transfers.${action}`, { jobId });
        } catch (error) {
          fail(error);
        }
      },
    };
  }, [client, queryClient, t]);
}
