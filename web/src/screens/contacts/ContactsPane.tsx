import { ArrowExportRegular, PersonRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import { useSettings } from '../../app/queries';
import type { BackupInfo, Contact, ContactsFormat, LabeledValue } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { ErrorBanner } from '../../components/ErrorState';
import { Checkbox, EmptyState, RadioCards, Skeleton } from '../../components/ui';
import { useI18n, type I18n, type MessageKey } from '../../i18n';
import { toast } from '../../state/toasts';
import { rememberJob, useContacts } from '../backup/backupQueries';
import backupStyles from '../backup/Backup.module.css';
import styles from './Contacts.module.css';

/** "mobile" → "Mobile" in the page's language; the user's own labels as they are. */
export function labelText(label: string, { t }: I18n): string {
  const key = `contacts.label.${label}` as MessageKey;
  const text = t(key);
  return text === key ? label : text;
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const letters = parts.length > 1 ? [parts[0]!, parts.at(-1)!] : parts;
  return letters.map((p) => Array.from(p)[0] ?? '').join('').toLocaleUpperCase() || '?';
}

function matches(contact: Contact, query: string): boolean {
  const q = query.trim().toLocaleLowerCase();
  if (!q) return true;
  const digits = q.replace(/\D/g, '');
  return (
    contact.displayName.toLocaleLowerCase().includes(q) ||
    (contact.organization ?? '').toLocaleLowerCase().includes(q) ||
    contact.emails.some((e) => e.value.toLocaleLowerCase().includes(q)) ||
    (digits.length >= 3 && contact.phones.some((p) => p.value.replace(/\D/g, '').includes(digits)))
  );
}

/** The contacts of one iPhone backup: search, details, and export as vCard or CSV. */
export function ContactsPane({ backup }: { backup: BackupInfo }) {
  const i18n = useI18n();
  const { t, n } = i18n;
  const contacts = useContacts(backup);
  const [query, setQuery] = useState('');
  const [current, setCurrent] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [exporting, setExporting] = useState(false);

  const list = useMemo(() => (contacts.data ?? []).filter((c) => matches(c, query)), [contacts.data, query]);
  const contact = list.find((c) => c.id === current) ?? list[0] ?? null;

  useEffect(() => {
    setSelected(new Set());
    setCurrent(null);
  }, [backup.id]);

  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  if (contacts.error) return <ErrorBanner error={contacts.error} onRetry={() => void contacts.refetch()} />;
  if (contacts.isPending) return <Skeleton height={240} />;
  if ((contacts.data ?? []).length === 0) return <EmptyState icon={<PersonRegular />} title={t('contacts.empty')} />;

  const allChecked: boolean | 'indeterminate' = list.length > 0 && list.every((c) => selected.has(c.id)) ? true : selected.size > 0 ? 'indeterminate' : false;

  return (
    <div className={styles.split} data-testid="contacts-pane">
      <section className={styles.pane} aria-label={t('contacts.tab.contacts')}>
        <div className={styles.paneHead}>
          <Checkbox checked={allChecked} onChange={(v) => setSelected(v ? new Set(list.map((c) => c.id)) : new Set())} label={t('common.selectAll')} />
          <input className={styles.search} type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('contacts.search')} aria-label={t('contacts.search')} />
        </div>
        <div className={styles.scroll} role="list">
          {list.length === 0 ? <p className={backupStyles.note} style={{ padding: 12 }}>{t('contacts.none')}</p> : null}
          {list.map((c) => (
            <div key={c.id} role="listitem" className={styles.item} aria-current={contact?.id === c.id} onClick={() => setCurrent(c.id)} data-testid="contact-row">
              <Checkbox checked={selected.has(c.id)} onChange={() => toggle(c.id)} label={c.displayName} />
              <span className={styles.avatar} aria-hidden>
                {initials(c.displayName)}
              </span>
              <span className={styles.itemText}>
                <span className={styles.itemTitle} dir="auto">
                  {c.displayName}
                </span>
                <span className={styles.itemMeta} dir="auto">
                  {c.organization && c.displayName !== c.organization ? c.organization : (c.phones[0]?.value ?? c.emails[0]?.value ?? '')}
                </span>
              </span>
            </div>
          ))}
        </div>
        <div className={styles.footer}>
          {selected.size > 0 ? t('common.selected', { count: selected.size }) : t('contacts.count', { count: list.length, n: n(list.length) })}
        </div>
      </section>
      <section className={styles.pane} aria-label={contact?.displayName ?? t('contacts.pick')}>
        <div className={styles.paneHead} style={{ justifyContent: 'flex-end' }}>
          <Button variant="accent" icon={<ArrowExportRegular />} onClick={() => setExporting(true)} data-testid="contacts-export">
            {selected.size > 0 ? t('contacts.exportSelected', { count: selected.size }) : t('contacts.exportAll')}
          </Button>
        </div>
        {contact ? <ContactDetails contact={contact} /> : <EmptyState title={t('contacts.pick')} />}
      </section>
      <ExportContactsDialog backup={backup} ids={selected.size > 0 ? [...selected] : null} open={exporting} onOpenChange={setExporting} />
    </div>
  );
}

