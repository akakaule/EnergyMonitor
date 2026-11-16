// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, type Alert, type HeatPumpStatus, type PropertySummary } from './api';
import { AlertList } from './common';
import { HeatPump } from './HeatPump';
import { PropertyDetail } from './PropertyDetail';
import type { Pod } from './sensibo';

const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);

const pod = (): Pod => ({
  id: 'abc',
  room: { name: 'Stue' },
  temperatureUnit: 'C',
  connectionStatus: { isAlive: true, lastSeen: { time: '2026-11-08T07:05:00Z', secondsAgo: 30 } },
  measurements: { temperature: 21.5, humidity: 45.4, time: { time: '2026-11-08T07:05:00Z', secondsAgo: 30 } },
  acState: { on: true, mode: 'heat', targetTemperature: 22, temperatureUnit: 'C', fanLevel: 'auto', swing: 'stopped' },
  remoteCapabilities: {
    modes: {
      heat: { temperatures: { C: { values: range(10, 30) } }, fanLevels: ['low', 'high', 'auto'], swing: ['stopped', 'rangeFull'] },
      cool: { temperatures: { C: { values: range(17, 30) } }, fanLevels: ['low', 'auto'] },
    },
  },
  smartMode: { enabled: false, type: 'temperature', lowTemperatureThreshold: 18, lowTemperatureState: { on: true, mode: 'heat', targetTemperature: 21, temperatureUnit: 'C' } },
  timer: null,
});

const status: HeatPumpStatus = {
  configured: true,
  lastSuccessAtUtc: '2026-11-08T07:00:00Z',
  lastAttemptAtUtc: '2026-11-08T07:00:00Z',
  lastError: null,
  oldestUtc: null,
  intervalMinutes: 15,
  devices: [
    {
      id: 'abc', name: 'Stue', propertyId: 'sommerhus', temperature: 19.5, humidity: 51, measuredAtUtc: '2026-11-08T07:00:00Z',
      lastSeenUtc: '2026-11-08T07:00:00Z', online: true, on: true, mode: 'heat', targetTemperature: 21, temperatureUnit: 'C',
    },
  ],
};

const day = {
  temperature: [
    { time: '2026-11-08T05:00:00Z', value: 21 },
    { time: '2026-11-08T05:05:00Z', value: 21.5 },
  ],
  humidity: [
    { time: '2026-11-08T05:00:00Z', value: 44 },
    { time: '2026-11-08T05:05:00Z', value: 45 },
  ],
};

const admin = { name: 'Test', role: 'admin' as const };

