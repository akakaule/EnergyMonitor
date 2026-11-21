// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, type HeatPumpStatus, type Health, type PropertySummary, type Rule } from './api';
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
  vi.spyOn(api, 'features').mockResolvedValue({ pricing: true, mcp: false, heatpump: false });
  vi.spyOn(api, 'properties').mockResolvedValue([property('sommerhus', 'Sommerhus'), property('hus', 'Hus')]);
  vi.spyOn(api, 'health').mockResolvedValue(health);
  vi.spyOn(api, 'rules').mockResolvedValue(rules);
  vi.spyOn(api, 'notificationSettings').mockResolvedValue({ dailyReportEnabled: true, recipients: [], tomorrowPricesEnabled: true });
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

  it('lets an administrator turn the message about tomorrow’s electricity prices off', async () => {
    const save = vi.spyOn(api, 'saveNotificationSettings').mockResolvedValue(undefined);
    render(<Settings me={admin} section="notifikationer" />);

    const toggle = await screen.findByRole('switch', { name: /Send elpriser for i morgen/ });
    expect((toggle as HTMLInputElement).checked).toBe(true);
    fireEvent.click(toggle);
    fireEvent.click(screen.getByRole('button', { name: 'Gem' }));

    await waitFor(() => expect(save).toHaveBeenCalledWith({ dailyReportEnabled: true, tomorrowPricesEnabled: false, recipients: [] }));
  });

  it('falls back to Drift for a section that does not exist', async () => {
    render(<Settings me={admin} section="ejendom/findes-ikke" />);

    await waitFor(() => expect(screen.getByRole('link', { name: /^Hus/ })).toBeTruthy());
    expect(panelTitle('Drift')).toBeTruthy();
  });

  describe('heat pump', () => {
    const heatpump: HeatPumpStatus = {
      configured: true, lastSuccessAtUtc: '2026-11-08T07:00:00Z', lastAttemptAtUtc: '2026-11-08T07:00:00Z', lastError: null, oldestUtc: '2026-10-01T10:00:00Z', intervalMinutes: 15,
      devices: [
        { id: 'abc', name: 'Stue', propertyId: null, temperature: 19.5, humidity: 51, measuredAtUtc: '2026-11-08T07:00:00Z', lastSeenUtc: '2026-11-08T07:00:00Z', online: true, on: true, mode: 'heat', targetTemperature: 21, temperatureUnit: 'C' },
        { id: 'def', name: 'Anneks', propertyId: 'hus', temperature: 12, humidity: 60, measuredAtUtc: '2026-11-08T07:00:00Z', lastSeenUtc: '2026-11-08T06:00:00Z', online: false, on: false, mode: null, targetTemperature: null, temperatureUnit: 'C' },
      ],
    };

    beforeEach(() => {
      vi.mocked(api.features).mockResolvedValue({ pricing: false, mcp: false, heatpump: true });
      vi.spyOn(api, 'heatpump').mockResolvedValue(heatpump);
    });

    it('is a System section showing the connection, the last measurement and how far back history goes', async () => {
      render(<Settings me={admin} section="varmepumpe" />);

      await waitFor(() => expect(panelTitle('Varmepumpe')).toBeTruthy());
      expect(panelTitle('Drift')).toBeNull();
      const menu = within(screen.getByRole('navigation', { name: 'Indstillinger' }));
      await waitFor(() => expect(menu.getByRole('link', { name: 'Varmepumpe Målt 8/11 kl. 08:00' }).getAttribute('aria-current')).toBe('page'));
      expect(screen.getByText('Forbundet')).toBeTruthy();
      expect(screen.getByText('2 varmepumper')).toBeTruthy();
      expect(screen.getByText('Hentes hvert 15. minut')).toBeTruthy();
      expect(screen.getByText('1. okt. 2026')).toBeTruthy();
    });

    it('reports a failing connection in words', async () => {
      vi.mocked(api.heatpump).mockResolvedValue({ ...heatpump, lastError: 'sensibo_key_rejected', lastAttemptAtUtc: '2026-11-08T07:15:00Z' });
      render(<Settings me={admin} section="varmepumpe" />);

      expect(await screen.findByText('Fejl')).toBeTruthy();
      expect(screen.getByText(/Seneste forsøg 8\/11 kl\. 08:15/)).toBeTruthy();
      expect(screen.getByText(/Sensibo afviste API-nøglen/)).toBeTruthy();
      expect(screen.getByRole('link', { name: 'Varmepumpe Hentning fejlede' })).toBeTruthy();
    });

    it('links each heat pump to a property', async () => {
      const save = vi.spyOn(api, 'saveHeatpumpLinks').mockResolvedValue();
      render(<Settings me={admin} section="varmepumpe" />);

      const stue = (await screen.findByLabelText('Stue')) as HTMLSelectElement;
      await waitFor(() => expect([...stue.options].map((o) => o.textContent)).toEqual(['Ingen', 'Sommerhus', 'Hus']));
      expect((screen.getByLabelText('Anneks') as HTMLSelectElement).value).toBe('hus');
      fireEvent.change(stue, { target: { value: 'sommerhus' } });
      fireEvent.change(screen.getByLabelText('Anneks'), { target: { value: '' } });
      fireEvent.click(screen.getByRole('button', { name: 'Gem tilknytning' }));

      await waitFor(() => expect(save).toHaveBeenCalledWith([{ deviceId: 'abc', propertyId: 'sommerhus' }, { deviceId: 'def', propertyId: null }]));
      expect(await screen.findByText('Gemt.')).toBeTruthy();
      await waitFor(() => expect(api.heatpump).toHaveBeenCalledTimes(2));
    });

    it('starts a collection on request', async () => {
      const collect = vi.spyOn(api, 'collectHeatpump').mockResolvedValue();
      render(<Settings me={admin} section="varmepumpe" />);

      await screen.findByText('Forbundet');
      fireEvent.click(screen.getByRole('button', { name: 'Hent nu' }));

      await waitFor(() => expect(collect).toHaveBeenCalled());
      expect(await screen.findByText('Målingerne hentes nu.')).toBeTruthy();
    });

    it('edits the indoor temperature and offline alarms of a property with a heat pump', async () => {
      vi.mocked(api.rules).mockResolvedValue([
        ...rules,
        { id: 'IndoorTooCold', type: 'IndoorTooCold', enabled: true, params: { ...params, minTemperatureC: 5, sustainedMinutes: 120 } },
        { id: 'HeatPumpOffline', type: 'HeatPumpOffline', enabled: true, params: { ...params, offlineMinutes: 60 } },
      ]);
      const saveRule = vi.spyOn(api, 'saveRule').mockResolvedValue();
      render(<Settings me={admin} section="ejendom/hus" />);

      const cold = within(await screen.findByRole('group', { name: 'For koldt inde' }));
      expect(cold.getByText(/frostalarm under 5 °C/)).toBeTruthy();
      fireEvent.change(cold.getByLabelText(/Alarm under/), { target: { value: '7.5' } });
      expect((cold.getByLabelText(/Alarm efter/) as HTMLInputElement).value).toBe('120');
      fireEvent.click(cold.getByRole('button', { name: 'Gem regel' }));
      await waitFor(() =>
        expect(saveRule).toHaveBeenCalledWith('hus', expect.objectContaining({ type: 'IndoorTooCold', params: expect.objectContaining({ minTemperatureC: 7.5 }) })),
      );

      const offline = within(screen.getByRole('group', { name: 'Varmepumpe offline' }));
      expect((offline.getByLabelText(/Offline i/) as HTMLInputElement).value).toBe('60');
    });

    it('is not in the menu when the feature is off', async () => {
      vi.mocked(api.features).mockResolvedValue({ pricing: false, mcp: false, heatpump: false });
      render(<Settings me={admin} section="varmepumpe" />);

      await waitFor(() => expect(screen.getByRole('link', { name: /^Hus/ })).toBeTruthy());
      expect(panelTitle('Drift')).toBeTruthy();
      expect(screen.queryByRole('link', { name: /^Varmepumpe/ })).toBeNull();
      expect(api.heatpump).not.toHaveBeenCalled();
    });
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
