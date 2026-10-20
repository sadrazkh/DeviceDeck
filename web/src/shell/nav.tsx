import {
  AppsFilled,
  AppsRegular,
  ArchiveFilled,
  ArchiveRegular,
  DocumentFolderFilled,
  DocumentFolderRegular,
  FolderFilled,
  FolderRegular,
  GridFilled,
  GridRegular,
  ImageMultipleFilled,
  ImageMultipleRegular,
  PeopleChatFilled,
  PeopleChatRegular,
  SettingsFilled,
  SettingsRegular,
  ToolboxFilled,
  ToolboxRegular,
  type FluentIcon,
} from '@fluentui/react-icons';
import type { DeviceCapability, DeviceSummary } from '../bridge/types';
import type { MessageKey } from '../i18n';
import type { Section } from '../state/ui';

export interface NavEntry {
  section: Section;
  label: MessageKey;
  icon: FluentIcon;
  /** Filled icon for the selected item. */
  iconSelected: FluentIcon;
  /** The section greys out when the phone lacks this. */
  needs?: DeviceCapability;
}

export const NAV: NavEntry[] = [
  { section: 'overview', label: 'nav.overview', icon: GridRegular, iconSelected: GridFilled },
  { section: 'photos', label: 'nav.photos', icon: ImageMultipleRegular, iconSelected: ImageMultipleFilled, needs: 'photosRead' },
  { section: 'files', label: 'nav.files', icon: FolderRegular, iconSelected: FolderFilled, needs: 'files' },
  { section: 'appDocuments', label: 'nav.appDocuments', icon: DocumentFolderRegular, iconSelected: DocumentFolderFilled, needs: 'files' },
  { section: 'apps', label: 'nav.apps', icon: AppsRegular, iconSelected: AppsFilled, needs: 'appsList' },
  { section: 'contacts', label: 'nav.contacts', icon: PeopleChatRegular, iconSelected: PeopleChatFilled, needs: 'contacts' },
  { section: 'backup', label: 'nav.backup', icon: ArchiveRegular, iconSelected: ArchiveFilled, needs: 'backup' },
  { section: 'toolbox', label: 'nav.toolbox', icon: ToolboxRegular, iconSelected: ToolboxFilled },
];

export const SETTINGS_NAV: NavEntry = { section: 'settings', label: 'nav.settings', icon: SettingsRegular, iconSelected: SettingsFilled };

export function navEnabled(entry: NavEntry, device: DeviceSummary | null): boolean {
  if (entry.section === 'settings') return true;
  if (!device || device.state !== 'ready') return false;
  return !entry.needs || device.capabilities.includes(entry.needs);
}

/** Below this width the rail collapses to icons. Keep in step with the 64 px rail in tokens.css. */
export const RAIL_COMPACT_QUERY = '(max-width: 1279.98px)';
