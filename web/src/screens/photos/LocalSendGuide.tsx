import { CheckmarkRegular, WarningRegular } from '@fluentui/react-icons';
import { useMemo, type ReactNode } from 'react';
import { cx } from '../../components/cx';
import { useI18n, type MessageKey } from '../../i18n';
import { encodeQr, qrPath } from '../toolbox/qr';
import { PhoneArt, SwitchArt, type PhoneScreen } from './LocalSendArt';
import styles from './LocalSend.module.css';

/** LocalSend in the App Store, as localsend.org's download page links it. */
export const APP_STORE_URL = 'https://apps.apple.com/us/app/localsend/id1661733229';

export type StepState = 'todo' | 'current' | 'done' | 'warn';

/** Where the person is, from what the dialog knows: the look over the cable, then the send. */
export type GuideStage =
  | 'looking'
  | 'notListening'
  | 'notAnswering'
  | 'ready'
  | 'accept'
  | 'sending'
  | 'done'
  | 'background'
  | 'failedAccept'
  | 'failedSending';

/** Which of the four steps is done, which is now, and which needs fixing. */
export function stepStates(stage: GuideStage): StepState[] {
  switch (stage) {
    case 'looking':
    case 'notListening':
      return ['current', 'todo', 'todo', 'todo'];
    case 'notAnswering':
    case 'background':
      return ['done', 'warn', 'todo', 'todo'];
    case 'ready':
    case 'accept':
      return ['done', 'done', 'current', 'todo'];
    case 'failedAccept':
      return ['done', 'done', 'warn', 'todo'];
    case 'sending':
      return ['done', 'done', 'done', 'current'];
    case 'failedSending':
      return ['done', 'done', 'done', 'warn'];
    case 'done':
      return ['done', 'done', 'done', 'done'];
  }
}

const STEPS: { screen: PhoneScreen; title: MessageKey; text: MessageKey }[] = [
  { screen: 'appStore', title: 'localsend.step1.title', text: 'localsend.step1.text' },
  { screen: 'receive', title: 'localsend.step2.title', text: 'localsend.step2.text' },
  { screen: 'accept', title: 'localsend.step3.title', text: 'localsend.step3.text' },
  { screen: 'photos', title: 'localsend.step4.title', text: 'localsend.step4.text' },
];

/**
 * The illustrated walkthrough of "Straight into Photos": install LocalSend (App Store link and QR), open it and keep it
 * on screen, tap Accept (and Allow the first time), then find the photos in Photos › Recents. The live status lights
 * up the step the person is on; a step that needs fixing turns amber.
 */
export function LocalSendGuide({ stage }: { stage: GuideStage }) {
  const { t, n } = useI18n();
  const states = stepStates(stage);
  const qr = useMemo(() => {
    const code = encodeQr(APP_STORE_URL, 'M');
    return { path: qrPath(code), size: code.size + 8 };
  }, []);

  const extras: ReactNode[] = [
    <a key="store" href={APP_STORE_URL} target="_blank" rel="noreferrer" className={styles.storeLink} data-testid="localsend-appstore">
      {t('localsend.step1.link')}
    </a>,
    <span key="tip" className={styles.stepTip}>
      {t('localsend.step2.tip')}
    </span>,
    null,
    null,
  ];

  return (
    <div className={styles.guideWrap}>
      <ol className={styles.guide} data-testid="localsend-guide" data-stage={stage}>
        {STEPS.map((step, i) => {
          const state = states[i]!;
          return (
            <li
              key={step.screen}
              className={cx(styles.stepCard, state === 'current' && styles.stepCurrent, state === 'done' && styles.stepDone, state === 'warn' && styles.stepWarn)}
              data-testid={`localsend-step-${i + 1}`}
              data-state={state}
              aria-current={state === 'current' ? 'step' : undefined}
            >
              <div className={styles.stepPicture}>
                <PhoneArt screen={step.screen} />
                {i === 0 ? (
                  <div className={styles.qrBox} data-testid="localsend-appstore-qr">
                    <svg viewBox={`0 0 ${qr.size} ${qr.size}`} role="img" aria-label={t('localsend.step1.qr')} shapeRendering="crispEdges">
                      <rect width={qr.size} height={qr.size} fill="#ffffff" />
                      <path d={qr.path} fill="#000000" />
                    </svg>
                  </div>
                ) : null}
              </div>
              <div className={styles.stepHead}>
                <span className={styles.stepNumber} aria-hidden>
                  {state === 'done' ? <CheckmarkRegular /> : state === 'warn' ? <WarningRegular /> : n(i + 1)}
                </span>
                <span className={styles.stepTitle}>{t(step.title)}</span>
              </div>
              <p className={styles.stepText}>{t(step.text)}</p>
              {extras[i]}
            </li>
          );
        })}
      </ol>
      <div className={styles.tip} data-testid="localsend-tip">
        <SwitchArt />
        <span className={styles.tipText}>
          <span className={styles.tipTitle}>{t('localsend.tip.title')}</span> {t('localsend.tip.text')}
        </span>
      </div>
    </div>
  );
}
