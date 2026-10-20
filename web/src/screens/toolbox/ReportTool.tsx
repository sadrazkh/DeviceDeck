import { DocumentPdfRegular, SaveRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useActions } from '../../app/actions';
import { useClient } from '../../app/bridgeContext';
import type { ReportFormat } from '../../bridge/toolbox';
import type { DeviceSummary } from '../../bridge/types';
import { Button } from '../../components/Button';
import { cx } from '../../components/cx';
import { ErrorBanner } from '../../components/ErrorState';
import { Segmented, Skeleton, Switch } from '../../components/ui';
import { toast } from '../../state/toasts';
import { useToolboxI18n } from './i18n';
import styles from './Toolbox.module.css';

export function ReportTool({ device }: { device: DeviceSummary }) {
  const client = useClient();
  const actions = useActions();
  const { tt, lang } = useToolboxI18n();
  const [language, setLanguage] = useState<'en' | 'fa'>(lang);
  const [reveal, setReveal] = useState(false);
  const [saving, setSaving] = useState<ReportFormat | null>(null);
  const preview = useQuery({
    queryKey: ['report.preview', device.id, language, reveal],
    queryFn: () => client.call('report.preview', { deviceId: device.id, language, revealSerials: reveal }),
    staleTime: 30_000,
  });

  const save = async (format: ReportFormat) => {
    setSaving(format);
    try {
      const { path } = await client.call('report.save', { deviceId: device.id, language, revealSerials: reveal, format });
      toast({ tone: 'success', title: tt('report.saved'), body: path, action: { label: tt('heic.showFiles'), run: () => void actions.revealInExplorer(path) } });
    } catch (error) {
      actions.fail(error);
    } finally {
      setSaving(null);
    }
  };

  return (
    <div className={styles.tool} data-testid="report-tool">
      <div className={styles.reportLayout}>
        <div className={cx(styles.panel, styles.options)}>
          <div className={styles.optionGroup}>
            <span className={styles.optionLabel}>{tt('report.language')}</span>
            <Segmented
              value={language}
              onChange={setLanguage}
              label={tt('report.language')}
              options={[
                { value: 'en', label: 'English' },
                { value: 'fa', label: 'فارسی' },
              ]}
            />
          </div>
          <div className={styles.optionGroup}>
            <Switch checked={reveal} onChange={setReveal} label={tt('report.reveal')} />
            <span className={cx(styles.muted, styles.small)}>{tt('report.revealHint')}</span>
          </div>
          <div className={styles.optionGroup}>
            <Button variant="accent" icon={<SaveRegular />} loading={saving === 'html'} disabled={saving !== null} onClick={() => void save('html')} data-testid="report-save-html">
              {tt('report.saveHtml')}
            </Button>
            <Button icon={<DocumentPdfRegular />} loading={saving === 'pdf'} disabled={saving !== null} onClick={() => void save('pdf')} data-testid="report-save-pdf">
              {tt('report.savePdf')}
            </Button>
          </div>
        </div>
        <div className={cx(styles.panel, styles.preview)} aria-label={tt('report.preview')}>
          {preview.isError ? (
            <ErrorBanner error={preview.error} onRetry={() => void preview.refetch()} />
          ) : preview.data ? (
            // No scripts in the report; the sandbox keeps it that way.
            <iframe title={tt('report.preview')} sandbox="" srcDoc={preview.data.html} data-testid="report-preview" />
          ) : (
            <div style={{ padding: 16 }}>
              <Skeleton height={28} width="40%" />
              <div style={{ height: 12 }} />
              <Skeleton height={200} />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
