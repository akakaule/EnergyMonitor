import { useState } from 'react';
import { PropertyCosts } from './Pricing';
import { api, type DayValue, type HeatPumpDevice } from './api';
import { BarChart, type Bar } from './BarChart';
import { AlertList, ErrorNote, StatusBadge, useLoad } from './common';
import { addDays, celsius, dateTime, daysBetween, hourLabel, humidity, kWh, longDate, measuredAt, number, percent, shortDate, today } from './format';
import { OnlineBadge } from './HeatPump';
import { label } from './sensibo';

type Mode = 'day' | 'week' | 'month' | 'custom';

const SPAN: Record<Exclude<Mode, 'custom'>, number> = { day: 1, week: 7, month: 30 };
const QUALITY: Record<DayValue['quality'], string> = { Complete: 'Komplet', Partial: 'Ufuldstændigt', Missing: 'Mangler' };

function toBar(d: DayValue): Bar {
  return {
    key: d.date,
    label: shortDate(d.date),
    title: longDate(d.date),
    value: d.quality === 'Missing' ? null : d.kWh,
    state: d.quality === 'Complete' ? 'complete' : d.quality === 'Partial' ? 'partial' : 'missing',
  };
}

function HourChart({ propertyId, day }: { propertyId: string; day: string }) {
  const hours = useLoad(() => api.hourly(propertyId, day), `${propertyId}|${day}`);
  if (hours.error) return <ErrorNote message={hours.error} onRetry={hours.reload} />;
  if (!hours.data) return <p className="muted">Henter…</p>;
  if (hours.data.items.length === 0) {
    return <p className="note note-info">Ingen timeværdier for {longDate(day)}: data mangler eller er endnu ikke modtaget.</p>;
  }

  const hourly = hours.data.items.every((h) => new Date(h.endUtc).getTime() - new Date(h.startUtc).getTime() <= 3_600_000);
  if (!hourly) {
    return <p className="note note-info">Målepunktet leverer kun døgnværdier for {longDate(day)}, så der vises ingen timegraf.</p>;
  }

  const bars: Bar[] = hours.data.items.map((h) => ({
    key: h.startUtc,
    label: hourLabel(h.startUtc),
    title: `${shortDate(day)} kl. ${hourLabel(h.startUtc)}–${hourLabel(h.endUtc)}`,
    value: h.kWh,
    state: h.kWh === null ? 'missing' : 'complete',
  }));
  return <BarChart bars={bars} height={180} ariaLabel={`Timeforbrug ${longDate(day)}`} />;
}

/** The heat pump in the property, as of the server's last collection. */
export function HeatPumpCard({ device }: { device: HeatPumpDevice }) {
  const target =
    device.targetTemperature === null
      ? ''
      : ` · ${number(device.targetTemperature, Number.isInteger(device.targetTemperature) ? 0 : 1)} °${device.temperatureUnit === 'F' ? 'F' : 'C'}`;
  const state = device.on === null ? 'Ukendt' : device.on ? `Tændt${device.mode ? ` · ${label(device.mode)}` : ''}${target}` : 'Slukket';
  return (
    <section className="card heatpump-card" aria-label={`Varmepumpe: ${device.name}`}>
      <div className="card-head">
        <h2>Varmepumpe</h2>
        <OnlineBadge online={device.online} detail={!device.online && device.lastSeenUtc ? `sidst set ${dateTime(device.lastSeenUtc)}` : undefined} />
      </div>
      <p className="hero">{celsius(device.temperature)}</p>
      <p className="card-date">Inde · {device.name} · målt {measuredAt(device.measuredAtUtc)}</p>
      <dl className="stats">
        <div><dt>Luftfugtighed</dt><dd>{humidity(device.humidity)}</dd></div>
        <div><dt>Drift</dt><dd>{state}</dd></div>
      </dl>
      <p className="card-foot-link"><a href="#/varmepumpe">Betjen varmepumpen ›</a></p>
    </section>
  );
}

