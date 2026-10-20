import { create } from 'zustand';

export const SECTIONS = [
  'overview',
  'photos',
  'files',
  'appDocuments',
  'apps',
  'contacts',
  'backup',
  'toolbox',
  'settings',
] as const;
export type Section = (typeof SECTIONS)[number];

export interface FilesPlace {
  root: string | null;
  path: string;
}

interface DevicePlace {
  section: Section;
  back: Section[];
  forward: Section[];
  albumId: string;
  files: FilesPlace;
  /** Where App documents is: root null = the grid of apps. */
  appDocs: FilesPlace;
}

const freshPlace = (section: Section = 'overview'): DevicePlace => ({
  section,
  back: [],
  forward: [],
  albumId: 'all',
  files: { root: null, path: '/' },
  appDocs: { root: null, path: '/' },
});

/** Where the page is: per phone, so switching phones keeps each one's place. */
interface UiState {
  selectedDeviceId: string | null;
  places: Record<string, DevicePlace>;
  /** Used when no phone is selected (Settings can still open). */
  globalSection: Section | null;
  drawerOpen: boolean;
  paletteOpen: boolean;
  indexProgress: Record<string, { done: number; total: number }>;
  /** Destinations of jobs this page started, for "Show in Explorer" (TransferJob has none). */
  jobDestinations: Record<string, string>;
  /** Phones whose Hidden album the user revealed. In memory only: it locks again when the app closes. */
  hiddenRevealed: Record<string, boolean>;
  /** The root (app) each copy this page started goes into, for progress on App documents cards. */
  jobRoots: Record<string, string>;

  selectDevice(id: string | null): void;
  navigate(section: Section): void;
  goBack(): void;
  goForward(): void;
  setAlbum(albumId: string): void;
  setFilesPlace(place: FilesPlace): void;
  setAppDocsPlace(place: FilesPlace): void;
  setDrawerOpen(open: boolean): void;
  setPaletteOpen(open: boolean): void;
  setIndexProgress(deviceId: string, done: number, total: number): void;
  rememberDestination(jobId: string, destination: string): void;
  setHiddenRevealed(deviceId: string, revealed: boolean): void;
  rememberJobRoot(jobId: string, root: string): void;
}

function placeOf(state: UiState, deviceId: string): DevicePlace {
  return state.places[deviceId] ?? freshPlace();
}

export const useUi = create<UiState>((set, get) => ({
  selectedDeviceId: null,
  places: {},
  globalSection: null,
  drawerOpen: false,
  paletteOpen: false,
  indexProgress: {},
  jobDestinations: {},
  hiddenRevealed: {},
  jobRoots: {},

  selectDevice: (id) =>
    set((s) => ({
      selectedDeviceId: id,
      globalSection: null,
      places: id && !s.places[id] ? { ...s.places, [id]: freshPlace() } : s.places,
    })),

  navigate: (section) => {
    const { selectedDeviceId: id } = get();
    if (!id) {
      set({ globalSection: section === 'settings' ? 'settings' : null });
      return;
    }
    set((s) => {
      const place = placeOf(s, id);
      if (place.section === section && s.globalSection === null) return s;
      return {
        globalSection: null,
        places: {
          ...s.places,
          [id]: { ...place, section, back: [...place.back, place.section].slice(-50), forward: [] },
        },
      };
    });
  },

  goBack: () => {
    const { selectedDeviceId: id } = get();
    if (!id) return;
    set((s) => {
      const place = placeOf(s, id);
      const previous = place.back.at(-1);
      if (!previous) return s;
      return {
        places: {
          ...s.places,
          [id]: { ...place, section: previous, back: place.back.slice(0, -1), forward: [place.section, ...place.forward] },
        },
      };
    });
  },

  goForward: () => {
    const { selectedDeviceId: id } = get();
    if (!id) return;
    set((s) => {
      const place = placeOf(s, id);
      const [next, ...rest] = place.forward;
      if (!next) return s;
      return {
        places: { ...s.places, [id]: { ...place, section: next, back: [...place.back, place.section], forward: rest } },
      };
    });
  },

  setAlbum: (albumId) => {
    const { selectedDeviceId: id } = get();
    if (!id) return;
    set((s) => ({ places: { ...s.places, [id]: { ...placeOf(s, id), albumId } } }));
  },

  setFilesPlace: (files) => {
    const { selectedDeviceId: id } = get();
    if (!id) return;
    set((s) => ({ places: { ...s.places, [id]: { ...placeOf(s, id), files } } }));
  },

  setAppDocsPlace: (appDocs) => {
    const { selectedDeviceId: id } = get();
    if (!id) return;
    set((s) => ({ places: { ...s.places, [id]: { ...placeOf(s, id), appDocs } } }));
  },

  setDrawerOpen: (drawerOpen) => set({ drawerOpen }),
  setPaletteOpen: (paletteOpen) => set({ paletteOpen }),
  setIndexProgress: (deviceId, done, total) =>
    set((s) => ({ indexProgress: { ...s.indexProgress, [deviceId]: { done, total } } })),
  rememberDestination: (jobId, destination) =>
    set((s) => ({ jobDestinations: { ...s.jobDestinations, [jobId]: destination } })),
  setHiddenRevealed: (deviceId, revealed) =>
    set((s) => ({ hiddenRevealed: { ...s.hiddenRevealed, [deviceId]: revealed } })),
  rememberJobRoot: (jobId, root) => set((s) => ({ jobRoots: { ...s.jobRoots, [jobId]: root } })),
}));

/** The section shown now for the selected phone. */
export function useSection(): Section {
  return useUi((s) => s.globalSection ?? (s.selectedDeviceId ? (s.places[s.selectedDeviceId]?.section ?? 'overview') : 'overview'));
}

export function useDevicePlace(): DevicePlace {
  return useUi((s) => (s.selectedDeviceId ? (s.places[s.selectedDeviceId] ?? defaultPlace) : defaultPlace));
}

const defaultPlace = freshPlace();
