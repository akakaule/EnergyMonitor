import type { KeyboardEvent, PointerEvent } from 'react';
import { useWidth } from './chart';
import { clock, dateTime, fullDate, monthName, number, weekdayName, zonedParts, zonedToUtc } from './format';
import type { Point } from './sensibo';

const MARGIN = { top: 8, right: 10, bottom: 22, left: 38 };

/** Round axis values covering min..max in about `count` even steps; the axis does not have to start at zero. */
export function niceTicks(min: number, max: number, count = 3): number[] {
  if (min === max) {
    min -= 1;
    max += 1;
  }
  const rawStep = (max - min) / count;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const step = [1, 2, 2.5, 5, 10].map((factor) => factor * magnitude).find((candidate) => candidate >= rawStep) ?? 10 * magnitude;
  const ticks: number[] = [];
  for (let value = Math.floor(min / step) * step; value <= Math.ceil(max / step) * step + step / 2; value += step) {
    ticks.push(Number(value.toFixed(6)));
  }
  return ticks;
}

/**
 * Danish-time axis ticks: every 6 hours for a day, midnights for a week, every 7th midnight for a month,
 * the first of each month beyond that.
 */
export function timeTicks(start: number, end: number, days: number): number[] {
  const s = zonedParts(start);
  const ticks: number[] = [];
  const collect = (at: (k: number) => number) => {
    for (let k = 0; ; k++) {
      const tick = at(k);
      if (tick > end) return ticks;
      if (tick >= start) ticks.push(tick);
    }
  };
  if (days <= 1) {
    const first = Math.ceil(s.hour / 6) * 6;
    return collect((k) => zonedToUtc(s.year, s.month, s.day, first + 6 * k));
  }
  if (days <= 31) {
    const step = days <= 7 ? 1 : 7;
    const offset = zonedToUtc(s.year, s.month, s.day) < start ? 1 : 0;
    return collect((k) => zonedToUtc(s.year, s.month, s.day + offset + step * k));
  }
  return collect((k) => zonedToUtc(s.year, s.month + k, 1));
}

/** The label of an axis tick. */
export function axisLabel(ms: number, days: number): string {
  if (days <= 1) return clock(ms);
  const p = zonedParts(ms);
  if (days <= 7) return weekdayName(p.weekday);
  if (days <= 31) return `${p.day}/${p.month}`;
  return monthName(p.month);
}

/** When a single reading was taken; ranges beyond a month can span years, so they show the year. */
export function pointTime(ms: number, days: number): string {
  if (days <= 1) return `kl. ${clock(ms)}`;
  if (days <= 31) return dateTime(new Date(ms).toISOString());
  return fullDate(ms);
}

/**
 * Splits the readings where they stop for a while (device offline, server down), so the line never
 * invents values across missing data. A gap is more than three times the usual spacing.
 */
export function segments(points: Point[]): Point[][] {
  const intervals = points
    .slice(1)
    .map((point, i) => point.time - points[i].time)
    .sort((a, b) => a - b);
  const maxGap = 3 * (intervals[Math.floor(intervals.length / 2)] ?? 0);
  const result: Point[][] = [];
  points.forEach((point, i) => {
    if (i === 0 || point.time - points[i - 1].time > maxGap) result.push([]);
    result[result.length - 1].push(point);
  });
  return result;
}

/** The index of the reading closest to `time` in readings sorted by time. */
export function nearestIndex(points: Point[], time: number): number {
  let low = 0;
  let high = points.length - 1;
  while (low < high) {
    const mid = (low + high) >> 1;
    if (points[mid].time < time) low = mid + 1;
    else high = mid;
  }
  return low > 0 && time - points[low - 1].time < points[low].time - time ? low - 1 : low;
}

interface Props {
  points: Point[];
  days: number;
  title: string;
  series: 'temperature' | 'humidity';
  format: (value: number) => string;
  /** Shared between the charts of one history, so their crosshairs move together. */
  hoverTime: number | null;
  onHover: (time: number | null) => void;
  height?: number;
}

const KEY_STEP: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, Home: -Infinity, End: Infinity };

