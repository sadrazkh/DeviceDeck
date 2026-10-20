// The mock's "This PC": a few drives and the usual user folders, for pc.roots / pc.list / pc.mkdir / pc.rename.

import type { PcEntry, PcRoot } from '../types';
import { GB, MB } from './mock-data';

/** A PC error with the contract's data.code. */
export class PcError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

interface Node {
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string;
  hidden?: boolean;
  children?: Map<string, Node>;
}

const HOME = 'C:\\Users\\Nima';
const DAY = 86_400_000;

function dir(name: string, children: Node[] = [], days = 3, hidden = false): Node {
  return {
    name,
    isDirectory: true,
    size: 0,
    modified: new Date(Date.UTC(2026, 9, 20) - days * DAY).toISOString(),
    hidden,
    children: new Map(children.map((c) => [c.name.toLowerCase(), c])),
  };
}

function file(name: string, size: number, days = 1, hidden = false): Node {
  return { name, isDirectory: false, size, modified: new Date(Date.UTC(2026, 9, 20, 9) - days * DAY).toISOString(), hidden };
}

/** Splits "C:\Users\Nima\Pictures" into ["C:", "Users", "Nima", "Pictures"]. */
function parts(path: string): string[] {
  return path.replace(/\//g, '\\').split('\\').filter(Boolean);
}

export class MockPc {
  private readonly drives = new Map<string, Node>([
    [
      'c:',
      dir('C:', [
        dir('Program Files', [dir('DeviceDeck', [file('DeviceDeck.exe', 182 * MB)])], 40),
        dir('Users', [
          dir('Nima', [
            dir('Desktop', [file('Shopping list.txt', 2_048, 2), file('desktop.ini', 282, 90, true)], 1),
            dir('Downloads', [file('VLC-3.0.21-win64.exe', 44 * MB, 6), file('Statement October.pdf', 312_000, 1)], 1),
            dir('Documents', [dir('Taxes', [file('Tax return 2025.pdf', 1_204_331, 210)], 210), file('Lease agreement 1405.pdf', 2_841_190, 20)], 5),
            dir('Pictures', [dir('Camera Roll', [], 30), file('Wallpaper.jpg', 4 * MB, 60)], 2),
            dir('Videos', [file('Birthday 1404.mp4', 820 * MB, 120)], 20),
            dir('Music', [], 200),
            dir('AppData', [], 1, true),
          ]),
        ], 100),
        dir('Windows', [], 30),
      ]),
    ],
    ['d:', dir('D:', [dir('Photos', [dir('2026', [], 3)], 3), dir('Phone files', [], 1), dir('Backups', [], 9)])],
  ]);

  roots(): PcRoot[] {
    const known: [PcRoot['kind'], string][] = [
      ['home', HOME],
      ['desktop', `${HOME}\\Desktop`],
      ['downloads', `${HOME}\\Downloads`],
      ['documents', `${HOME}\\Documents`],
      ['pictures', `${HOME}\\Pictures`],
      ['videos', `${HOME}\\Videos`],
      ['music', `${HOME}\\Music`],
    ];
    return [
      ...known.map(([kind, path]) => ({ path, label: parts(path).at(-1)!, kind, totalBytes: null, freeBytes: null })),
      { path: 'C:\\', label: 'Windows', kind: 'drive', totalBytes: 1000 * GB, freeBytes: 412 * GB },
      { path: 'D:\\', label: 'Data', kind: 'drive', totalBytes: 2000 * GB, freeBytes: 1310 * GB },
    ];
  }

  list(path: string, showHidden: boolean): PcEntry[] {
    const folder = this.find(path);
    if (!folder?.isDirectory) throw new PcError('path_not_found', 'That folder isn’t on this PC any more.');
    const base = this.normalise(path);
    return [...folder.children!.values()]
      .filter((n) => showHidden || !n.hidden)
      .map((n) => this.entry(base, n));
  }

  mkdir(path: string): PcEntry {
    const { parent, name } = this.parentOf(path);
    if (parent.children!.has(name.toLowerCase())) throw new PcError('already_exists', 'Something with that name is already in this folder.');
    const node = dir(name, [], 0);
    parent.children!.set(name.toLowerCase(), node);
    return this.entry(this.normalise(path).slice(0, -name.length - 1), node);
  }

  rename(path: string, name: string): PcEntry {
    const trimmed = name.trim();
    if (!trimmed || /[\\/:*?"<>|]/.test(trimmed)) throw new PcError('invalid_params', 'A name can’t be empty or contain \\ / : * ? " < > |.');
    const { parent, name: old } = this.parentOf(path);
    const node = parent.children!.get(old.toLowerCase());
    if (!node) throw new PcError('path_not_found', 'That file or folder isn’t on this PC any more.');
    if (trimmed.toLowerCase() !== old.toLowerCase() && parent.children!.has(trimmed.toLowerCase())) {
      throw new PcError('already_exists', 'Something with that name is already in this folder.');
    }
    parent.children!.delete(old.toLowerCase());
    node.name = trimmed;
    parent.children!.set(trimmed.toLowerCase(), node);
    return this.entry(this.normalise(path).slice(0, -old.length - 1), node);
  }

  /** A file the page may open with its default app. */
  requireFile(path: string): void {
    const node = this.find(path);
    if (!node || node.isDirectory) throw new PcError('path_not_found', 'That file isn’t on this PC any more.');
  }

  /** Adds copied files (the phone's, after files.copyToPc) so the PC pane shows them. */
  addFile(folder: string, name: string, size: number): void {
    let node = this.find(folder);
    if (!node) {
      try {
        this.mkdir(folder);
      } catch {
        return;
      }
      node = this.find(folder);
    }
    if (node?.isDirectory) node.children!.set(name.toLowerCase(), file(name, size, 0));
  }

  private find(path: string): Node | undefined {
    const [drive, ...rest] = parts(path);
    let node = drive ? this.drives.get(drive.toLowerCase()) : undefined;
    for (const segment of rest) {
      node = node?.children?.get(segment.toLowerCase());
    }
    return node;
  }

  private parentOf(path: string): { parent: Node; name: string } {
    const segments = parts(path);
    const name = segments.pop();
    const parent = segments.length > 0 ? this.find(segments.join('\\')) : undefined;
    if (!name || !parent?.isDirectory) throw new PcError('path_not_found', 'That folder isn’t on this PC any more.');
    return { parent, name };
  }

  private normalise(path: string): string {
    const segments = parts(path);
    return segments.length === 1 ? `${segments[0]}\\` : segments.join('\\');
  }

  private entry(base: string, node: Node): PcEntry {
    const path = base.endsWith('\\') ? `${base}${node.name}` : `${base}\\${node.name}`;
    return { path, name: node.name, isDirectory: node.isDirectory, size: node.size, modified: node.modified, readOnly: false, hidden: !!node.hidden };
  }
}
