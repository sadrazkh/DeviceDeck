// The live log's lines in the page: a capped buffer plus an incrementally kept filtered view, so 10 batches a
// second over 50,000 lines cost only the new lines unless the filter changes.

import { LOG_LEVELS, type LogLevel, type LogLine } from '../../bridge/toolbox';

export const LOG_CAP = 50_000;

export interface LogFilter {
  minLevel: LogLevel;
  search: string;
}

export const levelRank = (level: LogLevel) => LOG_LEVELS.indexOf(level);

export function matches(line: LogLine, filter: LogFilter): boolean {
  if (levelRank(line.level) < levelRank(filter.minLevel)) return false;
  const needle = filter.search.trim().toLowerCase();
  if (!needle) return true;
  return (
    line.message.toLowerCase().includes(needle) ||
    line.process.toLowerCase().includes(needle) ||
    (line.tag?.toLowerCase().includes(needle) ?? false)
  );
}

export class LogBuffer {
  private all: LogLine[] = [];
  private shown: LogLine[] = [];
  private filter: LogFilter = { minLevel: 'verbose', search: '' };

  constructor(private readonly cap = LOG_CAP) {}

  get total(): number {
    return this.all.length;
  }

  /** The lines that pass the filter, oldest first. Replaced (not mutated) on every change, so it works as React state. */
  get view(): readonly LogLine[] {
    return this.shown;
  }

  push(lines: readonly LogLine[]): void {
    if (lines.length === 0) return;
    this.all = this.all.concat(lines);
    let shown = this.shown.concat(lines.filter((l) => matches(l, this.filter)));
    if (this.all.length > this.cap) {
      const oldest = this.all[this.all.length - this.cap]!.seq;
      this.all = this.all.slice(this.all.length - this.cap);
      const cut = shown.findIndex((l) => l.seq >= oldest);
      shown = cut < 0 ? [] : shown.slice(cut);
    }
    this.shown = shown;
  }

  setFilter(filter: LogFilter): void {
    if (filter.minLevel === this.filter.minLevel && filter.search === this.filter.search) return;
    this.filter = { ...filter };
    this.shown = this.all.filter((l) => matches(l, this.filter));
  }

  clear(): void {
    this.all = [];
    this.shown = [];
  }
}

/** "16:44:01.123" in the PC's time zone. */
export function logTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

export const LEVEL_LETTER: Record<LogLevel, string> = {
  verbose: 'V',
  debug: 'D',
  info: 'I',
  notice: 'N',
  warning: 'W',
  error: 'E',
  fault: 'F',
};
