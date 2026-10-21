// An in-memory file system per phone root for the mock backend.

import type { FileEntry, FileRoot } from '../types';
import { ANDROID_ID, GB, IPHONE_ID, KB, MB } from './mock-data';
import { createRandom } from './random';

interface Node {
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string;
  readOnly: boolean;
  /** Listing this folder fails with this error code. */
  denied?: 'permission_denied';
  children?: Map<string, Node>;
  /** Children generated on first listing (the 20,000-item folder). */
  lazy?: () => Node[];
}

type Spec = [name: string, size: number | Spec[], options?: { readOnly?: boolean; denied?: boolean; daysAgo?: number; lazy?: () => Node[] }];

const BASE = Date.parse('2026-10-19T12:00:00Z');

function build(specs: Spec[], rndSeed: number): Map<string, Node> {
  const rnd = createRandom(rndSeed);
  const map = new Map<string, Node>();
  for (const [name, content, options] of specs) {
    const isDirectory = Array.isArray(content);
    const modified = new Date(BASE - (options?.daysAgo ?? rnd.between(0, 400)) * 86_400_000).toISOString();
    const node: Node = {
      name,
      isDirectory,
      size: isDirectory ? 0 : (content as number),
      modified,
      readOnly: options?.readOnly ?? false,
    };
    if (options?.denied) node.denied = 'permission_denied';
    if (isDirectory) node.children = build(content as Spec[], rndSeed + name.length * 31);
    if (options?.lazy) node.lazy = options.lazy;
    map.set(name.toLowerCase(), node);
  }
  return map;
}

function cameraFolder(count: number): () => Node[] {
  return () => {
    const rnd = createRandom(4242);
    const nodes: Node[] = [];
    let time = Date.parse('2026-10-19T17:00:00Z');
    for (let i = 0; i < count; i++) {
      time -= rnd.between(30, 9000) * 1000;
      const t = new Date(time);
      const stamp = t.toISOString().replace(/[-:]/g, '').replace('T', '_').slice(0, 15);
      const video = rnd.chance(0.05);
      nodes.push({
        name: `${stamp}${video ? '.mp4' : '.jpg'}`,
        isDirectory: false,
        size: Math.round(video ? rnd.between(20, 400) * MB : rnd.between(2.5, 7) * MB),
        modified: t.toISOString(),
        readOnly: false,
      });
    }
    return nodes;
  };
}

const iphoneRoots: Record<string, Spec[]> = {
  'app:org.videolan.vlc-ios': [
    ['Subtitles', [
      ['Persepolis.fa.srt', Math.round(92 * KB)],
      ['The Grand Budapest Hotel.en.srt', Math.round(108 * KB)],
    ]],
    ['Persepolis.mkv', Math.round(1.21 * GB), { daysAgo: 12 }],
    ['The Grand Budapest Hotel.mp4', Math.round(1.88 * GB), { daysAgo: 40 }],
  ],
  'app:com.readdle.ReaddleDocsIPad': [
    ['Contracts', [
      ['Lease 2026.pdf', Math.round(1.4 * MB)],
      ['Car insurance.pdf', Math.round(820 * KB)],
    ]],
    ['Scans', [
      ['Scan 18 Oct 2026.pdf', Math.round(3.2 * MB), { daysAgo: 1 }],
      ['Passport copy.pdf', Math.round(2.1 * MB)],
    ]],
    ['Inbox', []],
    ['Recipes.docx', Math.round(640 * KB)],
    ['Manual — coffee machine.pdf', Math.round(12.6 * MB)],
    ['Budget 2026.xlsx', Math.round(220 * KB), { daysAgo: 3 }],
  ],
  'app:com.goodiware.GoodReader': [],
  'app:com.firecore.infuse': [
    ['Movies', [
      ['A Separation (2011).mkv', Math.round(2.3 * GB)],
      ['Children of Heaven (1997).mp4', Math.round(1.4 * GB)],
      ['About Elly (2009).mkv', Math.round(1.0 * GB)],
    ]],
    ['Library Cache', [['metadata.db', Math.round(18 * MB)]], { readOnly: true }],
  ],
  media: [
    ['DCIM', [['100APPLE', [['IMG_0001.HEIC', Math.round(3.1 * MB)]]]], { readOnly: true }],
    ['Downloads', [['downloads.28.sqlitedb', Math.round(412 * KB)]]],
    ['Books', [['Sync', []]]],
    ['PhotoData', [], { denied: true }],
    ['iTunes_Control', [['Music', []]], { readOnly: true }],
    ['Recordings', [['New Recording 12.m4a', Math.round(8.4 * MB)]]],
  ],
};

