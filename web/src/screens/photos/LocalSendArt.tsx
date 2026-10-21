// Our own drawings for the LocalSend walkthrough: an iPhone showing each step. Generic shapes only (no LocalSend
// screenshots or logo); colours come from the theme tokens through LocalSend.module.css, so light and dark both work.

import { cx } from '../../components/cx';
import styles from './LocalSend.module.css';

export type PhoneScreen = 'appStore' | 'receive' | 'accept' | 'photos';

/** An iPhone (100 x 200) with one of the walkthrough's screens. Decorative: the caption beside it says the same. */
export function PhoneArt({ screen, className }: { screen: PhoneScreen; className?: string }) {
  return (
    <svg viewBox="0 0 100 200" className={cx(styles.phone, className)} aria-hidden focusable="false">
      <rect x="0.5" y="0.5" width="99" height="199" rx="17" className={styles.artBody} />
      <rect x="5" y="5" width="90" height="190" rx="13" className={styles.artScreen} />
      {screen === 'appStore' ? <AppStoreScreen /> : null}
      {screen === 'receive' ? <ReceiveScreen /> : null}
      {screen === 'accept' ? <AcceptScreen /> : null}
      {screen === 'photos' ? <PhotosScreen /> : null}
      <rect x="38" y="10" width="24" height="7" rx="3.5" className={styles.artBody} />
    </svg>
  );
}

function TabBar({ active }: { active: number }) {
  return (
    <>
      <rect x="5" y="176" width="90" height="0.8" className={styles.artLine} />
      {[22, 50, 78].map((x, i) => (
        <rect key={x} x={x - 6} y="182" width="12" height="5" rx="2.5" className={i === active ? styles.artAccent : styles.artMuted} />
      ))}
    </>
  );
}

/** A store page: the app's icon (a generic paper plane), its name and a Get button. */
function AppStoreScreen() {
  return (
    <>
      <rect x="13" y="26" width="24" height="24" rx="6" className={styles.artAccent} />
      <path d="M18 38.5 31.5 31.5 28 45 24.5 40.5Z M24.5 40.5 31.5 31.5" className={styles.artGlyph} />
      <rect x="42" y="28" width="38" height="5" rx="2.5" className={styles.artStrong} />
      <rect x="42" y="36" width="26" height="4" rx="2" className={styles.artMuted} />
      <rect x="42" y="43" width="22" height="9" rx="4.5" className={styles.artAccent} />
      <rect x="12" y="60" width="76" height="0.8" className={styles.artLine} />
      {[12, 39, 66].map((x) => (
        <rect key={x} x={x} y="67" width="23" height="46" rx="4" className={styles.artSoft} />
      ))}
      <rect x="12" y="122" width="70" height="4" rx="2" className={styles.artMuted} />
      <rect x="12" y="130" width="58" height="4" rx="2" className={styles.artMuted} />
      <rect x="12" y="138" width="64" height="4" rx="2" className={styles.artMuted} />
      <TabBar active={0} />
    </>
  );
}

/** A receive screen kept open: a big "ready" target and this phone's name under it. */
function ReceiveScreen() {
  return (
    <>
      <rect x="13" y="26" width="40" height="6" rx="3" className={styles.artStrong} />
      <circle cx="50" cy="90" r="32" className={styles.artSoft} />
      <circle cx="50" cy="90" r="22" className={styles.artAccentSoft} />
      <circle cx="50" cy="90" r="12" className={styles.artAccent} />
      <rect x="46" y="84" width="8" height="12" rx="2" className={styles.artGlyphFill} />
      <rect x="27" y="134" width="46" height="5" rx="2.5" className={styles.artStrong} />
      <rect x="33" y="144" width="34" height="4" rx="2" className={styles.artMuted} />
      <TabBar active={0} />
    </>
  );
}

/** The Accept sheet over the receive screen, and iOS's "add to Photos" question the first time. */
function AcceptScreen() {
  return (
    <>
      <ReceiveScreen />
      <rect x="5" y="5" width="90" height="190" rx="13" className={styles.artScrim} />
      <rect x="5" y="100" width="90" height="95" rx="13" className={styles.artSheet} />
      <rect x="42" y="105" width="16" height="3" rx="1.5" className={styles.artMuted} />
      <rect x="14" y="114" width="44" height="5" rx="2.5" className={styles.artStrong} />
      <rect x="14" y="123" width="30" height="4" rx="2" className={styles.artMuted} />
      {[14, 34, 54].map((x, i) => (
        <rect key={x} x={x} y="133" width="16" height="16" rx="3" className={PHOTO_TONES[i]} />
      ))}
      <rect x="14" y="160" width="34" height="13" rx="6.5" className={styles.artSoft} />
      <rect x="52" y="160" width="34" height="13" rx="6.5" className={styles.artAccent} />
      <g>
        <rect x="16" y="34" width="68" height="46" rx="9" className={styles.artSheet} />
        <rect x="24" y="42" width="52" height="4" rx="2" className={styles.artStrong} />
        <rect x="28" y="50" width="44" height="3.5" rx="1.75" className={styles.artMuted} />
        <rect x="16" y="60" width="68" height="0.8" className={styles.artLine} />
        <rect x="50" y="60" width="0.8" height="20" className={styles.artLine} />
        <rect x="23" y="67" width="20" height="4" rx="2" className={styles.artMuted} />
        <rect x="57" y="67" width="20" height="4" rx="2" className={styles.artAccent} />
      </g>
    </>
  );
}

const PHOTO_TONES = [styles.artTone1, styles.artTone2, styles.artTone3, styles.artTone4, styles.artTone5];

/** Photos › Recents with the new ones at the top, marked. */
function PhotosScreen() {
  const cells = [];
  for (let row = 0; row < 5; row++) {
    for (let col = 0; col < 3; col++) {
      const i = row * 3 + col;
      const x = 8 + col * 29;
      const y = 38 + row * 27;
      cells.push(<rect key={i} x={x} y={y} width="26" height="25" rx="2" className={PHOTO_TONES[(i * 2 + row) % PHOTO_TONES.length]} />);
      if (row === 0) {
        cells.push(<rect key={`ring-${i}`} x={x - 1} y={y - 1} width="28" height="27" rx="3" className={styles.artRing} />);
        cells.push(<circle key={`dot-${i}`} cx={x + 21} cy={y + 20} r="4" className={styles.artSuccess} />);
      }
    }
  }
  return (
    <>
      <rect x="10" y="25" width="36" height="6" rx="3" className={styles.artStrong} />
      {cells}
      <TabBar active={1} />
    </>
  );
}

/** Quick Save switched on. */
export function SwitchArt({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 48 28" className={cx(styles.switchArt, className)} aria-hidden focusable="false">
      <rect x="1" y="1" width="46" height="26" rx="13" className={styles.artAccent} />
      <circle cx="34" cy="14" r="10" className={styles.artGlyphFill} />
    </svg>
  );
}
