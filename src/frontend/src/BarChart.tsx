import { useState } from 'react';
import { useWidth } from './chart';
import { kWh, number } from './format';

export interface Bar {
  key: string;
  /** Short label under the axis, e.g. "6/11" or "07". */
  label: string;
  /** Full description for the tooltip and screen readers, e.g. "fre. 6. nov.". */
  title: string;
  /** null means no usable measurement. It is drawn as a gap, never as a zero-height bar. */
  value: number | null;
  state: 'complete' | 'partial' | 'missing';
  /** A bar that stands apart from the rest: part of the cheapest or dearest period, or already over. */
  tone?: 'low' | 'high' | 'past';
  /** Said after the value, e.g. "billigste periode", so the tone is never carried by colour alone. */
  note?: string;
}

interface Props {
  bars: Bar[];
  ariaLabel: string;
  height?: number;
  /** A horizontal reference, e.g. the median. */
  reference?: { value: number; label: string } | null;
  selectedKey?: string | null;
  onSelect?: (key: string) => void;
  /** Sparkline mode: no axes, labels or tooltip. */
  compact?: boolean;
  /** How a value is written in the tooltip and for screen readers. kWh with two decimals unless given. */
  format?: (value: number) => string;
}

/** A round axis maximum at or above the data, with 2–4 even steps. */
export function niceScale(max: number): { top: number; step: number } {
  if (!(max > 0)) return { top: 1, step: 0.5 };
  const magnitude = 10 ** Math.floor(Math.log10(max));
  const step = [0.2, 0.25, 0.5, 1, 2, 2.5, 5].map((m) => m * magnitude).find((s) => max / s <= 4) ?? magnitude * 5;
  return { top: Math.ceil(max / step) * step, step };
}

/**
 * The axis for values that may go below zero: the same steps as `niceScale`, extended downwards. With no
 * negative value it is exactly `niceScale`, starting at 0.
 */
export function niceRange(min: number, max: number): { bottom: number; top: number; step: number } {
  if (!(min < 0)) return { bottom: 0, ...niceScale(max) };
  const { step } = niceScale(Math.max(max, 0) - min);
  // Whole numbers of steps, without the stray digits of binary fractions (3 × 0,2 is not 0,6).
  const steps = (count: number) => Number((count * step).toFixed(10));
  return { bottom: steps(Math.floor(min / step)), top: steps(Math.max(1, Math.ceil(Math.max(max, 0) / step))), step };
}

const defaultFormat = (value: number) => kWh(value, 2);

function describe(bar: Bar, format: (value: number) => string): string {
  if (bar.value === null || bar.state === 'missing') return `${bar.title}: data mangler`;
  return `${bar.title}: ${format(bar.value)}${bar.state === 'partial' ? ' (ufuldstændigt døgn)' : ''}${bar.note ? ` (${bar.note})` : ''}`;
}

