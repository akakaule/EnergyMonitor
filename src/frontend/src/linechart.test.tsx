// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { BarChart } from './BarChart';
import { celsius } from './format';
import { axisLabel, LineChart, nearestIndex, niceTicks, pointTime, segments, timeTicks } from './LineChart';
import type { Point } from './sensibo';

afterEach(cleanup);

// Danish time: CEST is UTC+2 until 25 October 2026, then CET is UTC+1.
const dk = (iso: string) => Date.parse(iso);

describe('timeTicks', () => {
  it('marks every 6 hours of Danish time for a day', () => {
    expect(timeTicks(dk('2026-10-22T01:30:00+02:00'), dk('2026-10-22T19:00:00+02:00'), 1)).toEqual([
      dk('2026-10-22T06:00:00+02:00'),
      dk('2026-10-22T12:00:00+02:00'),
      dk('2026-10-22T18:00:00+02:00'),
    ]);
  });

  it('marks each Danish midnight for a week, also across the end of summer time', () => {
    const ticks = timeTicks(dk('2026-10-20T20:00:00+02:00'), dk('2026-10-27T20:00:00+01:00'), 7);
    expect(ticks).toHaveLength(7);
    expect(ticks[0]).toBe(dk('2026-10-21T00:00:00+02:00'));
    expect(ticks).toContain(dk('2026-10-25T00:00:00+02:00'));
    expect(ticks).toContain(dk('2026-10-26T00:00:00+01:00'));
    expect(ticks.map((t) => axisLabel(t, 7))).toEqual(['ons.', 'tors.', 'fre.', 'lør.', 'søn.', 'man.', 'tirs.']);
  });

  it('marks every 7th midnight for a month', () => {
    expect(timeTicks(dk('2026-09-22T20:00:00+02:00'), dk('2026-10-22T20:00:00+02:00'), 30)).toEqual([
      dk('2026-09-23T00:00:00+02:00'),
      dk('2026-09-30T00:00:00+02:00'),
      dk('2026-10-07T00:00:00+02:00'),
      dk('2026-10-14T00:00:00+02:00'),
      dk('2026-10-21T00:00:00+02:00'),
    ]);
  });

  it('marks the first of each month for a year', () => {
    const ticks = timeTicks(dk('2026-01-15T00:00:00+01:00'), dk('2026-06-20T00:00:00+02:00'), 365);
    expect(ticks).toEqual([
      dk('2026-02-01T00:00:00+01:00'),
      dk('2026-03-01T00:00:00+01:00'),
      dk('2026-04-01T00:00:00+02:00'),
      dk('2026-05-01T00:00:00+02:00'),
      dk('2026-06-01T00:00:00+02:00'),
    ]);
    expect(ticks.map((t) => axisLabel(t, 365))).toEqual(['feb.', 'mar.', 'apr.', 'maj', 'jun.']);
  });

  it('shows the year in a reading’s time once a range can span years', () => {
    const at = dk('2026-11-08T08:05:00+01:00');
    expect(pointTime(at, 1)).toBe('kl. 08:05');
    expect(pointTime(at, 30)).toBe('8/11 kl. 08:05');
    expect(pointTime(at, 365)).toBe('8. nov. 2026');
  });
});

describe('scales and gaps', () => {
  it('fits the value axis to the data instead of starting at zero', () => {
    expect(niceTicks(19.2, 22.8)).toEqual([18, 20, 22, 24]);
    expect(niceTicks(20.4, 21.6)).toEqual([20, 20.5, 21, 21.5, 22]);
    expect(niceTicks(41, 55)).toEqual([40, 45, 50, 55]);
    expect(niceTicks(21, 21)).toEqual([20, 21, 22]);
    expect(niceTicks(-3.4, 1.2)).toEqual([-4, -2, 0, 2]);
  });

  it('breaks the line where readings stop for more than three times the usual spacing', () => {
    const minute = 60_000;
    const points = [0, 5, 10, 15, 60, 65, 70].map((m) => ({ time: m * minute, value: 20 }));
    expect(segments(points).map((s) => s.length)).toEqual([4, 3]);
    expect(segments(points.slice(0, 4))).toHaveLength(1);
    expect(segments([])).toEqual([]);
  });

  it('finds the nearest reading', () => {
    const points = [0, 10, 20].map((time) => ({ time, value: time }));
    expect(nearestIndex(points, 4)).toBe(0);
    expect(nearestIndex(points, 6)).toBe(1);
    expect(nearestIndex(points, 99)).toBe(2);
  });
});

function Chart({ points }: { points: Point[] }) {
  const [hover, setHover] = useState<number | null>(null);
  return <LineChart points={points} days={1} title="Temperatur" series="temperature" format={(v) => celsius(v)} hoverTime={hover} onHover={setHover} />;
}

describe('LineChart', () => {
  const minute = 60_000;
  const start = dk('2026-10-22T10:00:00+02:00');
  const points = [0, 5, 10, 15, 60, 65, 70].map((m, i) => ({ time: start + m * minute, value: 20 + i / 2 }));

  it('never draws across missing readings', () => {
    const { container } = render(<Chart points={points} />);
    expect(container.querySelectorAll('path.series-line')).toHaveLength(2);
    expect(screen.getByRole('img').getAttribute('aria-label')).toBe(
      'Temperatur: fra 20,0 °C til 23,0 °C, laveste 20,0 °C, højeste 23,0 °C. Brug piletasterne for at læse målingerne.',
    );
  });

  it('draws a lone reading between gaps as a point', () => {
    const lone = [0, 5, 10, 60, 120, 125, 130].map((m) => ({ time: start + m * minute, value: 21 }));
    const { container } = render(<Chart points={lone} />);
    expect(container.querySelectorAll('path.series-line')).toHaveLength(2);
    expect(container.querySelectorAll('circle.series-point')).toHaveLength(1);
  });

  it('reads values with the keyboard in a status tooltip', () => {
    render(<Chart points={points} />);
    expect(screen.queryByRole('status')).toBeNull();

    const chart = screen.getByRole('img');
    fireEvent.focus(chart);
    expect(screen.getByRole('status').textContent).toBe('23,0 °Ckl. 11:10');

    fireEvent.keyDown(chart, { key: 'ArrowLeft' });
    expect(screen.getByRole('status').textContent).toBe('22,5 °Ckl. 11:05');

    fireEvent.keyDown(chart, { key: 'Home' });
    expect(screen.getByRole('status').textContent).toBe('20,0 °Ckl. 10:00');

    fireEvent.blur(chart);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('shows only the latest value when there is too little to draw', () => {
    const { container } = render(<Chart points={[{ time: start, value: 21.5 }]} />);
    expect(container.querySelector('svg')).toBeNull();
    expect(screen.getByText('21,5 °C')).toBeTruthy();
  });

  it('leaves the bar chart as it was after sharing the width hook', () => {
    const { container } = render(
      <BarChart ariaLabel="Forbrug" bars={[{ key: 'a', label: '5/11', title: 'tors. 5. nov.', value: 2, state: 'complete' }]} />,
    );
    expect(container.querySelector('svg')?.getAttribute('width')).toBe('320');
  });
});