function ContactDetails({ contact }: { contact: Contact }) {
  const i18n = useI18n();
  const { t, digits } = i18n;
  const values = (field: MessageKey, items: LabeledValue[], ltr: boolean) =>
    items.map((item, i) => (
      <div key={`${field}-${i}`} style={{ display: 'contents' }}>
        <dt>{labelText(item.label, i18n)}</dt>
        <dd className={ltr ? styles.ltr : undefined}>{item.value}</dd>
      </div>
    ));
  const birthday = contact.birthday
    ? contact.birthday.startsWith('--')
      ? digits(contact.birthday.slice(2))
      : i18n.date(`${contact.birthday}T12:00:00`, 'short')
    : null;
  const subtitle = [contact.jobTitle, contact.organization !== contact.displayName ? contact.organization : null, contact.department].filter(Boolean).join(' · ');
  return (
    <div className={styles.details} data-testid="contact-details">
      <div className={styles.detailsHead}>
        <span className={`${styles.avatar} ${styles.bigAvatar}`} aria-hidden>
          {initials(contact.displayName)}
        </span>
        <div>
          <div className={styles.name} dir="auto">
            {contact.displayName}
          </div>
          {subtitle ? (
            <div className={backupStyles.note} dir="auto">
              {subtitle}
            </div>
          ) : null}
        </div>
      </div>
      <dl className={styles.fields}>
        {values('contacts.field.phone', contact.phones, true)}
        {values('contacts.field.email', contact.emails, true)}
        {values('contacts.field.url', contact.urls, true)}
        {contact.addresses.map((a, i) => (
          <div key={`address-${i}`} style={{ display: 'contents' }}>
            <dt>{labelText(a.label, i18n)}</dt>
            <dd dir="auto">{[a.street, [a.postalCode, a.city].filter(Boolean).join(' '), a.region, a.country].filter(Boolean).join('\n')}</dd>
          </div>
        ))}
        {birthday ? (
          <>
            <dt>{t('contacts.field.birthday')}</dt>
            <dd>{birthday}</dd>
          </>
        ) : null}
        {contact.nickname ? (
          <>
            <dt>{t('contacts.field.nickname')}</dt>
            <dd dir="auto">{contact.nickname}</dd>
          </>
        ) : null}
        {contact.note ? (
          <>
            <dt>{t('contacts.field.note')}</dt>
            <dd dir="auto">{contact.note}</dd>
          </>
        ) : null}
      </dl>
    </div>
  );
}

function ExportContactsDialog({ backup, ids, open, onOpenChange }: { backup: BackupInfo; ids: string[] | null; open: boolean; onOpenChange(open: boolean): void }) {
  const { t } = useI18n();
  const client = useClient();
  const queryClient = useQueryClient();
  const actions = useActions();
  const settings = useSettings().data;
  const [format, setFormat] = useState<ContactsFormat>('vCard');
  const [folder, setFolder] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const destination = folder ?? settings?.fileCopyFolder ?? '';

  const start = async () => {
    setBusy(true);
    try {
      const job = await client.call('contacts.export', { backupId: backup.id, format, destination, ...(ids ? { contactIds: ids } : {}) });
      rememberJob(queryClient, job);
      toast({ tone: 'info', title: t('contacts.exportStarted'), duration: 3000 });
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
      title={ids ? t('contacts.exportSelected', { count: ids.length }) : t('contacts.exportAll')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="accent" onClick={() => void start()} loading={busy} disabled={!destination} data-testid="contacts-export-start">
            {t('contacts.exportStart')}
          </Button>
        </>
      }
    >
      <div className={backupStyles.steps}>
        <RadioCards
          label={t('contacts.format')}
          value={format}
          onChange={setFormat}
          options={[
            { value: 'vCard', label: t('contacts.format.vCard'), hint: t('contacts.format.vCardHint') },
            { value: 'csv', label: t('contacts.format.csv'), hint: t('contacts.format.csvHint') },
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
