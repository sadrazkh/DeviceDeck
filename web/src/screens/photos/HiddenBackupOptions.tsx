import { LockOpenRegular } from '@fluentui/react-icons';
import { Fragment, useEffect, useId, useState, type ReactNode } from 'react';
import {
  HIDDEN_FOLDER_DEFAULT,
  HIDDEN_FOLDER_FA,
  HIDDEN_FOLDER_MAX,
  hiddenFolderProblem,
  hiddenRoot,
} from '../../bridge/hiddenBackup';
import type { PhotoBackupHidden, PhotoBackupHiddenLayout } from '../../bridge/types';
import { Button } from '../../components/Button';
import { Banner, Segmented } from '../../components/ui';
import { useI18n } from '../../i18n';
import screen from '../Screen.module.css';
import { examplePath } from './backupQueries';
import styles from './Backup.module.css';

// Hidden photos in the photo backup (round 4, hiddenbackup): which photos (normal only, both, hidden only), where the
// hidden ones go (a folder of their own, dated inside, or with the others), that folder's name, an example path and a
// privacy note. The Back up panel shows it for an iPhone; Settings shows the same choices as the defaults.

export interface HiddenChoice {
  hidden: PhotoBackupHidden;
  hiddenLayout: PhotoBackupHiddenLayout;
  hiddenFolder: string;
}

interface Props {
  value: HiddenChoice;
  onChange(patch: Partial<HiddenChoice>): void;
  /** For the example: where a photo taken today would go. */
  destination: string;
  pattern: string;
  convertHeic: boolean;
  disabled?: boolean;
}

/** The Back up panel's section: one block under the other options. */
export function HiddenBackupSection({ value, onChange, destination, pattern, convertHeic, disabled, movesNote }: Props & { movesNote: boolean }) {
  const { t } = useI18n();
  const included = value.hidden !== 'exclude';
  return (
    <div className={styles.section} data-testid="backup-hidden" data-hidden={value.hidden} data-layout={value.hiddenLayout}>
      <span className={styles.label}>{t('photos.backup.hidden')}</span>
      <ModeControl value={value.hidden} onChange={(hidden) => onChange({ hidden })} />
      {included ? (
        <>
          <span className={styles.label}>{t('photos.backup.hidden.layout')}</span>
          <LayoutControl value={value.hiddenLayout} onChange={(hiddenLayout) => onChange({ hiddenLayout })} />
          {value.hiddenLayout === 'separate' ? (
            <div className={styles.nameField}>
              <span>{t('photos.backup.hidden.folder')}</span>
              <FolderField folder={value.hiddenFolder} onCommit={(hiddenFolder) => onChange({ hiddenFolder })} disabled={disabled} />
            </div>
          ) : null}
          <Example value={value} destination={destination} pattern={pattern} convertHeic={convertHeic} />
          {movesNote ? (
            <span className={styles.hint} data-testid="backup-hidden-moves">
              {t('photos.backup.hidden.moves')}
            </span>
          ) : null}
          <Privacy />
        </>
      ) : (
        <span className={styles.hint}>{t('photos.backup.hidden.excludeHint')}</span>
      )}
    </div>
  );
}

/** Settings › Photo backup: the same choices as rows, as the defaults of an iPhone without its own. */
export function HiddenBackupSettingsRows({ value, onChange, destination, pattern, convertHeic }: Props) {
  const { t } = useI18n();
  const included = value.hidden !== 'exclude';
  return (
    <div data-testid="settings-backup-hidden" data-hidden={value.hidden} data-layout={value.hiddenLayout}>
      <Row label={t('photos.backup.hidden')}>
        <ModeControl value={value.hidden} onChange={(hidden) => onChange({ hidden })} />
      </Row>
      {included ? (
        <Row label={t('photos.backup.hidden.layout')}>
          <LayoutControl value={value.hiddenLayout} onChange={(hiddenLayout) => onChange({ hiddenLayout })} />
        </Row>
      ) : null}
      {included && value.hiddenLayout === 'separate' ? (
        <Row label={t('photos.backup.hidden.folder')}>
          <FolderField folder={value.hiddenFolder} onCommit={(hiddenFolder) => onChange({ hiddenFolder })} />
        </Row>
      ) : null}
      <div className={styles.hiddenNotes}>
        {included ? <Example value={value} destination={destination} pattern={pattern} convertHeic={convertHeic} /> : null}
        <span className={styles.hint}>{included ? t('photos.backup.hidden.iphoneOnly') : `${t('photos.backup.hidden.excludeHint')} ${t('photos.backup.hidden.iphoneOnly')}`}</span>
        {included ? <Privacy /> : null}
      </div>
    </div>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className={screen.settingRow}>
      <span className={screen.settingLabel}>{label}</span>
      {children}
    </div>
  );
}

