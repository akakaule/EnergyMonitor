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
}

/** A round axis maximum at or above the data, with 2–4 even steps. */
export function niceScale(max: number): { top: number; step: number } {
  if (!(max > 0)) return { top: 1, step: 0.5 };
  const magnitude = 10 ** Math.floor(Math.log10(max));
  const step = [0.2, 0.25, 0.5, 1, 2, 2.5, 5].map((m) => m * magnitude).find((s) => max / s <= 4) ?? magnitude * 5;
  return { top: Math.ceil(max / step) * step, step };
}

function describe(bar: Bar): string {
  if (bar.value === null || bar.state === 'missing') return `${bar.title}: data mangler`;
  return `${bar.title}: ${kWh(bar.value, 2)}${bar.state === 'partial' ? ' (ufuldstændigt døgn)' : ''}`;
}

export function BarChart({ bars, ariaLabel, height = 200, reference, selectedKey, onSelect, compact }: Props) {
  const [ref, width] = useWidth();
  const [hover, setHover] = useState<number | null>(null);

  const pad = compact ? { top: 2, right: 0, bottom: 2, left: 0 } : { top: 12, right: 8, bottom: 22, left: 34 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const values = bars.map((b) => b.value ?? 0);
  const { top, step } = niceScale(Math.max(...values, reference?.value ?? 0));
  const y = (value: number) => pad.top + plotH - (value / top) * plotH;
  const band = bars.length > 0 ? plotW / bars.length : plotW;
  // Bars are capped at 24px and always leave a 2px gap, so neighbours never touch.
  const barW = Math.max(1, Math.min(24, band - 2));
  const labelEvery = Math.max(1, Math.ceil(bars.length / Math.max(1, Math.floor(plotW / 34))));
  const ticks: number[] = [];
  for (let t = 0; t <= top + step / 2; t += step) ticks.push(t);
  const active = hover !== null ? bars[hover] : null;

  return (
    <div className={compact ? 'chart chart-compact' : 'chart'} ref={ref}>
      <svg width={width} height={height} role="img" aria-label={ariaLabel} onMouseLeave={() => setHover(null)}>
        {!compact &&
          ticks.map((t) => (
            <g key={t}>
              <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className={t === 0 ? 'axis' : 'grid'} />
              <text x={pad.left - 6} y={y(t)} className="tick" textAnchor="end" dominantBaseline="middle">
                {number(t, step < 1 ? 1 : 0)}
              </text>
            </g>
          ))}

        {bars.map((bar, i) => {
          const x = pad.left + i * band + (band - barW) / 2;
          const missing = bar.value === null || bar.state === 'missing';
          const h = missing ? 0 : Math.max(bar.value! > 0 ? 1 : 0, pad.top + plotH - y(bar.value!));
          const r = Math.min(4, barW / 2, h);
          const base = pad.top + plotH;
          return (
            <g key={bar.key}>
              {missing ? (
                // A short muted mark on the baseline says "no data here" without suggesting a value.
                <rect x={x} y={base - 2} width={barW} height={2} className="bar-missing" />
              ) : (
                <path
                  d={`M${x},${base} V${base - h + r} Q${x},${base - h} ${x + r},${base - h} H${x + barW - r} Q${x + barW},${base - h} ${x + barW},${base - h + r} V${base} Z`}
                  className={`bar bar-${bar.state}${selectedKey === bar.key ? ' bar-selected' : ''}${hover === i ? ' bar-hover' : ''}`}
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
                  aria-label={describe(bar)}
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
            top: Math.max(0, (active.value === null ? pad.top + plotH : y(active.value)) - 8),
          }}
        >
          <span className="tooltip-title">{active.title}</span>
          <span className="tooltip-value">
            {active.value === null || active.state === 'missing' ? 'Data mangler' : kWh(active.value, 2)}
          </span>
          {active.state === 'partial' && <span className="tooltip-note">Ufuldstændigt døgn</span>}
        </div>
      )}
    </div>
  );
}
