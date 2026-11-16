import { describe, expect, it } from 'vitest';
import { ApiError, errorText } from './api';
import {
  availableModes,
  buildAcState,
  convertTemperature,
  describeState,
  formatAge,
  historyRanges,
  humanize,
  label,
  normalizeHistory,
  rangeLabel,
  stepTemperature,
  tableRows,
  tableStepMs,
  temperatureOptions,
  temperatureUnit,
  timerDate,
  type Pod,
} from './sensibo';

const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);

const device: Pod = {
  id: 'abc',
  temperatureUnit: 'C',
  acState: {
    timestamp: { time: '2026-10-22T10:00:00Z', secondsAgo: 5 },
    on: true,
    mode: 'heat',
    targetTemperature: 22,
    temperatureUnit: 'C',
    fanLevel: 'medium',
    swing: 'rangeFull',
    light: 'on',
  },
  remoteCapabilities: {
    modes: {
      heat: {
        temperatures: { C: { isNative: true, values: range(10, 30) }, F: { isNative: false, values: range(50, 86) } },
        fanLevels: ['quiet', 'low', 'medium', 'high', 'auto'],
        swing: ['stopped', 'rangeFull'],
        light: ['on', 'off'],
      },
      cool: {
        temperatures: { C: { isNative: true, values: range(17, 30) } },
        fanLevels: ['low', 'high', 'auto'],
        swing: ['stopped', 'rangeFull'],
        horizontalSwing: ['stopped', 'rangeFull'],
      },
      fan: {
        fanLevels: ['low', 'high'],
      },
    },
  },
};

describe('device capabilities', () => {
  it('lists the modes from remote capabilities', () => {
    expect(availableModes(device)).toEqual(['heat', 'cool', 'fan']);
    expect(availableModes({ id: 'x' })).toEqual([]);
  });

  it('prefers the device unit, then the AC state, then Celsius', () => {
    expect(temperatureUnit({ id: 'x', temperatureUnit: 'F', acState: { temperatureUnit: 'C' } })).toBe('F');
    expect(temperatureUnit({ id: 'x', acState: { temperatureUnit: 'F' } })).toBe('F');
    expect(temperatureUnit({ id: 'x' })).toBe('C');
  });

  it('returns the allowed temperatures for a mode and unit', () => {
    expect(temperatureOptions(device, 'cool', 'C')).toEqual(range(17, 30));
    expect(temperatureOptions(device, 'fan', 'C')).toEqual([]);
    expect(temperatureOptions(device, 'unknown', 'C')).toEqual([]);
  });
});

describe('buildAcState', () => {
  it('merges a change into the current state without the timestamp', () => {
    expect(buildAcState(device, { on: false })).toEqual({
      on: false,
      mode: 'heat',
      targetTemperature: 22,
      temperatureUnit: 'C',
      fanLevel: 'medium',
      swing: 'rangeFull',
      light: 'on',
    });
  });

  it('snaps the target temperature into the new mode range', () => {
    const cold = { ...device, acState: { ...device.acState, targetTemperature: 12 } };
    expect(buildAcState(cold, { mode: 'cool' }).targetTemperature).toBe(17);
  });

  it('drops properties the new mode does not support', () => {
    const state = buildAcState(device, { mode: 'fan' });
    expect(state.targetTemperature).toBeUndefined();
    expect(state.temperatureUnit).toBeUndefined();
    expect(state.swing).toBeUndefined();
    expect(state.light).toBeUndefined();
    expect(state.mode).toBe('fan');
  });

  it('falls back to a supported fan level, preferring auto', () => {
    const quiet = { ...device, acState: { ...device.acState, fanLevel: 'quiet' } };
    expect(buildAcState(quiet, { mode: 'cool' }).fanLevel).toBe('auto');
    expect(buildAcState(device, { mode: 'fan' }).fanLevel).toBe('low');
  });

  it('gives a temperature when leaving a mode without one', () => {
    const fanning = { ...device, acState: { on: true, mode: 'fan', fanLevel: 'low' } };
    const state = buildAcState(fanning, { mode: 'heat' });
    expect(state.targetTemperature).toBe(21);
    expect(state.temperatureUnit).toBe('C');
  });

  it('adds newly supported list properties with their first option', () => {
    expect(buildAcState(device, { mode: 'cool' }).horizontalSwing).toBe('stopped');
  });

  it('passes changes through when the mode has no known capabilities', () => {
    const bare: Pod = { id: 'x', acState: { on: false, mode: 'heat', targetTemperature: 40 } };
    expect(buildAcState(bare, { on: true })).toEqual({ on: true, mode: 'heat', targetTemperature: 40 });
  });
});

