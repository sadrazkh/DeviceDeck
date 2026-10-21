import {
  ArrowExportRegular,
  ChatRegular,
  DocumentRegular,
  ImageMultipleRegular,
  ImageRegular,
  LocationRegular,
  MicRegular,
  PeopleRegular,
  PersonRegular,
  PlayRegular,
  ProhibitedRegular,
  StarFilled,
  VideoRegular,
} from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useSettings } from '../../app/queries';
import type { BackupInfo, MessagesFormat, WhatsAppChat, WhatsAppMedia, WhatsAppMediaKind, WhatsAppMessage, WhatsAppStatus } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { ErrorBanner, describeError } from '../../components/ErrorState';
import { Banner, Checkbox, EmptyState, RadioCards, Skeleton } from '../../components/ui';
import { useI18n, type I18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { rememberJob } from '../backup/backupQueries';
import backupStyles from '../backup/Backup.module.css';
import { initials } from './ContactsPane';
import styles from './Contacts.module.css';
import wa from './WhatsApp.module.css';

const PAGE = 200;

export const whatsAppKeys = {
  status: (backupId: string) => ['whatsapp.status', backupId] as const,
  chats: (backupId: string) => ['whatsapp.chats', backupId] as const,
  thumbnail: (backupId: string, mediaId: string) => ['whatsapp.thumbnail', backupId, mediaId] as const,
};

/** WhatsApp's chats in one iPhone backup: what is there, the chats, one chat's thread, and exports. */
export function WhatsAppPane({ backup }: { backup: BackupInfo }) {
  const i18n = useI18n();
  const { t, date } = i18n;
  const client = useClient();
  const status = useQuery({ queryKey: whatsAppKeys.status(backup.id), queryFn: () => client.call('whatsapp.status', { backupId: backup.id }), retry: false });
  const found = status.data?.found === true;
  const chats = useQuery({ queryKey: whatsAppKeys.chats(backup.id), queryFn: () => client.call('whatsapp.chats', { backupId: backup.id }), enabled: found, retry: false });
  const [query, setQuery] = useState('');
  const [current, setCurrent] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [exporting, setExporting] = useState<'chats' | 'media' | null>(null);
  const [allScope, setAllScope] = useState(false);

  useEffect(() => {
    setSelected(new Set());
    setCurrent(null);
    setQuery('');
  }, [backup.id]);

  const list = useMemo(() => {
    const q = query.trim().toLocaleLowerCase();
    return (chats.data ?? []).filter(
      (c) => !q || c.title.toLocaleLowerCase().includes(q) || (c.phone ?? '').includes(q.replace(/[\s-]/g, '')) || (c.preview ?? '').toLocaleLowerCase().includes(q),
    );
  }, [chats.data, query]);
  const chat = list.find((c) => c.id === current) ?? list[0] ?? null;

  const error = status.error ?? chats.error;
  if (error) return <ErrorBanner error={error} onRetry={() => void (status.error ? status.refetch() : chats.refetch())} />;
  if (status.isPending || (found && chats.isPending)) return <Skeleton height={240} />;
  if (!status.data || !found) {
    return (
      <EmptyState
        icon={<ChatRegular />}
        title={t('whatsapp.none.title')}
        body={status.data?.installed ? t('whatsapp.none.installedBody', { device: backup.deviceName }) : t('whatsapp.none.body', { device: backup.deviceName })}
      />
    );
  }

  const all = chats.data ?? [];
  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const open = (mode: 'chats' | 'media', everything: boolean) => {
    setAllScope(everything);
    setExporting(mode);
  };

  return (
    <div className={wa.wrap} data-testid="whatsapp-pane">
      <Summary status={status.data} onExportAll={() => open('chats', true)} onExportMedia={() => open('media', true)} />
      {status.data.missing.length > 0 ? (
        <Banner tone="info" title={t('whatsapp.missing.title')}>
          {t('whatsapp.missing.body')}
          <details className={wa.details}>
            <summary>{t('whatsapp.missing.details')}</summary>
            <code>{status.data.missing.join(', ')}</code>
          </details>
        </Banner>
      ) : null}
      <div className={styles.split}>
        <section className={styles.pane} aria-label={t('contacts.tab.whatsapp')}>
          <div className={styles.paneHead}>
            <input className={styles.search} type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('whatsapp.search')} aria-label={t('whatsapp.search')} />
          </div>
          <div className={styles.scroll} role="list">
            {list.length === 0 ? <p className={styles.item}>{t('whatsapp.noMatch')}</p> : null}
            {list.map((c) => (
              <div key={c.id} role="listitem" className={styles.item} aria-current={chat?.id === c.id} onClick={() => setCurrent(c.id)} data-testid="whatsapp-chat">
                <Checkbox checked={selected.has(c.id)} onChange={() => toggle(c.id)} label={c.title} />
                <span className={styles.avatar} aria-hidden>
                  {c.isGroup ? <PeopleRegular /> : initials(c.title)}
                </span>
                <span className={styles.itemText}>
                  <span className={styles.itemTitle}>
                    <span className={wa.titleText} dir="auto">
                      {c.title}
                    </span>
                    {c.account === 'business' ? <span className={wa.badge}>{t('whatsapp.badge.business')}</span> : null}
                    {c.archived ? <span className={wa.badge}>{t('whatsapp.badge.archived')}</span> : null}
                  </span>
                  <span className={styles.itemMeta} dir="auto">
                    {c.preview ?? (c.previewMedia ? kindLabel(c.previewMedia, t) : '')}
                  </span>
                  <span className={wa.counts}>
                    {[t('whatsapp.messages', { count: c.messageCount }), c.mediaCount > 0 ? t('whatsapp.media', { count: c.mediaCount }) : null].filter(Boolean).join(' · ')}
                  </span>
                </span>
                {c.lastMessageAt ? <span className={styles.itemDate}>{date(c.lastMessageAt, 'short')}</span> : null}
              </div>
            ))}
          </div>
          <div className={styles.footer}>{selected.size > 0 ? t('common.selected', { count: selected.size }) : t('whatsapp.count', { count: list.length })}</div>
        </section>
        <section className={styles.pane} aria-label={chat?.title ?? t('whatsapp.pick')}>
          <div className={styles.paneHead}>
            <span className={styles.itemText}>
              <span className={styles.itemTitle}>
                <span className={wa.titleText} dir="auto">
                  {chat?.title}
                </span>
              </span>
              {chat ? (
                <span className={styles.itemMeta}>
                  {chat.phone ? <span className={wa.ltr}>{i18n.digits(chat.phone)}</span> : null}
                  {chat.phone ? ' · ' : ''}
                  {[t('whatsapp.messages', { count: chat.messageCount }), chat.mediaCount ? t('whatsapp.media', { count: chat.mediaCount }) : null].filter(Boolean).join(' · ')}
                </span>
              ) : null}
            </span>
            <span className={wa.headActions}>
              <Button icon={<ImageMultipleRegular />} onClick={() => open('media', false)} disabled={!chat} data-testid="whatsapp-export-media">
                {t('whatsapp.exportMedia')}
              </Button>
              <Button variant="accent" icon={<ArrowExportRegular />} onClick={() => open('chats', false)} disabled={!chat} data-testid="whatsapp-export">
                {selected.size > 1 ? t('whatsapp.exportMany', { count: selected.size }) : t('whatsapp.export')}
              </Button>
            </span>
          </div>
          {chat ? <Thread key={`${backup.id}:${chat.id}`} backup={backup} chat={chat} /> : <EmptyState title={t('whatsapp.pick')} />}
        </section>
      </div>
      <ExportDialog
        mode={exporting}
        backup={backup}
        status={status.data}
        chats={all}
        current={allScope ? null : chat}
        selected={allScope ? new Set() : selected}
        onClose={() => setExporting(null)}
      />
    </div>
  );
}

