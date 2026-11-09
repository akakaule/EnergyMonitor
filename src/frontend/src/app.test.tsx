// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, errorText, type PropertySummary } from './api';
import { parseRoute } from './App';
import { BarChart, niceScale } from './BarChart';
import { addDays, age, dateTime, dayRange, daysBetween, kWh, longDate, percent, shortDate, today } from './format';
import { Overview, PropertyCard } from './Overview';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('format', () => {
  it('formats kWh the Danish way and never shows a missing value as zero', () => {
    expect(kWh(1.24)).toBe('1,2 kWh');
    expect(kWh(0)).toBe('0,0 kWh');
    expect(kWh(null)).toBe('–');
    expect(kWh(undefined)).toBe('–');
  });

  it('formats dates and deviations', () => {
    expect(shortDate('2026-11-06')).toBe('6/11');
    expect(longDate('2026-11-06')).toBe('fre. 6. nov.');
    expect(percent(-83.7)).toBe('−84 %');
    expect(percent(12.2)).toBe('+12 %');
    expect(dayRange(['2026-11-07'])).toBe('7/11');
    expect(dayRange(['2026-11-07', '2026-11-08', '2026-11-09'])).toBe('7/11–9/11');
    expect(age(32.1)).toBe('32 timer gammel');
    expect(age(100)).toBe('4 døgn gammel');
    expect(age(null)).toBe('ingen målinger');
  });

  it('does calendar arithmetic across month ends and DST changes', () => {
    expect(addDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(addDays('2026-10-24', 2)).toBe('2026-10-26');
    expect(daysBetween('2026-10-24', '2026-10-26')).toBe(2);
  });

  it('uses Danish time, not the device time zone', () => {
    // 23:30 UTC on 7 November is already 8 November in Denmark.
    expect(today(new Date('2026-11-07T23:30:00Z'))).toBe('2026-11-08');
    expect(dateTime('2026-11-08T07:05:00Z')).toBe('8/11 kl. 08:05');
    expect(dateTime('2026-07-01T07:05:00Z')).toBe('1/7 kl. 09:05');
    expect(dateTime(null)).toBe('aldrig');
  });
});

describe('routing and errors', () => {
  it('parses the three screens', () => {
    expect(parseRoute('')).toEqual({ page: 'overview' });
    expect(parseRoute('#/ejendom/sommerhus')).toEqual({ page: 'property', id: 'sommerhus' });
    expect(parseRoute('#/indstillinger')).toEqual({ page: 'settings' });
    expect(parseRoute('#/ejendom/../x')).toEqual({ page: 'overview' });
  });

  it('explains known error codes', () => {
    expect(errorText(new ApiError(502, 'token_invalid'))).toContain('adgangsnøglen');
    expect(errorText(new ApiError(500, 'http_500'))).toContain('http_500');
    expect(errorText(new TypeError('failed to fetch'))).toContain('forbindelse');
  });
});

describe('BarChart', () => {
  const bars = [
    { key: 'a', label: '5/11', title: 'tors. 5. nov.', value: 7.4, state: 'complete' as const },
    { key: 'b', label: '6/11', title: 'fre. 6. nov.', value: 3.1, state: 'partial' as const },
    { key: 'c', label: '7/11', title: 'lør. 7. nov.', value: null, state: 'missing' as const },
  ];

  it('picks a round axis', () => {
    expect(niceScale(7.4)).toEqual({ top: 8, step: 2 });
    expect(niceScale(18.2)).toEqual({ top: 20, step: 5 });
    expect(niceScale(0)).toEqual({ top: 1, step: 0.5 });
  });

  it('draws a missing day as a gap marker, not as a zero bar', () => {
    const { container } = render(<BarChart bars={bars} ariaLabel="Dagligt forbrug" />);

    expect(container.querySelectorAll('path.bar')).toHaveLength(2);
    expect(container.querySelectorAll('rect.bar-missing')).toHaveLength(1);
    expect(screen.getByLabelText('lør. 7. nov.: data mangler')).toBeTruthy();
    expect(screen.getByLabelText('fre. 6. nov.: 3,10 kWh (ufuldstændigt døgn)')).toBeTruthy();
    expect(screen.queryByLabelText(/7\. nov\.: 0/)).toBeNull();
  });

  it('shows a tooltip on hover and reports selection', () => {
    const onSelect = vi.fn();
    render(<BarChart bars={bars} ariaLabel="Dagligt forbrug" onSelect={onSelect} reference={{ value: 7, label: 'Median 7,0 kWh' }} />);

    fireEvent.mouseEnter(screen.getByLabelText('lør. 7. nov.: data mangler'));
    expect(screen.getByRole('status').textContent).toContain('Data mangler');

    fireEvent.click(screen.getByLabelText('tors. 5. nov.: 7,40 kWh'));
    expect(onSelect).toHaveBeenCalledWith('a');
    expect(screen.getByText('Median 7,0 kWh')).toBeTruthy();
  });
});

const sommerhus: PropertySummary = {
  id: 'sommerhus', name: 'Sommerhus', monitoringEnabled: true, hasMeteringPoint: true, meteringPoint: '…0028',
  status: 'Observer', statusLabel: 'Observer', hasBaseline: true,
  latestComplete: { date: '2026-11-06', kWh: 1.2, quality: 'Complete', expectedIntervals: 24, receivedIntervals: 24 },
  baseline: 7.4, deviationKWh: -6.2, deviationPercent: -83.8, sum7: 45.6, sum30: null,
  pendingDays: ['2026-11-07'], isStale: false, dataAgeHours: 32.1, lastSuccessfulSyncUtc: '2026-11-08T07:05:00Z',
  openAlerts: 1,
  sparkline: [{ date: '2026-11-05', kWh: 7.4, quality: 'Complete', expectedIntervals: 24, receivedIntervals: 24 }],
};

describe('PropertyCard', () => {
  it('shows the latest complete day with its date, freshness and what is still pending', () => {
    render(<PropertyCard p={sommerhus} />);

    expect(screen.getByText('1,2 kWh')).toBeTruthy();
    expect(screen.getByText('Seneste komplette døgn · fre. 6. nov.')).toBeTruthy();
    expect(screen.getByText('Observer')).toBeTruthy();
    expect(screen.getByText('−84 % i forhold til median 7,4 kWh')).toBeTruthy();
    expect(screen.getByText('Data for 7/11 er endnu ikke komplette.')).toBeTruthy();
    expect(screen.getByText(/Synkroniseret 8\/11 kl\. 08:05 · nyeste komplette måling 32 timer gammel/)).toBeTruthy();
    expect(screen.getByText('45,6 kWh')).toBeTruthy();
  });

  it('shows missing data as missing, never as 0 kWh', () => {
    render(<PropertyCard p={{ ...sommerhus, status: 'DataMissing', statusLabel: 'Data mangler', latestComplete: null, baseline: null, hasBaseline: false, sum7: null, sum30: null, sparkline: [], pendingDays: [], dataAgeHours: null, isStale: true }} />);

    expect(screen.getByText('Data mangler')).toBeTruthy();
    expect(screen.getByText('Ingen komplette døgn modtaget endnu')).toBeTruthy();
    expect(document.body.textContent).not.toContain('0,0 kWh');
  });

  it('says so when history is too short to compare', () => {
    render(<PropertyCard p={{ ...sommerhus, status: 'Normal', statusLabel: 'Normal', hasBaseline: false, baseline: null, deviationPercent: null }} />);

    expect(screen.getByText('Utilstrækkelig historik til sammenligning')).toBeTruthy();
  });
});

describe('Overview', () => {
  it('loads properties and open alarms, and reports a failed sync', async () => {
    vi.spyOn(api, 'properties').mockResolvedValue([sommerhus]);
    vi.spyOn(api, 'alerts').mockResolvedValue([]);
    vi.spyOn(api, 'health').mockResolvedValue({
      serverTimeUtc: '2026-11-08T07:05:00Z', syncRunning: false,
      lastSync: { startedAtUtc: '2026-11-08T07:05:00Z', finishedAtUtc: '2026-11-08T07:06:00Z', status: 'Failed', intervalCount: 0, errorCode: 'http_503' },
      lastSuccessfulSyncUtc: '2026-11-07T16:00:00Z', lastError: null,
      token: { configured: true, expiresAtUtc: null, daysLeft: null, state: 'ok' },
      notifications: { channel: 'telegram', configured: true, pending: 0, lastError: null },
    });

    render(<Overview />);

    await waitFor(() => expect(screen.getByText('Sommerhus')).toBeTruthy());
    expect(screen.getByText(/Seneste synkronisering fejlede \(http_503\)\. Viste tal er fra 7\/11 kl\. 17:00\./)).toBeTruthy();
    expect(screen.getByText('Ingen alarmer.')).toBeTruthy();
  });

  it('shows an error with a retry instead of empty cards when the server cannot be reached', async () => {
    vi.spyOn(api, 'properties').mockRejectedValue(new TypeError('failed to fetch'));
    vi.spyOn(api, 'alerts').mockResolvedValue([]);
    vi.spyOn(api, 'health').mockRejectedValue(new TypeError('failed to fetch'));

    render(<Overview />);

    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('ingen forbindelse'));
    expect(screen.getByText('Prøv igen')).toBeTruthy();
  });
});
