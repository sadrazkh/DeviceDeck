// App files for the mock backend: what the host's IAppFilesSource answers, from the mock file system.
// An iPhone's apps are its "app:" roots; an Android's app folders are folders of its internal storage,
// mounted as roots of their own ("appmedia:<package>", "shared:Download"), as the host opens them.

import type { AppFilesEntry, AppFilesOverview, AppFolder, FolderUsage } from '../appFiles';
import type { AppInfo, DevicePlatform } from '../types';
import type { MockFileSystem } from './files';

const SHARED = ['Documents', 'Download'] as const;

function folderNames(fs: MockFileSystem, path: string): string[] {
  return fs.list('internal', path).filter((e) => e.isDirectory).map((e) => e.name);
}

export function mockAppFiles(platform: DevicePlatform, fs: MockFileSystem, apps: AppInfo[]): AppFilesOverview {
  if (platform === 'ios') {
    const entries = apps
      .filter((a) => a.fileSharing)
      .sort((a, b) => a.name.localeCompare(b.name))
      .map<AppFilesEntry>((a) => ({ id: a.id, name: a.name, kind: 'app', appId: a.id, folders: [{ rootKey: `app:${a.id}`, label: 'Documents', empty: false }] }));
    return { entries, privateData: 'notApplicable' };
  }

  const byPackage = new Map<string, AppFolder[]>();
  const add = (prefix: string, label: string, parent: string, names: string[]) => {
    for (const name of names) {
      const rootKey = `${prefix}${name}`;
      if (!fs.mount(rootKey, 'internal', `${parent}/${name}`)) continue;
      const folders = byPackage.get(name) ?? [];
      folders.push({ rootKey, label, empty: fs.usage('internal', `${parent}/${name}`).files === 0 });
      byPackage.set(name, folders);
    }
  };

  add('appmedia:', 'Media', '/Android/media', safe(() => folderNames(fs, '/Android/media')) ?? []);
  const data = safe(() => folderNames(fs, '/Android/data'));
  if (data && data.length > 0) add('appdata:', 'Data', '/Android/data', data);

  // Like the host: package names; the page shows the labels from apps.list.
  const entries: AppFilesEntry[] = [...byPackage.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([id, folders]) => ({ id, name: id, kind: 'app', appId: id, folders }));
  for (const name of SHARED) {
    const rootKey = `shared:${name}`;
    if (!fs.mount(rootKey, 'internal', `/${name}`)) continue;
    entries.push({ id: rootKey, name, kind: 'sharedFolder', appId: null, folders: [{ rootKey, label: name, empty: fs.usage('internal', `/${name}`).files === 0 }] });
  }
  return { entries, privateData: data && data.length > 0 ? 'allowed' : 'blocked' };
}

export function mockMeasure(fs: MockFileSystem, root: string, paths: string[]): FolderUsage {
  const items = paths.map((path) => ({ path, ...fs.usage(root, path) }));
  const norm = (p: string) => '/' + p.split('/').filter(Boolean).join('/');
  const outer = items.filter(
    (item, i) =>
      items.findIndex((o) => norm(o.path) === norm(item.path)) === i &&
      !items.some((o) => norm(o.path) !== norm(item.path) && (norm(o.path) === '/' || norm(item.path).startsWith(norm(o.path) + '/'))),
  );
  return {
    items,
    files: outer.reduce((n, i) => n + i.files, 0),
    folders: outer.reduce((n, i) => n + i.folders, 0),
    bytes: outer.reduce((n, i) => n + i.bytes, 0),
  };
}

function safe<T>(run: () => T): T | null {
  try {
    return run();
  } catch {
    return null;
  }
}