function Summary({ status, onExportAll, onExportMedia }: { status: WhatsAppStatus; onExportAll(): void; onExportMedia(): void }) {
  const { t, bytes } = useI18n();
  const sum = (pick: (a: WhatsAppStatus['accounts'][number]) => number) => status.accounts.reduce((total, a) => total + pick(a), 0);
  const media = sum((a) => a.media);
  const inBackup = sum((a) => a.mediaInBackup);
  return (
    <div className={wa.summary} data-testid="whatsapp-summary">
      <span className={wa.summaryText}>
        <strong>{status.accounts.map((a) => a.name).join(' · ')}</strong>
        <span>{t('whatsapp.summary.chats', { count: sum((a) => a.chats) })}</span>
        <span>{t('whatsapp.summary.messages', { count: sum((a) => a.messages) })}</span>
        <span>{t('whatsapp.summary.media', { count: media, size: bytes(sum((a) => a.mediaBytes)) })}</span>
        {media > inBackup ? <span>{t('whatsapp.summary.offloaded', { count: media - inBackup })}</span> : null}
      </span>
      <span className={wa.summaryActions}>
        <Button icon={<ArrowExportRegular />} onClick={onExportAll} data-testid="whatsapp-export-all">
          {t('whatsapp.exportAll')}
        </Button>
        <Button icon={<ImageMultipleRegular />} onClick={onExportMedia} data-testid="whatsapp-export-media-all">
          {t('whatsapp.exportMediaAll')}
        </Button>
      </span>
    </div>
  );
}

