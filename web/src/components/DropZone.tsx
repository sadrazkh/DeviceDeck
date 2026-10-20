import { ArrowDownloadRegular, ProhibitedRegular } from '@fluentui/react-icons';
import { AnimatePresence, motion } from 'framer-motion';
import { useCallback, useRef, useState, type DragEvent, type ReactNode } from 'react';
import { isDraggingOut } from '../shell/dragOut';
import styles from './DropZone.module.css';

export interface DropVerdict {
  ok: boolean;
  /** Names the destination before release, or why this place can't take the files. */
  message: string;
}

// A drag out of the window (to File Explorer) carries files too; it isn't a drop here.
const hasFiles = (e: DragEvent) => !isDraggingOut() && Array.from(e.dataTransfer?.types ?? []).includes('Files');

/**
 * Files dragged from Explorer onto a view land in that view. While dragging, the overlay names
 * the destination; a place that can't take them says why.
 */
export function useFileDrop(options: { verdict(): DropVerdict; onDrop(files: File[]): void; disabled?: boolean }) {
  const [over, setOver] = useState(false);
  const depth = useRef(0);
  const { verdict, onDrop, disabled } = options;

  const onDragEnter = useCallback(
    (e: DragEvent) => {
      if (disabled || !hasFiles(e)) return;
      e.preventDefault();
      depth.current++;
      setOver(true);
    },
    [disabled],
  );
  const onDragOver = useCallback(
    (e: DragEvent) => {
      if (disabled || !hasFiles(e)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = verdict().ok ? 'copy' : 'none';
    },
    [disabled, verdict],
  );
  const onDragLeave = useCallback(() => {
    depth.current = Math.max(0, depth.current - 1);
    if (depth.current === 0) setOver(false);
  }, []);
  const onDropEvent = useCallback(
    (e: DragEvent) => {
      if (disabled || !hasFiles(e)) return;
      e.preventDefault();
      depth.current = 0;
      setOver(false);
      const files = Array.from(e.dataTransfer.files);
      if (files.length > 0 && verdict().ok) onDrop(files);
    },
    [disabled, onDrop, verdict],
  );

  return {
    over,
    bind: { onDragEnter, onDragOver, onDragLeave, onDrop: onDropEvent },
  };
}

export function DropOverlay({ visible, verdict, icon }: { visible: boolean; verdict: DropVerdict; icon?: ReactNode }) {
  return (
    <AnimatePresence>
      {visible ? (
        <motion.div
          className={styles.overlay}
          data-ok={verdict.ok || undefined}
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.12 }}
          aria-live="polite"
        >
          <div className={styles.card}>
            <span className={styles.icon} aria-hidden>
              {verdict.ok ? (icon ?? <ArrowDownloadRegular />) : <ProhibitedRegular />}
            </span>
            <span className={styles.message}>{verdict.message}</span>
          </div>
        </motion.div>
      ) : null}
    </AnimatePresence>
  );
}
