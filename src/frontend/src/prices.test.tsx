// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, type DayPrices, type PriceView } from './api';
import { BarChart, niceRange } from './BarChart';
import { groupPrices, hourRange, nameList, PriceCard, PriceCards } from './Prices';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

/** 14:20 in Denmark on Sunday 8 November 2026. */
const NOW = Date.parse('2026-11-08T13:20:00Z');
const iso = (ms: number) => new Date(ms).toISOString();

/** A complete day whose hourly total comes from the Danish hour (winter time: UTC+1). */
function day(date: string, total: (hour: number) => number | null, extra: Partial<DayPrices> = {}): DayPrices {
  const start = Date.parse(`${date}T00:00:00Z`) - 3_600_000;
  const hours = Array.from({ length: 24 }, (_, h) => {
    const t = total(h);
    return {
      startUtc: iso(start + h * 3_600_000), endUtc: iso(start + (h + 1) * 3_600_000),
      spot: t === null ? null : t / 2, energy: t === null ? null : t / 2, supplier: 0.1, network: 0.2, tax: 0.3,
      vat: t === null ? null : t / 2 - 0.6, total: t,
    };
  });
  const totals = hours.map((h) => h.total).filter((t): t is number => t !== null);
  const complete = totals.length === 24;
  return {
    date, status: complete ? 'Complete' : totals.length === 0 ? 'Pending' : 'Partial',
    average: complete ? totals.reduce((a, b) => a + b, 0) / 24 : null,
    min: complete ? Math.min(...totals) : null, max: complete ? Math.max(...totals) : null,
    cheapest: null, mostExpensive: null, negativeHours: 0, hours, ...extra,
  };
}

const window3 = (date: string, fromHour: number, average: number) => {
  const start = Date.parse(`${date}T00:00:00Z`) - 3_600_000;
  return { fromUtc: iso(start + fromHour * 3_600_000), toUtc: iso(start + (fromHour + 3) * 3_600_000), average };
};

const curve = (h: number) => (h >= 17 && h < 20 ? 3.5 : h >= 21 ? 1 : 2);
const todayPrices = day('2026-11-08', curve, { cheapest: window3('2026-11-08', 21, 1), mostExpensive: window3('2026-11-08', 17, 3.5) });
const pending = day('2026-11-09', () => null);

const view = (days: DayPrices[], extra: Partial<PriceView> = {}): PriceView => ({
  propertyId: 'hjem', area: 'DK1', kind: 'Spot', windowHours: 3, assumptions: ['current_tariffs'],
  lastPriceSyncUtc: '2026-11-08T12:30:00Z', days, ...extra,
});

describe('price helpers', () => {
  it('writes hour ranges in Danish time and ends a day at 24', () => {
    expect(hourRange('2026-11-08T01:00:00Z', '2026-11-08T04:00:00Z')).toBe('kl. 02–05');
    expect(hourRange('2026-11-08T20:00:00Z', '2026-11-08T23:00:00Z')).toBe('kl. 21–24');
    expect(hourRange('2026-07-01T10:00:00Z', '2026-07-01T11:00:00Z')).toBe('kl. 12–13');
  });

  it('lists names the Danish way', () => {
    expect(nameList(['Hjem'])).toBe('Hjem');
    expect(nameList(['Hjem', 'Sommerhus'])).toBe('Hjem og Sommerhus');
    expect(nameList(['A', 'B', 'C'])).toBe('A, B og C');
  });

  it('groups properties that pay the same price and names those without an agreement', () => {
    const same = view([todayPrices]);
    const dearer = view([day('2026-11-08', (h) => curve(h) + 0.5)]);
    const none = view([], { area: null, kind: null, assumptions: ['no_agreement'] });

    const { groups, missing } = groupPrices([
      { name: 'Hjem', view: same }, { name: 'Sommerhus', view: { ...same, propertyId: 'sommerhus' } },
      { name: 'Kolonihave', view: dearer }, { name: 'Garage', view: none },
    ]);

    expect(groups.map((g) => g.names)).toEqual([['Hjem', 'Sommerhus'], ['Kolonihave']]);
    expect(missing).toEqual(['Garage']);
  });
});

