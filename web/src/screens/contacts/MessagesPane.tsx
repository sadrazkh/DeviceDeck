import { ArrowExportRegular, AttachRegular, ChatRegular, PeopleRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useSettings } from '../../app/queries';
import type { BackupInfo, Conversation, Message, MessagesFormat } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { ErrorBanner, describeError } from '../../components/ErrorState';
import { Banner, Checkbox, EmptyState, RadioCards, Skeleton } from '../../components/ui';
import { useI18n } from '../../i18n';
import { toast } from '../../state/toasts';
import { rememberJob, useConversations } from '../backup/backupQueries';
import backupStyles from '../backup/Backup.module.css';
import { initials } from './ContactsPane';
import styles from './Contacts.module.css';

const PAGE = 200;

/** The conversations of one iPhone backup: list, thread (newest at the bottom, earlier pages on demand), export. */
export function MessagesPane({ backup }: { backup: BackupInfo }) {
  const i18n = useI18n();
  const { t, date } = i18n;
  const conversations = useConversations(backup);
  const [query, setQuery] = useState('');
  const [current, setCurrent] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [exporting, setExporting] = useState(false);

  const list = useMemo(() => {
    const q = query.trim().toLocaleLowerCase();
    return (conversations.data ?? []).filter(
      (c) => !q || c.title.toLocaleLowerCase().includes(q) || (c.preview ?? '').toLocaleLowerCase().includes(q) || c.members.some((m) => m.handle.includes(q)),
    );
  }, [conversations.data, query]);
  const conversation = list.find((c) => c.id === current) ?? list[0] ?? null;

  useEffect(() => {
    setSelected(new Set());
    setCurrent(null);
  }, [backup.id]);

  if (conversations.error) return <ErrorBanner error={conversations.error} onRetry={() => void conversations.refetch()} />;
  if (conversations.isPending) return <Skeleton height={240} />;
  if ((conversations.data ?? []).length === 0) return <EmptyState icon={<ChatRegular />} title={t('messages.empty')} />;

  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const exportIds = selected.size > 0 ? [...selected] : conversation ? [conversation.id] : [];

  return (
    <div className={styles.split} data-testid="messages-pane">
      <section className={styles.pane} aria-label={t('contacts.tab.messages')}>
        <div className={styles.paneHead}>
          <input className={styles.search} type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('messages.search')} aria-label={t('messages.search')} />
        </div>
        <div className={styles.scroll} role="list">
          {list.map((c) => (
            <div key={c.id} role="listitem" className={styles.item} aria-current={conversation?.id === c.id} onClick={() => setCurrent(c.id)} data-testid="conversation-row">
              <Checkbox checked={selected.has(c.id)} onChange={() => toggle(c.id)} label={c.title} />
              <span className={styles.avatar} aria-hidden>
                {c.isGroup ? <PeopleRegular /> : initials(c.title)}
              </span>
              <span className={styles.itemText}>
                <span className={styles.itemTitle} dir="auto">
                  {c.title}
                </span>
                <span className={styles.itemMeta} dir="auto">
                  {c.preview ?? (c.attachmentCount > 0 ? t('messages.attachmentOnly') : '')}
                </span>
              </span>
              {c.lastMessageAt ? <span className={styles.itemDate}>{date(c.lastMessageAt, 'short')}</span> : null}
            </div>
          ))}
        </div>
        <div className={styles.footer}>{selected.size > 0 ? t('common.selected', { count: selected.size }) : t('messages.count', { count: list.length })}</div>
      </section>
      <section className={styles.pane} aria-label={conversation?.title ?? t('messages.pick')}>
        <div className={styles.paneHead}>
          <span className={styles.itemText}>
            <span className={styles.itemTitle} dir="auto">
              {conversation?.title}
            </span>
            {conversation ? (
              <span className={styles.itemMeta}>
                {[conversation.service, t('messages.messages', { count: conversation.messageCount }), conversation.attachmentCount ? t('messages.attachments', { count: conversation.attachmentCount }) : null]
                  .filter(Boolean)
                  .join(' · ')}
              </span>
            ) : null}
          </span>
          <Button variant="accent" icon={<ArrowExportRegular />} onClick={() => setExporting(true)} disabled={exportIds.length === 0} data-testid="messages-export">
            {selected.size > 1 ? t('messages.exportMany', { count: selected.size }) : t('messages.export')}
          </Button>
        </div>
        {conversation ? <Thread key={`${backup.id}:${conversation.id}`} backup={backup} conversation={conversation} /> : <EmptyState title={t('messages.pick')} />}
      </section>
      <ExportMessagesDialog backup={backup} ids={exportIds} open={exporting} onOpenChange={setExporting} />
    </div>
  );
}