function Thread({ backup, chat }: { backup: BackupInfo; chat: WhatsAppChat }) {
  const i18n = useI18n();
  const { t } = i18n;
  const client = useClient();
  const [messages, setMessages] = useState<WhatsAppMessage[]>([]);
  const [offset, setOffset] = useState<number | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);
  const keepBottom = useRef<number | null>(null);
  // Pages asked for so far: development mode runs the first effect twice, and a page must land once.
  const requested = useRef(new Set<number>());
  const time = useMemo(() => new Intl.DateTimeFormat(i18n.lang === 'fa' ? 'fa-IR' : 'en-GB', { hour: '2-digit', minute: '2-digit' }), [i18n.lang]);

  // `limit` stops at the page already shown, so the first page of a long thread isn't read twice.
  const load = async (from: number, limit = PAGE) => {
    if (requested.current.has(from)) return;
    requested.current.add(from);
    setLoading(true);
    setError(null);
    try {
      const page = await client.call('whatsapp.messages', { backupId: backup.id, chatId: chat.id, offset: from, limit });
      setMessages((m) => [...page.items, ...m]);
      setOffset(from);
    } catch (e) {
      requested.current.delete(from);
      setError(e);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load(Math.max(0, chat.messageCount - PAGE));
    // One chat per component (the parent keys it).
  }, []);

  // Newest at the bottom on first load; earlier pages keep the reader's place.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (keepBottom.current !== null) {
      el.scrollTop = el.scrollHeight - keepBottom.current;
      keepBottom.current = null;
      return;
    }
    el.scrollTop = el.scrollHeight;
    const frame = requestAnimationFrame(() => (el.scrollTop = el.scrollHeight));
    return () => cancelAnimationFrame(frame);
  }, [messages]);

  const earlier = () => {
    if (offset === null || offset === 0 || loading) return;
    const el = scroller.current;
    keepBottom.current = el ? el.scrollHeight - el.scrollTop : null;
    const from = Math.max(0, offset - PAGE);
    void load(from, offset - from);
  };

  let lastDay = '';
  let lastSender: string | null = null;
  return (
    <div className={styles.thread} ref={scroller} data-testid="whatsapp-thread">
      {error ? <Banner tone="danger" title={describeError(error, t).message} /> : null}
      {offset !== null && offset > 0 ? (
        <Button className={styles.loadEarlier} size="small" onClick={earlier} loading={loading}>
          {t('messages.loadEarlier')}
        </Button>
      ) : null}
      {offset === null && loading ? <Skeleton height={120} /> : null}
      {messages.map((m) => {
        const day = m.date ? i18n.date(m.date, 'short') : '';
        const showDay = day !== lastDay;
        lastDay = day;
        if (m.kind === 'event') {
          lastSender = null;
          return (
            <Fragment key={m.id}>
              {showDay && day ? <div className={styles.day}>{day}</div> : null}
              <div className={styles.event} dir="auto" data-testid="whatsapp-event">
                {m.text ? t('whatsapp.event', { text: m.text }) : t('whatsapp.eventUnknown')}
              </div>
            </Fragment>
          );
        }
        const sender = m.fromMe ? 'me' : (m.senderJid ?? m.senderName ?? '');
        const showSender = chat.isGroup && !m.fromMe && (showDay || sender !== lastSender);
        lastSender = sender;
        return (
          <Fragment key={m.id}>
            {showDay && day ? <div className={styles.day}>{day}</div> : null}
            <div className={styles.message} data-me={m.fromMe || undefined} data-testid="whatsapp-message" data-kind={m.kind}>
              {showSender ? (
                <span className={styles.sender} dir="auto">
                  {m.senderName ?? m.senderJid}
                </span>
              ) : null}
              <div className={styles.bubble} dir="auto">
                {m.quote ? (
                  <span className={wa.quote} data-testid="whatsapp-quote">
                    <span className={wa.quoteFrom} dir="auto">
                      {m.quote.fromMe ? t('whatsapp.you') : (m.quote.senderName ?? chat.title)}
                    </span>
                    <span className={wa.quoteText} dir="auto">
                      {m.quote.text ?? (m.quote.mediaKind ? kindLabel(m.quote.mediaKind, t) : '')}
                    </span>
                  </span>
                ) : null}
                {m.media ? <MediaView backup={backup} media={m.media} /> : null}
                {m.kind === 'deleted' ? (
                  <span className={wa.note}>
                    <ProhibitedRegular aria-hidden /> {t('whatsapp.deleted')}
                  </span>
                ) : m.kind === 'other' ? (
                  <span className={wa.note}>{t('whatsapp.unsupported')}</span>
                ) : (
                  m.text
                )}
              </div>
              {m.date ? (
                <span className={styles.time}>
                  {m.starred ? <StarFilled className={wa.star} aria-label={t('whatsapp.starred')} /> : null}
                  {time.format(new Date(m.date))}
                </span>
              ) : null}
            </div>
          </Fragment>
        );
      })}
    </div>
  );
}

