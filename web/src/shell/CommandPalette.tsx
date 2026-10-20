import {
  AppsRegular,
  ArrowSwapRegular,
  ArrowSyncRegular,
  DocumentRegular,
  FolderRegular,
  LocalLanguageRegular,
  NavigationRegular,
  PhoneRegular,
  PlugConnectedRegular,
  PlugDisconnectedRegular,
  SearchRegular,
  TextDensityRegular,
  WeatherMoonRegular,
  WeatherSunnyRegular,
  DesktopRegular,
} from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { Dialog as RadixDialog } from 'radix-ui';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { AppInfo, FileEntry } from '../bridge/types';
import { useMock } from '../app/bridgeContext';
import { useDevices, useSelectedDevice, useUpdateSettings } from '../app/queries';
import { cx } from '../components/cx';
import { useI18n, type MessageKey } from '../i18n';
import { ANDROID_ID, IPHONE_ID, drivers, driversIosMissing } from '../bridge/mock/mock-data';
import { usePrefs } from '../state/prefs';
import { useUi } from '../state/ui';
import { NAV, SETTINGS_NAV, navEnabled } from './nav';
import styles from './Palette.module.css';

interface Command {
  id: string;
  group: MessageKey;
  label: string;
  hint?: string;
  icon: ReactNode;
  run(): void;
}

function matches(label: string, query: string): boolean {
  const text = label.toLocaleLowerCase();
  return query
    .toLocaleLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((term) => text.includes(term));
}

