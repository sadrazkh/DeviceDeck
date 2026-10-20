import type { ReactNode } from 'react';
import { BridgeError } from '../bridge/errors';
import { useI18n, type I18n, type MessageKey } from '../i18n';
import { Button } from './Button';
import { Banner } from './ui';

const NEXT_STEPS = new Set([
  'device_not_found',
  'device_disconnected',
  'device_locked',
  'trust_required',
  'unauthorized',
  'not_supported',
  'path_not_found',
  'permission_denied',
  'already_exists',
  'install_failed',
  'driver_missing',
  'protocol_error',
  'internal',
  'timeout',
  // Backup (round 2)
  'wrong_password',
  'password_required',
  'busy',
  'backup_missing',
  'backup_corrupt',
  'no_space',
  'refused',
]);

/** One sentence on what happened, one on what to do, and the technical code for Details. */
export function describeError(error: unknown, t: I18n['t']): { message: string; next: string; details: string } {
  if (error instanceof BridgeError) {
    const code = NEXT_STEPS.has(error.code) ? error.code : 'internal';
    return {
      message: error.message,
      next: t(`error.next.${code}` as MessageKey),
      details: [`${error.method} → ${error.code} (${error.rpcCode})`, error.detail].filter(Boolean).join('\n'),
    };
  }
  const text = error instanceof Error ? error.message : String(error);
  return { message: t('error.title'), next: t('error.next.internal'), details: text };
}

export function ErrorBanner({ error, onRetry, title }: { error: unknown; onRetry?(): void; title?: ReactNode }) {
  const { t } = useI18n();
  const { message, next, details } = describeError(error, t);
  return (
    <Banner
      tone="danger"
      title={title ?? message}
      details={details}
      actions={onRetry ? <Button onClick={onRetry}>{t('common.retry')}</Button> : undefined}
    >
      {title ? `${message} ${next}` : next}
    </Banner>
  );
}