describe('BarChart with prices', () => {
  it('extends the axis below zero only when a value is negative', () => {
    expect(niceRange(0, 7.4)).toEqual({ bottom: 0, top: 8, step: 2 });
    expect(niceRange(-0.4, 3.5)).toEqual({ bottom: -1, top: 4, step: 1 });
    expect(niceRange(-0.5, -0.1)).toEqual({ bottom: -0.6, top: 0.2, step: 0.2 });
  });

  it('draws a negative value below the zero line and says it with the given unit', () => {
    const { container } = render(
      <BarChart
        ariaLabel="Elpris"
        format={(v) => `${v.toFixed(2).replace('.', ',').replace('-', '−')} kr./kWh`}
        bars={[
          { key: 'a', label: '02', title: 'kl. 02–03', value: -0.5, state: 'complete', tone: 'low', note: 'billigste periode' },
          { key: 'b', label: '03', title: 'kl. 03–04', value: 1.5, state: 'complete' },
        ]}
      />,
    );

    expect(screen.getByLabelText('kl. 02–03: −0,50 kr./kWh (billigste periode)')).toBeTruthy();
    expect(screen.getByLabelText('kl. 03–04: 1,50 kr./kWh')).toBeTruthy();
    const [negative, positive] = [...container.querySelectorAll('path.bar')].map((p) => p.getAttribute('d')!);
    // Both bars start on the zero line; the negative one ends below it, the positive one above.
    const zero = Number(/^M[\d.]+,([\d.]+)/.exec(negative)![1]);
    expect(Number(/^M[\d.]+,([\d.]+)/.exec(positive)![1])).toBe(zero);
    expect(Number(/H[\d.]+ Q[\d.]+,([\d.]+)/.exec(negative)![1])).toBeGreaterThan(zero);
    expect(Number(/H[\d.]+ Q[\d.]+,([\d.]+)/.exec(positive)![1])).toBeLessThan(zero);
    expect(container.querySelector('path.bar-tone-low')).toBeTruthy();
  });
});

