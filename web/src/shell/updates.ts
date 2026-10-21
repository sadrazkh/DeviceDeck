// Updates you can see (round 4): which version is on offer, the What's new dialog's state, release notes, and the
// banner's "already seen" (per version, in this WebView's storage).

import { useQuery } from '@tanstack/react-query';
import { create } from 'zustand';
import type { UpdateStatus } from '../bridge/types';
import { useClient } from '../app/bridgeContext';

/** The version the title-bar button and the banner are about: downloaded (Setup) or out on GitHub (the zip). */
export function offerOf(status: UpdateStatus | undefined): { version: string; kind: 'ready' | 'available' } | null {
  if (!status?.newVersion) return null;
  if (status.state === 'ready') return { version: status.newVersion, kind: 'ready' };
  if (status.state === 'available') return { version: status.newVersion, kind: 'available' };
  return null;
}

/** What What's new is opened for: the update on offer, the version just installed (once), or the current one from Settings. */
export interface WhatsNewRequest {
  version: string;
  reason: 'offer' | 'installed' | 'current';
}

interface WhatsNewState {
  request: WhatsNewRequest | null;
  show(request: WhatsNewRequest): void;
  close(): void;
}

export const useWhatsNew = create<WhatsNewState>((set) => ({
  request: null,
  show: (request) => set({ request }),
  close: () => set({ request: null }),
}));

export function useReleaseNotes(version: string | null) {
  const client = useClient();
  return useQuery({
    queryKey: ['app.releaseNotes', version],
    queryFn: () => client.call('app.releaseNotes', { version: version! }),
    enabled: version !== null,
    staleTime: 10 * 60_000,
    retry: false,
  });
}

/** "DeviceDeck-0.4.0-win-x64-Setup.exe", as the release lists it. */
export function setupFileName(version: string, architecture: UpdateStatus['architecture']): string {
  return `DeviceDeck-${version}-win-${architecture}-Setup.exe`;
}

const BANNER_KEY = 'devicedeck.updateBanner';

/** The version whose banner was dismissed or acted on. Storage can be missing or refuse (private windows, tests). */
export function bannerSeen(): string | null {
  try {
    return localStorage.getItem(BANNER_KEY);
  } catch {
    return null;
  }
}

export function markBannerSeen(version: string) {
  try {
    localStorage.setItem(BANNER_KEY, version);
  } catch {
    // Then it shows again next time; nothing else depends on it.
  }
}
