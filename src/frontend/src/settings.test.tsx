// @vitest-environment jsdom
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, type Health, type PropertySummary, type Rule } from './api';
import { Settings } from './Settings';

const property = (id: string, name: string): PropertySummary => ({
  id, name, monitoringEnabled: true, hasMeteringPoint: true, meteringPoint: '…0028',
  status: 'Normal', statusLabel: 'Normal', hasBaseline: true,
  latestComplete: { date: '2026-11-06', kWh: 1.2, quality: 'Complete', expectedIntervals: 24, receivedIntervals: 24 },
  baseline: 7.4, deviationKWh: -6.2, deviationPercent: -83.8, sum7: 45.6, sum30: null,
  pendingDays: [], isStale: false, dataAgeHours: 32.1, lastSuccessfulSyncUtc: '2026-11-08T07:05:00Z',
  openAlerts: 0, sparkline: [],
});

const health: Health = {
  serverTimeUtc: '2026-11-08T07:05:00Z', syncRunning: false,
  lastSync: { startedAtUtc: '2026-11-08T07:05:00Z', finishedAtUtc: '2026-11-08T07:06:00Z', status: 'Succeeded', intervalCount: 24, errorCode: null },
  lastSuccessfulSyncUtc: '2026-11-08T07:06:00Z', lastError: null,
  token: { configured: true, expiresAtUtc: null, daysLeft: 300, state: 'ok' },
  notifications: { channel: 'telegram', configured: false, pending: 2, lastError: null },
};

const params = { thresholdPercent: 50, minBaselineKWh: 3, baselineDays: 14, minBaselineDays: 7, escalateAfterDays: 2, missingHours: 72, consecutiveFailures: 3 };
const rules: Rule[] = [
  { id: 'LowConsumption', type: 'LowConsumption', enabled: true, params },
  { id: 'HighConsumption', type: 'HighConsumption', enabled: true, params: { ...params, thresholdPercent: 200 } },
  { id: 'MissingData', type: 'MissingData', enabled: false, params },
];