const androidRoots: Record<string, Spec[]> = {
  internal: [
    ['Alarms', []],
    ['Android', [
      ['data', [], { denied: true }],
      ['obb', [], { denied: true }],
      ['media', [
        ['com.whatsapp', [['WhatsApp', [
          ['Media', [
            ['WhatsApp Documents', [['Invoice-0921.pdf', Math.round(211 * KB)]]],
            ['WhatsApp Voice Notes', [['PTT-20261012-WA0003.opus', Math.round(48 * KB)]]],
            // Round 4 (whatsapp): photos, and WhatsApp's own encrypted chat backups next to the media.
            ['WhatsApp Images', [['IMG-20261012-WA0001.jpg', Math.round(212 * KB)], ['IMG-20261013-WA0004.jpg', Math.round(188 * KB), { daysAgo: 2 }]]],
          ]],
          ['Databases', [['msgstore.db.crypt14', Math.round(38 * MB), { daysAgo: 0 }], ['msgstore-2026-10-19.1.db.crypt14', Math.round(37.6 * MB), { daysAgo: 1 }]]],
          ['Backups', [['backup_settings.json.crypt1', Math.round(1 * KB)]]],
        ]]]],
        ['org.telegram.messenger', [['Telegram', [
          ['Telegram Documents', [['Lecture 7 slides.pdf', Math.round(6.1 * MB), { daysAgo: 6 }]]],
          ['Telegram Video', [['VID_20261012_211500.mp4', Math.round(48 * MB), { daysAgo: 8 }]]],
        ]]]],
        ['com.spotify.music', []],
      ]],
    ]],
    ['DCIM', [
      ['Camera', [], { lazy: cameraFolder(20_000) }],
      ['Screenshots', [['Screenshot_20261019_101233_One UI Home.jpg', Math.round(1.1 * MB), { daysAgo: 0 }]]],
    ]],
    ['Documents', [
      ['Thesis draft v7.docx', Math.round(2.8 * MB), { daysAgo: 2 }],
      ['Bank statement Sep.pdf', Math.round(410 * KB)],
    ]],
    ['Download', [
      ['Termux-app_v0.119.0.apk', Math.round(112 * MB), { daysAgo: 50 }],
      ['Timetable.pdf', Math.round(760 * KB)],
      ['playlist.m3u', Math.round(3 * KB)],
    ]],
    ['Movies', [['Clip 2026-08-14.mp4', Math.round(640 * MB)]]],
    ['Music', [['Shajarian — Morgh-e Sahar.flac', Math.round(41 * MB)], ['Googoosh — Hejrat.mp3', Math.round(9.8 * MB)]]],
    ['Pictures', [['Snapseed', [['IMG_20261002_edit.jpg', Math.round(4.2 * MB)]]]]],
    ['Podcasts', []],
    ['Recordings', [['Voice 001.m4a', Math.round(3.6 * MB)]]],
    ['Ringtones', []],
  ],
};

export class MockFileSystem {
  private readonly roots = new Map<string, Map<string, Node>>();

  constructor(deviceId: string) {
    const specs = deviceId === IPHONE_ID ? iphoneRoots : deviceId === ANDROID_ID ? androidRoots : {};
    let seed = 7;
    for (const [key, spec] of Object.entries(specs)) this.roots.set(key, build(spec, seed++));
  }

  static rootsFor(deviceId: string, fileSharingApps: { id: string; name: string }[]): FileRoot[] {
    if (deviceId === IPHONE_ID) {
      return [
        ...fileSharingApps.map<FileRoot>((app) => ({
          key: `app:${app.id}`,
          label: app.name,
          kind: 'appDocuments',
          appId: app.id,
        })),
        { key: 'media', label: 'Media folder', kind: 'media', appId: null },
      ];
    }
    if (deviceId === ANDROID_ID) {
      return [{ key: 'internal', label: 'Internal storage', kind: 'internalStorage', appId: null }];
    }
    return [];
  }

  hasRoot(root: string): boolean {
    return this.roots.has(root);
  }

  /** Throws { code } on a missing path or a blocked folder. */
  list(root: string, path: string): FileEntry[] {
    const dir = this.resolveDir(root, path);
    if (dir.denied) throw { code: dir.denied };
    return [...this.childrenOf(dir).values()].map((n) => toEntry(joinPath(path, n.name), n));
  }

  stat(root: string, path: string): FileEntry | null {
    const node = this.find(root, path);
    return node ? toEntry(normalise(path), node) : null;
  }

  mkdir(root: string, path: string): FileEntry {
    const { parent, name } = this.parentOf(root, path);
    if (parent.readOnly) throw { code: 'permission_denied' };
    const children = this.childrenOf(parent);
    if (children.has(name.toLowerCase())) throw { code: 'already_exists' };
    const node: Node = { name, isDirectory: true, size: 0, modified: new Date().toISOString(), readOnly: false, children: new Map() };
    children.set(name.toLowerCase(), node);
    return toEntry(normalise(path), node);
  }

  rename(root: string, from: string, to: string): FileEntry {
    const source = this.parentOf(root, from);
    const target = this.parentOf(root, to);
    const sourceChildren = this.childrenOf(source.parent);
    const node = sourceChildren.get(source.name.toLowerCase());
    if (!node) throw { code: 'path_not_found' };
    if (node.readOnly || source.parent.readOnly) throw { code: 'permission_denied' };
    const targetChildren = this.childrenOf(target.parent);
    const clash = targetChildren.get(target.name.toLowerCase());
    if (clash && clash !== node) throw { code: 'already_exists' };
    sourceChildren.delete(source.name.toLowerCase());
    node.name = target.name;
    node.modified = new Date().toISOString();
    targetChildren.set(target.name.toLowerCase(), node);
    return toEntry(normalise(to), node);
  }

