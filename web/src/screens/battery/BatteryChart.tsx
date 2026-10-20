import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useI18n } from '../../i18n';
import styles from './Battery.module.css';

export interface ChartPoint {
  at: string;
  value: number;
  /** A second line in the tooltip (the system version). */
  note?: string;
}

/** Clean ticks: steps of 1, 2, 5 × 10ⁿ (at least 1, every measure is whole), about four lines. Same as the exported report. */
export function niceRange(min: number, max: number, cap?: number): { lo: number; hi: number; step: number } {
  if (max - min < 1) {
    min -= 1;
    max += 1;
  }
  const raw = (max - min) / 4;
  const magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = Math.max(1, [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= raw)!);
  let lo = Math.floor(min / step) * step;
  let hi = Math.ceil(max / step) * step;
  if (cap !== undefined && hi > cap) {
    hi = cap;
    lo = Math.min(lo, hi - step);
  }
  return { lo, hi, step };
}

const HEIGHT = 240;
const TOP = 16;
const BOTTOM = 30;
const START = 64;
const END = 88;

/**
 * One measure over time: a 2 px line with dots, hairline grid, the latest value at the line's end, and a crosshair
 * that snaps to the nearest reading (pointer or arrow keys). Time runs with the reading direction: right to left in Persian.
 */
