import type { AcState, Pod } from './sensibo';
import type { CostResult, PricingConfiguration } from './Pricing';
export type Quality = 'Complete' | 'Partial' | 'Missing';

export interface DayValue {
  date: string;
  kWh: number | null;
  quality: Quality;
  expectedIntervals: number;
  receivedIntervals: number;
}

export interface HourValue {
  startUtc: string;
  endUtc: string;
  kWh: number | null;
  quality: string;
}

export type Status = 'Normal' | 'Observer' | 'Alarm' | 'DataMissing';

export interface PropertySummary {
  id: string;
  name: string;
  monitoringEnabled: boolean;
  hasMeteringPoint: boolean;
  meteringPoint: string;
  status: Status;
  statusLabel: string;
  hasBaseline: boolean;
  latestComplete: DayValue | null;
  baseline: number | null;
  deviationKWh: number | null;
  deviationPercent: number | null;
  sum7: number | null;
  sum30: number | null;
  pendingDays: string[];
  isStale: boolean;
  dataAgeHours: number | null;
  lastSuccessfulSyncUtc: string | null;
  openAlerts: number;
  sparkline: DayValue[];
}

export interface Alert {
  id: string;
  propertyId: string;
  propertyName: string;
  ruleType: string;
  severity: 'Observer' | 'Alarm';
  state: 'Open' | 'Closed';
  label: string;
  firstDate: string;
  lastDate: string;
  observed: number | null;
  baseline: number | null;
  threshold: number | null;
  explanation: string;
  createdAtUtc: string;
  acknowledgedAtUtc: string | null;
  closedAtUtc: string | null;
}

export interface RuleParams {
  thresholdPercent: number;
  minBaselineKWh: number;
  baselineDays: number;
  minBaselineDays: number;
  escalateAfterDays: number;
  missingHours: number;
  consecutiveFailures: number;
  /** IndoorTooCold: the indoor temperature that counts as too cold. */
  minTemperatureC?: number;
  /** IndoorTooCold: how long it must stay too cold before Observer becomes Alarm. */
  sustainedMinutes?: number;
  /** HeatPumpOffline: how long the device may be silent. */
  offlineMinutes?: number;
}

/** A property's rules. IndoorTooCold and HeatPumpOffline exist for a property with a linked heat pump. */
export type RuleType = 'LowConsumption' | 'HighConsumption' | 'MissingData' | 'IndoorTooCold' | 'HeatPumpOffline';

export interface Rule {
  id: string;
  type: RuleType;
  enabled: boolean;
  params: RuleParams;
}

export interface Health {
  serverTimeUtc: string;
  syncRunning: boolean;
  lastSync: { startedAtUtc: string; finishedAtUtc: string | null; status: string; intervalCount: number; errorCode: string | null } | null;
  lastSuccessfulSyncUtc: string | null;
  lastError: { errorCode: string | null; at: string | null } | null;
  token: { configured: boolean; expiresAtUtc: string | null; daysLeft: number | null; state: 'missing' | 'unknown' | 'expired' | 'expiring' | 'ok' };
  notifications: { channel: string; configured: boolean; pending: number; lastError: string | null };
}

export interface DailyConsumption {
  resolution: 'day';
  median: number | null;
  completeDays: number;
  items: DayValue[];
}

export interface MeteringPoint {
  meteringPointId: string;
  masked: string;
  address: string;
  type: string | null;
  readingOccurrence: string | null;
}

export interface NotificationSettings {
  dailyReportEnabled: boolean;
  recipients: string[];
  /** Send tomorrow's electricity prices on the days they stand out. */
  tomorrowPricesEnabled: boolean;
}

export interface Me {
  name: string;
  role: 'admin' | 'viewer';
}

/** "Ca. pris": always a total; what is assumed is listed in `assumptions`. Amounts in DKK, components excl. VAT. */
export interface Estimate {
  fromUtc: string;
  toUtc: string;
  total: number;
  kWh: number;
  consumptionCoverage: number;
  components: Record<string, number>;
  assumptions: string[];
}