export function CommandPalette() {
  const i18n = useI18n();
  const { t } = i18n;
  const open = useUi((s) => s.paletteOpen);
  const setOpen = useUi((s) => s.setPaletteOpen);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);
  const commands = useCommands(open);

  const visible = useMemo(() => {
    const q = query.trim();
    const filtered = q ? commands.filter((c) => matches(`${c.label} ${c.hint ?? ''}`, q)) : commands.filter((c) => c.group !== 'palette.group.files');
    // At most 8 per group so one long group doesn't push the rest away.
    const counts = new Map<string, number>();
    return filtered.filter((c) => {
      const n = (counts.get(c.group) ?? 0) + 1;
      counts.set(c.group, n);
      return n <= 8;
    });
  }, [commands, query]);

  useEffect(() => setActive(0), [query, open]);
  useEffect(() => {
    if (!open) setQuery('');
  }, [open]);
  useEffect(() => {
    listRef.current?.querySelector('[data-active]')?.scrollIntoView({ block: 'nearest' });
  }, [active]);

  const run = (command: Command | undefined) => {
    if (!command) return;
    setOpen(false);
    command.run();
  };

  let lastGroup: string | null = null;
  return (
    <RadixDialog.Root open={open} onOpenChange={setOpen}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className={styles.overlay} />
        <RadixDialog.Content className={styles.palette} aria-label={t('palette.title')} data-testid="command-palette">
          <RadixDialog.Title className="visually-hidden">{t('palette.title')}</RadixDialog.Title>
          <RadixDialog.Description className="visually-hidden">{t('palette.placeholder')}</RadixDialog.Description>
          <div className={styles.inputRow}>
            <SearchRegular className={styles.inputIcon} aria-hidden />
            <input
              className={styles.input}
              value={query}
              placeholder={t('palette.placeholder')}
              onChange={(e) => setQuery(e.target.value)}
              aria-controls="palette-list"
              aria-activedescendant={visible[active] ? `cmd-${visible[active].id}` : undefined}
              role="combobox"
              aria-expanded
              onKeyDown={(e) => {
                if (e.key === 'ArrowDown') {
                  e.preventDefault();
                  setActive((a) => Math.min(visible.length - 1, a + 1));
                } else if (e.key === 'ArrowUp') {
                  e.preventDefault();
                  setActive((a) => Math.max(0, a - 1));
                } else if (e.key === 'Enter') {
                  e.preventDefault();
                  run(visible[active]);
                }
              }}
            />
          </div>
          <div className={styles.list} id="palette-list" role="listbox" ref={listRef}>
            {visible.length === 0 ? <div className={styles.empty}>{t('palette.empty')}</div> : null}
            {visible.map((command, index) => {
              const header = command.group !== lastGroup ? t(command.group) : null;
              lastGroup = command.group;
              return (
                <div key={command.id}>
                  {header ? <div className={styles.group}>{header}</div> : null}
                  <div
                    id={`cmd-${command.id}`}
                    role="option"
                    aria-selected={index === active}
                    data-active={index === active || undefined}
                    className={cx(styles.item)}
                    onMouseMove={() => setActive(index)}
                    onClick={() => run(command)}
                  >
                    <span className={styles.itemIcon} aria-hidden>
                      {command.icon}
                    </span>
                    <span className={styles.itemLabel}>{command.label}</span>
                    {command.hint ? <span className={styles.itemHint}>{command.hint}</span> : null}
                  </div>
                </div>
              );
            })}
          </div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

function useCommands(open: boolean): Command[] {
  const { t } = useI18n();
  const device = useSelectedDevice();
  const devices = useDevices().data ?? [];
  const queryClient = useQueryClient();
  const mock = useMock();
  const updateSettings = useUpdateSettings();
  const prefs = usePrefs();
  const ui = useUi.getState();

  return useMemo(() => {
    if (!open) return [];
    const list: Command[] = [];

    for (const entry of [...NAV, SETTINGS_NAV]) {
      if (!navEnabled(entry, device)) continue;
      const Icon = entry.icon;
      list.push({ id: `nav-${entry.section}`, group: 'palette.group.sections', label: t(entry.label), icon: <Icon />, run: () => ui.navigate(entry.section) });
    }

    list.push(
      { id: 'transfers', group: 'palette.group.actions', label: t('palette.showTransfers'), icon: <ArrowSyncRegular />, run: () => ui.setDrawerOpen(true) },
      prefs.language === 'en'
        ? { id: 'lang-fa', group: 'palette.group.actions', label: t('palette.langFa'), icon: <LocalLanguageRegular />, run: () => updateSettings.mutate({ language: 'fa' }) }
        : { id: 'lang-en', group: 'palette.group.actions', label: t('palette.langEn'), icon: <LocalLanguageRegular />, run: () => updateSettings.mutate({ language: 'en' }) },
      { id: 'theme-light', group: 'palette.group.actions', label: t('palette.themeLight'), icon: <WeatherSunnyRegular />, run: () => updateSettings.mutate({ theme: 'light' }) },
      { id: 'theme-dark', group: 'palette.group.actions', label: t('palette.themeDark'), icon: <WeatherMoonRegular />, run: () => updateSettings.mutate({ theme: 'dark' }) },
      { id: 'theme-system', group: 'palette.group.actions', label: t('palette.themeSystem'), icon: <DesktopRegular />, run: () => updateSettings.mutate({ theme: 'system' }) },
      prefs.density === 'comfortable'
        ? { id: 'density-compact', group: 'palette.group.actions', label: t('palette.densityCompact'), icon: <TextDensityRegular />, run: () => updateSettings.mutate({ density: 'compact' }) }
        : { id: 'density-comfortable', group: 'palette.group.actions', label: t('palette.densityComfortable'), icon: <TextDensityRegular />, run: () => updateSettings.mutate({ density: 'comfortable' }) },
      prefs.navLayout === 'rail'
        ? { id: 'nav-tabs', group: 'palette.group.actions', label: t('palette.navTabs'), icon: <NavigationRegular />, run: () => prefs.setNavLayout('tabs') }
        : { id: 'nav-rail', group: 'palette.group.actions', label: t('palette.navRail'), icon: <NavigationRegular />, run: () => prefs.setNavLayout('rail') },
    );

    for (const d of devices) {
      if (d.id === device?.id) continue;
      list.push({ id: `device-${d.id}`, group: 'palette.group.devices', label: t('palette.switchTo', { device: d.name }), icon: <ArrowSwapRegular />, run: () => ui.selectDevice(d.id) });
    }

    if (device) {
      const apps = queryClient.getQueriesData<AppInfo[]>({ queryKey: ['apps.list', device.id] }).flatMap(([, data]) => data ?? []);
      const seen = new Set<string>();
      for (const app of apps) {
        if (seen.has(app.id)) continue;
        seen.add(app.id);
        list.push({
          id: `app-${app.id}`,
          group: 'palette.group.apps',
          label: app.name,
          hint: app.fileSharing ? t('apps.openDocuments') : app.id,
          icon: <AppsRegular />,
          run: () => {
            if (app.fileSharing) {
              ui.setFilesPlace({ root: `app:${app.id}`, path: '/' });
              ui.navigate('files');
            } else ui.navigate('apps');
          },
        });
      }
      const lists = queryClient.getQueriesData<FileEntry[]>({ queryKey: ['files.list', device.id] });
      let fileCount = 0;
      for (const [key, entries] of lists) {
        const root = key[2] as string;
        for (const entry of entries ?? []) {
          if (fileCount++ > 3000) break;
          list.push({
            id: `file-${root}-${entry.path}`,
            group: 'palette.group.files',
            label: entry.name,
            hint: entry.path,
            icon: entry.isDirectory ? <FolderRegular /> : <DocumentRegular />,
            run: () => {
              const parent = entry.isDirectory ? entry.path : entry.path.slice(0, entry.path.lastIndexOf('/')) || '/';
              ui.setFilesPlace({ root, path: parent });
              ui.navigate('files');
            },
          });
        }
      }
    }

    if (mock) {
      const sim = 'palette.group.simulation' as const;
      for (const d of devices) {
        if (d.state === 'disconnected') {
          list.push({ id: `sim-back-${d.id}`, group: sim, label: t('palette.simPlugBack', { device: d.name }), icon: <PlugConnectedRegular />, run: () => mock.plugBack(d.id) });
        } else {
          list.push({ id: `sim-unplug-${d.id}`, group: sim, label: t('palette.simUnplug', { device: d.name }), icon: <PlugDisconnectedRegular />, run: () => mock.unplug(d.id) });
        }
        if (d.state === 'awaitingTrust' || d.state === 'unauthorized') {
          list.push({ id: `sim-approve-${d.id}`, group: sim, label: t('palette.simApprove', { device: d.name }), icon: <PhoneRegular />, run: () => mock.approve(d.id) });
        }
      }
      if (!devices.some((d) => d.id === IPHONE_ID)) {
        list.push({ id: 'sim-new-iphone', group: sim, label: t('palette.simNewIphone'), icon: <PhoneRegular />, run: () => mock.connectNew(IPHONE_ID) });
      }
      if (!devices.some((d) => d.id === ANDROID_ID)) {
        list.push({ id: 'sim-new-android', group: sim, label: t('palette.simNewAndroid'), icon: <PhoneRegular />, run: () => mock.connectNew(ANDROID_ID) });
      }
      if (devices.some((d) => d.id === IPHONE_ID && d.state === 'ready')) {
        list.push({
          id: 'sim-conflict',
          group: sim,
          label: t('palette.simConflict'),
          icon: <ArrowSyncRegular />,
          run: () => {
            ui.selectDevice(IPHONE_ID);
            ui.setFilesPlace({ root: 'app:org.videolan.vlc-ios', path: '/' });
            ui.navigate('files');
            void mock.handlers['files.copyToDevice']({
              deviceId: IPHONE_ID,
              root: 'app:org.videolan.vlc-ios',
              path: '/',
              files: ['C:\\Users\\Nima\\Videos\\Persepolis.mkv'],
            });
          },
        });
      }
      list.push(
        { id: 'sim-driver-missing', group: sim, label: t('palette.simDriverMissing'), icon: <PlugDisconnectedRegular />, run: () => mock.setDrivers(driversIosMissing) },
        { id: 'sim-driver-ready', group: sim, label: t('palette.simDriverReady'), icon: <PlugConnectedRegular />, run: () => mock.setDrivers(drivers) },
      );
    }
    return list;
  }, [open, device, devices, queryClient, mock, updateSettings, prefs, ui, t]);
}
