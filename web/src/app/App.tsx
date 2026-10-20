import { MotionConfig } from 'framer-motion';
import { useEffect, useRef } from 'react';
import { AppShell } from '../shell/AppShell';
import { usePrefs, type NavLayout } from '../state/prefs';
import { SECTIONS, useUi, type Section } from '../state/ui';
import { useBridge } from './bridgeContext';
import { useDevices, useSettings } from './queries';
import { useDocumentState } from './useDocumentState';
import { useLiveUpdates } from './useLiveUpdates';
import { useShortcuts } from './useShortcuts';

/** Optional start state from the URL, for screenshots and demos: ?device=android&screen=photos&nav=tabs */
function startFromUrl() {
  const query = new URLSearchParams(location.search);
  const screen = query.get('screen');
  const nav = query.get('nav');
  return {
    device: query.get('device'),
    screen: SECTIONS.includes(screen as Section) ? (screen as Section) : null,
    nav: nav === 'tabs' || nav === 'rail' ? (nav as NavLayout) : null,
  };
}

export function App() {
  const { client } = useBridge();
  const settings = useSettings();
  const devices = useDevices();
  const applied = useRef(false);
  const started = useRef(false);

  useDocumentState(client.kind === 'webview' ? 'webview' : 'browser');
  useLiveUpdates();
  useShortcuts();

  useEffect(() => {
    if (settings.data && !applied.current) {
      applied.current = true;
      usePrefs.getState().apply(settings.data);
      const { nav } = startFromUrl();
      if (nav) usePrefs.getState().setNavLayout(nav);
    }
  }, [settings.data]);

  // Pick a phone when there is none: the URL's, else the first ready one, else the first.
  useEffect(() => {
    const list = devices.data;
    if (!list) return;
    const ui = useUi.getState();
    if (!started.current) {
      started.current = true;
      const { device, screen } = startFromUrl();
      const wanted =
        list.find((d) => d.id === device) ??
        (device === 'android' ? list.find((d) => d.platform === 'android') : device === 'iphone' ? list.find((d) => d.platform === 'ios') : undefined);
      const pick = wanted ?? list.find((d) => d.state === 'ready') ?? list[0];
      if (pick) ui.selectDevice(pick.id);
      if (screen) ui.navigate(screen);
      return;
    }
    if (ui.selectedDeviceId && !list.some((d) => d.id === ui.selectedDeviceId)) {
      ui.selectDevice(list[0]?.id ?? null);
    } else if (!ui.selectedDeviceId && list[0]) {
      ui.selectDevice(list[0].id);
    }
  }, [devices.data]);

  // Hold the first paint until the language is known, so Persian never flashes English.
  if (!settings.data && !settings.isError) return null;
  return (
    <MotionConfig reducedMotion="user">
      <AppShell />
    </MotionConfig>
  );
}