function ModeControl({ value, onChange }: { value: PhotoBackupHidden; onChange(value: PhotoBackupHidden): void }) {
  const { t } = useI18n();
  return (
    <Segmented
      label={t('photos.backup.hidden')}
      value={value}
      onChange={onChange}
      options={[
        { value: 'exclude', label: t('photos.backup.hidden.exclude') },
        { value: 'include', label: t('photos.backup.hidden.include') },
        { value: 'only', label: t('photos.backup.hidden.only') },
      ]}
    />
  );
}

function LayoutControl({ value, onChange }: { value: PhotoBackupHiddenLayout; onChange(value: PhotoBackupHiddenLayout): void }) {
  const { t } = useI18n();
  return (
    <Segmented
      label={t('photos.backup.hidden.layout')}
      value={value}
      onChange={onChange}
      options={[
        { value: 'separate', label: t('photos.backup.hidden.separate') },
        { value: 'together', label: t('photos.backup.hidden.together') },
      ]}
    />
  );
}

/** The folder's name, saved when the field is left (or Enter) and only when it is one valid folder name. */
function FolderField({ folder, onCommit, disabled }: { folder: string; onCommit(name: string): void; disabled?: boolean }) {
  const { t, lang } = useI18n();
  const [draft, setDraft] = useState(folder);
  const [editing, setEditing] = useState(false);
  const errorId = useId();
  useEffect(() => {
    if (!editing) setDraft(folder);
  }, [folder, editing]);
  const problem = hiddenFolderProblem(draft);
  const suggestion = lang === 'fa' ? HIDDEN_FOLDER_FA : HIDDEN_FOLDER_DEFAULT;
  const commit = () => {
    setEditing(false);
    if (problem) {
      setDraft(folder); // the saved name stays; the error said why
      return;
    }
    if (draft.trim() !== folder) onCommit(draft.trim());
  };
  return (
    <span className={styles.folderField}>
      <input
        className={styles.nameInput}
        value={draft}
        dir="auto"
        spellCheck={false}
        disabled={disabled}
        maxLength={HIDDEN_FOLDER_MAX + 8}
        aria-label={t('photos.backup.hidden.folder')}
        aria-invalid={problem ? true : undefined}
        aria-describedby={problem && editing ? errorId : undefined}
        onFocus={() => setEditing(true)}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur();
        }}
        data-testid="backup-hidden-folder"
      />
      {folder !== suggestion ? (
        <Button
          variant="link"
          disabled={disabled}
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => {
            setEditing(false);
            setDraft(suggestion);
            onCommit(suggestion);
          }}
          data-testid="backup-hidden-suggest"
        >
          {t('photos.backup.hidden.suggest', { name: suggestion })}
        </Button>
      ) : null}
      {problem && editing ? (
        <span className={styles.fieldError} id={errorId} role="alert" data-testid="backup-hidden-folder-error">
          {t(`photos.backup.hidden.problem.${problem}`, { max: HIDDEN_FOLDER_MAX })}
        </span>
      ) : null}
    </span>
  );
}

/** "For example  E:\Backup\Sadal\Hidden\2026\10\IMG_1234.HEIC": where a hidden photo taken today would go. */
function Example({ value, destination, pattern, convertHeic }: { value: HiddenChoice; destination: string; pattern: string; convertHeic: boolean }) {
  const { t } = useI18n();
  const apart = hiddenRoot(destination, value.hidden, value.hiddenLayout, value.hiddenFolder) ? value.hiddenFolder : null;
  const path = examplePath(destination, pattern, new Date(), convertHeic, apart);
  return (
    <span className={styles.example}>
      <span className={styles.hint}>{t('photos.backup.hidden.example')}</span>
      {/* Each folder isolated: "پنهان\2026\10" would otherwise be laid out right to left as one run. */}
      <code className={styles.examplePath} title={path} data-testid="backup-hidden-example">
        {path.split('\\').map((part, i) => (
          <Fragment key={i}>
            {i > 0 ? '\\' : null}
            <bdi className={/\p{Script=Arabic}/u.test(part) ? styles.persianPart : undefined}>{part}</bdi>
          </Fragment>
        ))}
      </code>
    </span>
  );
}

function Privacy() {
  const { t } = useI18n();
  return (
    <div className={styles.wide} data-testid="backup-hidden-privacy">
      <Banner tone="warning" icon={<LockOpenRegular />}>
        {t('photos.backup.hidden.privacy')}
      </Banner>
    </div>
  );
}