export function PropertyDetail({ id }: { id: string }) {
  const [mode, setMode] = useState<Mode>('month');
  const [to, setTo] = useState(() => addDays(today(), -1));
  const [from, setFrom] = useState(() => addDays(today(), -30));
  const [selected, setSelected] = useState<string | null>(null);
  const [table, setTable] = useState(false);

  const features = useLoad(() => api.features(), 'features');
  const properties = useLoad(() => api.properties(), 'properties');
  const daily = useLoad(() => api.daily(id, from, to), `${id}|${from}|${to}`);
  const alerts = useLoad(() => api.propertyAlerts(id), `alerts|${id}`);
  const heatpumpOn = features.data?.heatpump ?? false;
  const heatpump = useLoad(() => (heatpumpOn ? api.heatpump() : Promise.resolve(null)), `heatpump|${heatpumpOn}`);
  const devices = heatpump.data?.devices.filter((d) => d.propertyId === id) ?? [];
  const property = properties.data?.find((p) => p.id === id);

  const choose = (next: Exclude<Mode, 'custom'>) => {
    setMode(next);
    setFrom(addDays(to, -(SPAN[next] - 1)));
    setSelected(null);
  };
  const shift = (direction: number) => {
    const span = daysBetween(from, to) + 1;
    setFrom(addDays(from, direction * span));
    setTo(addDays(to, direction * span));
    setSelected(null);
  };
  const range = (nextFrom: string, nextTo: string) => {
    if (!nextFrom || !nextTo || nextFrom > nextTo) return;
    setMode('custom');
    setFrom(nextFrom);
    setTo(nextTo);
    setSelected(null);
  };

  if (properties.data && !property) return <ErrorNote message="Ejendommen findes ikke." />;

  const items = daily.data?.items ?? [];
  const single = from === to;
  const hourDay = single ? from : selected;
  const picked = items.find((d) => d.date === hourDay) ?? null;
  const median = daily.data?.median ?? null;
  const allComplete = items.length > 0 && items.every((d) => d.quality === 'Complete');
  const total = allComplete ? items.reduce((sum, d) => sum + (d.kWh ?? 0), 0) : null;

  return (
    <>
      <div className="detail-head">
        <a className="link" href="#/">← Oversigt</a>
        <div className="card-head">
          <h1>{property?.name ?? '…'}</h1>
          {property && <StatusBadge status={property.status} label={property.statusLabel} />}
        </div>
        {property?.latestComplete && (
          <p className="muted">Seneste komplette døgn: {longDate(property.latestComplete.date)} · {kWh(property.latestComplete.kWh)}</p>
        )}
      </div>

      {devices.map((d) => <HeatPumpCard key={d.id} device={d} />)}

      <div className="controls">
        <div className="segmented" role="group" aria-label="Periode">
          {(['day', 'week', 'month'] as const).map((m) => (
            <button key={m} type="button" className={mode === m ? 'on' : ''} aria-pressed={mode === m} onClick={() => choose(m)}>
              {m === 'day' ? 'Dag' : m === 'week' ? 'Uge' : 'Måned'}
            </button>
          ))}
        </div>
        <div className="range">
          <button type="button" className="button button-small" onClick={() => shift(-1)} aria-label="Forrige periode">‹</button>
          <label>
            <span className="visually-hidden">Fra</span>
            <input type="date" value={from} max={to} onChange={(e) => range(e.target.value, to)} />
          </label>
          <span aria-hidden="true">–</span>
          <label>
            <span className="visually-hidden">Til</span>
            <input type="date" value={to} min={from} max={today()} onChange={(e) => range(from, e.target.value)} />
          </label>
          <button type="button" className="button button-small" onClick={() => shift(1)} disabled={to >= today()} aria-label="Næste periode">›</button>
        </div>
      </div>

      {features.data?.pricing && <PropertyCosts id={id} from={from} to={to} />}

      {daily.error && <ErrorNote message={daily.error} onRetry={daily.reload} />}

      {!single && (
        <section className="card">
          <div className="chart-head">
            <h2>Dagligt forbrug (kWh)</h2>
            <button type="button" className="link" onClick={() => setTable((t) => !t)}>{table ? 'Vis graf' : 'Vis tabel'}</button>
          </div>
          {table ? (
            <table className="table">
              <thead>
                <tr><th>Dato</th><th className="num">kWh</th><th className="num">Afvigelse</th><th>Status</th></tr>
              </thead>
              <tbody>
                {items.map((d) => (
                  <tr key={d.date}>
                    <td>{longDate(d.date)}</td>
                    <td className="num">{d.quality === 'Missing' ? '–' : kWh(d.kWh, 2).replace(' kWh', '')}</td>
                    <td className="num">{d.quality === 'Complete' && d.kWh !== null && median ? percent(((d.kWh - median) / median) * 100) : '–'}</td>
                    <td>{QUALITY[d.quality]}{d.quality === 'Partial' ? ` (${d.receivedIntervals}/${d.expectedIntervals})` : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <>
              <BarChart
                bars={items.map(toBar)}
                ariaLabel={`Dagligt forbrug ${shortDate(from)} til ${shortDate(to)}`}
                reference={median !== null ? { value: median, label: `Median ${kWh(median)}` } : null}
                selectedKey={selected}
                onSelect={setSelected}
              />
              <ul className="legend">
                <li><span className="swatch swatch-complete" />Komplet døgn</li>
                <li><span className="swatch swatch-partial" />Ufuldstændigt døgn</li>
                <li><span className="swatch swatch-missing" />Data mangler</li>
              </ul>
              <p className="muted small">Vælg en dag for at se timeforbruget.</p>
            </>
          )}
          <dl className="stats">
            <div><dt>I alt</dt><dd>{kWh(total)}</dd></div>
            <div><dt>Median pr. døgn</dt><dd>{kWh(median)}</dd></div>
            <div><dt>Komplette døgn</dt><dd>{daily.data ? `${daily.data.completeDays} af ${items.length}` : '–'}</dd></div>
          </dl>
          {daily.data && !allComplete && <p className="muted small">I alt vises kun, når alle døgn i perioden er komplette.</p>}
        </section>
      )}

      {hourDay && (
        <section className="card">
          <div className="chart-head">
            <h2>Timeforbrug · {longDate(hourDay)}</h2>
          </div>
          {picked && (
            <p className="card-compare">
              {picked.quality === 'Missing' ? 'Data mangler for dette døgn.' : `${kWh(picked.kWh, 2)}${picked.quality === 'Partial' ? ` · ufuldstændigt (${picked.receivedIntervals} af ${picked.expectedIntervals} timer)` : ''}`}
              {picked.quality === 'Complete' && picked.kWh !== null && property?.baseline
                ? ` · ${percent(((picked.kWh - property.baseline) / property.baseline) * 100)} (${kWh(picked.kWh - property.baseline)}) i forhold til median ${kWh(property.baseline)}`
                : ''}
            </p>
          )}
          <HourChart propertyId={id} day={hourDay} />
        </section>
      )}

      <section>
        <h2 className="section-title">Alarmer</h2>
        {alerts.error ? (
          <ErrorNote message={alerts.error} onRetry={alerts.reload} />
        ) : (
          <AlertList
            alerts={alerts.data ?? []}
            onChanged={() => {
              alerts.reload();
              properties.reload();
            }}
          />
        )}
      </section>
    </>
  );
}
