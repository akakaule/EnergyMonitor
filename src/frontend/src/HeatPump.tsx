import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { api, errorText, type Me } from './api';
import { ErrorNote, Switch, useLoad, usePolling } from './common';
import { celsius, clock, fullDate, humidity, measuredAt, number } from './format';
import {
  acUnit,
  availableModes,
  buildAcState,
  convertTemperature,
  describeState,
  formatAge,
  historyRanges,
  label,
  LIST_PROPERTIES,
  measurements,
  modeCapabilities,
  normalizeHistory,
  rangeLabel,
  stepTemperature,
  tableRows,
  temperatureOptions,
  temperatureUnit,
  timerDate,
  type AcState,
  type ListProperty,
  type Pod,
} from './sensibo';
import { LineChart, pointTime } from './LineChart';

const REFRESH_MS = 60_000;
const HISTORY_REFRESH_MS = 10 * 60_000;
const TEMPERATURE_DEBOUNCE_MS = 800;
const TIMER_MINUTES = [15, 30, 60, 120, 240, 480];
const DEVICE_KEY = 'energymonitor.heatpump.device';

const LIST_LABELS: Record<ListProperty, string> = {
  fanLevel: 'Blæser',
  swing: 'Lodret sving',
  horizontalSwing: 'Vandret sving',
  light: 'Displaylys',
};

/** A temperature in the device's unit; Danish devices use Celsius. */
function degrees(value: number | null | undefined, unit: string, decimals = 1): string {
  if (unit !== 'F' || value === null || value === undefined) return celsius(value, decimals);
  return `${number(value, decimals)} °F`;
}

/** A target temperature, which is usually a whole degree. */
function target(value: number, unit: string): string {
  return degrees(value, unit, Number.isInteger(value) ? 0 : 1);
}

function duration(minutes: number): string {
  if (minutes < 60) return `${minutes} min.`;
  const hours = minutes / 60;
  return hours === 1 ? '1 time' : `${number(hours, Number.isInteger(hours) ? 0 : 1)} timer`;
}

function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage can be unavailable (private mode); the choice then lasts for this visit.
  }
}

/** Online or offline is always an icon and a word, never colour alone. */
export function OnlineBadge({ online, detail }: { online: boolean; detail?: string }) {
  return (
    <span className={online ? 'badge badge-normal' : 'badge badge-alarm'}>
      <span className="badge-icon" aria-hidden="true">{online ? '✓' : '×'}</span>
      {online ? 'Online' : 'Offline'}
      {detail ? ` · ${detail}` : ''}
    </span>
  );
}

class Rejected extends Error {}

