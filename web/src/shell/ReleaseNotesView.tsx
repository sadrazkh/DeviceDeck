// Release notes as React elements, from the safe subset in releaseNotes.ts. No HTML from the notes reaches the page:
// every node is text or one of the elements below, and links go to https://github.com only (in the browser).

import { Fragment, useMemo, type ReactNode } from 'react';
import { notesForLanguage, parseNotes, type Block, type Inline } from './releaseNotes';
import styles from './Update.module.css';

function inline(nodes: Inline[]): ReactNode {
  return nodes.map((node, i) => {
    switch (node.kind) {
      case 'text':
        return <Fragment key={i}>{node.text}</Fragment>;
      case 'strong':
        return <strong key={i}>{inline(node.children)}</strong>;
      case 'em':
        return <em key={i}>{inline(node.children)}</em>;
      case 'code':
        return (
          <code key={i} className={styles.notesCode}>
            {node.text}
          </code>
        );
      case 'link':
        return (
          <a key={i} href={node.href} target="_blank" rel="noreferrer noopener" className={styles.notesLink}>
            {inline(node.children)}
          </a>
        );
    }
  });
}

function block(node: Block, i: number): ReactNode {
  switch (node.kind) {
    case 'heading': {
      // The dialog's own title is the h2.
      const Tag = node.level === 1 ? 'h3' : node.level === 4 ? 'h5' : 'h4';
      return (
        <Tag key={i} className={node.level === 1 ? styles.notesTitle : styles.notesHeading}>
          {inline(node.children)}
        </Tag>
      );
    }
    case 'paragraph':
      return <p key={i}>{inline(node.children)}</p>;
    case 'quote':
      return (
        <blockquote key={i} className={styles.notesQuote}>
          {inline(node.children)}
        </blockquote>
      );
    case 'code':
      return (
        <pre key={i} className={styles.notesPre}>
          <code>{node.text}</code>
        </pre>
      );
    case 'rule':
      return <hr key={i} className={styles.notesRule} />;
    case 'list': {
      const List = node.ordered ? 'ol' : 'ul';
      return (
        <List key={i} className={styles.notesList}>
          {node.items.map((item, j) => (
            <li key={j}>
              {inline(item.children)}
              {item.sublist ? block(item.sublist, 0) : null}
            </li>
          ))}
        </List>
      );
    }
  }
}

/** The notes' part for the UI's language, laid out in its own direction (English notes stay left-to-right in Persian). */
export function ReleaseNotesView({ markdown, lang }: { markdown: string; lang: 'en' | 'fa' }) {
  const part = useMemo(() => notesForLanguage(markdown, lang), [markdown, lang]);
  const blocks = useMemo(() => parseNotes(part.text), [part.text]);
  return (
    <div className={styles.notes} dir={part.lang === 'fa' ? 'rtl' : 'ltr'} lang={part.lang} data-testid="release-notes" data-lang={part.lang}>
      {blocks.map(block)}
    </div>
  );
}

/** Whether the notes have a part in this language (Persian UI, English-only notes: say so). */
export function notesLanguage(markdown: string, lang: 'en' | 'fa'): 'en' | 'fa' {
  return notesForLanguage(markdown, lang).lang;
}
