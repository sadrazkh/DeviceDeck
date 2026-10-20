// Shapes of the "App files" section of docs/bridge-contract.md, from
// src/DeviceDeck.Core/Abstractions/IAppFilesSource.cs.

export type AppFilesKind = 'app' | 'sharedFolder';

/** Can adb read the apps' private folders (Android/data)? notApplicable on an iPhone. */
export type AppDataAccess = 'notApplicable' | 'allowed' | 'blocked';

export interface AppFolder {
  /**
   * A file root: "app:<bundleId>" (an iPhone app's Documents), "appmedia:<package>", "appdata:<package>",
   * "shared:Documents", "shared:Download".
   */
  rootKey: string;
  /** "Documents", "Media", "Data", "Download": names the folder when an app has more than one. */
  label: string;
  /** Holds no file at all (Android checks; an iPhone always says false). */
  empty: boolean;
}

export interface AppFilesEntry {
  /** Bundle id or package name; a shared folder's root key. */
  id: string;
  /** The app's name when the host knows it (iPhone); on Android the package name, so show apps.list labels. */
  name: string;
  kind: AppFilesKind;
  /** For icon/{deviceId}/{appId}; null for a shared folder. */
  appId: string | null;
  /** At least one; the first is where a drop on the app's card goes. */
  folders: AppFolder[];
}

export interface AppFilesOverview {
  entries: AppFilesEntry[];
  privateData: AppDataAccess;
}

export interface PathUsage {
  path: string;
  files: number;
  /** Folders inside the path, not counting the path itself. */
  folders: number;
  bytes: number;
}

/** One item per path asked for, in order; totals count a path inside another one once. */
export interface FolderUsage {
  items: PathUsage[];
  files: number;
  folders: number;
  bytes: number;
}
