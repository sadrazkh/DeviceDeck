import { useEffect } from 'react';
import { resolvedTheme, usePrefs } from '../state/prefs';

/**
 * Mirrors the page state onto <html> for tokens.css: lang/dir (Persian mirrors the layout),
 * data-theme, data-density, data-nav and data-host (webview keeps the root transparent for Mica).
 */
export function useDocumentState(host: 'webview' | 'browser') {
  const language = usePrefs((s) => s.language);
  const theme = usePrefs((s) => resolvedTheme(s));
  const density = usePrefs((s) => s.density);
  const nav = usePrefs((s) => s.navLayout);
  const setSystemDark = usePrefs((s) => s.setSystemDark);

  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)');
    setSystemDark(query.matches);
    const onChange = () => setSystemDark(query.matches);
    query.addEventListener('change', onChange);
    return () => query.removeEventListener('change', onChange);
  }, [setSystemDark]);

  useEffect(() => {
    const root = document.documentElement;
    root.lang = language;
    root.dir = language === 'fa' ? 'rtl' : 'ltr';
    root.dataset.theme = theme;
    root.dataset.density = density;
    root.dataset.nav = nav;
    root.dataset.host = host;
  }, [language, theme, density, nav, host]);
}