export function LineChart({ points, days, title, series, format, hoverTime, onHover, height = 150 }: Props) {
  const [ref, width] = useWidth<HTMLElement>(320, 240);
  const latest = points.length > 0 ? points[points.length - 1] : null;

  const caption = (
    <figcaption className="line-chart-title">
      <span>{title}</span>
      <strong>{latest ? format(latest.value) : '–'}</strong>
    </figcaption>
  );
  if (points.length < 2 || !latest) {
    return (
      <figure className="line-chart" ref={ref}>
        {caption}
      </figure>
    );
  }

  const start = points[0].time;
  const end = latest.time;
  const values = points.map((point) => point.value);
  const low = Math.min(...values);
  const high = Math.max(...values);
  const yTicks = niceTicks(low, high, 3);
  const yMin = yTicks[0];
  const yMax = yTicks[yTicks.length - 1];
  const step = yTicks.length > 1 ? yTicks[1] - yTicks[0] : 1;
  const plotRight = width - MARGIN.right;
  const plotBottom = height - MARGIN.bottom;
  const x = (time: number) => MARGIN.left + ((time - start) / (end - start || 1)) * (plotRight - MARGIN.left);
  const y = (value: number) => plotBottom - ((value - yMin) / (yMax - yMin || 1)) * (plotBottom - MARGIN.top);
  const timeAt = (px: number) => start + ((px - MARGIN.left) / (plotRight - MARGIN.left)) * (end - start);

  const active = hoverTime === null ? null : nearestIndex(points, hoverTime);
  const point = active === null ? null : points[active];

  const pointerTime = (event: PointerEvent<SVGSVGElement>) => {
    const bounds = event.currentTarget.getBoundingClientRect();
    const scale = bounds.width > 0 ? width / bounds.width : 1;
    return timeAt((event.clientX - bounds.left) * scale);
  };
  const onKeyDown = (event: KeyboardEvent<SVGSVGElement>) => {
    const delta = KEY_STEP[event.key];
    if (delta === undefined) return;
    event.preventDefault();
    const index = Math.min(points.length - 1, Math.max(0, (active ?? points.length - 1) + delta));
    onHover(points[index].time);
  };

  return (
    <figure className={`line-chart series-${series}`} ref={ref}>
      {caption}
      <svg
        width={width}
        height={height}
        role="img"
        tabIndex={0}
        aria-label={`${title}: fra ${format(points[0].value)} til ${format(latest.value)}, laveste ${format(low)}, højeste ${format(high)}. Brug piletasterne for at læse målingerne.`}
        onPointerMove={(event) => onHover(pointerTime(event))}
        onPointerDown={(event) => onHover(pointerTime(event))}
        onPointerLeave={() => onHover(null)}
        onFocus={() => onHover(latest.time)}
        onBlur={() => onHover(null)}
        onKeyDown={onKeyDown}
      >
        {yTicks.map((tick) => (
          <g key={tick}>
            <line className="grid" x1={MARGIN.left} x2={plotRight} y1={y(tick)} y2={y(tick)} />
            <text className="tick" x={MARGIN.left - 6} y={y(tick)} textAnchor="end" dominantBaseline="middle">
              {number(tick, step < 1 ? 1 : 0)}
            </text>
          </g>
        ))}
        <line className="axis" x1={MARGIN.left} x2={plotRight} y1={plotBottom} y2={plotBottom} />
        {timeTicks(start, end, days)
          .filter((tick) => x(tick) >= MARGIN.left + 12 && x(tick) <= plotRight - 12)
          .map((tick) => (
            <text key={tick} className="tick" x={x(tick)} y={height - 6} textAnchor="middle">
              {axisLabel(tick, days)}
            </text>
          ))}

        {segments(points).map((segment) => {
          const first = segment[0];
          const last = segment[segment.length - 1];
          if (segment.length === 1) {
            return <circle key={first.time} className="series-point" cx={x(first.time)} cy={y(first.value)} r={2} />;
          }
          const line = segment.map((p, i) => `${i ? 'L' : 'M'}${x(p.time).toFixed(1)},${y(p.value).toFixed(1)}`).join('');
          const area = `${line}L${x(last.time).toFixed(1)},${plotBottom}L${x(first.time).toFixed(1)},${plotBottom}Z`;
          return (
            <g key={first.time}>
              <path className="series-area" d={area} />
              <path className="series-line" d={line} />
            </g>
          );
        })}
        <circle className="series-dot" cx={x(end)} cy={y(latest.value)} r={4} />

        {point && (
          <g>
            <line className="crosshair" x1={x(point.time)} x2={x(point.time)} y1={MARGIN.top} y2={plotBottom} />
            <circle className="series-dot" cx={x(point.time)} cy={y(point.value)} r={4} />
          </g>
        )}
      </svg>
      {point && (
        <div
          className={x(point.time) > width / 2 ? 'line-tooltip line-tooltip-left' : 'line-tooltip'}
          role="status"
          style={{ left: x(point.time) }}
        >
          <strong>{format(point.value)}</strong>
          <span>{pointTime(point.time, days)}</span>
        </div>
      )}
    </figure>
  );
}