/** The price of one kWh in one hour, DKK. The parts are excl. VAT; `total` is incl. VAT and null without a price. */
export interface HourPrice {
  startUtc: string;
  endUtc: string;
  spot: number | null;
  energy: number | null;
  supplier: number;
  network: number;
  tax: number;
  vat: number | null;
  total: number | null;
}

export interface PriceWindow {
  fromUtc: string;
  toUtc: string;
  average: number;
}

export interface DayPrices {
  date: string;
  /** Pending: no prices yet, typically tomorrow before 13:00. */
  status: 'Complete' | 'Partial' | 'Pending';
  average: number | null;
  min: number | null;
  max: number | null;
  /** For today: among the hours that are not over yet. */
  cheapest: PriceWindow | null;
  mostExpensive: PriceWindow | null;
  negativeHours: number;
  hours: HourPrice[];
}

/** Hourly prices for a property. `area` is null and `days` empty when it has no electricity agreement. */
export interface PriceView {
  propertyId: string;
  area: string | null;
  kind: string | null;
  windowHours: number;
  assumptions: string[];
  lastPriceSyncUtc: string | null;
  days: DayPrices[];
}

export interface Features {
  pricing: boolean;
  mcp: boolean;
  heatpump: boolean;
}

/** A heat pump as of the server's last background collection. */
export interface HeatPumpDevice {
  id: string;
  name: string;
  propertyId: string | null;
  temperature: number | null;
  humidity: number | null;
  measuredAtUtc: string | null;
  /** When Sensibo last heard from the device. */
  lastSeenUtc: string | null;
  online: boolean;
  on: boolean | null;
  mode: string | null;
  targetTemperature: number | null;
  temperatureUnit: string | null;
}

export interface HeatPumpStatus {
  /** Whether the server has a Sensibo API key. */
  configured: boolean;
  lastSuccessAtUtc: string | null;
  lastAttemptAtUtc: string | null;
  /** An error code, explained by `errorText`. */
  lastError: string | null;
  /** The oldest measurement the server has recorded; longer history ranges need it. */
  oldestUtc: string | null;
  intervalMinutes: number;
  devices: HeatPumpDevice[];
}

export interface RawHistory {
  temperature: { time: string; value: number }[];
  humidity: { time: string; value: number }[];
}

/** Sensibo's answer to a new AC state. */
export interface AcStateResult {
  status?: string;
  acState?: AcState;
  failureReason?: string | null;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
  ) {
    super(code);
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: {
      // The server refuses state-changing calls without this header (CSRF protection).
      'X-Requested-With': 'EnergyMonitor',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 401) {
    // Not signed in: go through the server's sign-in and come back to where we were.
    const here = window.location.pathname + window.location.hash;
    window.location.assign(`/auth/login?returnUrl=${encodeURIComponent(here)}`);
    throw new ApiError(401, 'unauthenticated');
  }

  if (!response.ok) {
    let code = `http_${response.status}`;
    try {
      const problem = (await response.json()) as { error?: string };
      if (problem.error) code = problem.error;
    } catch {
      // No JSON body: keep the status code.
    }
    throw new ApiError(response.status, code);
  }

  if (response.status === 204 || response.status === 202) return undefined as T;
  return (await response.json()) as T;
}

const POD_FIELDS = 'id,room,acState,measurements,mainMeasurementsSensor,remoteCapabilities,connectionStatus,temperatureUnit,smartMode,timer';

/** Sensibo's API, reached through the server, which adds the key. Sensibo wraps every answer in `result`. */
async function sensibo<T>(method: string, path: string, body?: unknown): Promise<T> {
  return (await request<{ result?: T } | undefined>(method, `/api/heatpump/sensibo/${path}`, body))?.result as T;
}