  delete(root: string, paths: string[], recursive: boolean): number {
    let deleted = 0;
    for (const path of paths) {
      const { parent, name } = this.parentOf(root, path);
      const children = this.childrenOf(parent);
      const node = children.get(name.toLowerCase());
      if (!node) throw { code: 'path_not_found' };
      if (node.readOnly || parent.readOnly) throw { code: 'permission_denied' };
      if (node.isDirectory && !recursive && this.childrenOf(node).size > 0) throw { code: 'permission_denied' };
      children.delete(name.toLowerCase());
      deleted++;
    }
    return deleted;
  }

  /** Adds or replaces a file (a finished copyToDevice item). */
  writeFile(root: string, path: string, size: number): void {
    const { parent, name } = this.parentOf(root, path);
    this.childrenOf(parent).set(name.toLowerCase(), {
      name,
      isDirectory: false,
      size,
      modified: new Date().toISOString(),
      readOnly: false,
    });
  }

  /** Opens a folder of another root as a root of its own (Android app folders); false when it isn't there. */
  mount(key: string, root: string, path: string): boolean {
    const node = this.find(root, path);
    if (!node || !node.isDirectory || node.denied) return false;
    this.roots.set(key, this.childrenOf(node));
    return true;
  }

  /** Files, folders (not counting the path itself) and bytes under a path; zeros when it is gone. */
  usage(root: string, path: string): { files: number; folders: number; bytes: number } {
    const node = this.find(root, path);
    if (!node) return { files: 0, folders: 0, bytes: 0 };
    if (!node.isDirectory) return { files: 1, folders: 0, bytes: node.size };
    const total = { files: 0, folders: 0, bytes: 0 };
    if (node.denied) return total;
    for (const child of this.childrenOf(node).values()) {
      const inner = this.usage(root, joinPath(path, child.name));
      total.files += inner.files;
      total.folders += inner.folders + (child.isDirectory ? 1 : 0);
      total.bytes += inner.bytes;
    }
    return total;
  }

  /** The total size of the files under a path (for copyToPc jobs). */
  sizeOf(root: string, path: string): number {
    const node = this.find(root, path);
    if (!node) return 0;
    if (!node.isDirectory) return node.size;
    let total = 0;
    for (const child of this.childrenOf(node).values()) total += this.sizeOf(root, joinPath(path, child.name));
    return total;
  }

  private find(root: string, path: string): Node | null {
    const tree = this.roots.get(root);
    if (!tree) return null;
    const parts = split(path);
    if (parts.length === 0) return { name: '', isDirectory: true, size: 0, modified: '', readOnly: false, children: tree };
    let children = tree;
    let node: Node | undefined;
    for (const part of parts) {
      node = children.get(part.toLowerCase());
      if (!node) return null;
      if (node.isDirectory) children = this.childrenOf(node);
    }
    return node ?? null;
  }

  private resolveDir(root: string, path: string): Node {
    if (!this.roots.has(root)) throw { code: 'path_not_found' };
    const node = this.find(root, path);
    if (!node || !node.isDirectory) throw { code: 'path_not_found' };
    return node;
  }

  private parentOf(root: string, path: string): { parent: Node; name: string } {
    const parts = split(path);
    const name = parts.pop();
    if (!name) throw { code: 'path_not_found' };
    return { parent: this.resolveDir(root, '/' + parts.join('/')), name };
  }

  private childrenOf(node: Node): Map<string, Node> {
    if (!node.children) node.children = new Map();
    if (node.lazy) {
      for (const child of node.lazy()) node.children.set(child.name.toLowerCase(), child);
      delete node.lazy;
    }
    return node.children;
  }

  // ---------- Long names (round 3, ui) ----------

  /** Adds folders and files under a folder of a root, making the root when it is missing (mock/longNames.ts). */
  addSample(root: string, path: string, specs: SampleSpec[], seed: number): void {
    if (!this.roots.has(root)) this.roots.set(root, new Map());
    const into = split(path).length === 0 ? this.roots.get(root)! : this.childrenOf(this.resolveDir(root, path));
    for (const [key, node] of build(specs, seed)) into.set(key, node);
  }
}

function split(path: string): string[] {
  return path.split('/').filter((p) => p.length > 0);
}

export function normalise(path: string): string {
  return '/' + split(path).join('/');
}

export function joinPath(dir: string, name: string): string {
  const base = normalise(dir);
  return base === '/' ? `/${name}` : `${base}/${name}`;
}

function toEntry(path: string, node: Node): FileEntry {
  return {
    path,
    name: node.name,
    isDirectory: node.isDirectory,
    size: node.size,
    modified: node.modified || null,
    readOnly: node.readOnly,
  };
}

// Long names (round 3, ui): the shape of mock/longNames.ts's sample.
export type SampleSpec = Spec;
