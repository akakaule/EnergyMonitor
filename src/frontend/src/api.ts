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
}

export interface Rule {
  id: string;
  type: 'LowConsumption' | 'HighConsumption' | 'MissingData';
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
}

export interface Me {
  name: string;
  role: 'admin' | 'viewer';
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

export const api = {
  features: () => request<{ pricing: boolean; mcp: boolean }>('GET', '/api/features'),
  priceStatus: (id: string, from: string, to: string) => request<{ sources: { source: string; lastSuccessUtc: string | null; errorCode: string | null }[] }>('GET', `/api/energy/${id}/status?from=${from}&to=${to}`),
  costs: (id: string, from: string, to: string) => request<CostResult>('GET', `/api/energy/${id}/costs?from=${from}&to=${to}`),
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
};

/** A message a person can act on; unknown codes are shown as they are. */
export function errorText(error: unknown): string {
  if (error instanceof ApiError) return ERRORS[error.code] ?? `Der opstod en fejl (${error.code}).`;
  return 'Der er ingen forbindelse til serveren.';
}