export function HeatPump({ me }: { me: Me }) {
  const admin = me.role === 'admin';
  const status = useLoad(() => api.heatpump(), 'heatpump');
  const [pods, setPods] = useState<Pod[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadedAt, setLoadedAt] = useState(0);
  const [chosen, setChosen] = useState<string | null>(() => readStorage(DEVICE_KEY));
  const [notice, setNotice] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(0);
  const [pendingTarget, setPendingTarget] = useState<number | null>(null);
  const [timerMinutes, setTimerMinutes] = useState(60);
  const timerTitle = useId();

  // Commands run one at a time, and a refresh waits for them so it cannot overwrite an optimistic state.
  const busy = useRef(0);
  const pending = useRef<number | null>(null);
  const queue = useRef<Promise<void>>(Promise.resolve());
  const debounce = useRef<number | undefined>(undefined);
  const current = useRef<Pod | null>(null);

  const load = useCallback((): Promise<void> => {
    const blocked = () => busy.current > 0 || pending.current !== null;
    if (blocked()) return Promise.resolve();
    return api.pods().then(
      (list) => {
        if (blocked()) return;
        setPods(list);
        setLoadedAt(Date.now());
        setLoadError(null);
      },
      (e: unknown) => setLoadError(errorText(e)),
    );
  }, []);

  useEffect(() => {
    void load();
  }, [load]);
  usePolling(load, REFRESH_MS);

  const device = pods?.find((p) => p.id === chosen) ?? pods?.[0] ?? null;
  useEffect(() => {
    current.current = device;
  });

  const updatePod = (id: string, change: (pod: Pod) => Pod) =>
    setPods((list) => list?.map((p) => (p.id === id ? change(p) : p)) ?? list);

  const send = (action: () => Promise<void>) => {
    busy.current++;
    setInFlight((n) => n + 1);
    setNotice(null);
    const run = queue.current
      .then(action)
      .catch((e: unknown) => setNotice(e instanceof Rejected ? e.message : errorText(e)))
      .finally(() => {
        busy.current--;
        setInFlight((n) => n - 1);
      });
    queue.current = run;
    return run;
  };

  const changeAcState = (pod: Pod, changes: Partial<AcState>) => {
    const previous = pod.acState ?? null;
    const next = buildAcState(pod, changes);
    updatePod(pod.id, (p) => ({ ...p, acState: { ...next, timestamp: previous?.timestamp } }));
    void send(async () => {
      try {
        const result = await api.setAcState(pod.id, next);
        if (result?.status === 'Failed') {
          throw new Rejected(`Varmepumpen tog ikke imod ændringen${result.failureReason ? `: ${result.failureReason}` : '.'}`);
        }
        if (result?.acState) updatePod(pod.id, (p) => ({ ...p, acState: result.acState }));
      } catch (e) {
        updatePod(pod.id, (p) => ({ ...p, acState: previous }));
        throw e;
      }
    });
  };

  // Several taps on + or − become one command, sent when the taps stop.
  const stepTarget = (pod: Pod, delta: number) => {
    const ac = pod.acState;
    if (ac?.targetTemperature === undefined) return;
    const values = temperatureOptions(pod, ac.mode, acUnit(pod));
    const from = pending.current ?? ac.targetTemperature;
    const next = values.length > 0 ? stepTemperature(values, from, delta) : from + delta;
    pending.current = next;
    setPendingTarget(next);
    window.clearTimeout(debounce.current);
    debounce.current = window.setTimeout(() => {
      pending.current = null;
      setPendingTarget(null);
      const latest = current.current?.id === pod.id ? current.current : pod;
      if (next !== latest.acState?.targetTemperature) changeAcState(latest, { targetTemperature: next });
    }, TEMPERATURE_DEBOUNCE_MS);
  };

  const setTimer = (pod: Pod, minutes: number) => {
    const acState = buildAcState(pod, { on: !pod.acState?.on });
    void send(async () => {
      await api.setTimer(pod.id, minutes, acState);
      const targetTime = new Date(Date.now() + minutes * 60_000).toISOString();
      updatePod(pod.id, (p) => ({ ...p, timer: { isEnabled: true, acState, targetTime } }));
    }).then(load);
  };

  const cancelTimer = (pod: Pod) => {
    void send(async () => {
      await api.cancelTimer(pod.id);
      updatePod(pod.id, (p) => ({ ...p, timer: null }));
    }).then(load);
  };

  const setClimateReact = (pod: Pod, enabled: boolean) => {
    const toggle = (on: boolean) => (p: Pod) => (p.smartMode ? { ...p, smartMode: { ...p.smartMode, enabled: on } } : p);
    updatePod(pod.id, toggle(enabled));
    void send(async () => {
      try {
        await api.setClimateReact(pod.id, enabled);
      } catch (e) {
        updatePod(pod.id, toggle(!enabled));
        throw e;
      }
    });
  };

  const notConfigured = status.data?.configured === false;
  const head = (
    <div className="page-head">
      <h1>Varmepumpe</h1>
      {pods && pods.length > 1 && device && (
        <select
          aria-label="Vælg varmepumpe"
          value={device.id}
          onChange={(e) => {
            setChosen(e.target.value);
            writeStorage(DEVICE_KEY, e.target.value);
          }}
        >
          {pods.map((p) => (
            <option key={p.id} value={p.id}>{p.room?.name ?? p.id}</option>
          ))}
        </select>
      )}
    </div>
  );

  if (notConfigured) {
    return (
      <>
        {head}
        <section className="card">
          <h2>Varmepumpen er ikke sat op</h2>
          <p className="muted">Serveren mangler en Sensibo API-nøgle (Sensibo:ApiKey). Når den er sat, vises varmepumpen her.</p>
        </section>
      </>
    );
  }

  if (!device) {
    return (
      <>
        {head}
        {loadError ? (
          <ErrorNote message={loadError} onRetry={() => void load()} />
        ) : pods ? (
          <p className="empty">Sensibo-kontoen har ingen varmepumper.</p>
        ) : (
          <p className="muted">Henter…</p>
        )}
      </>
    );
  }

  const unit = temperatureUnit(device);
  const now = measurements(device);
  const connection = device.connectionStatus ?? {};
  const online = connection.isAlive !== false;
  const ac = device.acState ?? {};
  const capabilities = modeCapabilities(device, ac.mode);
  const modes = availableModes(device);
  const shownTarget = pendingTarget ?? ac.targetTemperature;
  const targets = temperatureOptions(device, ac.mode, acUnit(device));
  const timer = device.timer?.isEnabled ? device.timer : null;
  const timerAt = timer ? timerDate(timer, loadedAt) : null;
  const smart = device.smartMode ?? null;

  const listOptions = (property: ListProperty): string[] =>
    capabilities?.[LIST_PROPERTIES[property]] ?? (capabilities ? [] : [ac[property]].filter((v): v is string => Boolean(v)));

  const threshold = (value: number) => (smart?.type === 'humidity' ? humidity(value) : degrees(convertTemperature(value, unit), unit));
  const climate = smart
    ? [
        smart.lowTemperatureThreshold != null && `Under ${threshold(smart.lowTemperatureThreshold)}: ${describeState(smart.lowTemperatureState)}`,
        smart.highTemperatureThreshold != null && `Over ${threshold(smart.highTemperatureThreshold)}: ${describeState(smart.highTemperatureState)}`,
      ]
        .filter(Boolean)
        .join(' · ') || 'Opsat i Sensibo-appen.'
    : '';

  return (
    <>
      {head}
      {loadError && <ErrorNote message={loadError} onRetry={() => void load()} />}
      {notice && <p className="note note-error" role="alert">{notice}</p>}

      <section className="card">
        <div className="card-head">
          <h2>{device.room?.name ?? 'Varmepumpe'}</h2>
          <OnlineBadge online={online} detail={!online && connection.lastSeen ? `sidst set ${formatAge(connection.lastSeen.secondsAgo)}` : undefined} />
        </div>
        <p className="hero">{degrees(convertTemperature(now.temperature, unit), unit)}</p>
        <p className="card-date">Indetemperatur · målt {measuredAt(now.time?.time)}</p>
        <dl className="stats">
          <div><dt>Luftfugtighed</dt><dd>{humidity(now.humidity)}</dd></div>
          {now.feelsLike != null && <div><dt>Føles som</dt><dd>{degrees(convertTemperature(now.feelsLike, unit), unit)}</dd></div>}
          <div><dt>Drift</dt><dd>{ac.on ? 'Tændt' : 'Slukket'}</dd></div>
        </dl>
      </section>

      {admin ? (
        <section className={ac.on ? 'card hp-controls' : 'card hp-controls is-off'} aria-label="Betjening">
          <div className="power-row">
            <button type="button" className="power" aria-pressed={Boolean(ac.on)} onClick={() => changeAcState(device, { on: !ac.on })}>
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M12 3v9M6.3 6.3a8 8 0 1 0 11.4 0" />
              </svg>
              {ac.on ? 'Tændt' : 'Slukket'}
            </button>
            {shownTarget !== undefined && (
              <div className="target" role="group" aria-label="Ønsket temperatur">
                <button
                  type="button"
                  className="step"
                  aria-label="Lavere temperatur"
                  disabled={targets.length > 0 && shownTarget <= targets[0]}
                  onClick={() => stepTarget(device, -1)}
                >
                  −
                </button>
                <output className="target-value" aria-live="polite">{target(shownTarget, acUnit(device))}</output>
                <button
                  type="button"
                  className="step"
                  aria-label="Højere temperatur"
                  disabled={targets.length > 0 && shownTarget >= targets[targets.length - 1]}
                  onClick={() => stepTarget(device, 1)}
                >
                  +
                </button>
              </div>
            )}
          </div>

          {modes.length > 0 && (
            <div className="chips" role="group" aria-label="Driftsform">
              {modes.map((mode) => (
                <button
                  key={mode}
                  type="button"
                  className={ac.mode === mode ? 'on' : ''}
                  aria-pressed={ac.mode === mode}
                  onClick={() => ac.mode !== mode && changeAcState(device, { mode })}
                >
                  {label(mode)}
                </button>
              ))}
            </div>
          )}

          <div className="fields">
            {(Object.keys(LIST_PROPERTIES) as ListProperty[]).map((property) => {
              const options = listOptions(property);
              if (options.length === 0) return null;
              return (
                <label key={property} className="field">
                  {LIST_LABELS[property]}
                  <select value={ac[property] ?? ''} onChange={(e) => changeAcState(device, { [property]: e.target.value })}>
                    {options.map((option) => (
                      <option key={option} value={option}>{label(option)}</option>
                    ))}
                  </select>
                </label>
              );
            })}
          </div>
          {inFlight > 0 && <p className="muted small" role="status">Sender til varmepumpen…</p>}
        </section>
      ) : (
        <section className="card">
          <h2>Betjening</h2>
          <p>Varmepumpen kører: {describeState(ac)}.</p>
          <p className="muted small">Kun administratorer kan betjene varmepumpen.</p>
        </section>
      )}

      <section className="card" aria-labelledby={timerTitle}>
        <h2 id={timerTitle}>Timer</h2>
        {timer && timerAt ? (
          <div className="hp-row">
            <span>
              {timer.acState?.on === false ? 'Slukker' : 'Tænder'} kl. {clock(timerAt.getTime())}
              {loadedAt > 0 && ` (om ${duration(Math.max(0, Math.round((timerAt.getTime() - loadedAt) / 60_000)))})`}
            </span>
            {admin && (
              <button type="button" className="button button-secondary" onClick={() => cancelTimer(device)}>
                Annullér timer
              </button>
            )}
          </div>
        ) : admin ? (
          <form
            className="hp-row"
            onSubmit={(e) => {
              e.preventDefault();
              setTimer(device, timerMinutes);
            }}
          >
            <label className="hp-row">
              {ac.on ? 'Sluk' : 'Tænd'}
              <select value={timerMinutes} onChange={(e) => setTimerMinutes(Number(e.target.value))}>
                {TIMER_MINUTES.map((m) => (
                  <option key={m} value={m}>om {duration(m)}</option>
                ))}
              </select>
            </label>
            <button type="submit" className="button button-secondary">Sæt timer</button>
          </form>
        ) : (
          <p className="muted">Ingen timer.</p>
        )}
      </section>

      {smart && (
        <section className="card">
          <div className="card-head">
            <h2>Climate React</h2>
            {admin ? (
              <Switch checked={smart.enabled} onChange={(enabled) => setClimateReact(device, enabled)}>Aktiv</Switch>
            ) : (
              <span>{smart.enabled ? 'Aktiv' : 'Slået fra'}</span>
            )}
          </div>
          <p className="muted">{climate}</p>
        </section>
      )}

      <HeatPumpHistory key={device.id} deviceId={device.id} unit={unit} recordedSince={status.data?.oldestUtc ?? null} />
    </>
  );
}

