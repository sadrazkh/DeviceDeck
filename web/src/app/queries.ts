// React Query hooks over the bridge. Event handlers (useBridgeEvents) keep the caches live.

import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import type { BridgeClient } from '../bridge';
import type { DeviceSummary, PhotoAsset, Settings, TransferJob } from '../bridge/types';
import { usePrefs } from '../state/prefs';
import { useUi } from '../state/ui';
import { useClient } from './bridgeContext';

export const keys = {
  hello: ['app.hello'] as const,
  drivers: ['drivers.status'] as const,
  devices: ['device.list'] as const,
  storage: (id: string) => ['device.storage', id] as const,
  battery: (id: string) => ['device.battery', id] as const,
  details: (id: string) => ['device.details', id] as const,
  albums: (id: string) => ['photos.albums', id] as const,
  photos: (id: string, albumId: string) => ['photos.list', id, albumId] as const,
  roots: (id: string) => ['files.roots', id] as const,
  files: (id: string, root: string, path: string) => ['files.list', id, root, path] as const,
  apps: (id: string, includeSystem: boolean) => ['apps.list', id, includeSystem] as const,
  transfers: ['transfers.list'] as const,
  settings: ['settings.get'] as const,
};

export function useHello() {
  const client = useClient();
  return useQuery({ queryKey: keys.hello, queryFn: () => client.call('app.hello'), staleTime: Infinity });
}

export function useDrivers() {
  const client = useClient();
  return useQuery({ queryKey: keys.drivers, queryFn: () => client.call('drivers.status') });
}

export function useDevices() {
  const client = useClient();
  return useQuery({ queryKey: keys.devices, queryFn: () => client.call('device.list'), staleTime: Infinity });
}

/** The phone the page is showing, or null. */
export function useSelectedDevice(): DeviceSummary | null {
  const { data } = useDevices();
  const id = useUi((s) => s.selectedDeviceId);
  return useMemo(() => data?.find((d) => d.id === id) ?? null, [data, id]);
}

const isReady = (device: DeviceSummary | null | undefined): device is DeviceSummary => device?.state === 'ready';

export function useStorage(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: keys.storage(device?.id ?? ''),
    queryFn: () => client.call('device.storage', { deviceId: device!.id }),
    enabled: isReady(device) && device.capabilities.includes('storage'),
  });
}

export function useBattery(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: keys.battery(device?.id ?? ''),
    queryFn: () => client.call('device.battery', { deviceId: device!.id }),
    enabled: isReady(device) && device.capabilities.includes('battery'),
    refetchInterval: 60_000,
  });
}

export function useDetails(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: keys.details(device?.id ?? ''),
    queryFn: () => client.call('device.details', { deviceId: device!.id }),
    enabled: isReady(device) && device.capabilities.includes('info'),
  });
}

export function useAlbums(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: keys.albums(device?.id ?? ''),
    queryFn: () => client.call('photos.albums', { deviceId: device!.id }),
    enabled: isReady(device) && device.capabilities.includes('photosRead'),
  });
}

const PHOTO_PAGE = 2000;

/**
 * Every asset of an album, fetched page by page in the background so the grid can group by month
 * while later pages arrive.
 */
export function usePhotoLibrary(device: DeviceSummary | null, albumId: string) {
  const client = useClient();
  const query = useInfiniteQuery({
    queryKey: keys.photos(device?.id ?? '', albumId),
    queryFn: ({ pageParam }) =>
      client.call('photos.list', { deviceId: device!.id, albumId: albumId === 'all' ? null : albumId, offset: pageParam, limit: PHOTO_PAGE }),
    initialPageParam: 0,
    getNextPageParam: (last, pages) => {
      const loaded = pages.reduce((n, p) => n + p.items.length, 0);
      return loaded < last.total && last.items.length > 0 ? loaded : undefined;
    },
    enabled: isReady(device) && device.capabilities.includes('photosRead'),
    staleTime: 5 * 60_000,
  });
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = query;
  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage) void fetchNextPage();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const assets = useMemo<PhotoAsset[]>(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);
  const total = query.data?.pages.at(-1)?.total ?? 0;
  return { ...query, assets, total };
}

export function useFileRoots(device: DeviceSummary | null) {
  const client = useClient();
  return useQuery({
    queryKey: keys.roots(device?.id ?? ''),
    queryFn: () => client.call('files.roots', { deviceId: device!.id }),
    enabled: isReady(device) && device.capabilities.includes('files'),
  });
}