/** A photo, video, voice note, document, location or contact card inside a bubble. */
function MediaView({ backup, media }: { backup: BackupInfo; media: WhatsAppMedia }) {
  const { t, bytes, duration, digits } = useI18n();
  const size = media.size > 0 ? bytes(media.size) : null;
  switch (media.kind) {
    case 'image':
    case 'sticker':
    case 'video':
    case 'gif':
      return <MediaTile backup={backup} media={media} />;
    case 'voice':
    case 'audio':
      return <VoiceNote backup={backup} media={media} />;
    case 'location':
      return (
        <FileLine icon={<LocationRegular />} name={media.name === 'Location' ? t('whatsapp.kind.location') : media.name}>
          {media.latitude !== null && media.longitude !== null ? (
            <span className={wa.ltr}>{digits(`${media.latitude.toFixed(5)}, ${media.longitude.toFixed(5)}`)}</span>
          ) : null}
        </FileLine>
      );
    case 'contact':
      return <FileLine icon={<PersonRegular />} name={media.name} meta={t('whatsapp.kind.contact')} />;
    default:
      return (
        <FileLine
          icon={<DocumentRegular />}
          name={media.name}
          meta={[size, media.durationSeconds ? duration(media.durationSeconds) : null, media.inBackup ? null : t('whatsapp.missingFile')].filter(Boolean).join(' · ')}
          missing={!media.inBackup}
        />
      );
  }
}

function FileLine({ icon, name, meta, missing, children }: { icon: ReactNode; name: string; meta?: string; missing?: boolean; children?: ReactNode }) {
  const { t } = useI18n();
  return (
    <span className={`${wa.file} ${missing ? wa.missing : ''}`} title={missing ? t('whatsapp.missingFile.hint') : undefined} data-testid="whatsapp-file">
      {icon}
      <span className={wa.fileText}>
        <span className={wa.fileName} dir="auto">
          {name}
        </span>
        {meta ? <span className={wa.fileMeta}>{meta}</span> : null}
        {children ? <span className={wa.fileMeta}>{children}</span> : null}
      </span>
    </span>
  );
}

