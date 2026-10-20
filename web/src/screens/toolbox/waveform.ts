// Pure helpers for the ringtone maker: waveform peaks and keeping a selection sane.

/** The loudest sample (0..1) in each of `buckets` equal slices of `samples`. */
export function computePeaks(samples: Float32Array, buckets: number): number[] {
  const peaks = new Array<number>(Math.max(0, buckets)).fill(0);
  if (buckets <= 0 || samples.length === 0) return peaks;
  const per = samples.length / buckets;
  for (let b = 0; b < buckets; b++) {
    const from = Math.floor(b * per);
    const to = Math.min(samples.length, Math.max(from + 1, Math.floor((b + 1) * per)));
    let max = 0;
    // Look at up to ~2,000 samples per slice: plenty for a picture, quick for a five-minute song.
    const step = Math.max(1, Math.floor((to - from) / 2000));
    for (let i = from; i < to; i += step) {
      const v = Math.abs(samples[i]!);
      if (v > max) max = v;
    }
    peaks[b] = Math.min(1, max);
  }
  return peaks;
}

export const MIN_LENGTH = 1;
export const MAX_LENGTH = 300;
export const SUGGESTED_LENGTH = 30;

/** Rounds to tenths and keeps 0 ≤ start < end ≤ duration, at least a second and at most five minutes long. */
export function clampSelection(start: number, end: number, duration: number): { start: number; end: number } {
  const round = (v: number) => Math.round(v * 10) / 10;
  const total = Math.max(0, duration);
  let s = Math.max(0, Math.min(start, total));
  let e = Math.max(0, Math.min(end, total));
  if (e < s) [s, e] = [e, s];
  if (e - s < MIN_LENGTH) {
    e = Math.min(total, s + MIN_LENGTH);
    s = Math.max(0, e - MIN_LENGTH);
  }
  if (e - s > MAX_LENGTH) e = s + MAX_LENGTH;
  return { start: round(s), end: round(Math.min(e, total)) };
}

/** The first selection offered: from the start, up to 30 seconds. */
export function initialSelection(duration: number): { start: number; end: number } {
  return clampSelection(0, Math.min(duration, SUGGESTED_LENGTH), duration);
}

/** "1:05.3" */
export function formatSeconds(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = seconds - m * 60;
  return `${m}:${s.toFixed(1).padStart(4, '0')}`;
}
