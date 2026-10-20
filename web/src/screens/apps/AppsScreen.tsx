import type { DeviceSummary } from '../../bridge/types';
import { ComingNextScreen } from '../ComingNextScreen';

export function AppsScreen(_props: { device: DeviceSummary }) {
  return <ComingNextScreen section="apps" />;
}
