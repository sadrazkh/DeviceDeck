import type { FileEntry } from '../../bridge/types';
import type { MessageKey } from '../../i18n';

export type FileKind = 'folder' | 'video' | 'image' | 'audio' | 'document' | 'archive' | 'app' | 'subtitle' | 'file';

const EXT: Record<string, FileKind> = {
  mkv: 'video', mp4: 'video', mov: 'video', avi: 'video', m4v: 'video', webm: 'video',
  jpg: 'image', jpeg: 'image', png: 'image', heic: 'image', gif: 'image', webp: 'image', dng: 'image',
  mp3: 'audio', m4a: 'audio', flac: 'audio', wav: 'audio', aac: 'audio', ogg: 'audio', m3u: 'audio',
  pdf: 'document', doc: 'document', docx: 'document', xls: 'document', xlsx: 'document', txt: 'document', epub: 'document', ppt: 'document', pptx: 'document',
  zip: 'archive', rar: 'archive', '7z': 'archive', tar: 'archive', gz: 'archive',
  apk: 'app', apks: 'app', xapk: 'app', ipa: 'app',
  srt: 'subtitle', ass: 'subtitle', vtt: 'subtitle',
};

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
}

export function kindOf(entry: Pick<FileEntry, 'name' | 'isDirectory'>): FileKind {
  if (entry.isDirectory) return 'folder';
  return EXT[extensionOf(entry.name)] ?? 'file';
}

export const KIND_LABEL: Record<Exclude<FileKind, 'file'>, MessageKey> = {
  folder: 'files.kind.folder',
  video: 'files.kind.video',
  image: 'files.kind.image',
  audio: 'files.kind.audio',
  document: 'files.kind.document',
  archive: 'files.kind.archive',
  app: 'files.kind.app',
  subtitle: 'files.kind.subtitle',
};

/** Folders Android 11+ closes to every app on a computer: shown greyed with the reason. */
export function isAndroidBlocked(path: string): boolean {
  return /^\/Android\/(data|obb)(\/|$)/i.test(path);
}

/** Lower case, Persian and Arabic digits as Latin ones, Arabic yeh and kaf as the Persian letters. */
function searchForm(text: string): string {
  return text
    .toLocaleLowerCase()
    .replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0))
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/ي/g, 'ی')
    .replace(/ك/g, 'ک');
}

/** Folder search: every word of the query appears somewhere in the name. */
export function matchesName(name: string, query: string): boolean {
  const target = searchForm(name);
  return searchForm(query)
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => target.includes(word));
}

export type SortKey = 'name' | 'size' | 'kind' | 'modified';

export function sortEntries(entries: readonly FileEntry[], key: SortKey, descending: boolean, collator: Intl.Collator): FileEntry[] {
  const sign = descending ? -1 : 1;
  return [...entries].sort((a, b) => {
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1; // folders first, always
    let diff = 0;
    if (key === 'size') diff = a.size - b.size;
    else if (key === 'kind') diff = collator.compare(kindOf(a) + extensionOf(a.name), kindOf(b) + extensionOf(b.name));
    else if (key === 'modified') diff = (a.modified ?? '').localeCompare(b.modified ?? '');
    if (diff === 0) diff = collator.compare(a.name, b.name);
    return diff * sign;
  });
}

export function parentPath(path: string): string {
  const parts = path.split('/').filter(Boolean);
  parts.pop();
  return '/' + parts.join('/');
}

export function joinPath(dir: string, name: string): string {
  return dir === '/' ? `/${name}` : `${dir}/${name}`;
}
