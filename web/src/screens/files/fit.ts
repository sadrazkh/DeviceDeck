// Sizing parts of the file browser from the space they really have (Files shows two panes side by side, so the
// window's width says little): a ResizeObserver on the box, and a level that steps tighter while things don't fit.

import { useLayoutEffect, useState, type RefObject } from 'react';

/** The element's inner width, kept up to date. */
export function useWidth(ref: RefObject<HTMLElement | null>): number {
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    setWidth(element.clientWidth);
    const observer = new ResizeObserver(() => setWidth(element.clientWidth));
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref]);
  return width;
}

interface FitState {
  level: number;
  reset: string;
  /** The box's width when each level was found too wide: a looser level is tried again only once it is wider. */
  failedAt: number[];
}

/**
 * The roomiest level (0 … max) at which `fits()` holds, measured after each layout: one level tighter while it doesn't
 * fit, one looser once the box is wider than where that looser level last failed (so it settles, never flickers).
 * `reset` names what has to fit (a folder's name, the language); when it changes the search starts again at 0.
 * Layout effects run before the browser paints, so only the level that fits is ever seen.
 */
export function useFitLevel(box: RefObject<HTMLElement | null>, max: number, fits: () => boolean, reset: string): number {
  useWidth(box); // a new width is a new layout to check
  const [state, setState] = useState<FitState>({ level: 0, reset, failedAt: [] });
  const current = state.reset === reset ? state : { level: 0, reset, failedAt: [] };
  if (current !== state) setState(current);

  useLayoutEffect(() => {
    const element = box.current;
    if (!element || element.clientWidth === 0) return;
    const at = element.clientWidth;
    if (!fits()) {
      if (current.level < max) {
        const failedAt = [...current.failedAt];
        failedAt[current.level] = at;
        setState({ ...current, level: current.level + 1, failedAt });
      }
    } else if (current.level > 0 && at > (current.failedAt[current.level - 1] ?? Number.POSITIVE_INFINITY)) {
      setState({ ...current, level: current.level - 1 });
    }
  });

  return Math.min(current.level, max);
}