export function useFileList(device: DeviceSummary | null, root: string | null, path: string) {
  const client = useClient();
  return useQuery({
    queryKey: keys.files(device?.id ?? '', root ?? '', path),
    queryFn: () => client.call('files.list', { deviceId: device!.id, root: root!, path }),
    enabled: isReady(device) && root !== null,
    retry: false,
  });
}

export function useApps(device: DeviceSummary | null, includeSystem: boolean) {
  const client = useClient();
  return useQuery({
    queryKey: keys.apps(device?.id ?? '', includeSystem),
    queryFn: () => client.call('apps.list', { deviceId: device!.id, includeSystem }),
    enabled: isReady(device) && device.capabilities.includes('appsList'),
  });
}

export function useTransfers() {
  const client = useClient();
  return useQuery({ queryKey: keys.transfers, queryFn: () => client.call('transfers.list'), staleTime: Infinity });
}

export function useSettings() {
  const client = useClient();
  return useQuery({ queryKey: keys.settings, queryFn: () => client.call('settings.get'), staleTime: Infinity });
}

/** Changes settings: the page updates at once, then follows what the host stored. */
export function useUpdateSettings() {
  const client = useClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (patch: Partial<Settings>) => client.call('settings.set', patch),
    onMutate: (patch) => {
      usePrefs.getState().apply(patch);
      const previous = queryClient.getQueryData<Settings>(keys.settings);
      if (previous) queryClient.setQueryData(keys.settings, { ...previous, ...patch });
      return { previous };
    },
    onError: (_error, _patch, context) => {
      if (context?.previous) {
        queryClient.setQueryData(keys.settings, context.previous);
        usePrefs.getState().apply(context.previous);
      }
    },
    onSuccess: (settings) => {
      queryClient.setQueryData(keys.settings, settings);
      usePrefs.getState().apply(settings);
    },
  });
}

// ---------- Live updates from host events ----------

export interface TransferTransition {
  job: TransferJob;
  previous: TransferJob | undefined;
}

export interface DeviceTransition {
  device: DeviceSummary;
  previous: DeviceSummary | undefined;
}

function upsert<T extends { id: string }>(list: T[] | undefined, item: T): T[] {
  if (!list) return [item];
  const index = list.findIndex((x) => x.id === item.id);
  if (index < 0) return [...list, item];
  const next = list.slice();
  next[index] = item;
  return next;
}

/** Refreshes a phone's data when it becomes ready again (after Trust, a reconnect or a reboot). */
function invalidateDevice(queryClient: QueryClient, deviceId: string) {
  for (const prefix of ['device.storage', 'device.battery', 'device.details', 'photos.albums', 'files.roots', 'files.list', 'apps.list']) {
    void queryClient.invalidateQueries({ queryKey: [prefix, deviceId] });
  }
}

export function connectBridgeEvents(
  client: BridgeClient,
  queryClient: QueryClient,
  handlers: {
    onTransfer?: (t: TransferTransition) => void;
    onDevice?: (t: DeviceTransition) => void;
    onDeviceRemoved?: (deviceId: string) => void;
  } = {},
): () => void {
  const offs = [
    client.on('device.added', (device) => {
      const previous = queryClient.getQueryData<DeviceSummary[]>(keys.devices)?.find((d) => d.id === device.id);
      queryClient.setQueryData<DeviceSummary[]>(keys.devices, (list) => upsert(list, device));
      handlers.onDevice?.({ device, previous });
    }),
    client.on('device.changed', (device) => {
      const previous = queryClient.getQueryData<DeviceSummary[]>(keys.devices)?.find((d) => d.id === device.id);
      queryClient.setQueryData<DeviceSummary[]>(keys.devices, (list) => upsert(list, device));
      if (device.state === 'ready' && previous && previous.state !== 'ready') invalidateDevice(queryClient, device.id);
      handlers.onDevice?.({ device, previous });
    }),
    client.on('device.removed', ({ deviceId }) => {
      queryClient.setQueryData<DeviceSummary[]>(keys.devices, (list) => list?.filter((d) => d.id !== deviceId));
      handlers.onDeviceRemoved?.(deviceId);
    }),
    client.on('drivers.changed', (drivers) => {
      queryClient.setQueryData(keys.drivers, drivers);
    }),
    client.on('transfer.updated', (job) => {
      const previous = queryClient.getQueryData<TransferJob[]>(keys.transfers)?.find((j) => j.id === job.id);
      queryClient.setQueryData<TransferJob[]>(keys.transfers, (list) => upsert(list, job));
      handlers.onTransfer?.({ job, previous });
    }),
    client.on('photos.indexProgress', ({ deviceId, done, total }) => {
      useUi.getState().setIndexProgress(deviceId, done, total);
    }),
  ];
  return () => offs.forEach((off) => off());
}
