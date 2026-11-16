// Pure helpers for Sensibo data: fitting AC states to what the remote supports, history and Danish labels.

export type ListProperty = 'fanLevel' | 'swing' | 'horizontalSwing' | 'light';

export interface AcState {
  on?: boolean;
  mode?: string;
  targetTemperature?: number;
  temperatureUnit?: string;
  fanLevel?: string;
  swing?: string;
  horizontalSwing?: string;
  light?: string;
  timestamp?: { time: string; secondsAgo: number };
}

export interface ModeCapabilities {
  temperatures?: Record<string, { isNative?: boolean; values: number[] }>;
  fanLevels?: string[];
  swing?: string[];
  horizontalSwing?: string[];
  light?: string[];
}

export interface Measurements {
  temperature?: number | null;
  humidity?: number | null;
  feelsLike?: number | null;
  time?: { time: string; secondsAgo: number } | null;
}

export interface SmartMode {
  enabled: boolean;
  type?: string;
  lowTemperatureThreshold?: number | null;
  highTemperatureThreshold?: number | null;
  lowTemperatureState?: AcState | null;
  highTemperatureState?: AcState | null;
}

export interface PodTimer {
  isEnabled?: boolean;
  acState?: AcState;
  targetTime?: string;
  targetTimeSecondsFromNow?: number;
}

/** A Sensibo device as `users/me/pods` returns it, with the fields the page asks for. */
export interface Pod {
  id: string;
  room?: { name?: string } | null;
  acState?: AcState | null;
  measurements?: Measurements | null;
  mainMeasurementsSensor?: { measurements?: Measurements | null } | null;
  remoteCapabilities?: { modes?: Record<string, ModeCapabilities> } | null;
  connectionStatus?: { isAlive?: boolean; lastSeen?: { time: string; secondsAgo: number } | null } | null;
  temperatureUnit?: string | null;
  smartMode?: SmartMode | null;
  timer?: PodTimer | null;
}

export interface Point {
  time: number;
  value: number;
}

export interface History {
  temperature: Point[];
  humidity: Point[];
}

/** acState property → remoteCapabilities key listing its allowed values. */
export const LIST_PROPERTIES: Record<ListProperty, keyof Omit<ModeCapabilities, 'temperatures'>> = {
  fanLevel: 'fanLevels',
  swing: 'swing',
  horizontalSwing: 'horizontalSwing',
  light: 'light',
};

const DEFAULT_TEMPERATURE: Record<string, number> = { C: 21, F: 70 };

export function availableModes(device: Pod | null | undefined): string[] {
  return Object.keys(device?.remoteCapabilities?.modes ?? {});
}

export function modeCapabilities(device: Pod | null | undefined, mode: string | undefined): ModeCapabilities | undefined {
  return mode === undefined ? undefined : device?.remoteCapabilities?.modes?.[mode];
}

export function temperatureUnit(device: Pod | null | undefined): string {
  return device?.temperatureUnit ?? device?.acState?.temperatureUnit ?? 'C';
}

/** The unit the target temperature is set in. */
export function acUnit(device: Pod): string {
  return device.acState?.temperatureUnit ?? temperatureUnit(device);
}

export function temperatureOptions(device: Pod | null | undefined, mode: string | undefined, unit: string): number[] {
  return modeCapabilities(device, mode)?.temperatures?.[unit]?.values ?? [];
}

function nearest(values: number[], target: number): number {
  return values.reduce((best, value) => (Math.abs(value - target) < Math.abs(best - target) ? value : best));
}

/**
 * Builds the full acState to POST after applying `changes` to the device's current state.
 * Sensibo rejects values the remote does not support, so the result is fitted to the target mode:
 * the temperature snaps to the nearest allowed value and unsupported properties are dropped.
 */
export function buildAcState(device: Pod | null | undefined, changes: Partial<AcState>): AcState {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars -- the timestamp is Sensibo's, not part of a command
  const { timestamp, ...current } = device?.acState ?? {};
  const next: AcState = { ...current, ...changes };
  const capabilities = modeCapabilities(device, next.mode);
  if (!capabilities) return next;

  const unit = next.temperatureUnit ?? temperatureUnit(device);
  const temperatures = capabilities.temperatures?.[unit]?.values ?? [];
  if (temperatures.length > 0) {
    next.targetTemperature = nearest(temperatures, next.targetTemperature ?? DEFAULT_TEMPERATURE[unit] ?? temperatures[0]);
    next.temperatureUnit = unit;
  } else {
    delete next.targetTemperature;
    delete next.temperatureUnit;
  }

  for (const [property, capabilityKey] of Object.entries(LIST_PROPERTIES) as [ListProperty, keyof typeof capabilities][]) {
    const options = (capabilities[capabilityKey] as string[] | undefined) ?? [];
    if (options.length === 0) {
      delete next[property];
    } else if (!options.includes(next[property] ?? '')) {
      next[property] = property === 'fanLevel' && options.includes('auto') ? 'auto' : options[0];
    }
  }
  return next;
}

/** Steps `delta` places through the ascending allowed `values`, clamping at both ends. */
export function stepTemperature(values: number[], current: number, delta: number): number {
  if (values.length === 0) return current;
  const index = values.indexOf(current);
  if (index >= 0) return values[Math.min(values.length - 1, Math.max(0, index + delta))];
  const candidates = delta > 0 ? values.filter((value) => value > current) : values.filter((value) => value < current).reverse();
  return candidates[0] ?? nearest(values, current);
}

interface RawHistory {
  temperature?: { time: string; value: number }[] | null;
  humidity?: { time: string; value: number }[] | null;
}