function Thread({ backup, conversation }: { backup: BackupInfo; conversation: Conversation }) {
  const i18n = useI18n();
  const { t, bytes } = i18n;
  const client = useClient();
  const [messages, setMessages] = useState<Message[]>([]);
  const [offset, setOffset] = useState<number | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);
  const keepBottom = useRef<number | null>(null);
  // Pages asked for so far: development mode runs the first effect twice, and a page must land once.
  const requested = useRef(new Set<number>());

  const load = async (from: number) => {
    if (requested.current.has(from)) return;
    requested.current.add(from);
    setLoading(true);
    setError(null);
    try {
      const page = await client.call('messages.list', { backupId: backup.id, conversationId: conversation.id, offset: from, limit: PAGE });
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
    void load(Math.max(0, conversation.messageCount - PAGE));
    // One conversation per component (the parent keys it).
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
    // Fonts and wrapped text can still grow the thread after this layout.
    const frame = requestAnimationFrame(() => (el.scrollTop = el.scrollHeight));
    return () => cancelAnimationFrame(frame);
  }, [messages]);

  const earlier = () => {
    if (offset === null || offset === 0 || loading) return;
    const el = scroller.current;
    keepBottom.current = el ? el.scrollHeight - el.scrollTop : null;
    void load(Math.max(0, offset - PAGE));
  };

  let lastDay = '';
  let lastSender: string | null = null;
  return (
    <div className={styles.thread} ref={scroller} data-testid="message-thread">
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
        const sender = m.fromMe ? 'me' : (m.handle ?? '');
        const showSender = conversation.isGroup && !m.fromMe && sender !== lastSender;
        lastSender = m.kind === 'event' ? null : sender;
        return (
          <Fragment key={m.id}>
            {showDay && day ? <div className={styles.day}>{day}</div> : null}
            {m.kind === 'event' ? (
              <div className={styles.event} dir="auto">
                {m.text ? t('messages.event', { text: m.text }) : t('messages.eventUnknown')}
              </div>
            ) : (
              <div className={styles.message} data-me={m.fromMe || undefined} data-testid="message">
                {showSender ? (
                  <span className={styles.sender} dir="auto">
                    {m.senderName ?? m.handle}
                  </span>
                ) : null}
                <div className={styles.bubble} dir="auto">
                  {m.attachments.map((a) => (
                    <div key={a.id} className={styles.attachment} data-missing={!a.inBackup || undefined} title={a.inBackup ? undefined : t('messages.attachment.missing')}>
                      <AttachRegular aria-hidden />
                      <span dir="auto">{a.name}</span>
                      {a.size > 0 ? <span>· {bytes(a.size)}</span> : null}
                    </div>
                  ))}
                  {m.text}
                </div>
                {m.date ? <span className={styles.time}>{new Intl.DateTimeFormat(i18n.lang === 'fa' ? 'fa-IR' : 'en-GB', { hour: '2-digit', minute: '2-digit' }).format(new Date(m.date))}</span> : null}
              </div>
            )}
          </Fragment>
        );
      })}
    </div>
  );
}

function ExportMessagesDialog({ backup, ids, open, onOpenChange }: { backup: BackupInfo; ids: string[]; open: boolean; onOpenChange(open: boolean): void }) {
  const { t } = useI18n();
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const [format, setFormat] = useState<MessagesFormat>('html');
  const [folder, setFolder] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const destination = folder ?? settings?.fileCopyFolder ?? '';

  const start = async () => {
    setBusy(true);
    try {
      const job = await client.call('messages.export', { backupId: backup.id, conversationIds: ids, format, destination });
      rememberJob(queryClient, job);
      toast({ tone: 'info', title: t('messages.exportStarted'), duration: 3000 });
      onOpenChange(false);
    } catch (e) {
      actions.fail(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={ids.length > 1 ? t('messages.exportMany', { count: ids.length }) : t('messages.export')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void start()} loading={busy} disabled={!destination || ids.length === 0} data-testid="messages-export-start">
            {t('contacts.exportStart')}
          </Button>
        </>
      }
    >
      <div className={backupStyles.steps}>
        <RadioCards
          label={t('messages.format')}
          value={format}
          onChange={setFormat}
          options={[
            { value: 'html', label: t('messages.format.html'), hint: t('messages.format.htmlHint') },
            { value: 'pdf', label: t('messages.format.pdf'), hint: t('messages.format.pdfHint') },
          ]}
        />
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
