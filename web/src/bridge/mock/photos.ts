// Generates each mock phone's photo library: deterministic, newest first, with albums.

import type { PhotoAlbum, PhotoAsset, PhotoKind } from '../types';
import { ANDROID_ID, IPHONE_ID, MB, androidFolderNames, iphoneAlbumNames } from './mock-data';
import { createRandom } from './random';

export interface MockAsset extends PhotoAsset {
  /** Seed for the generated placeholder image. */
  seed: number;
  albumIds: string[];
}

export interface MockLibrary {
  assets: MockAsset[];
  /** Items the user hid in Photos: only in the "hidden" album, as on a real iPhone. */
  hidden: MockAsset[];
  byId: Map<string, MockAsset>;
  albums: PhotoAlbum[];
}

interface LibrarySpec {
  photos: number;
  videos: number;
  screenshots: number;
  live: number;
  seed: number;
  platform: 'ios' | 'android';
}

const specs: Record<string, LibrarySpec> = {
  // 12,481 photos (of which screenshots and Live Photos) + 732 videos
  [IPHONE_ID]: { photos: 12481, videos: 732, screenshots: 1180, live: 3920, seed: 17, platform: 'ios' },
  // 21,906 photos + 1,140 videos
  [ANDROID_ID]: { photos: 21906, videos: 1140, screenshots: 2410, live: 0, seed: 25, platform: 'android' },
};

/** The newest photo in the mock libraries. */
export const LIBRARY_NEWEST = Date.parse('2026-10-19T17:42:00Z');

const pad = (n: number, width: number) => String(n).padStart(width, '0');

function androidStamp(time: number) {
  const t = new Date(time);
  return (
    `${t.getUTCFullYear()}${pad(t.getUTCMonth() + 1, 2)}${pad(t.getUTCDate(), 2)}_` +
    `${pad(t.getUTCHours(), 2)}${pad(t.getUTCMinutes(), 2)}${pad(t.getUTCSeconds(), 2)}`
  );
}

export function generateLibrary(deviceId: string): MockLibrary {
  const spec = specs[deviceId];
  if (!spec) return { assets: [], hidden: [], byId: new Map(), albums: [] };
  const rnd = createRandom(spec.seed);
  const total = spec.photos + spec.videos;

  // Decide each item's kind up front so the counts are exact.
  const kinds: PhotoKind[] = [];
  for (let i = 0; i < spec.videos; i++) kinds.push('video');
  for (let i = 0; i < spec.screenshots; i++) kinds.push('screenshot');
  for (let i = 0; i < spec.live; i++) kinds.push('livePhoto');
  while (kinds.length < total) kinds.push('photo');
  for (let i = kinds.length - 1; i > 0; i--) {
    const j = Math.floor(rnd.next() * (i + 1));
    [kinds[i], kinds[j]] = [kinds[j]!, kinds[i]!];
  }

  const isIos = spec.platform === 'ios';
  const userAlbums = isIos ? iphoneAlbumNames : androidFolderNames;
  const userAlbumIds = userAlbums.map((_, i) => `${isIos ? 'album' : 'folder'}-${i + 1}`);

  const assets: MockAsset[] = new Array(total);
  let time = LIBRARY_NEWEST;
  for (let i = 0; i < total; i++) {
    // Bursts of shots seconds apart, then gaps of hours: about 7 years of photos.
    time -= rnd.chance(0.58) ? rnd.between(4, 600) * 1000 : rnd.between(1, 24) * 3600 * 1000;
    const kind = kinds[i]!;
    const number = total - i; // oldest is 1
    const ageYears = (LIBRARY_NEWEST - time) / (365 * 24 * 3600 * 1000);
    const portrait = rnd.chance(0.62);

    let width: number;
    let height: number;
    let size: number;
    let duration: number | null = null;
    let fileName: string;
    if (kind === 'video') {
      duration = Math.round(rnd.chance(0.75) ? rnd.between(3, 45) : rnd.between(45, 420));
      const fourK = rnd.chance(0.45);
      width = fourK ? 3840 : 1920;
      height = fourK ? 2160 : 1080;
      if (portrait) [width, height] = [height, width];
      size = Math.round(duration * (fourK ? 5.6 : 2.2) * MB * rnd.between(0.8, 1.2));
      fileName = isIos ? `IMG_${pad(number % 10000, 4)}.MOV` : `${androidStamp(time)}.mp4`;
    } else if (kind === 'screenshot') {
      width = isIos ? 1206 : 1440;
      height = isIos ? 2622 : 3120;
      size = Math.round(rnd.between(0.3, 1.8) * MB);
      fileName = isIos ? `IMG_${pad(number % 10000, 4)}.PNG` : `Screenshot_${androidStamp(time)}_One UI Home.jpg`;
    } else {
      const big = rnd.chance(0.12);
      width = big ? 8064 : 4032;
      height = big ? 6048 : 3024;
      if (portrait) [width, height] = [height, width];
      size = Math.round((big ? rnd.between(9, 16) : rnd.between(2.4, 6.5)) * MB);
      fileName = isIos ? `IMG_${pad(number % 10000, 4)}.HEIC` : `${androidStamp(time)}.jpg`;
    }

    const id = `${isIos ? 'ios' : 'and'}-${number}`;
    const albumIds: string[] = [];
    if (isIos) {
      if (kind !== 'screenshot') albumIds.push('camera');
      if (rnd.chance(0.16)) albumIds.push(rnd.pick(userAlbumIds));
    } else {
      // Every Android item sits in exactly one folder; most in Camera.
      if (kind === 'screenshot') albumIds.push('folder-2');
      else if (rnd.chance(0.68)) albumIds.push('folder-1');
      else albumIds.push(userAlbumIds[2 + Math.floor(rnd.next() * (userAlbumIds.length - 2))]!);
    }

    assets[i] = {
      id,
      fileName,
      kind,
      takenAt: new Date(time).toISOString(),
      size,
      width,
      height,
      durationSeconds: duration,
      isFavorite: rnd.chance(0.025),
      inCloudOnly: isIos && ageYears > 2 && rnd.chance(0.09),
      livePairId: kind === 'livePhoto' ? `${id}-mov` : null,
      seed: (spec.seed * 100_003 + number * 7919) >>> 0,
      albumIds,
    };
  }

  const hidden = isIos ? generateHidden(spec.seed, HIDDEN_COUNT) : [];
  const byId = new Map([...assets, ...hidden].map((a) => [a.id, a]));
  const albums = buildAlbums(assets, isIos, userAlbums, userAlbumIds);
  if (isIos) {
    // The iOS provider lists the Hidden album as id "hidden" (kind "user" until AlbumKind has Hidden).
    albums.push({ id: HIDDEN_ALBUM_ID, name: 'Hidden', kind: 'user', count: hidden.length });
  }
  return { assets, hidden, byId, albums };
}

