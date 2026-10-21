// Files the host handed over from a File Explorer drop (see components/nativeDrop.ts) know their PC path. When the
// page passes them back (files.copyToDevice and friends), the WebView2 transport sends those paths, the way it sends
// a picked file's path, instead of the File objects.

const paths = new WeakMap<File, string>();

export function registerNativeFile(file: File, path: string): File {
  paths.set(file, path);
  return file;
}

export function nativePathOf(file: File): string | undefined {
  return paths.get(file);
}