/** A picture loaded when it comes near the view (thumbnails are read out of the backup one by one). */
function MediaTile({ backup, media }: { backup: BackupInfo; media: WhatsAppMedia }) {
  const { t, duration } = useI18n();
  const client = useClient();
  const ref = useRef<HTMLSpanElement>(null);
  const near = useNearView(ref);
  const thumb = useQuery({
    queryKey: whatsAppKeys.thumbnail(backup.id, media.id),
    queryFn: () => client.call('whatsapp.thumbnail', { backupId: backup.id, mediaId: media.id, size: 480 }),
    enabled: near && media.hasThumbnail,
    staleTime: Infinity,
    retry: false,
  });
  const icon = media.kind === 'video' || media.kind === 'gif' ? <VideoRegular aria-hidden /> : <ImageRegular aria-hidden />;
  const label = kindLabel(media.kind, t);
  return (
    <span
      ref={ref}
      className={`${wa.tile} ${media.kind === 'sticker' ? wa.sticker : ''}`}
      title={media.inBackup ? media.name : t('whatsapp.missingFile.hint')}
      data-testid="whatsapp-tile"
      data-missing={media.inBackup ? undefined : true}
    >
      {thumb.data?.dataUrl ? (
        <img src={thumb.data.dataUrl} alt={label} draggable={false} />
      ) : (
        <span className={wa.tileEmpty}>
          {icon}
          <span>{media.inBackup ? label : t('whatsapp.missingFile')}</span>
        </span>
      )}
      {(media.kind === 'video' || media.kind === 'gif') && media.durationSeconds ? (
        <span className={wa.playBadge}>
          <PlayRegular aria-hidden />
          {duration(media.durationSeconds)}
        </span>
      ) : null}
    </span>
  );
}

/** A voice note: read out of the backup and played only when asked. */
function VoiceNote({ backup, media }: { backup: BackupInfo; media: WhatsAppMedia }) {
  const { t, duration } = useI18n();
  const client = useClient();
  const [src, setSrc] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const length = media.durationSeconds ? duration(media.durationSeconds) : null;

  if (!media.inBackup) {
    return <FileLine icon={<MicRegular />} name={kindLabel(media.kind, t)} meta={[length, t('whatsapp.missingFile')].filter(Boolean).join(' · ')} missing />;
  }

  const play = async () => {
    setBusy(true);
    setFailed(null);
    try {
      setSrc((await client.call('whatsapp.media', { backupId: backup.id, mediaId: media.id })).dataUrl);
    } catch (e) {
      setFailed(describeError(e, t).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className={wa.voice} data-testid="whatsapp-voice">
      {src ? (
        <audio controls autoPlay src={src} onError={() => setFailed(t('whatsapp.playFailed'))} data-testid="whatsapp-audio" />
      ) : (
        <Button size="small" icon={<PlayRegular />} onClick={() => void play()} loading={busy} aria-label={`${t('whatsapp.play')} · ${kindLabel(media.kind, t)}`}>
          {t('whatsapp.play')}
        </Button>
      )}
      <MicRegular aria-hidden />
      <span>{[kindLabel(media.kind, t), length].filter(Boolean).join(' · ')}</span>
      {failed ? <span className={wa.note}>{failed}</span> : null}
    </span>
  );
}

function useNearView(ref: RefObject<Element | null>): boolean {
  const [near, setNear] = useState(false);
  useEffect(() => {
    const el = ref.current;
    if (!el || near) return;
    if (typeof IntersectionObserver === 'undefined') {
      setNear(true);
      return;
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setNear(true);
        observer.disconnect();
      }
    }, { rootMargin: '300px' });
    observer.observe(el);
    return () => observer.disconnect();
  }, [near, ref]);
  return near;
}

