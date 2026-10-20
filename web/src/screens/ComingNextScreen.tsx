import { EmptyState } from '../components/ui';
import { useI18n } from '../i18n';
import { NAV } from '../shell/nav';
import type { Section } from '../state/ui';
import styles from './Screen.module.css';

/** A tidy placeholder for the sections that come in later phases. */
export function ComingNextScreen({ section }: { section: Section }) {
  const { t } = useI18n();
  const entry = NAV.find((n) => n.section === section);
  const Icon = entry?.icon;
  const name = entry ? t(entry.label) : section;
  return (
    <div className={styles.coming} data-testid={`screen-${section}`}>
      <EmptyState icon={Icon ? <Icon /> : null} title={t('coming.title', { section: name })} body={t('coming.body')} />
    </div>
  );
}
