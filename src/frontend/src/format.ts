// Formatting and calendar helpers. Days are Danish calendar days ("YYYY-MM-DD"); only display and
// "what is today" depend on the Europe/Copenhagen time zone, never on the device's own zone.

export const ZONE = 'Europe/Copenhagen';

const WEEKDAYS = ['søn.', 'man.', 'tirs.', 'ons.', 'tors.', 'fre.', 'lør.'];
const MONTHS = ['jan.', 'feb.', 'mar.', 'apr.', 'maj', 'jun.', 'jul.', 'aug.', 'sep.', 'okt.', 'nov.', 'dec.'];

export function number(value: number, decimals = 1): string {
  return value.toFixed(decimals).replace('.', ',').replace('-', '−');
}

export function kWh(value: number | null | undefined, decimals = 1): string {
  // A missing value is shown as a dash. It is never shown as 0.
  return value === null || value === undefined ? '–' : `${number(value, decimals)} kWh`;
}

export function percent(value: number): string {
  const rounded = Math.round(value);
  return `${rounded > 0 ? '+' : rounded < 0 ? '−' : ''}${Math.abs(rounded)} %`;
}

function parts(date: string): [number, number, number] {
  const [y, m, d] = date.split('-').map(Number);
  return [y, m, d];
}

export function shortDate(date: string): string {
  const [, m, d] = parts(date);
  return `${d}/${m}`;
}

export function longDate(date: string): string {
  const [y, m, d] = parts(date);
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return `${WEEKDAYS[weekday]} ${d}. ${MONTHS[m - 1]}`;
}

export function addDays(date: string, days: number): string {
  const [y, m, d] = parts(date);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

export function daysBetween(from: string, to: string): number {
  const [y1, m1, d1] = parts(from);
  const [y2, m2, d2] = parts(to);
  return Math.round((Date.UTC(y2, m2 - 1, d2) - Date.UTC(y1, m1 - 1, d1)) / 86_400_000);
}

/** Today's date in Denmark, whatever time zone the device is set to. */
export function today(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

/** "8/11 kl. 08:05" in Danish time. */
export function dateTime(utc: string | null | undefined): string {
  if (!utc) return 'aldrig';
  const p = Object.fromEntries(
    new Intl.DateTimeFormat('en-GB', {
      timeZone: ZONE, day: 'numeric', month: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
    }).formatToParts(new Date(utc)).map((x) => [x.type, x.value]),
  );
  return `${Number(p.day)}/${Number(p.month)} kl. ${p.hour}:${p.minute}`;
}

/** Hour of day in Danish time, e.g. "07". */
export function hourLabel(utc: string): string {
  return new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, hour: '2-digit', hourCycle: 'h23' }).format(new Date(utc));
}

export function age(hours: number | null): string {
  if (hours === null) return 'ingen målinger';
  if (hours < 1) return 'under 1 time gammel';
  if (hours < 48) return `${Math.round(hours)} timer gammel`;
  return `${Math.round(hours / 24)} døgn gammel`;
}

export function dayRange(days: string[]): string {
  if (days.length === 0) return '';
  return days.length === 1 ? shortDate(days[0]) : `${shortDate(days[0])}–${shortDate(days[days.length - 1])}`;
}

/** "21,5 °C"; a missing reading is a dash. */
export function celsius(value: number | null | undefined, decimals = 1): string {
  return value === null || value === undefined ? '–' : `${number(value, decimals)} °C`;
}

/** Relative humidity, "45 %". */
export function humidity(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : `${Math.round(value)} %`;
}

export interface ZonedParts {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  weekday: number;
}

const zoned = new Intl.DateTimeFormat('en-GB', {
  timeZone: ZONE, year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});

/** The Danish wall-clock time of an instant. */
export function zonedParts(ms: number): ZonedParts {
  const p = Object.fromEntries(zoned.formatToParts(new Date(ms)).map((x) => [x.type, x.value]));
  const [year, month, day] = [Number(p.year), Number(p.month), Number(p.day)];
  return { year, month, day, hour: Number(p.hour), minute: Number(p.minute), weekday: new Date(Date.UTC(year, month - 1, day)).getUTCDay() };
}

/**
 * The instant of a Danish wall-clock time. Out-of-range fields roll over (day 32 is the 1st of the next month).
 * In the hour skipped when summer time starts, the result is the hour after.
 */
export function zonedToUtc(year: number, month: number, day: number, hour = 0, minute = 0): number {
  const wall = Date.UTC(year, month - 1, day, hour, minute);
  const offset = (at: number) => {
    const p = zonedParts(at);
    return Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute) - at;
  };
  const guess = wall - offset(wall);
  return wall - offset(guess);
}

const pad = (n: number) => String(n).padStart(2, '0');

/** "14:05" in Danish time. */
export function clock(ms: number): string {
  const p = zonedParts(ms);
  return `${pad(p.hour)}:${pad(p.minute)}`;
}

/** "8. nov. 2026" in Danish time. */
export function fullDate(ms: number): string {
  const p = zonedParts(ms);
  return `${p.day}. ${MONTHS[p.month - 1]} ${p.year}`;
}

/** Short Danish names for chart axes: "man.", "nov.". */
export function weekdayName(weekday: number): string {
  return WEEKDAYS[weekday];
}

export function monthName(month: number): string {
  return MONTHS[month - 1];
}

/** "kl. 14:05" for a time today, "8/11 kl. 14:05" for an earlier day. */
export function measuredAt(utc: string | null | undefined, now: Date = new Date()): string {
  if (!utc) return 'ukendt tidspunkt';
  const ms = new Date(utc).getTime();
  return today(new Date(ms)) === today(now) ? `kl. ${clock(ms)}` : dateTime(utc);
}
