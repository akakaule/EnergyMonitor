// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, type HeatingDay, type HeatingView } from './api';
import { HeatingCard } from './Heating';
import { ScatterChart } from './ScatterChart';
import { parsePosition } from './Settings';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

/** 20 days, colder day by day, that use 2 kWh plus 0,5 kWh per degree-day; the latest day uses 3 kWh too much. */
const days: HeatingDay[] = Array.from({ length: 20 }, (_, i) => {
  const degreeDays = 4 + i * 0.5;
  const expectedKWh = 2 + 0.5 * degreeDays;
  return {
    date: `2026-10-${String(i + 12).padStart(2, '0')}`, kWh: i === 19 ? expectedKWh + 3 : expectedKWh,
    outdoorC: 17 - degreeDays, indoorC: null, degreeDays, expectedKWh,
  };
});

const view = (extra: Partial<HeatingView> = {}): HeatingView => ({
  propertyId: 'sommerhus', status: 'Ok', periodDays: 90, baseTemperatureC: 17, usesIndoor: false,
  model: { baseKWhPerDay: 2, kWhPerDegreeDay: 0.5, rSquared: 0.92, days: 20 },
  latest: days[19], lastWeatherSyncUtc: '2026-11-08T07:05:00Z', weatherErrorCode: null, days, ...extra,
});

describe('HeatingCard', () => {
  it('says what a degree costs, the base load, and how the latest day compares with what the cold explains', async () => {
    vi.spyOn(api, 'heating').mockResolvedValue(view());
    const { container } = render(<HeatingCard id="sommerhus" located />);

    await waitFor(() => expect(screen.getByText('Pr. grad koldere')).toBeTruthy());
    expect(screen.getByText('0,50 kWh/døgn')).toBeTruthy();
    expect(screen.getByText('2,0 kWh/døgn')).toBeTruthy();
    expect(screen.getByText('92 %')).toBeTruthy();
    expect(screen.getByText('lør. 31. okt.: 11,8 kWh ved 3,5 °C ude · forventet 8,8 kWh (+34 %)')).toBeTruthy();
    expect(container.querySelectorAll('circle.scatter-dot')).toHaveLength(20);
    expect(container.querySelectorAll('circle.scatter-dot-highlight')).toHaveLength(1);
    expect(container.querySelector('line.scatter-line')).toBeTruthy();
    expect(screen.getByText(/hvor mange grader udetemperaturen lå under 17 °C/)).toBeTruthy();
    expect(screen.getByText(/hele målepunktets, ikke varmepumpens alene/)).toBeTruthy();
    expect(screen.getByText(/Udetemperatur hentet 8\/11 kl\. 08:05/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open-Meteo.com' })).toBeTruthy();
    expect(screen.queryByText(/tallene er usikre/)).toBeNull();
  });

  it('measures against the recorded indoor temperature when there is one, and names it per day', async () => {
    vi.spyOn(api, 'heating').mockResolvedValue(view({ usesIndoor: true, days: days.map((d) => ({ ...d, indoorC: 10 })), latest: { ...days[19], indoorC: 10 } }));
    render(<HeatingCard id="sommerhus" located />);

    await waitFor(() => expect(screen.getByText(/varmepumpens målte indetemperatur/)).toBeTruthy());
    expect(screen.getByLabelText('man. 12. okt.: 4,0 kWh ved 13,0 °C ude og 10,0 °C inde')).toBeTruthy();
    expect(screen.getByText('Grader koldere ude end inde')).toBeTruthy();
  });

  it('asks for another period on request', async () => {
    const heating = vi.spyOn(api, 'heating').mockResolvedValue(view());
    render(<HeatingCard id="sommerhus" located />);
    await waitFor(() => expect(heating).toHaveBeenCalledWith('sommerhus', 90));

    fireEvent.click(screen.getByRole('button', { name: '1 år' }));

    await waitFor(() => expect(heating).toHaveBeenCalledWith('sommerhus', 365));
  });

  it('warns when the consumption does not follow the cold', async () => {
    vi.spyOn(api, 'heating').mockResolvedValue(view({ model: { baseKWhPerDay: 6, kWhPerDegreeDay: 0.02, rSquared: 0.04, days: 20 } }));
    render(<HeatingCard id="hjem" located />);

    await waitFor(() => expect(screen.getByText(/tallene er usikre/)).toBeTruthy());
  });

  it('explains what is missing instead of showing figures', async () => {
    const heating = vi.spyOn(api, 'heating').mockResolvedValue(view({ status: 'InsufficientData', model: null, latest: null, days: days.slice(0, 5) }));
    const first = render(<HeatingCard id="sommerhus" located />);
    await waitFor(() => expect(screen.getByText(/ikke nok døgn .* \(5 af mindst 14\)/)).toBeTruthy());
    expect(screen.queryByText('Pr. grad koldere')).toBeNull();
    first.unmount();

    heating.mockResolvedValue(view({ status: 'InsufficientData', model: null, latest: null, days: [], lastWeatherSyncUtc: null, weatherErrorCode: 'weather_rate_limited' }));
    const second = render(<HeatingCard id="sommerhus" located />);
    await waitFor(() => expect(screen.getByText(/Udetemperaturen er ikke hentet endnu/)).toBeTruthy());
    expect(screen.getByText(/Vejrtjenesten begrænser antallet af kald/)).toBeTruthy();
    second.unmount();

    heating.mockClear();
    render(<HeatingCard id="sommerhus" located={false} />);
    expect(screen.getByText(/Angiv ejendommens position/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Indstillinger' }).getAttribute('href')).toBe('#/indstillinger/ejendom/sommerhus');
    expect(heating).not.toHaveBeenCalled();
  });
});

describe('ScatterChart', () => {
  it('describes each point in words and shows them on hover', () => {
    render(
      <ScatterChart
        ariaLabel="Forbrug mod graddage" xLabel="Grader" yLabel="kWh"
        points={[{ key: 'a', x: 2, y: 3, label: 'Dag a' }, { key: 'b', x: 8, y: 6, label: 'Dag b' }]}
        line={{ intercept: 2, slope: 0.5 }}
      />,
    );

    expect(screen.getByRole('img', { name: 'Forbrug mod graddage' })).toBeTruthy();
    fireEvent.mouseEnter(screen.getByLabelText('Dag b'));
    expect(screen.getByRole('status').textContent).toBe('Dag b');
  });
});

describe('parsePosition', () => {
  it('reads decimals with comma or point, and nothing as no position', () => {
    expect(parsePosition('55,93', '11.6')).toEqual({ latitude: 55.93, longitude: 11.6 });
    expect(parsePosition(' -33,9 ', '-70')).toEqual({ latitude: -33.9, longitude: -70 });
    expect(parsePosition('', '  ')).toBeNull();
  });

  it('refuses half a position, text, and values off the globe', () => {
    for (const [latitude, longitude] of [['55,93', ''], ['', '11,6'], ['nord', '11,6'], ['91', '11,6'], ['55,9', '181'], ['55,9,1', '11']]) {
      expect(() => parsePosition(latitude, longitude)).toThrowError(ApiError);
    }
  });
});