const pod = (id: string, suffix: string) => `pods/${encodeURIComponent(id)}${suffix}`;

export const api = {
  features: () => request<Features>('GET', '/api/features'),
  heatpump: () => request<HeatPumpStatus>('GET', '/api/heatpump'),
  heatpumpHistory: (deviceId: string, days: number) =>
    request<RawHistory>('GET', `/api/heatpump/history/${encodeURIComponent(deviceId)}?days=${days}`),
  pods: async () => (await sensibo<Pod[] | undefined>('GET', `users/me/pods?fields=${POD_FIELDS}`)) ?? [],
  /** The last 24 hours, straight from Sensibo. */
  liveHistory: (deviceId: string) => sensibo<RawHistory | undefined>('GET', pod(deviceId, '/historicalMeasurements?days=1')),
  setAcState: (deviceId: string, acState: AcState) => sensibo<AcStateResult | undefined>('POST', pod(deviceId, '/acStates'), { acState }),
  setTimer: (deviceId: string, minutesFromNow: number, acState: AcState) =>
    sensibo<unknown>('PUT', pod(deviceId, '/timer/'), { minutesFromNow, acState }),
  cancelTimer: (deviceId: string) => sensibo<unknown>('DELETE', pod(deviceId, '/timer/')),
  setClimateReact: (deviceId: string, enabled: boolean) => sensibo<unknown>('PUT', pod(deviceId, '/smartmode'), { enabled }),
  saveHeatpumpLinks: (links: { deviceId: string; propertyId: string | null }[]) =>
    request<void>('PUT', '/api/admin/heatpump/links', { links }),
  collectHeatpump: () => request<void>('POST', '/api/admin/heatpump/collect'),
  priceStatus: (id: string, from: string, to: string) => request<{ sources: { source: string; lastSuccessUtc: string | null; errorCode: string | null }[] }>('GET', `/api/energy/${id}/status?from=${from}&to=${to}`),
  costs: (id: string, from: string, to: string) => request<CostResult>('GET', `/api/energy/${id}/costs?from=${from}&to=${to}`),
  estimate: (id: string, from: string, to: string) => request<Estimate>('GET', `/api/energy/${id}/estimate?from=${from}&to=${to}`),
  prices: (id: string, from: string, to: string) => request<PriceView>('GET', `/api/energy/${id}/prices?from=${from}&to=${to}`),
  pricing: (id: string) => request<PricingConfiguration>('GET', `/api/energy/${id}/configuration`),
  savePricing: (id: string, config: PricingConfiguration) => request<void>('PUT', `/api/energy/${id}/configuration`, config),
  me: () => request<Me>('GET', '/api/me'),
  properties: () => request<PropertySummary[]>('GET', '/api/properties'),
  daily: (id: string, from: string, to: string) =>
    request<DailyConsumption>('GET', `/api/properties/${id}/consumption?from=${from}&to=${to}&resolution=day`),
  hourly: (id: string, day: string) =>
    request<{ items: HourValue[] }>('GET', `/api/properties/${id}/consumption?from=${day}&to=${day}&resolution=hour`),
  alerts: (state?: 'open' | 'closed') => request<Alert[]>('GET', `/api/alerts${state ? `?state=${state}` : ''}`),
  propertyAlerts: (id: string) => request<Alert[]>('GET', `/api/properties/${id}/alerts`),
  acknowledge: (id: string) => request<void>('POST', `/api/alerts/${encodeURIComponent(id)}/acknowledge`),
  rules: (id: string) => request<Rule[]>('GET', `/api/properties/${id}/rules`),
  saveRule: (id: string, rule: Rule) =>
    request<void>('PUT', `/api/properties/${id}/rules/${rule.id}`, { enabled: rule.enabled, params: rule.params }),
  health: () => request<Health>('GET', '/api/system/health'),
  sync: () => request<void>('POST', '/api/admin/sync'),
  testNotification: () => request<{ ok: boolean }>('POST', '/api/admin/notifications/test'),
  notificationSettings: () => request<NotificationSettings>('GET', '/api/admin/settings/notifications'),
  saveNotificationSettings: (settings: NotificationSettings) =>
    request<void>('PUT', '/api/admin/settings/notifications', settings),
  reportPreview: () => request<{ text: string }>('GET', '/api/admin/report'),
  meteringPoints: () => request<MeteringPoint[]>('GET', '/api/admin/metering-points'),
  createProperty: (name: string, meteringPointId: string | null) =>
    request<{ id: string }>('POST', '/api/admin/properties', { name, meteringPointId, monitoringEnabled: true }),
  saveProperty: (id: string, name: string, meteringPointId: string | null, monitoringEnabled: boolean) =>
    request<void>('PUT', `/api/admin/properties/${id}`, { name, meteringPointId, monitoringEnabled }),
  deleteData: (id: string) => request<void>('DELETE', `/api/admin/properties/${id}/data`),
};