beforeEach(() => {
  vi.spyOn(api, 'heatpump').mockResolvedValue(status);
  vi.spyOn(api, 'pods').mockResolvedValue([pod()]);
  vi.spyOn(api, 'liveHistory').mockResolvedValue(day);
  vi.spyOn(api, 'heatpumpHistory').mockResolvedValue(day);
  vi.spyOn(api, 'setAcState').mockImplementation(async (_id, acState) => ({ status: 'Success', acState }));
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('HeatPump page', () => {
  it('shows the live temperature, humidity, connection and controls', async () => {
    render(<HeatPump me={admin} />);

    expect(await screen.findByRole('heading', { name: 'Stue' })).toBeTruthy();
    expect(screen.getByText('21,5 °C')).toBeTruthy();
    expect(screen.getByText('45 %')).toBeTruthy();
    expect(screen.getByText('Online')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Tændt' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByText('22 °C')).toBeTruthy();
    const modes = within(screen.getByRole('group', { name: 'Driftsform' }));
    expect(modes.getByRole('button', { name: 'Varme' }).getAttribute('aria-pressed')).toBe('true');
    expect(modes.getByRole('button', { name: 'Køl' }).getAttribute('aria-pressed')).toBe('false');
    expect(screen.getByLabelText('Blæser')).toBeTruthy();
    // The remote has no light or horizontal swing, so neither is offered.
    expect(screen.queryByLabelText('Displaylys')).toBeNull();
    expect(screen.queryByLabelText('Vandret sving')).toBeNull();
    expect(screen.getByText('Under 18,0 °C: varme 21 °C')).toBeTruthy();
    expect(await screen.findByRole('img', { name: /^Temperatur: fra 21,0 °C til 21,5 °C/ })).toBeTruthy();
    expect(api.liveHistory).toHaveBeenCalledWith('abc');
  });

  it('turns the heat pump off at once and sends the full state', async () => {
    render(<HeatPump me={admin} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Tændt' }));

    expect(screen.getByRole('button', { name: 'Slukket' }).getAttribute('aria-pressed')).toBe('false');
    await waitFor(() =>
      expect(api.setAcState).toHaveBeenCalledWith('abc', { on: false, mode: 'heat', targetTemperature: 22, temperatureUnit: 'C', fanLevel: 'auto', swing: 'stopped' }),
    );
  });

  it('fits the state to a new mode before sending it', async () => {
    render(<HeatPump me={admin} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Køl' }));

    await waitFor(() => expect(api.setAcState).toHaveBeenCalledWith('abc', { on: true, mode: 'cool', targetTemperature: 22, temperatureUnit: 'C', fanLevel: 'auto' }));
    expect(screen.getByRole('button', { name: 'Køl' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('rolls back a change Sensibo could not make and says why', async () => {
    vi.mocked(api.setAcState).mockRejectedValue(new ApiError(502, 'sensibo_unreachable'));
    render(<HeatPump me={admin} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Tændt' }));

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Der er ingen forbindelse til Sensibo.');
    expect(screen.getByRole('button', { name: 'Tændt' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('rolls back a change the heat pump refused', async () => {
    vi.mocked(api.setAcState).mockResolvedValue({ status: 'Failed', failureReason: 'IR timeout' });
    render(<HeatPump me={admin} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Tændt' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Varmepumpen tog ikke imod ændringen: IR timeout');
    expect(screen.getByRole('button', { name: 'Tændt' })).toBeTruthy();
  });

  it('sends one command after several taps on the target temperature', async () => {
    render(<HeatPump me={admin} />);

    const up = await screen.findByRole('button', { name: 'Højere temperatur' });
    fireEvent.click(up);
    fireEvent.click(up);
    expect(screen.getByText('24 °C')).toBeTruthy();
    expect(api.setAcState).not.toHaveBeenCalled();

    await waitFor(() => expect(api.setAcState).toHaveBeenCalledTimes(1), { timeout: 2000 });
    expect(vi.mocked(api.setAcState).mock.calls[0][1].targetTemperature).toBe(24);
  });

  it('sets and cancels a timer', async () => {
    const setTimer = vi.spyOn(api, 'setTimer').mockResolvedValue({});
    const cancelTimer = vi.spyOn(api, 'cancelTimer').mockResolvedValue({});
    render(<HeatPump me={admin} />);

    fireEvent.change(await screen.findByLabelText('Sluk'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sæt timer' }));
    await waitFor(() => expect(setTimer).toHaveBeenCalledWith('abc', 30, expect.objectContaining({ on: false, mode: 'heat' })));

    vi.mocked(api.pods).mockResolvedValue([{ ...pod(), timer: { isEnabled: true, acState: { on: false }, targetTime: '2026-11-08T08:00:00Z' } }]);
    fireEvent.click(screen.getByRole('button', { name: 'Sæt timer' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Annullér timer' }));
    expect(screen.getByText(/Slukker kl\. 09:00/)).toBeTruthy();
    await waitFor(() => expect(cancelTimer).toHaveBeenCalledWith('abc'));
  });

  it('switches Climate React and rolls back on failure', async () => {
    const climate = vi.spyOn(api, 'setClimateReact').mockRejectedValue(new ApiError(429, 'sensibo_rate_limited'));
    render(<HeatPump me={admin} />);

    const toggle = await screen.findByRole('switch', { name: 'Aktiv' });
    fireEvent.click(toggle);
    await waitFor(() => expect(climate).toHaveBeenCalledWith('abc', true));
    expect((await screen.findByRole('alert')).textContent).toContain('Vent et minut');
    expect((screen.getByRole('switch', { name: 'Aktiv' }) as HTMLInputElement).checked).toBe(false);
  });

  it('offers only the last day until the server has recorded history', async () => {
    render(<HeatPump me={admin} />);

    await screen.findByRole('heading', { name: 'Historik' });
    expect(screen.queryByRole('group', { name: 'Periode' })).toBeNull();
    expect(screen.getByText(/Sensibo gemmer kun det seneste døgn/)).toBeTruthy();
  });

  it('fetches longer ranges from the server’s recordings', async () => {
    vi.mocked(api.heatpump).mockResolvedValue({ ...status, oldestUtc: '2026-10-01T00:00:00Z' });
    render(<HeatPump me={admin} />);

    const ranges = within(await screen.findByRole('group', { name: 'Periode' }));
    expect(ranges.getAllByRole('button').map((b) => b.textContent)).toEqual(['Døgn', 'Uge', 'Måned', 'År']);
    fireEvent.click(ranges.getByRole('button', { name: 'Uge' }));

    await waitFor(() => expect(api.heatpumpHistory).toHaveBeenCalledWith('abc', 7));
    expect(ranges.getByRole('button', { name: 'Uge' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('picks between several heat pumps', async () => {
    vi.mocked(api.pods).mockResolvedValue([pod(), { ...pod(), id: 'def', room: { name: 'Køkken' }, measurements: { temperature: 18 } }]);
    render(<HeatPump me={admin} />);

    fireEvent.change(await screen.findByLabelText('Vælg varmepumpe'), { target: { value: 'def' } });

    expect(await screen.findByRole('heading', { name: 'Køkken' })).toBeTruthy();
    expect(screen.getByText('18,0 °C')).toBeTruthy();
  });

  it('gives readers the state without the controls', async () => {
    render(<HeatPump me={{ name: 'Test', role: 'viewer' }} />);

    expect(await screen.findByText('Varmepumpen kører: varme 22 °C.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Tændt' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Sæt timer' })).toBeNull();
  });

  it('says so when the server has no Sensibo key', async () => {
    vi.mocked(api.heatpump).mockResolvedValue({ ...status, configured: false, devices: [] });
    vi.mocked(api.pods).mockRejectedValue(new ApiError(503, 'sensibo_not_configured'));
    render(<HeatPump me={admin} />);

    expect(await screen.findByRole('heading', { name: 'Varmepumpen er ikke sat op' })).toBeTruthy();
  });

  it('shows offline with when the device was last seen', async () => {
    vi.mocked(api.pods).mockResolvedValue([{ ...pod(), connectionStatus: { isAlive: false, lastSeen: { time: '2026-11-08T05:00:00Z', secondsAgo: 7200 } } }]);
    render(<HeatPump me={admin} />);

    expect(await screen.findByText('Offline · sidst set for 2 t. siden')).toBeTruthy();
  });
});

describe('heat pump on the property page', () => {
  const property: PropertySummary = {
    id: 'sommerhus', name: 'Sommerhus', monitoringEnabled: true, hasMeteringPoint: true, meteringPoint: '…0028',
    status: 'Normal', statusLabel: 'Normal', hasBaseline: true, latestComplete: null,
    baseline: null, deviationKWh: null, deviationPercent: null, sum7: null, sum30: null,
    pendingDays: [], isStale: false, dataAgeHours: null, lastSuccessfulSyncUtc: null, openAlerts: 0, sparkline: [],
  };

  beforeEach(() => {
    vi.spyOn(api, 'features').mockResolvedValue({ pricing: false, mcp: false, heatpump: true });
    vi.spyOn(api, 'properties').mockResolvedValue([property, { ...property, id: 'hus', name: 'Hus' }]);
    vi.spyOn(api, 'daily').mockResolvedValue({ resolution: 'day', median: null, completeDays: 0, items: [] });
    vi.spyOn(api, 'propertyAlerts').mockResolvedValue([]);
  });

  it('shows the linked heat pump’s indoor climate and links to its controls', async () => {
    render(<PropertyDetail id="sommerhus" />);

    const card = within(await screen.findByRole('region', { name: 'Varmepumpe: Stue' }));
    expect(card.getByText('19,5 °C')).toBeTruthy();
    expect(card.getByText('51 %')).toBeTruthy();
    expect(card.getByText('Tændt · Varme · 21 °C')).toBeTruthy();
    expect(card.getByText(/Inde · Stue · målt .*08:00/)).toBeTruthy();
    expect(card.getByText('Online')).toBeTruthy();
    expect(card.getByRole('link').getAttribute('href')).toBe('#/varmepumpe');
  });

  it('says when an offline heat pump was last seen', async () => {
    vi.mocked(api.heatpump).mockResolvedValue({ ...status, devices: [{ ...status.devices[0], online: false, lastSeenUtc: '2026-11-07T07:05:00Z' }] });
    render(<PropertyDetail id="sommerhus" />);

    expect(await screen.findByText('Offline · sidst set 7/11 kl. 08:05')).toBeTruthy();
  });

  it('shows nothing for a property without a heat pump', async () => {
    render(<PropertyDetail id="hus" />);

    await waitFor(() => expect(api.heatpump).toHaveBeenCalled());
    expect(screen.queryByRole('region', { name: /Varmepumpe/ })).toBeNull();
  });

  it('does not ask for heat pumps when the feature is off', async () => {
    vi.mocked(api.features).mockResolvedValue({ pricing: false, mcp: false, heatpump: false });
    render(<PropertyDetail id="sommerhus" />);

    await screen.findByRole('heading', { name: 'Sommerhus' });
    expect(api.heatpump).not.toHaveBeenCalled();
  });
});

describe('heat pump alerts', () => {
  const alert: Alert = {
    id: '1', propertyId: 'sommerhus', propertyName: 'Sommerhus', ruleType: 'IndoorTooCold', severity: 'Alarm', state: 'Open',
    label: 'For koldt inde', firstDate: '2026-11-08', lastDate: '2026-11-08', observed: 4.2, baseline: null, threshold: 5,
    explanation: 'Stue har været under 5 °C i 2 timer.', createdAtUtc: '2026-11-08T07:00:00Z', acknowledgedAtUtc: null, closedAtUtc: null,
  };

  it('shows a temperature alert in degrees, not kWh', () => {
    render(<AlertList alerts={[alert]} onChanged={() => undefined} />);

    expect(screen.getByText('Målt 4,2 °C · grænse 5,0 °C')).toBeTruthy();
    expect(document.body.textContent).not.toContain('kWh');
  });

  it('shows no measurement line for an offline alert', () => {
    render(<AlertList alerts={[{ ...alert, ruleType: 'HeatPumpOffline', label: 'Varmepumpe offline', observed: 75, threshold: 60 }]} onChanged={() => undefined} />);

    expect(screen.getByText('Varmepumpe offline')).toBeTruthy();
    expect(screen.queryByText(/Målt/)).toBeNull();
  });
});
