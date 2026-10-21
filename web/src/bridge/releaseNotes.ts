// Updates you can see (round 4): app.releaseNotes (docs/bridge-contract.md, "Updates you can see").

export interface ReleaseNotes {
  version: string;
  /** The version's GitHub release page. */
  url: string;
  /** The release's name on GitHub ("DeviceDeck v0.4.0"), when known. */
  title: string | null;
  /** As written on GitHub: English, then a Persian <details> block. null when they couldn't be had: show a link to url. */
  markdown: string | null;
  published: string | null;
  /** github: fetched (or unchanged since the cached copy); cache: GitHub unreachable, last time's copy; app: came with the app or the update. */
  source: 'github' | 'cache' | 'app' | 'none';
}