const ERRORS: Record<string, string> = {
  invalid_pricing: 'Prisopsætningen indeholder ugyldige eller overlappende gyldighedsperioder, enheder eller satser.',
  invalid_range: 'Vælg en gyldig periode på højst 90 dage.',
  http_403: 'Du har ikke adgang til dette.',
  sync_running: 'En synkronisering kører allerede.',
  invalid_params: 'En eller flere værdier er uden for det tilladte interval.',
  invalid_recipients: 'Modtagere skal være Telegram chat-id’er (tal) eller @kanalnavne.',
  invalid_name: 'Navnet skal udfyldes og må højst være 40 tegn.',
  invalid_metering_point: 'Målepunkts-id skal være 18 cifre.',
  name_taken: 'Der findes allerede en ejendom med det navn.',
  token_invalid: 'Eloverblik afviste adgangsnøglen. Den skal fornyes.',
  token_missing: 'Der er ikke registreret en Eloverblik-adgangsnøgle.',
  http_429: 'Eloverblik begrænser antallet af kald lige nu. Prøv igen senere.',
  http_503: 'Eloverblik (DataHub) er midlertidigt utilgængelig.',
  network_error: 'Der er ingen forbindelse til Eloverblik.',
  unknown_property: 'Ejendommen findes ikke.',
  property_taken: 'En ejendom kan kun have én varmepumpe.',
  internal_error: 'Serveren kunne ikke gemme målingerne. Se loggen.',
  sensibo_not_configured: 'Der er ikke sat en Sensibo API-nøgle op på serveren.',
  sensibo_key_rejected: 'Sensibo afviste API-nøglen. Den skal udskiftes på serveren.',
  sensibo_rate_limited: 'Sensibo begrænser antallet af kald lige nu. Vent et minut, og prøv igen.',
  sensibo_not_found: 'Sensibo kan ikke finde varmepumpen.',
  sensibo_error: 'Sensibo svarede med en fejl. Prøv igen om lidt.',
  sensibo_unreachable: 'Der er ingen forbindelse til Sensibo.',
  sensibo_timeout: 'Sensibo svarede ikke i tide. Prøv igen.',
  sensibo_bad_request: 'Sensibo afviste værdien. Varmepumpen understøtter den måske ikke.',
  collect_running: 'Målingerne hentes allerede.',
  invalid_device: 'En af varmepumperne findes ikke længere. Hent siden igen.',
  invalid_days: 'Vælg et gyldigt antal dage.',
};

/** A message a person can act on for an error code from the server; unknown codes are shown as they are. */
export function codeText(code: string): string {
  return ERRORS[code] ?? `Der opstod en fejl (${code}).`;
}

/** A message a person can act on; unknown codes are shown as they are. */
export function errorText(error: unknown): string {
  if (error instanceof ApiError) return codeText(error.code);
  return 'Der er ingen forbindelse til serveren.';
}