export function BarChart({ bars, ariaLabel, height = 200, reference, selectedKey, onSelect, compact, format = defaultFormat }: Props) {
  const [ref, width] = useWidth();
  const [hover, setHover] = useState<number | null>(null);

  const pad = compact ? { top: 2, right: 0, bottom: 2, left: 0 } : { top: 12, right: 8, bottom: 22, left: 34 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const values = bars.map((b) => b.value ?? 0);
  const { bottom, top, step } = niceRange(Math.min(0, ...values), Math.max(...values, reference?.value ?? 0));
  const y = (value: number) => pad.top + plotH - ((value - bottom) / (top - bottom)) * plotH;
  // Bars grow from the zero line, which is above the bottom of the plot when a value is negative.
  const base = y(0);
  const band = bars.length > 0 ? plotW / bars.length : plotW;
  // Bars are capped at 24px and always leave a 2px gap, so neighbours never touch.
  const barW = Math.max(1, Math.min(24, band - 2));
  const labelEvery = Math.max(1, Math.ceil(bars.length / Math.max(1, Math.floor(plotW / 34))));
  const ticks: number[] = [];
  for (let i = Math.round(bottom / step); i * step <= top + step / 2; i++) ticks.push(Number((i * step).toFixed(10)));
  // Steps such as 0,25 need two decimals; one would show 0,3 and 0,8.
  const tickDecimals = step >= 1 ? 0 : Math.abs(step * 10 - Math.round(step * 10)) < 1e-9 ? 1 : 2;
  const active = hover !== null ? bars[hover] : null;

  return (
    <div className={compact ? 'chart chart-compact' : 'chart'} ref={ref}>
      <svg width={width} height={height} role="img" aria-label={ariaLabel} onMouseLeave={() => setHover(null)}>
        {!compact &&
          ticks.map((t) => (
            <g key={t}>
              <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className={Math.abs(t) < step / 1000 ? 'axis' : 'grid'} />
              <text x={pad.left - 6} y={y(t)} className="tick" textAnchor="end" dominantBaseline="middle">
                {number(t, tickDecimals)}
              </text>
            </g>
          ))}

        {bars.map((bar, i) => {
          const x = pad.left + i * band + (band - barW) / 2;
          const missing = bar.value === null || bar.state === 'missing';
          const negative = !missing && bar.value! < 0;
          // A value other than zero is always at least a pixel tall.
          const h = missing ? 0 : Math.max(bar.value! !== 0 ? 1 : 0, Math.abs(base - y(bar.value!)));
          const r = Math.min(4, barW / 2, h);
          // The rounded end points away from the zero line: up for a positive value, down for a negative one.
          const end = negative ? base + h : base - h;
          const turn = negative ? end - r : end + r;
          return (
            <g key={bar.key}>
              {missing ? (
                // A short muted mark on the baseline says "no data here" without suggesting a value.
                <rect x={x} y={base - 2} width={barW} height={2} className="bar-missing" />
              ) : (
                <path
                  d={`M${x},${base} V${turn} Q${x},${end} ${x + r},${end} H${x + barW - r} Q${x + barW},${end} ${x + barW},${turn} V${base} Z`}
                  className={`bar bar-${bar.state}${bar.tone ? ` bar-tone-${bar.tone}` : ''}${selectedKey === bar.key ? ' bar-selected' : ''}${hover === i ? ' bar-hover' : ''}`}
                />
              )}
              {!compact && i % labelEvery === 0 && (
                <text x={pad.left + i * band + band / 2} y={height - 6} className="tick" textAnchor="middle">
                  {bar.label}
                </text>
              )}
              {!compact && (
                // The hit target is the whole column, far larger than a thin or missing bar.
                <rect
                  x={pad.left + i * band}
                  y={pad.top}
                  width={band}
                  height={plotH}
                  className="hit"
                  role={onSelect ? 'button' : undefined}
                  tabIndex={onSelect ? 0 : undefined}
                  aria-label={describe(bar, format)}
                  aria-pressed={onSelect ? selectedKey === bar.key : undefined}
                  onMouseEnter={() => setHover(i)}
                  onFocus={() => setHover(i)}
                  onBlur={() => setHover(null)}
                  onClick={() => {
                    setHover(i);
                    onSelect?.(bar.key);
                  }}
                  onKeyDown={(e) => {
                    if (onSelect && (e.key === 'Enter' || e.key === ' ')) {
                      e.preventDefault();
                      onSelect(bar.key);
                    }
                  }}
                />
              )}
            </g>
          );
        })}

        {!compact && reference && (
          <g>
            <line x1={pad.left} x2={width - pad.right} y1={y(reference.value)} y2={y(reference.value)} className="reference" />
            <text x={width - pad.right} y={y(reference.value) - 4} className="reference-label" textAnchor="end">
              {reference.label}
            </text>
          </g>
        )}
      </svg>

      {!compact && active && hover !== null && (
        <div
          className="tooltip"
          role="status"
          style={{
            left: Math.min(Math.max(pad.left + hover * band + band / 2, 70), width - 70),
            top: Math.max(0, (active.value === null ? base : Math.min(base, y(active.value))) - 8),
          }}
        >
          <span className="tooltip-title">{active.title}</span>
          <span className="tooltip-value">
            {active.value === null || active.state === 'missing' ? 'Data mangler' : format(active.value)}
          </span>
          {active.note && <span className="tooltip-note">{active.note}</span>}
          {active.state === 'partial' && <span className="tooltip-note">Ufuldstændigt døgn</span>}
        </div>
      )}
    </div>
  );
}
