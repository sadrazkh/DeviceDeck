// This PC for the Files screen: roots, folder listings and the path helpers they need.

import { useQuery } from '@tanstack/react-query';
import type { PcRoot } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';

export const pcKeys = {
  roots: ['pc.roots'] as const,
  list: (path: string) => ['pc.list', path] as const,
};

export function usePcRoots() {
  const client = useClient();
  return useQuery({ queryKey: pcKeys.roots, queryFn: () => client.call('pc.roots'), staleTime: 30_000 });
}

export function usePcList(path: string | null) {
  const client = useClient();
  return useQuery({
    queryKey: pcKeys.list(path ?? ''),
    queryFn: () => client.call('pc.list', { path: path! }),
    enabled: !!path,
    retry: false,
  });
}

/** "C:\Users\Nima" → "C:\Users"; "C:\" → null (the top). UNC shares stop at \\server\share. */
export function pcParent(path: string): string | null {
  const trimmed = path.replace(/[\\/]+$/, '');
  if (/^[A-Za-z]:$/.test(trimmed)) return null;
  const unc = /^\\\\[^\\]+\\[^\\]+$/.test(trimmed);
  if (unc) return null;
  const cut = trimmed.lastIndexOf('\\');
  if (cut < 0) return null;
  const parent = trimmed.slice(0, cut);
  return /^[A-Za-z]:$/.test(parent) ? `${parent}\\` : parent;
}

export function pcJoin(folder: string, name: string): string {
  return folder.endsWith('\\') ? `${folder}${name}` : `${folder}\\${name}`;
}

/** The last segment, or the drive ("C:") for a root. */
export function pcName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '');
  const cut = trimmed.lastIndexOf('\\');
  return cut < 0 ? trimmed : trimmed.slice(cut + 1);
}

/** The known folder or drive a path is in, for the label next to it. */
export function rootOf(path: string, roots: readonly PcRoot[]): PcRoot | undefined {
  const lower = path.toLowerCase();
  return [...roots]
    .filter((r) => {
      const root = r.path.replace(/[\\/]+$/, '').toLowerCase();
      return lower === root || lower.startsWith(`${root}\\`) || lower === `${root}\\`;
    })
    .sort((a, b) => b.path.length - a.path.length)[0];
}