describe('PriceCard', () => {
  it('shows the price now, the rest of the day’s cheapest and dearest period, and fades the hours that are over', () => {
    const { container } = render(<PriceCard group={{ names: ['Hjem', 'Sommerhus'], view: view([todayPrices, pending]) }} now={NOW} />);

    expect(screen.getByRole('heading', { name: 'Hjem og Sommerhus' })).toBeTruthy();
    expect(container.querySelector('.hero')!.textContent).toBe('2,00 kr./kWh');
    expect(screen.getByText('Nu, kl. 14–15 · DK1')).toBeTruthy();
    expect(screen.getByText(/Billigst resten af dagen: kl\. 21–24 · gns\. 1,00 kr\.\/kWh/)).toBeTruthy();
    expect(screen.getByText(/Dyrest resten af dagen: kl\. 17–20 · gns\. 3,50 kr\.\/kWh/)).toBeTruthy();
    expect(container.querySelectorAll('path.bar')).toHaveLength(24);
    expect(container.querySelectorAll('path.bar-tone-past')).toHaveLength(14); // 00–14
    expect(container.querySelectorAll('path.bar-tone-low')).toHaveLength(3);
    expect(container.querySelectorAll('path.bar-tone-high')).toHaveLength(3);
    expect(screen.getByLabelText('kl. 18–19: 3,50 kr./kWh (dyreste periode)')).toBeTruthy();
    expect(screen.getByText('Priser hentet 8/11 kl. 13:30')).toBeTruthy();
  });

  it('breaks the chosen hour down into its parts, starting with the hour in progress', () => {
    const { container } = render(<PriceCard group={{ names: ['Hjem'], view: view([todayPrices, pending]) }} now={NOW} />);
    const breakdown = () => within(container.querySelector<HTMLElement>('.price-breakdown')!);

    expect(breakdown().getByText(/kl\. 14–15/)).toBeTruthy();

    fireEvent.click(screen.getByLabelText('kl. 18–19: 3,50 kr./kWh (dyreste periode)'));

    expect(breakdown().getByText(/kl\. 18–19/)).toBeTruthy();
    expect(breakdown().getByText('3,50 kr./kWh')).toBeTruthy();
    expect(breakdown().getByText('Spotpris').nextElementSibling!.textContent).toBe('1,75');
    expect(breakdown().getByText('Nettarif').nextElementSibling!.textContent).toBe('0,20');
    expect(breakdown().getByText('Moms').nextElementSibling!.textContent).toBe('1,15');
  });

  it('says when tomorrow’s prices are expected, and shows them with the day’s average once they are in', () => {
    const tomorrow = day('2026-11-09', (h) => (h === 2 || h === 3 ? -0.2 : 2), {
      cheapest: window3('2026-11-09', 1, 0.53), mostExpensive: window3('2026-11-09', 4, 2), negativeHours: 2,
    });
    const first = render(<PriceCard group={{ names: ['Hjem'], view: view([todayPrices, pending]) }} now={NOW} />);
    fireEvent.click(screen.getByRole('button', { name: 'I morgen' }));
    expect(screen.getByText('Priserne for i morgen kommer normalt lidt efter kl. 13.')).toBeTruthy();
    expect(first.container.querySelector('.hero')).toBeNull();
    first.unmount();

    const { container } = render(<PriceCard group={{ names: ['Hjem'], view: view([todayPrices, tomorrow]) }} now={NOW} />);
    fireEvent.click(screen.getByRole('button', { name: 'I morgen' }));

    expect(container.querySelector('.hero')!.textContent).toBe('1,82 kr./kWh');
    expect(screen.getByText('Gennemsnit man. 9. nov. · DK1')).toBeTruthy();
    expect(screen.getByText(/^Billigst: kl\. 01–04/)).toBeTruthy();
    expect(screen.getByText('2 timer med negativ spotpris.')).toBeTruthy();
    expect(screen.getByLabelText('kl. 02–03: −0,20 kr./kWh (billigste periode)')).toBeTruthy();
    expect(container.querySelector('path.bar-tone-past')).toBeNull();
    expect(container.querySelector('.price-breakdown')).toBeNull(); // no hour is chosen for tomorrow yet
  });

  it('never shows an hour without a price as zero, and says what the price leaves out', () => {
    const partial = day('2026-11-08', (h) => (h === 5 ? null : 2));
    render(<PriceCard group={{ names: ['Hjem'], view: view([partial], { kind: 'Fixed', assumptions: ['charges_missing'] }) }} now={NOW} />);

    expect(screen.getByLabelText('kl. 05–06: data mangler')).toBeTruthy();
    expect(screen.getByText(/Nogle timer mangler en pris/)).toBeTruthy();
    expect(screen.getByText(/nettarif og afgifter er ikke med i prisen/)).toBeTruthy();
    expect(screen.getByText(/Fastprisaftale/)).toBeTruthy();
    expect(screen.getByText('Fast pris')).toBeTruthy();
    expect(screen.queryByText('Gennemsnit')).toBeNull();
  });
});

describe('PriceCards', () => {
  it('asks for today and tomorrow, shares a card between properties with the same price, and points to the missing agreement', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(NOW);
    const prices = vi.spyOn(api, 'prices').mockImplementation(async (id) =>
      id === 'garage' ? view([], { propertyId: id, area: null, kind: null, assumptions: ['no_agreement'] }) : view([todayPrices, pending]));

    render(<PriceCards properties={[{ id: 'hjem', name: 'Hjem' }, { id: 'sommerhus', name: 'Sommerhus' }, { id: 'garage', name: 'Garage' }]} />);

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Hjem og Sommerhus' })).toBeTruthy());
    expect(prices).toHaveBeenCalledWith('hjem', '2026-11-08', '2026-11-09');
    expect(screen.getAllByText('Nu, kl. 14–15 · DK1')).toHaveLength(1);
    expect(screen.getByText(/Garage har ingen elaftale/)).toBeTruthy();
  });
});