describe('history and temperatures', () => {
  it('steps through allowed values and clamps at the ends', () => {
    const values = [16, 18, 20, 22];
    expect(stepTemperature(values, 18, 1)).toBe(20);
    expect(stepTemperature(values, 18, -1)).toBe(16);
    expect(stepTemperature(values, 22, 1)).toBe(22);
    expect(stepTemperature(values, 16, -1)).toBe(16);
    expect(stepTemperature(values, 19, 1)).toBe(20);
    expect(stepTemperature([], 19, 1)).toBe(19);
  });

  it('converts series to sorted time/value points', () => {
    const history = normalizeHistory({
      temperature: [
        { time: '2026-10-22T10:05:00Z', value: 21.5 },
        { time: '2026-10-22T10:00:00Z', value: 21.0 },
      ],
      humidity: [{ time: '2026-10-22T10:00:00Z', value: 45 }],
    });
    expect(history.temperature).toEqual([
      { time: Date.parse('2026-10-22T10:00:00Z'), value: 21.0 },
      { time: Date.parse('2026-10-22T10:05:00Z'), value: 21.5 },
    ]);
    expect(history.humidity).toEqual([{ time: Date.parse('2026-10-22T10:00:00Z'), value: 45 }]);
    expect(normalizeHistory(null)).toEqual({ temperature: [], humidity: [] });
  });

  it('turns Sensibo Celsius readings into the display unit', () => {
    expect(convertTemperature(20, 'C')).toBe(20);
    expect(convertTemperature(20, 'F')).toBe(68);
    expect(convertTemperature(undefined, 'F')).toBeUndefined();
    expect(convertTemperature(null, 'F')).toBeNull();
  });

  it('offers long ranges only when the server has recorded measurements', () => {
    expect(historyRanges(false)).toEqual([1]);
    expect(historyRanges(true)).toEqual([1, 7, 30, 365]);
    expect([1, 7, 30, 365].map(rangeLabel)).toEqual(['Døgn', 'Uge', 'Måned', 'År']);
  });

  it('spaces table rows to suit the range, newest first', () => {
    const hour = 3_600_000;
    expect([1, 7, 30, 365].map(tableStepMs)).toEqual([hour, 6 * hour, 24 * hour, 7 * 24 * hour]);
    const at = (minutes: number) => Date.UTC(2026, 9, 22, 10) + minutes * 60_000;
    const rows = tableRows(
      {
        temperature: [{ time: at(0), value: 20 }, { time: at(30), value: 21 }, { time: at(60), value: 22 }],
        humidity: [{ time: at(60), value: 40 }],
      },
      1,
    );
    expect(rows).toEqual([
      { time: at(60), temperature: 22, humidity: 40 },
      { time: at(0), temperature: 20, humidity: null },
    ]);
  });

  it('reads a timer target time with or without a zone', () => {
    expect(timerDate({ targetTime: '2026-10-22T10:00:00Z' }, 0)?.toISOString()).toBe('2026-10-22T10:00:00.000Z');
    expect(timerDate({ targetTime: '2026-10-22T10:00:00' }, 0)?.toISOString()).toBe('2026-10-22T10:00:00.000Z');
    expect(timerDate({ targetTimeSecondsFromNow: 60 }, Date.UTC(2026, 9, 22, 10))?.toISOString()).toBe('2026-10-22T10:01:00.000Z');
    expect(timerDate({}, 0)).toBeNull();
  });
});

describe('Danish labels', () => {
  it('names modes and fan levels in Danish', () => {
    expect(['cool', 'heat', 'fan', 'dry', 'auto'].map(label)).toEqual(['Køl', 'Varme', 'Ventilator', 'Affugt', 'Auto']);
    expect(label('medium_high')).toBe('Mellem-høj');
    expect(label('rangeFull')).toBe('Fuldt udsving');
  });

  it('falls back to a readable form of values without a Danish name', () => {
    expect(humanize('heat')).toBe('Heat');
    expect(humanize('rangeFull')).toBe('Range full');
    expect(humanize('medium_high')).toBe('Medium high');
    expect(label('turboBoost')).toBe('Turbo boost');
  });

  it('describes an AC state in words', () => {
    expect(describeState({ on: true, mode: 'heat', targetTemperature: 21, temperatureUnit: 'C' })).toBe('varme 21 °C');
    expect(describeState({ on: false, mode: 'heat' })).toBe('slukket');
    expect(describeState(null)).toBe('slukket');
  });

  it('describes how long ago something happened', () => {
    expect(formatAge(20)).toBe('lige nu');
    expect(formatAge(150)).toBe('for 2 min. siden');
    expect(formatAge(7200)).toBe('for 2 t. siden');
    expect(formatAge(200000)).toBe('for 2 døgn siden');
    expect(formatAge(undefined)).toBe('');
  });

  it('explains Sensibo failures without sending the person to sign in', () => {
    expect(errorText(new ApiError(502, 'sensibo_key_rejected'))).toContain('API-nøglen');
    expect(errorText(new ApiError(429, 'sensibo_rate_limited'))).toContain('Vent et minut');
    expect(errorText(new ApiError(502, 'sensibo_unreachable'))).toContain('forbindelse til Sensibo');
    expect(errorText(new ApiError(504, 'sensibo_timeout'))).toContain('i tide');
    expect(errorText(new ApiError(503, 'sensibo_not_configured'))).toContain('Sensibo API-nøgle');
    expect(errorText(new ApiError(400, 'sensibo_bad_request'))).toContain('afviste værdien');
    expect(errorText(new ApiError(409, 'collect_running'))).toContain('allerede');
    expect(errorText(new ApiError(400, 'invalid_device'))).toContain('varmepumpe');
    expect(errorText(new ApiError(400, 'invalid_days'))).toBe('Vælg et gyldigt antal dage.');
  });
});