/** The backup's day as the host names folders after it ("2026-10-19", the PC's local day). */
function localDay(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

export function kindLabel(kind: WhatsAppMediaKind, t: I18n['t']): string {
  return t(`whatsapp.kind.${kind}`);
}

type Scope = 'current' | 'selected' | 'all';

/** Export chats (HTML or PDF) or their media: this chat, the selected ones, or all of them. */
function ExportDialog({
  mode,
  backup,
  status,
  chats,
  current,
  selected,
  onClose,
}: {
  mode: 'chats' | 'media' | null;
  backup: BackupInfo;
  status: WhatsAppStatus;
  chats: WhatsAppChat[];
  current: WhatsAppChat | null;
  selected: Set<string>;
  onClose(): void;
}) {
  const i18n = useI18n();
  const { t } = i18n;
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const [format, setFormat] = useState<MessagesFormat>('html');
  const [scope, setScope] = useState<Scope>('current');
  const [folder, setFolder] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const destination = folder ?? settings?.fileCopyFolder ?? '';
  const open = mode !== null;

  useEffect(() => {
    if (open) setScope(selected.size > 1 ? 'selected' : current ? 'current' : 'all');
    // Chosen once each time the dialog opens.
  }, [open]);

  const options: { value: Scope; label: string; hint?: string }[] = [];
  if (current) options.push({ value: 'current', label: t('whatsapp.scope.current', { name: current.title }) });
  if (selected.size > 0) options.push({ value: 'selected', label: t('whatsapp.scope.selected', { count: selected.size }) });
  options.push({ value: 'all', label: t('whatsapp.scope.all', { count: chats.length }) });

  const ids = scope === 'current' && current ? [current.id] : scope === 'selected' ? [...selected] : [];
  const scoped = ids.length ? chats.filter((c) => ids.includes(c.id)) : chats;
  const mediaFiles = scope === 'all' ? status.accounts.reduce((n, a) => n + a.mediaInBackup, 0) : scoped.reduce((n, c) => n + c.mediaCount, 0);
  const vars = { device: backup.deviceName, date: localDay(backup.createdAt) };

  const start = async () => {
    setBusy(true);
    try {
      const chatIds = ids.length ? ids : undefined;
      const job =
        mode === 'media'
          ? await client.call('whatsapp.exportMedia', { backupId: backup.id, destination, chatIds })
          : await client.call('whatsapp.export', { backupId: backup.id, format, destination, chatIds });
      rememberJob(queryClient, job);
      toast({ tone: 'info', title: t(mode === 'media' ? 'whatsapp.mediaStarted' : 'whatsapp.exportStarted'), duration: 3000 });
      onClose();
    } catch (e) {
      actions.fail(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => !next && onClose()}
      title={mode === 'media' ? t('whatsapp.mediaTitle') : t('whatsapp.exportTitle')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void start()} loading={busy} disabled={!destination} data-testid="whatsapp-export-start">
            {t('contacts.exportStart')}
          </Button>
        </>
      }
    >
      <div className={backupStyles.steps}>
        <div className={wa.group}>
          <span className={wa.groupLabel}>{t('whatsapp.scope')}</span>
          <RadioCards label={t('whatsapp.scope')} value={scope} onChange={setScope} options={options} />
        </div>
        {mode === 'chats' ? (
          <div className={wa.group}>
            <span className={wa.groupLabel}>{t('messages.format')}</span>
            <RadioCards
              label={t('messages.format')}
              value={format}
              onChange={setFormat}
              options={[
                { value: 'html', label: t('messages.format.html'), hint: t('messages.format.htmlHint') },
                { value: 'pdf', label: t('messages.format.pdf'), hint: t('messages.format.pdfHint') },
              ]}
            />
          </div>
        ) : null}
        <p className={wa.prose}>
          {mode === 'media' ? `${t('whatsapp.media.where', vars)} ${t('whatsapp.media.files', { count: mediaFiles })}.` : t('whatsapp.export.where', vars)}
        </p>
        <dl className={backupStyles.facts}>
          <dt>{t('contacts.exportTo')}</dt>
          <dd>
            <span className={backupStyles.path}>{destination}</span>{' '}
            <Button
              size="small"
              variant="link"
              onClick={async () => {
                const picked = await actions.pickFolder(t('contacts.exportTo'));
                if (picked) setFolder(picked);
              }}
            >
              {t('common.change')}
            </Button>
          </dd>
        </dl>
      </div>
    </Dialog>
  );
}
