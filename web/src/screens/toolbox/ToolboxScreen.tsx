import {
  ArrowLeftRegular,
  BugRegular,
  DocumentBulletListRegular,
  ImageArrowForwardRegular,
  MusicNote2Regular,
  PhoneDesktopRegular,
  PulseRegular,
  Wifi1Regular,
  type FluentIcon,
} from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { create } from 'zustand';
import { useClient } from '../../app/bridgeContext';
import type { DeviceSummary } from '../../bridge/types';
import type { ToolboxFeatures } from '../../bridge/toolbox';
import { Button } from '../../components/Button';
import { ErrorBanner } from '../../components/ErrorState';
import { Badge, Skeleton } from '../../components/ui';
import { ScreenLayout } from '../ScreenLayout';
import { CrashTool } from './CrashTool';
import { HeicTool } from './HeicTool';
import { useToolboxI18n, type ToolboxKey } from './i18n';
import { LogTool } from './LogTool';
import { MirrorTool } from './MirrorTool';
import { ReportTool } from './ReportTool';
import { RingtoneTool } from './RingtoneTool';
import styles from './Toolbox.module.css';
import { WifiTool } from './WifiTool';

export type ToolId = 'log' | 'crash' | 'report' | 'heic' | 'mirror' | 'wifi' | 'ringtone';

interface ToolEntry {
  id: ToolId;
  icon: FluentIcon;
  title: ToolboxKey;
  blurb: ToolboxKey;
  /** Why the tile is off for this phone, or null when it works. */
  unavailable(device: DeviceSummary, features: ToolboxFeatures): ToolboxKey | null;
  /** A note on the tile that doesn't turn it off. */
  note?(device: DeviceSummary): ToolboxKey | null;
}

export const TOOLS: ToolEntry[] = [
  { id: 'log', icon: PulseRegular, title: 'log.title', blurb: 'log.blurb', unavailable: (_, f) => (f.log ? null : 'notOnThisPhone') },
  { id: 'crash', icon: BugRegular, title: 'crash.title', blurb: 'crash.blurb', unavailable: (_, f) => (f.crashReports ? null : 'notOnThisPhone') },
  { id: 'report', icon: DocumentBulletListRegular, title: 'report.title', blurb: 'report.blurb', unavailable: (_, f) => (f.report ? null : 'notOnThisPhone') },
  { id: 'heic', icon: ImageArrowForwardRegular, title: 'heic.title', blurb: 'heic.blurb', unavailable: () => null },
  {
    id: 'mirror',
    icon: PhoneDesktopRegular,
    title: 'mirror.title',
    blurb: 'mirror.blurb',
    unavailable: (d, f) => (d.platform === 'ios' ? 'onlyAndroid' : f.mirror ? null : 'notOnThisPhone'),
  },
  { id: 'wifi', icon: Wifi1Regular, title: 'wifi.title', blurb: 'wifi.blurb', unavailable: () => null },
  {
    id: 'ringtone',
    icon: MusicNote2Regular,
    title: 'ringtone.title',
    blurb: 'ringtone.blurb',
    // On an iPhone the tile opens the explanation of why it can't be done.
    unavailable: (d, f) => (d.platform === 'android' && !f.ringtone ? 'notOnThisPhone' : null),
    note: (d) => (d.platform === 'ios' ? 'onlyAndroid' : null),
  },
];

/** ?tool=log opens a tool straight away (screenshots, demos). */
function toolFromUrl(): ToolId | null {
  const wanted = new URLSearchParams(typeof location === 'undefined' ? '' : location.search).get('tool');
  return TOOLS.some((t) => t.id === wanted) ? (wanted as ToolId) : null;
}

/**
 * The open tool per phone. In a store rather than component state: the screen can be remounted (the shell's
 * page transition does so once at start-up) and switching sections should come back to the same tool.
 */
export const useToolboxPlace = create<{ tools: Record<string, ToolId | null>; fromUrl: ToolId | null; open(deviceId: string, tool: ToolId | null): void }>(
  (set) => ({
    tools: {},
    fromUrl: toolFromUrl(),
    open: (deviceId, tool) => set((s) => ({ tools: { ...s.tools, [deviceId]: tool }, fromUrl: null })),
  }),
);

export function useToolboxFeatures(device: DeviceSummary) {
  const client = useClient();
  return useQuery({
    queryKey: ['toolbox.features', device.id],
    queryFn: () => client.call('toolbox.features', { deviceId: device.id }),
    enabled: device.state === 'ready',
  });
}

export function ToolboxScreen({ device }: { device: DeviceSummary }) {
  const { tt } = useToolboxI18n();
  const features = useToolboxFeatures(device);
  const tool = useToolboxPlace((s) => (device.id in s.tools ? s.tools[device.id]! : s.fromUrl));
  const setTool = (id: ToolId | null) => useToolboxPlace.getState().open(device.id, id);

  if (tool) {
    const entry = TOOLS.find((t) => t.id === tool)!;
    return (
      <ScreenLayout
        title={tt(entry.title)}
        subtitle={device.name}
        actions={
          <Button icon={<ArrowLeftRegular className="flip-rtl" />} onClick={() => setTool(null)} data-testid="toolbox-back">
            {tt('back')}
          </Button>
        }
        fill
        testId={`tool-${tool}`}
      >
        <Tool id={tool} device={device} features={features.data} />
      </ScreenLayout>
    );
  }

  return (
    <ScreenLayout title={tt('title')} subtitle={tt('subtitle', { name: device.name })} testId="screen-toolbox">
      {features.isError ? <ErrorBanner error={features.error} onRetry={() => void features.refetch()} /> : null}
      <div className={styles.tiles} data-testid="toolbox-tiles">
        {TOOLS.map((entry) => {
          if (!features.data) return <Skeleton key={entry.id} height={112} radius="var(--radius-card)" />;
          const reason = entry.unavailable(device, features.data);
          const note = entry.note?.(device) ?? null;
          const Icon = entry.icon;
          return (
            <button
              key={entry.id}
              type="button"
              className={styles.tile}
              disabled={reason !== null}
              onClick={() => setTool(entry.id)}
              data-testid={`tool-tile-${entry.id}`}
            >
              <span className={styles.tileIcon} aria-hidden>
                <Icon />
              </span>
              <span className={styles.tileTitle}>
                {tt(entry.title)}
                {reason ? <Badge>{tt(reason)}</Badge> : note ? <Badge tone="info">{tt(note)}</Badge> : null}
              </span>
              <span className={styles.tileBlurb}>{tt(entry.blurb)}</span>
            </button>
          );
        })}
      </div>
    </ScreenLayout>
  );
}

function Tool({ id, device, features }: { id: ToolId; device: DeviceSummary; features: ToolboxFeatures | undefined }) {
  switch (id) {
    case 'log':
      return <LogTool device={device} />;
    case 'crash':
      return <CrashTool device={device} />;
    case 'report':
      return <ReportTool device={device} />;
    case 'heic':
      return <HeicTool />;
    case 'mirror':
      return <MirrorTool device={device} />;
    case 'wifi':
      return <WifiTool device={device} wirelessDebugging={features?.wirelessDebugging ?? false} />;
    case 'ringtone':
      return <RingtoneTool device={device} />;
  }
}

