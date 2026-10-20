// Mouse, touch and keyboard in the mirror window → Android input for scrcpy-server.

/** Android KeyEvent codes DeviceDeck sends. */
export const KEY = {
  home: 3,
  back: 4,
  volumeUp: 24,
  volumeDown: 25,
  power: 26,
  tab: 61,
  space: 62,
  enter: 66,
  del: 67,
  menu: 82,
  pageUp: 92,
  pageDown: 93,
  escape: 111,
  forwardDel: 112,
  moveHome: 122,
  moveEnd: 123,
  appSwitch: 187,
  dpadUp: 19,
  dpadDown: 20,
  dpadLeft: 21,
  dpadRight: 22,
} as const;

const KEYS_BY_NAME: Record<string, number> = {
  Enter: KEY.enter,
  Backspace: KEY.del,
  Delete: KEY.forwardDel,
  Tab: KEY.tab,
  Escape: KEY.back,
  ArrowUp: KEY.dpadUp,
  ArrowDown: KEY.dpadDown,
  ArrowLeft: KEY.dpadLeft,
  ArrowRight: KEY.dpadRight,
  Home: KEY.moveHome,
  End: KEY.moveEnd,
  PageUp: KEY.pageUp,
  PageDown: KEY.pageDown,
};

export type KeyAction = { kind: 'key'; keyCode: number } | { kind: 'text'; text: string } | null;

/** What a key press sends: a key code for editing and navigation keys, the text for printable characters. */
export function keyAction(e: { key: string; ctrlKey: boolean; altKey: boolean; metaKey: boolean }): KeyAction {
  if (e.ctrlKey || e.metaKey || e.altKey) return null;
  const code = KEYS_BY_NAME[e.key];
  if (code !== undefined) return { kind: 'key', keyCode: code };
  // One character (any script, emoji included) is text; "Shift", "F5" and the like are not.
  if ([...e.key].length === 1) return { kind: 'text', text: e.key };
  return null;
}

export interface Rect {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** A pointer position on the shown video, in the video's own pixels; null outside it. */
export function toVideoPoint(clientX: number, clientY: number, shown: Rect, video: { width: number; height: number }): { x: number; y: number } | null {
  if (shown.width <= 0 || shown.height <= 0 || video.width <= 0 || video.height <= 0) return null;
  const fx = (clientX - shown.left) / shown.width;
  const fy = (clientY - shown.top) / shown.height;
  if (fx < 0 || fy < 0 || fx > 1 || fy > 1) return null;
  return {
    x: Math.min(video.width - 1, Math.max(0, Math.round(fx * video.width))),
    y: Math.min(video.height - 1, Math.max(0, Math.round(fy * video.height))),
  };
}

/** The largest size with the video's shape that fits the box. */
export function fitSize(box: { width: number; height: number }, video: { width: number; height: number }): { width: number; height: number } {
  if (video.width <= 0 || video.height <= 0 || box.width <= 0 || box.height <= 0) return { width: 0, height: 0 };
  const scale = Math.min(box.width / video.width, box.height / video.height);
  return { width: Math.floor(video.width * scale), height: Math.floor(video.height * scale) };
}

/** Wheel deltas (pixels, down positive) → scrcpy notches (-16..16, up positive). */
export function wheelToScroll(deltaX: number, deltaY: number, deltaMode: number): { hScroll: number; vScroll: number } {
  const unit = deltaMode === 1 ? 1 / 3 : deltaMode === 2 ? 3 : 1 / 100; // lines, pages, pixels
  const clamp = (v: number) => Math.max(-16, Math.min(16, v));
  return { hScroll: clamp(-deltaX * unit), vScroll: clamp(-deltaY * unit) };
}
