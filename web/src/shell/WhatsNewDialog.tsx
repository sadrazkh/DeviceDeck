// "What's new" (round 4): the release notes of the version on offer, with Restart and update (a Setup copy) or Download
// (the zip: its GitHub release page) and Later; also once by itself on the first start of a new version, and from Settings.

import { ArrowCircleDownRegular, ArrowSyncCircleRegular, OpenRegular } from '@fluentui/react-icons';
import { useEffect, useRef, useState } from 'react';
import { useActions } from '../app/actions';
import { useClient } from '../app/bridgeContext';
import { useTransfers } from '../app/queries';
import { Button } from '../components/Button';
import { Dialog } from '../components/Dialog';
import { Banner, Skeleton } from '../components/ui';
import { useI18n } from '../i18n';
import { overallProgress } from '../state/transfers';
import { useUpdateStatus } from './desktop';
import { notesLanguage, ReleaseNotesView } from './ReleaseNotesView';
import styles from './Update.module.css';
import { markBannerSeen, setupFileName, useReleaseNotes, useWhatsNew } from './updates';

export function WhatsNewDialog() {
  const i18n = useI18n();
  const { t, digits, lang } = i18n;
  const client = useClient();
  const actions = useActions();
  const status = useUpdateStatus().data;
  const request = useWhatsNew((s) => s.request);
  const show = useWhatsNew((s) => s.show);
  const close = useWhatsNew((s) => s.close);
  const jobs = useTransfers().data ?? [];
  const [restarting, setRestarting] = useState(false);
  const shownFor = useRef<string | null>(null);

  // The first start of a new version: What's new once, by itself.
  useEffect(() => {
    if (!status?.whatsNew || shownFor.current === status.whatsNew) return;
    shownFor.current = status.whatsNew;
    show({ version: status.whatsNew, reason: 'installed' });
    client.call('app.whatsNewShown', { version: status.whatsNew }).catch(() => undefined);
  }, [status?.whatsNew, client, show]);

  const version = request?.version ?? null;
  const notes = useReleaseNotes(version);
  if (!request || !status) return null;

  const mode =
    status.newVersion === request.version && status.state === 'ready'
      ? 'ready'
      : status.newVersion === request.version && status.state === 'available'
        ? 'available'
        : request.reason === 'installed'
          ? 'installed'
          : 'current';
  const shown = digits(request.version);
  const busy = overallProgress(jobs).active;
  const lead =
    mode === 'ready'
      ? t('update.whatsNew.readyLead', { version: shown })
      : mode === 'available'
        ? t('update.whatsNew.availableLead', { version: shown })
        : mode === 'installed'
          ? t('update.whatsNew.installedLead', { version: shown })
          : request.version === status.currentVersion
            ? t('update.whatsNew.currentLead')
            : null;
  const releaseUrl = notes.data?.url ?? `https://github.com/sadrazkh/DeviceDeck/releases/tag/v${request.version}`;

  const restart = async () => {
    setRestarting(true);
    markBannerSeen(request.version);
    try {
      await client.call('app.applyUpdate');
      // The host quits now and Velopack starts the new version.
    } catch (error) {
      setRestarting(false);
      actions.fail(error);
    }
  };
  const download = async () => {
    markBannerSeen(request.version);
    try {
      await client.call('app.openReleasePage', { version: request.version });
      close();
    } catch (error) {
      actions.fail(error);
    }
  };

  const markdown = notes.data?.markdown ?? null;
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : close())}
      wide
      className={styles.dialog}
      title={t('update.whatsNew.title', { version: shown })}
      description={lead ?? undefined}
      footer={
        mode === 'ready' ? (
          <>
            <Button onClick={close} disabled={restarting}>
              {t('update.whatsNew.later')}
            </Button>
            <Button variant="accent" icon={<ArrowSyncCircleRegular />} loading={restarting} onClick={() => void restart()} autoFocus data-testid="whats-new-restart">
              {restarting ? t('update.whatsNew.restarting') : t('update.whatsNew.restart')}
            </Button>
          </>
        ) : mode === 'available' ? (
          <>
            <Button onClick={close}>{t('update.whatsNew.later')}</Button>
            <Button variant="accent" icon={<ArrowCircleDownRegular />} onClick={() => void download()} autoFocus data-testid="whats-new-download">
              {t('update.whatsNew.download')}
            </Button>
          </>
        ) : (
          <Button variant="accent" onClick={close} autoFocus>
            {t('common.close')}
          </Button>
        )
      }
    >
      <div className={styles.dialogBody} data-testid="whats-new" data-mode={mode}>
        {mode === 'available' ? (
          <p className={styles.hint}>{t('update.whatsNew.availableHint', { setup: setupFileName(request.version, status.architecture) })}</p>
        ) : null}
        {mode === 'ready' && busy > 0 ? <Banner tone="warning">{t('update.whatsNew.busy', { count: busy })}</Banner> : null}
        <section className={styles.notesFrame} aria-label={t('update.notes.label')}>
          {notes.isPending ? (
            <div className={styles.notesLoading} aria-label={t('update.notes.loading')} role="status">
              <Skeleton width="70%" height={20} />
              <Skeleton width="92%" />
              <Skeleton width="85%" />
              <Skeleton width="60%" />
            </div>
          ) : markdown ? (
            <>
              {notes.data?.source === 'cache' ? <p className={styles.notesNote}>{t('update.notes.fromCache')}</p> : null}
              {lang === 'fa' && notesLanguage(markdown, lang) === 'en' ? <p className={styles.notesNote}>{t('update.notes.englishOnly')}</p> : null}
              <ReleaseNotesView markdown={markdown} lang={lang} />
            </>
          ) : (
            <p className={styles.notesMissing} data-testid="release-notes-missing">
              {t('update.notes.missing')}{' '}
              <a href={releaseUrl} target="_blank" rel="noreferrer noopener" className={styles.notesLink}>
                {t('update.notes.link')}
                <OpenRegular aria-hidden className={styles.linkIcon} />
              </a>
            </p>
          )}
        </section>
      </div>
    </Dialog>
  );
}