function HeatPumpHistory({ deviceId, unit, recordedSince }: { deviceId: string; unit: string; recordedSince: string | null }) {
  const ranges = historyRanges(recordedSince !== null);
  const [chosen, setChosen] = useState(1);
  const days = ranges.includes(chosen) ? chosen : 1;
  // Sensibo keeps 24 hours; longer ranges come from what the server recorded.
  const history = useLoad(
    async () => ({
      days,
      history: normalizeHistory(days === 1 ? await api.liveHistory(deviceId) : await api.heatpumpHistory(deviceId, days)),
    }),
    `heatpump-history|${deviceId}|${days}`,
  );
  usePolling(history.reload, HISTORY_REFRESH_MS);
  const [hover, setHover] = useState<number | null>(null);
  const [table, setTable] = useState(false);

  const loaded = history.data;
  const shownDays = loaded?.days ?? days;
  const temperature = (loaded?.history.temperature ?? []).map((p) => ({ ...p, value: convertTemperature(p.value, unit) }));
  const humidityPoints = loaded?.history.humidity ?? [];
  const empty = loaded !== null && temperature.length === 0 && humidityPoints.length === 0;

  return (
    <section className="card">
      <div className="chart-head">
        <h2>Historik</h2>
        {ranges.length > 1 && (
          <div className="segmented" role="group" aria-label="Periode">
            {ranges.map((d) => (
              <button key={d} type="button" className={d === days ? 'on' : ''} aria-pressed={d === days} onClick={() => setChosen(d)}>
                {rangeLabel(d)}
              </button>
            ))}
          </div>
        )}
      </div>
      <p className="muted small">
        {recordedSince
          ? `Serveren har registreret målinger siden ${fullDate(new Date(recordedSince).getTime())}.`
          : 'Sensibo gemmer kun det seneste døgn. Længere perioder kan vises, når serveren har registreret målinger.'}
      </p>
      {history.error && <ErrorNote message={history.error} onRetry={history.reload} />}
      {!loaded && !history.error && <p className="muted">Henter…</p>}
      {empty && <p className="empty">Ingen målinger i perioden.</p>}
      {loaded && !empty && (
        <>
          <div className="line-charts">
            <LineChart
              points={temperature}
              days={shownDays}
              title="Temperatur"
              series="temperature"
              format={(v) => degrees(v, unit)}
              hoverTime={hover}
              onHover={setHover}
            />
            <LineChart points={humidityPoints} days={shownDays} title="Luftfugtighed" series="humidity" format={humidity} hoverTime={hover} onHover={setHover} />
          </div>
          <button type="button" className="link" onClick={() => setTable((t) => !t)}>
            {table ? 'Skjul tabel' : 'Vis målinger som tabel'}
          </button>
          {table && (
            <div className="table-scroll">
              <table className="table">
                <thead>
                  <tr><th>Tidspunkt</th><th className="num">Temperatur</th><th className="num">Luftfugtighed</th></tr>
                </thead>
                <tbody>
                  {tableRows({ temperature, humidity: humidityPoints }, shownDays).map((row) => (
                    <tr key={row.time}>
                      <td>{pointTime(row.time, shownDays)}</td>
                      <td className="num">{degrees(row.temperature, unit)}</td>
                      <td className="num">{humidity(row.humidity)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}
