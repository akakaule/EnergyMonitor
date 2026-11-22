import { useState } from 'react';
import { useWidth } from './chart';
import { number } from './format';
import { niceTicks } from './LineChart';

export interface ScatterPoint {
  key: string;
  x: number;
  y: number;
  /** Everything about the point in words, for the tooltip and for screen readers. */
  label: string;
}

interface Props {
  points: ScatterPoint[];
  ariaLabel: string;
  xLabel: string;
  yLabel: string;
  /** A straight line y = intercept + slope × x, drawn across the plot. */
  line?: { intercept: number; slope: number } | null;
  /** The point that stands out, e.g. the latest day. */
  highlightKey?: string | null;
  height?: number;
}

const PAD = { top: 10, right: 12, bottom: 36, left: 40 };

/** Dots for pairs of values, with an optional fitted line. Both axes start at zero or below the lowest value. */
export function ScatterChart({ points, ariaLabel, xLabel, yLabel, line, highlightKey, height = 240 }: Props) {
  const [ref, width] = useWidth();
  const [active, setActive] = useState<number | null>(null);

  const xTicks = niceTicks(Math.min(0, ...points.map((p) => p.x)), Math.max(1, ...points.map((p) => p.x)), 4);
  const yTicks = niceTicks(Math.min(0, ...points.map((p) => p.y)), Math.max(1, ...points.map((p) => p.y)), 4);
  const [x0, x1] = [xTicks[0], xTicks[xTicks.length - 1]];
  const [y0, y1] = [yTicks[0], yTicks[yTicks.length - 1]];
  const plotW = width - PAD.left - PAD.right;
  const plotH = height - PAD.top - PAD.bottom;
  const px = (x: number) => PAD.left + ((x - x0) / (x1 - x0)) * plotW;
  const py = (y: number) => PAD.top + plotH - ((y - y0) / (y1 - y0)) * plotH;
  const decimals = (ticks: number[]) => (ticks.every((t) => Number.isInteger(t)) ? 0 : 1);
  // The line is clipped to the plot, so a steep one never runs over the axis labels.
  const clip = (y: number) => Math.min(y1, Math.max(y0, y));
  const shown = active !== null ? points[active] : null;

  return (
    <div className="chart scatter" ref={ref}>
      <svg width={width} height={height} role="img" aria-label={ariaLabel} onMouseLeave={() => setActive(null)}>
        {yTicks.map((t) => (
          <g key={`y${t}`}>
            <line x1={PAD.left} x2={width - PAD.right} y1={py(t)} y2={py(t)} className={t === 0 ? 'axis' : 'grid'} />
            <text x={PAD.left - 6} y={py(t)} className="tick" textAnchor="end" dominantBaseline="middle">{number(t, decimals(yTicks))}</text>
          </g>
        ))}
        {xTicks.map((t) => (
          <text key={`x${t}`} x={px(t)} y={height - PAD.bottom + 14} className="tick" textAnchor="middle">{number(t, decimals(xTicks))}</text>
        ))}
        <text x={PAD.left + plotW / 2} y={height - 4} className="tick axis-title" textAnchor="middle">{xLabel}</text>
        <text x={PAD.left} y={PAD.top - 2} className="tick axis-title" textAnchor="start">{yLabel}</text>

        {line && (
          <line
            className="scatter-line"
            x1={px(x0)} y1={py(clip(line.intercept + line.slope * x0))}
            x2={px(x1)} y2={py(clip(line.intercept + line.slope * x1))}
          />
        )}

        {points.map((p, i) => (
          <circle
            key={p.key}
            cx={px(p.x)}
            cy={py(p.y)}
            r={p.key === highlightKey || active === i ? 6 : 4}
            className={`scatter-dot${p.key === highlightKey ? ' scatter-dot-highlight' : ''}${active === i ? ' scatter-dot-active' : ''}`}
            tabIndex={0}
            role="img"
            aria-label={p.label}
            onMouseEnter={() => setActive(i)}
            onFocus={() => setActive(i)}
            onBlur={() => setActive(null)}
            onClick={() => setActive(i)}
          />
        ))}
      </svg>

      {shown && (
        <div
          className="tooltip"
          role="status"
          style={{ left: Math.min(Math.max(px(shown.x), 90), width - 90), top: Math.max(0, py(shown.y) - 10) }}
        >
          <span className="tooltip-value">{shown.label}</span>
        </div>
      )}
    </div>
  );
}
