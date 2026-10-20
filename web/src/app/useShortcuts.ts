import { useEffect } from 'react';
import { NAV, SETTINGS_NAV, navEnabled } from '../shell/nav';
import { useUi } from '../state/ui';
import { useSelectedDevice } from './queries';

/** Ctrl+K / Ctrl+F palette, Ctrl+1–9 sections, Alt+←/→ back and forward. */
export function useShortcuts() {
  const device = useSelectedDevice();
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const ui = useUi.getState();
      const key = e.key.toLowerCase();
      if ((e.ctrlKey || e.metaKey) && !e.altKey && (key === 'k' || key === 'f')) {
        e.preventDefault();
        ui.setPaletteOpen(!ui.paletteOpen || key === 'f');
        return;
      }
      if ((e.ctrlKey || e.metaKey) && !e.altKey && /^[1-9]$/.test(e.key)) {
        const entry = [...NAV, SETTINGS_NAV][Number(e.key) - 1];
        if (entry && navEnabled(entry, device)) {
          e.preventDefault();
          ui.navigate(entry.section);
        }
        return;
      }
      if (e.altKey && !e.ctrlKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
        e.preventDefault();
        if (e.key === 'ArrowLeft') ui.goBack();
        else ui.goForward();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [device]);
}