export const HIDDEN_ALBUM_ID = 'hidden';
/** As many as the owner's own iPhone holds. */
export const HIDDEN_COUNT = 1847;

function generateHidden(seed: number, count: number): MockAsset[] {
  const rnd = createRandom(seed * 31 + 7);
  const items: MockAsset[] = [];
  let time = LIBRARY_NEWEST - 3 * 86_400_000;
  for (let i = 0; i < count; i++) {
    time -= rnd.between(0.2, 30) * 3600 * 1000;
    const video = rnd.chance(0.08);
    const number = count - i;
    items.push({
      id: `ios-hidden-${number}`,
      fileName: `IMG_H${pad(number, 4)}.${video ? 'MOV' : 'HEIC'}`,
      kind: video ? 'video' : 'photo',
      takenAt: new Date(time).toISOString(),
      size: Math.round((video ? rnd.between(20, 120) : rnd.between(2, 5)) * MB),
      width: video ? 1920 : 3024,
      height: video ? 1080 : 4032,
      durationSeconds: video ? Math.round(rnd.between(4, 60)) : null,
      isFavorite: false,
      inCloudOnly: false,
      livePairId: null,
      seed: (seed * 7_919 + number * 104_729) >>> 0,
      albumIds: [HIDDEN_ALBUM_ID],
    });
  }
  return items;
}

function buildAlbums(assets: MockAsset[], isIos: boolean, names: string[], ids: string[]): PhotoAlbum[] {
  const count = (pred: (a: MockAsset) => boolean) => assets.reduce((n, a) => (pred(a) ? n + 1 : n), 0);
  const inAlbum = (albumId: string) => count((a) => a.albumIds.includes(albumId));
  const smart: PhotoAlbum[] = [
    { id: 'all', name: isIos ? 'All photos' : 'All', kind: 'all', count: assets.length },
  ];
  if (isIos) smart.push({ id: 'camera', name: 'Camera', kind: 'camera', count: inAlbum('camera') });
  smart.push(
    { id: 'screenshots', name: 'Screenshots', kind: 'screenshots', count: count((a) => a.kind === 'screenshot') },
    { id: 'videos', name: 'Videos', kind: 'videos', count: count((a) => a.kind === 'video') },
  );
  if (isIos) smart.push({ id: 'live', name: 'Live Photos', kind: 'live', count: count((a) => a.kind === 'livePhoto') });
  smart.push({ id: 'favorites', name: 'Favourites', kind: 'favorites', count: count((a) => a.isFavorite) });

  const user = names.map<PhotoAlbum>((name, i) => ({
    id: ids[i]!,
    name,
    kind: isIos ? 'user' : 'folder',
    count: inAlbum(ids[i]!),
  }));
  return [...smart, ...user];
}

export function assetsInAlbum(library: MockLibrary, albumId: string | null | undefined): MockAsset[] {
  if (!albumId || albumId === 'all') return library.assets;
  if (albumId === HIDDEN_ALBUM_ID) return library.hidden;
  switch (albumId) {
    case 'screenshots':
      return library.assets.filter((a) => a.kind === 'screenshot');
    case 'videos':
      return library.assets.filter((a) => a.kind === 'video');
    case 'live':
      return library.assets.filter((a) => a.kind === 'livePhoto');
    case 'favorites':
      return library.assets.filter((a) => a.isFavorite);
    default:
      return library.assets.filter((a) => a.albumIds.includes(albumId));
  }
}

/** Strips the mock-only fields so a page of assets matches the contract exactly. */
export function toContractAsset(asset: MockAsset): PhotoAsset {
  const { seed: _seed, albumIds: _albumIds, ...rest } = asset;
  return rest;
}