export function normalizeHistory(result: RawHistory | null | undefined): History {
  const series = (points: RawHistory['temperature']) =>
    (points ?? []).map((point) => ({ time: Date.parse(point.time), value: point.value })).sort((a, b) => a.time - b.time);
  return { temperature: series(result?.temperature), humidity: series(result?.humidity) };
}

const RANGE_LABELS: Record<number, string> = { 1: 'Døgn', 7: 'Uge', 30: 'Måned', 365: 'År' };

/** Sensibo itself only keeps 24 hours; longer ranges need the server to have recorded measurements. */
export function historyRanges(recorded: boolean): number[] {
  return recorded ? [1, 7, 30, 365] : [1];
}

export function rangeLabel(days: number): string {
  return RANGE_LABELS[days] ?? `${days} dage`;
}

/** Spacing between rows of the history table, so each range gives a readable number of rows. */
export function tableStepMs(days: number): number {
  const hour = 3_600_000;
  if (days <= 1) return hour;
  if (days <= 7) return 6 * hour;
  if (days <= 31) return 24 * hour;
  return 7 * 24 * hour;
}

/** One reading per table step, newest first, with the humidity measured at the same time. */
export function tableRows(history: History, days: number): { time: number; temperature: number; humidity: number | null }[] {
  const stepMs = tableStepMs(days);
  const humidityAt = new Map(history.humidity.map((point) => [point.time, point.value]));
  const rows: { time: number; temperature: number; humidity: number | null }[] = [];
  let nextBoundary = -Infinity;
  for (const point of history.temperature) {
    if (point.time < nextBoundary) continue;
    rows.push({ time: point.time, temperature: point.value, humidity: humidityAt.get(point.time) ?? null });
    nextBoundary = Math.floor(point.time / stepMs) * stepMs + stepMs;
  }
  return rows.reverse();
}

/** Sensibo reports measurements in Celsius regardless of the device's display unit. */
export function convertTemperature<T extends number | null | undefined>(celsius: T, unit: string): T {
  if (celsius === null || celsius === undefined) return celsius;
  return (unit === 'F' ? (celsius * 9) / 5 + 32 : celsius) as T;
}

/** 'rangeFull' → 'Range full', 'medium_high' → 'Medium high'. The fallback for values without a Danish name. */
export function humanize(value: string): string {
  const words = String(value)
    .replace(/_/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

const LABELS: Record<string, string> = {
  // Modes
  cool: 'Køl',
  heat: 'Varme',
  fan: 'Ventilator',
  dry: 'Affugt',
  auto: 'Auto',
  // Fan levels
  quiet: 'Stille',
  low: 'Lav',
  medium_low: 'Mellem-lav',
  medium: 'Mellem',
  medium_high: 'Mellem-høj',
  high: 'Høj',
  strong: 'Kraftig',
  // Swing
  stopped: 'Stoppet',
  rangeFull: 'Fuldt udsving',
  rangeTop: 'Udsving øverst',
  rangeMiddle: 'Udsving midt',
  rangeBottom: 'Udsving nederst',
  rangeCenter: 'Udsving midt',
  fixedTop: 'Fast øverst',
  fixedMiddleTop: 'Fast midt-øverst',
  fixedMiddle: 'Fast midt',
  fixedMiddleBottom: 'Fast midt-nederst',
  fixedBottom: 'Fast nederst',
  fixedLeft: 'Fast venstre',
  fixedCenterLeft: 'Fast midt-venstre',
  fixedCenter: 'Fast midt',
  fixedCenterRight: 'Fast midt-højre',
  fixedRight: 'Fast højre',
  fixedLeftRight: 'Fast venstre og højre',
  horizontal: 'Vandret',
  both: 'Begge',
  // Light
  on: 'Tændt',
  off: 'Slukket',
  dim: 'Dæmpet',
};

/** The Danish name of a Sensibo mode, fan level, swing or light value. */
export function label(value: string): string {
  return LABELS[value] ?? humanize(value);
}

/** "slukket", "varme 21 °C". */
export function describeState(acState: AcState | null | undefined): string {
  if (!acState || acState.on === false) return 'slukket';
  const unit = acState.temperatureUnit === 'F' ? '°F' : '°C';
  const temperature = acState.targetTemperature === undefined ? '' : ` ${acState.targetTemperature} ${unit}`;
  return `${label(acState.mode ?? 'on').toLowerCase()}${temperature}`;
}

/** "lige nu", "for 2 min. siden". */
export function formatAge(secondsAgo: number | null | undefined): string {
  if (secondsAgo === null || secondsAgo === undefined || Number.isNaN(secondsAgo)) return '';
  if (secondsAgo < 60) return 'lige nu';
  if (secondsAgo < 3600) return `for ${Math.floor(secondsAgo / 60)} min. siden`;
  if (secondsAgo < 86400) return `for ${Math.floor(secondsAgo / 3600)} t. siden`;
  return `for ${Math.floor(secondsAgo / 86400)} døgn siden`;
}

/** The current measurements, preferring an external sensor when the device has one. */
export function measurements(device: Pod): Measurements {
  return device.mainMeasurementsSensor?.measurements ?? device.measurements ?? {};
}

/** When a timer fires. Sensibo has sent the target time both with and without a zone. */
export function timerDate(timer: PodTimer, loadedAt: number): Date | null {
  const iso = timer.targetTime ?? '';
  if (/(Z|[+-]\d\d:?\d\d)$/.test(iso)) return new Date(iso);
  if (typeof timer.targetTimeSecondsFromNow === 'number') return new Date(loadedAt + timer.targetTimeSecondsFromNow * 1000);
  return iso ? new Date(`${iso}Z`) : null;
}
