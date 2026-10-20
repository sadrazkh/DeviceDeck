import { create } from 'zustand';
import type { Settings } from '../bridge/types';

export type NavLayout = 'rail' | 'tabs';

/**
 * The shell's layout switch. Direction A uses the side rail; B ("Midnight Pro", icon rail) and
 * C ("Device Stage", top tabs) are adopted by changing this default plus tokens.css.
 */
export const DEFAULT_NAV_LAYOUT: NavLayout = 'rail';

/** The appearance and language the page shows now, mirrored from the host's Settings. */
interface PrefsState {
  language: Settings['language'];
  theme: Settings['theme'];
  density: Settings['density'];
  navLayout: NavLayout;
  /** Settings.theme resolved against Windows' light/dark preference. */
  systemDark: boolean;
  apply(settings: Partial<Settings>): void;
  setNavLayout(layout: NavLayout): void;
  setSystemDark(dark: boolean): void;
}

export const usePrefs = create<PrefsState>((set) => ({
  language: 'en',
  theme: 'system',
  density: 'comfortable',
  navLayout: DEFAULT_NAV_LAYOUT,
  systemDark: false,
  apply: (settings) =>
    set((s) => ({
      language: settings.language ?? s.language,
      theme: settings.theme ?? s.theme,
      density: settings.density ?? s.density,
    })),
  setNavLayout: (navLayout) => set({ navLayout }),
  setSystemDark: (systemDark) => set({ systemDark }),
}));

export function resolvedTheme(state: Pick<PrefsState, 'theme' | 'systemDark'>): 'light' | 'dark' {
  return state.theme === 'system' ? (state.systemDark ? 'dark' : 'light') : state.theme;
}
