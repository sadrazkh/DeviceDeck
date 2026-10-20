import type { DeviceSummary } from '../../bridge/types';
import { ComingNextScreen } from '../ComingNextScreen';

export function FilesScreen(_props: { device: DeviceSummary }) {
  return <ComingNextScreen section="files" />;
}