export function BatteryChart({
  points,
  format,
  tickFormat = format,
  label,
  reference,
  floorAtZero,
  cap,
}: {
  points: ChartPoint[];
  format(value: number): string;
  /** Axis labels, when they should be shorter than the value (no unit). */
  tickFormat?(value: number): string;
  label: string;
  reference?: { value: number; label: string } | null;
  floorAtZero?: boolean;
  cap?: number;
}) {
  const { t, date, dir } = useI18n();
  const rtl = dir === 'rtl';
  const box = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(720);
  const [active, setActive] = useState<number | null>(null);
  const liveId = useId();

  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = () => setWidth(Math.max(320, Math.round(el.getBoundingClientRect().width)));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const geometry = useMemo(() => {
    if (points.length === 0) return null;
    const values = points.map((p) => p.value);
    if (reference) values.push(reference.value);
    const range = niceRange(floorAtZero ? 0 : Math.min(...values), Math.max(...values), cap);
    const t0 = Date.parse(points[0]!.at);
    const t1 = Date.parse(points[points.length - 1]!.at);
    const plot = width - START - END;
    const ltrX = (at: string) => (t1 === t0 ? START + plot / 2 : START + ((Date.parse(at) - t0) / (t1 - t0)) * plot);
    const x = (at: string) => (rtl ? width - ltrX(at) : ltrX(at));
    const y = (v: number) => TOP + (1 - (v - range.lo) / (range.hi - range.lo)) * (HEIGHT - TOP - BOTTOM);
    const ticks: number[] = [];
    for (let v = range.lo; v <= range.hi + range.step / 2; v += range.step) ticks.push(v);
    // Dates under the axis: the ends, and a few between when there is room.
    const slots = Math.max(1, Math.min(5, Math.floor(plot / 140)));
    const dates = t1 === t0 ? [points[0]!.at] : Array.from({ length: slots + 1 }, (_, i) => new Date(t0 + ((t1 - t0) * i) / slots).toISOString());
    return { x, y, ticks, dates, xs: points.map((p) => x(p.at)) };
  }, [points, reference, floorAtZero, cap, width, rtl]);

  if (!geometry) return <p className={styles.muted}>{t('battery.chart.noData')}</p>;
  const { x, y, ticks, dates, xs } = geometry;
  const last = points[points.length - 1]!;
  const axisStart = rtl ? width - START : START;
  const axisEnd = rtl ? END : width - END;
  // text-anchor is logical: in a right-to-left page "start" is the text's right edge. The x positions are mirrored
  // by hand, so the same logical anchors put every label on the correct side in both directions.

  const nearest = (clientX: number) => {
    const rect = box.current!.getBoundingClientRect();
    const px = ((clientX - rect.left) / rect.width) * width;
    let best = 0;
    xs.forEach((value, i) => {
      if (Math.abs(value - px) < Math.abs(xs[best]! - px)) best = i;
    });
    return best;
  };

  const onKey = (e: KeyboardEvent) => {
    const later = rtl ? 'ArrowLeft' : 'ArrowRight';
    const earlier = rtl ? 'ArrowRight' : 'ArrowLeft';
    if (e.key === later || e.key === earlier) {
      e.preventDefault();
      const step = e.key === later ? 1 : -1;
      setActive((i) => Math.max(0, Math.min(points.length - 1, (i ?? (step > 0 ? -1 : points.length)) + step)));
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      setActive(e.key === 'Home' ? 0 : points.length - 1);
    } else if (e.key === 'Escape') {
      setActive(null);
    }
  };

  const point = active !== null ? points[active] : null;
  const tipX = active !== null ? xs[active]! : 0;
  // The chart's x is physical (it is mirrored by hand), so the tooltip is placed with a physical left too.
  const tipLeft = `${(tipX / width) * 100}%`;
  const flip = tipX > width * 0.66;
  const pathD = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.at).toFixed(1)},${y(p.value).toFixed(1)}`).join(' ');

  return (
    <div
      ref={box}
      className={styles.chart}
      tabIndex={0}
      role="group"
      aria-label={label}
      aria-describedby={liveId}
      onKeyDown={onKey}
      onBlur={() => setActive(null)}
      onPointerMove={(e: PointerEvent) => setActive(nearest(e.clientX))}
      onPointerLeave={() => setActive(null)}
      data-testid="battery-chart"
    >
      <svg viewBox={`0 0 ${width} ${HEIGHT}`} width="100%" height={HEIGHT} aria-hidden>
        {ticks.map((v) => (
          <g key={v}>
            <line className={styles.grid} x1={Math.min(axisStart, axisEnd)} x2={Math.max(axisStart, axisEnd)} y1={y(v)} y2={y(v)} />
            <text className={styles.tick} x={rtl ? axisStart + 8 : axisStart - 8} y={y(v) + 4} textAnchor="end">
              {tickFormat(v)}
            </text>
          </g>
        ))}
        {dates.map((at, i) => (
          <text
            key={at}
            className={styles.tick}
            x={x(at)}
            y={HEIGHT - 8}
            textAnchor={dates.length === 1 ? 'middle' : i === 0 ? 'start' : i === dates.length - 1 ? 'end' : 'middle'}
          >
            {date(at, 'short')}
          </text>
        ))}
        {reference ? (
          <g>
            <line className={styles.reference} x1={Math.min(axisStart, axisEnd)} x2={Math.max(axisStart, axisEnd)} y1={y(reference.value)} y2={y(reference.value)} />
            <text className={styles.tick} x={rtl ? axisEnd - 8 : axisEnd + 8} y={y(reference.value) + 4} textAnchor="start">
              {reference.label}
            </text>
          </g>
        ) : null}
        {points.length > 1 ? <path className={styles.line} d={pathD} /> : null}
        {active !== null ? <line className={styles.crosshair} x1={tipX} x2={tipX} y1={TOP} y2={HEIGHT - BOTTOM} /> : null}
        {points.map((p, i) => (
          <circle key={`${p.at}-${i}`} className={styles.dot} cx={x(p.at)} cy={y(p.value)} r={i === active ? 6 : 4} />
        ))}
        <text
          className={styles.endLabel}
          x={rtl ? x(last.at) - 10 : x(last.at) + 10}
          y={reference && Math.abs(y(reference.value) - y(last.value)) < 16 ? y(last.value) - 10 : y(last.value) + 4}
          textAnchor="start"
        >
          {format(last.value)}
        </text>
      </svg>
      {point ? (
        <div className={styles.tooltip} style={{ left: tipLeft, transform: flip ? 'translateX(calc(-100% - 12px))' : 'translateX(12px)' }} role="presentation">
          <strong className={styles.tooltipValue}>{format(point.value)}</strong>
          <span>{date(point.at)}</span>
          {point.note ? <span className={styles.muted}>{point.note}</span> : null}
        </div>
      ) : null}
      <span id={liveId} className="visually-hidden" aria-live="polite">
        {point ? `${format(point.value)}, ${date(point.at)}` : ''}
      </span>
    </div>
  );
}
