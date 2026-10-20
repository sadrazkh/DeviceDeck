// Selection rules shared by the photo grid, the file list and the apps table:
// click, Ctrl+click, Shift ranges (added with Ctrl+Shift), Ctrl+A and Esc.

export interface Selection {
  ids: ReadonlySet<string>;
  /** Where a Shift range starts: the last item clicked without Shift. */
  anchor: string | null;
}

export const emptySelection: Selection = { ids: new Set(), anchor: null };

export interface ClickModifiers {
  ctrl?: boolean;
  shift?: boolean;
}

/** `order` is the visible order of every selectable id. */
export function clickSelect(selection: Selection, id: string, mods: ClickModifiers, order: readonly string[]): Selection {
  if (mods.shift && selection.anchor !== null) {
    const from = order.indexOf(selection.anchor);
    const to = order.indexOf(id);
    if (from >= 0 && to >= 0) {
      const [start, end] = from <= to ? [from, to] : [to, from];
      const range = order.slice(start, end + 1);
      const ids = new Set(mods.ctrl ? selection.ids : []);
      for (const r of range) ids.add(r);
      return { ids, anchor: selection.anchor };
    }
  }
  if (mods.ctrl) {
    const ids = new Set(selection.ids);
    if (ids.has(id)) ids.delete(id);
    else ids.add(id);
    return { ids, anchor: id };
  }
  return { ids: new Set([id]), anchor: id };
}

/** Selection-mode toggle (the tick on a photo tile): never clears the others. */
export function toggleSelect(selection: Selection, id: string): Selection {
  const ids = new Set(selection.ids);
  if (ids.has(id)) ids.delete(id);
  else ids.add(id);
  return { ids, anchor: id };
}

export function selectAll(order: readonly string[]): Selection {
  return { ids: new Set(order), anchor: order[0] ?? null };
}

/** Adds a group (a month) or removes it when every item in it is already selected. */
export function toggleGroup(selection: Selection, group: readonly string[]): Selection {
  const allIn = group.length > 0 && group.every((id) => selection.ids.has(id));
  const ids = new Set(selection.ids);
  for (const id of group) {
    if (allIn) ids.delete(id);
    else ids.add(id);
  }
  return { ids, anchor: selection.anchor };
}

/** Drops ids that no longer exist (after a delete or a reload). */
export function pruneSelection(selection: Selection, existing: ReadonlySet<string>): Selection {
  let changed = false;
  const ids = new Set<string>();
  for (const id of selection.ids) {
    if (existing.has(id)) ids.add(id);
    else changed = true;
  }
  if (!changed) return selection;
  return { ids, anchor: selection.anchor && existing.has(selection.anchor) ? selection.anchor : null };
}