beforeEach(() => {
  vi.spyOn(api, 'features').mockResolvedValue({ pricing: true, mcp: false });
  vi.spyOn(api, 'properties').mockResolvedValue([property('sommerhus', 'Sommerhus'), property('hus', 'Hus')]);
  vi.spyOn(api, 'health').mockResolvedValue(health);
  vi.spyOn(api, 'rules').mockResolvedValue(rules);
  vi.spyOn(api, 'notificationSettings').mockResolvedValue({ dailyReportEnabled: true, recipients: [] });
  vi.spyOn(api, 'pricing').mockResolvedValue({
    agreements: [{ fromUtc: '2026-11-12T23:00:00+00:00', toUtc: null, area: 'DK1', kind: 'Spot', fixedPrice: null, supplierMarkup: null, dailySubscription: null, settlementMinutes: 60, vatRate: 0.25 }],
    rates: [], requiredTariffCodes: [], tariffsConfirmed: false,
  });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const admin = { name: 'Test', role: 'admin' as const };
const panelTitle = (name: string) => screen.queryByRole('heading', { level: 2, name });

describe('Settings', () => {
  it('lists every section in the menu and opens Drift by default', async () => {
    render(<Settings me={admin} />);

    const menu = within(await screen.findByRole('navigation', { name: 'Indstillinger' }));
    await waitFor(() => expect(menu.getByRole('link', { name: /^Hus/ })).toBeTruthy());
    expect(menu.getByRole('link', { name: /^Drift/ }).getAttribute('aria-current')).toBe('page');
    expect(menu.getByRole('link', { name: /^Notifikationer/ }).getAttribute('href')).toBe('#/indstillinger/notifikationer');
    expect(menu.getByRole('link', { name: /^Sommerhus/ }).getAttribute('href')).toBe('#/indstillinger/ejendom/sommerhus');
    expect(menu.getByRole('link', { name: 'Ny ejendom' })).toBeTruthy();

    expect(panelTitle('Drift')).toBeTruthy();
    expect(panelTitle('Notifikationer')).toBeNull();
    expect(panelTitle('Hus')).toBeNull();
  });

  it('shows the state of each section under its name in the menu, loading the status once', async () => {
    vi.mocked(api.properties).mockResolvedValue([
      { ...property('sommerhus', 'Sommerhus'), monitoringEnabled: false },
      { ...property('hus', 'Hus'), status: 'Observer', statusLabel: 'Observer', meteringPoint: '…0980' },
      { ...property('ny', 'Ny'), hasMeteringPoint: false, meteringPoint: '' },
    ]);
    render(<Settings me={admin} />);

    const menu = within(await screen.findByRole('navigation', { name: 'Indstillinger' }));
    await waitFor(() => expect(menu.getByRole('link', { name: 'Hus Observer · …0980' })).toBeTruthy());
    expect(menu.getByRole('link', { name: 'Sommerhus På pause' })).toBeTruthy();
    expect(menu.getByRole('link', { name: 'Ny Intet målepunkt' })).toBeTruthy();
    expect(menu.getByRole('link', { name: 'Notifikationer Ikke sat op' })).toBeTruthy();
    expect(menu.getByRole('link', { name: 'Drift Synkroniseret 8/11 kl. 08:06' })).toBeTruthy();
    expect(api.health).toHaveBeenCalledTimes(1);
  });

  it('flags a failed sync in the menu', async () => {
    vi.mocked(api.health).mockResolvedValue({ ...health, lastSync: { ...health.lastSync!, status: 'Failed', errorCode: 'http_503' } });
    render(<Settings me={admin} />);

    expect(await screen.findByRole('link', { name: 'Drift Synkronisering fejlede' })).toBeTruthy();
  });

  it('shows only the selected section', async () => {
    render(<Settings me={admin} section="notifikationer" />);

    await waitFor(() => expect(panelTitle('Notifikationer')).toBeTruthy());
    expect(panelTitle('Drift')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Synkronisér nu' })).toBeNull();
  });

  it('keeps a property together with its alarm limits, electricity agreement and data', async () => {
    render(<Settings me={admin} section="ejendom/hus" />);

    await waitFor(() => expect(panelTitle('Hus')).toBeTruthy());
    expect(panelTitle('Sommerhus')).toBeNull();

    const panel = within(panelTitle('Hus')!.closest('section')!);
    await waitFor(() => expect(panel.getByRole('heading', { name: 'Elaftale' })).toBeTruthy());
    expect(panel.getByRole('heading', { name: 'Alarmgrænser' })).toBeTruthy();
    expect(panel.getByRole('group', { name: /Lavt forbrug/ })).toBeTruthy();
    expect(panel.getByRole('button', { name: 'Hent målepunkter' })).toBeTruthy();
    expect(panel.getByRole('button', { name: 'Slet forbrugsdata…' })).toBeTruthy();
    expect(api.pricing).toHaveBeenCalledWith('hus');
  });

  it('falls back to Drift for a section that does not exist', async () => {
    render(<Settings me={admin} section="ejendom/findes-ikke" />);

    await waitFor(() => expect(screen.getByRole('link', { name: /^Hus/ })).toBeTruthy());
    expect(panelTitle('Drift')).toBeTruthy();
  });

  it('gives readers the operations status without a menu or admin actions', async () => {
    render(<Settings me={{ name: 'Test', role: 'viewer' }} section="notifikationer" />);

    await waitFor(() => expect(screen.getByText('Gennemført')).toBeTruthy());
    expect(panelTitle('Drift')).toBeTruthy();
    expect(screen.queryByRole('navigation', { name: 'Indstillinger' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Synkronisér nu' })).toBeNull();
    expect(screen.getByText(/logget ind som læser/)).toBeTruthy();
  });
});
