// Items this PC can't delete (v0.4.2, deletejob2). Windows doesn't show every iPhone item to a PC. On the owner's
// iPhone, nothing moved over from his earlier iPhone was shown, including 38 of the 39 largest videos. Once Windows'
// view has been read completely (read ahead in the background), those items are marked "Delete on the iPhone"
// before anyone selects them. Until then a quiet line says it is checking, and nothing is marked: never a guess.

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import type { PhotoDeleteReach } from '../../bridge/photoDeleteTypes';
import type { DeviceSummary } from '../../bridge/types';
import { useClient } from '../../app/bridgeContext';
import { Badge, LinearProgress, Tooltip } from '../../components/ui';
import { useI18n } from '../../i18n';
import styles from './PhotoDelete.module.css';

export const reachKey = (deviceId: string) => ['photos.deleteReach', deviceId] as const;

export interface DeleteReach {
  /** Known from a complete read of Windows' view. */
  ready: boolean;
  /** While checking: objects read of all (null otherwise, or when this phone has nothing to check). */
  checking: { done: number; total: number } | null;
  /** Only the Photos app on the phone can delete it (false until ready). */
  phoneOnly(id: string): boolean;
}

/**
 * Which items only the phone can delete; polled while Windows' view is being read, so the marks appear by themselves.
 * Asking starts the read ahead on the host, so it is asked only where a delete is near (`active`): Clean-up's results,
 * a selection on the Photos screen, the delete's confirmation. What was known goes with the connection.
 */
export function usePhotoDeleteReach(device: DeviceSummary, active = true): DeleteReach {
  const client = useClient();
  const queryClient = useQueryClient();
  const connected = device.platform === 'ios' && device.state === 'ready' && device.capabilities.includes('photosDelete');
  const enabled = connected && active;
  useEffect(() => {
    if (!connected) queryClient.removeQueries({ queryKey: reachKey(device.id) });
  }, [connected, device.id, queryClient]);
  const query = useQuery({
    queryKey: reachKey(device.id),
    queryFn: () => client.call('photos.deleteReach', { deviceId: device.id }),
    enabled,
    staleTime: 60_000,
    refetchInterval: (q) => (q.state.data?.state === 'checking' ? 1000 : false),
  });
  const data: PhotoDeleteReach | undefined = enabled ? query.data : undefined;
  return useMemo(() => {
    const ready = data?.state === 'ready';
    const set = new Set(ready ? data.notListed : []);
    return {
      ready,
      checking: data?.state === 'checking' ? { done: data.done, total: data.total } : null,
      phoneOnly: (id: string) => set.has(id),
    };
  }, [data]);
}

/** "Delete on the iPhone", with why on hover or focus (one tooltip: no native title beside it). */
export function ReachBadge() {
  const { t } = useI18n();
  return (
    <Tooltip content={<span className={styles.tooltipText}>{t('reach.badgeWhy')}</span>}>
      <Badge tone="warning" tabIndex={0} data-testid="reach-badge">
        {t('reach.badge')}
      </Badge>
    </Tooltip>
  );
}

/** The quiet line while Windows' view is read: nothing is marked until it is complete. */
export function ReachChecking({ checking, className }: { checking: { done: number; total: number }; className?: string }) {
  const { t } = useI18n();
  const text = checking.total > 0 ? t('reach.checkingProgress', { done: checking.done, total: checking.total }) : t('reach.checking');
  return (
    <div className={`${styles.reachChecking} ${className ?? ''}`} data-testid="reach-checking" role="status">
      <span className={styles.reachCheckingText} title={text}>
        {text}
      </span>
      <LinearProgress value={checking.total > 0 ? checking.done / checking.total : null} label={t('reach.checking')} className={styles.reachBar} />
    </div>
  );
}

/** In a delete's confirmation: what stays on the phone, and why. */
export function LeftOutList({ rows, count = rows.length }: { rows: { key: string; label: string; detail?: string; size: string }[]; count?: number }) {
  const { t } = useI18n();
  if (count === 0) return null;
  return (
    <div className={styles.leftOut} data-testid="delete-left-out" data-count={count}>
      <p className={styles.leftOutTitle}>{t('reach.leftOut', { count })}</p>
      <p className={styles.hint}>{t('reach.leftOutWhy')}</p>
      <ul className={styles.list} aria-label={t('reach.leftOut', { count })}>
        {rows.map((row) => (
          <li key={row.key} className={`${styles.row} ${styles.rowPlain}`} data-testid="delete-left-out-row">
            <span className={styles.text}>
              <span className={styles.name}>{row.label}</span>
              {row.detail ? <span className={styles.detail}>{row.detail}</span> : null}
            </span>
            <span className={styles.size}>{row.size}</span>
          </li>
        ))}
      </ul>
      {count > rows.length ? <p className={styles.hint}>{t('photoDelete.more', { count: count - rows.length })}</p> : null}
    </div>
  );
}
