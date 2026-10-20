// App files on the page: the apps that share files (appFiles.list), their names and icons, and what a folder holds
// (appFiles.measure). Used by App documents and by Files.

import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { AppDataAccess, AppFilesEntry, AppFolder } from '../../bridge/appFiles';
import { isBridgeError } from '../../bridge/errors';
import type { AppInfo, DeviceSummary, FileRoot } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';
import { useApps, useFileRoots } from '../../app/queries';

export const appFilesKeys = {
  list: (deviceId: string) => ['appFiles.list', deviceId] as const,
  measure: (deviceId: string, root: string, paths: readonly string[]) => ['appFiles.measure', deviceId, root, ...paths] as const,
};

/** An app or shared folder, with the name to show (an Android package gets its label from apps.list). */
export interface AppPlace extends AppFilesEntry {
  label: string;
  /** The app is installed but its label isn't known yet (Android labels load slowly the first time). */
  labelPending: boolean;
}

export interface AppFilesState {
  places: AppPlace[];
  privateData: AppDataAccess;
  isPending: boolean;
  error: unknown;
  refetch(): void;
}

const ready = (device: DeviceSummary | null): device is DeviceSummary =>
  device?.state === 'ready' && device.capabilities.includes('appDocuments');

/** A host without appFiles.* (older build): the iPhone's "app:" roots stand in. */
function fromRoots(roots: FileRoot[]): AppFilesEntry[] {
  return roots
    .filter((r) => r.kind === 'appDocuments')
    .map((r) => ({ id: r.appId ?? r.key, name: r.label, kind: 'app', appId: r.appId, folders: [{ rootKey: r.key, label: 'Documents', empty: false }] }));
}

export function useAppFiles(device: DeviceSummary | null): AppFilesState {
  const client = useClient();
  const list = useQuery({
    queryKey: appFilesKeys.list(device?.id ?? ''),
    queryFn: () => client.call('appFiles.list', { deviceId: device!.id }),
    enabled: ready(device),
    retry: false,
  });
  const unsupported = isBridgeError(list.error, 'method_not_found') || isBridgeError(list.error, 'not_supported');
  const roots = useFileRoots(unsupported ? device : null);
  const android = device?.platform === 'android';
  // Android needs every package's label, system apps included (Photos, Downloads…).
  const apps = useApps(android ? device : null, true).data;

  const places = useMemo(() => {
    const entries = unsupported ? fromRoots(roots.data ?? []) : (list.data?.entries ?? []);
    return entries.map((e) => withLabel(e, android ? apps : undefined));
  }, [unsupported, roots.data, list.data, android, apps]);

  return {
    places,
    privateData: unsupported ? 'notApplicable' : (list.data?.privateData ?? 'notApplicable'),
    isPending: unsupported ? roots.isPending : list.isPending,
    error: unsupported ? roots.error : list.error,
    refetch: () => void (unsupported ? roots.refetch() : list.refetch()),
  };
}

function withLabel(entry: AppFilesEntry, apps: AppInfo[] | undefined): AppPlace {
  if (entry.kind !== 'app' || entry.name !== entry.id) return { ...entry, label: entry.name, labelPending: false };
  const app = apps?.find((a) => a.id === entry.id);
  return { ...entry, label: app?.name ?? entry.id, labelPending: apps === undefined };
}

/** The place a root key belongs to, and which of its folders it is. */
export function findFolder(places: readonly AppPlace[], rootKey: string | null): { place: AppPlace; folder: AppFolder } | null {
  if (!rootKey) return null;
  for (const place of places) {
    const folder = place.folders.find((f) => f.rootKey === rootKey);
    if (folder) return { place, folder };
  }
  return null;
}

export const isEmptyPlace = (place: AppPlace) => place.folders.every((f) => f.empty);

/** Files, folders and bytes under some paths of a root, counted by the host. */
export function useFolderUsage(device: DeviceSummary | null, root: string | null, paths: readonly string[], enabled = true) {
  const client = useClient();
  const supported = device?.state === 'ready' && (device.capabilities.includes('appDocuments') || device.capabilities.includes('files'));
  return useQuery({
    queryKey: appFilesKeys.measure(device?.id ?? '', root ?? '', paths),
    queryFn: () => client.call('appFiles.measure', { deviceId: device!.id, root: root!, paths: [...paths] }),
    enabled: enabled && supported && root !== null && paths.length > 0,
    retry: false,
    staleTime: 30_000,
  });
}
