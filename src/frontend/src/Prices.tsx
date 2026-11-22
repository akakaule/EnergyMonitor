import { useState } from 'react';
import { api, type DayPrices, type HourPrice, type PriceView } from './api';
import { BarChart, type Bar } from './BarChart';
import { ErrorNote, useLoad, usePolling } from './common';
import { addDays, dateTime, hourLabel, longDate, number, today } from './format';

/** "2,31 kr./kWh" */
export const krPerKWh = (value: number) => `${number(value, 2)} kr./kWh`;

/** "kl. 02–05" in Danish time. A period that ends at midnight ends at 24. */
export function hourRange(fromUtc: string, toUtc: string): string {
  const end = hourLabel(toUtc);
  return `kl. ${hourLabel(fromUtc)}–${end === '00' ? '24' : end}`;
}

/** "Hjem", "Hjem og Sommerhus", "A, B og C" */
export function nameList(names: string[]): string {
  return names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} og ${names[names.length - 1]}`;
}

export interface PriceGroup {
  names: string[];
  view: PriceView;
}

/**
 * Properties that pay the same price hour by hour share a card. `missing` names the properties without an
 * electricity agreement, which have no price area and so no prices.
 */
export function groupPrices(items: { name: string; view: PriceView }[]): { groups: PriceGroup[]; missing: string[] } {
  const groups = new Map<string, PriceGroup>();
  const missing: string[] = [];
  for (const { name, view } of items) {
    if (!view.area) {
      missing.push(name);
      continue;
    }
    const key = JSON.stringify([view.area, view.kind, view.assumptions, view.days]);
    const group = groups.get(key);
    if (group) group.names.push(name);
    else groups.set(key, { names: [name], view });
  }
  return { groups: [...groups.values()], missing };
}

const inWindow = (h: HourPrice, w: { fromUtc: string; toUtc: string } | null | undefined) =>
  !!w && Date.parse(h.startUtc) >= Date.parse(w.fromUtc) && Date.parse(h.endUtc) <= Date.parse(w.toUtc);

/** The cheapest and the dearest period, unless the price is the same all day. */
function periods(day: DayPrices) {
  const { cheapest, mostExpensive } = day;
  return cheapest && mostExpensive && cheapest.average !== mostExpensive.average ? { cheapest, mostExpensive } : null;
}

function bars(day: DayPrices, now: number, isToday: boolean): Bar[] {
  const p = periods(day);
  return day.hours.map((h) => {
    const low = inWindow(h, p?.cheapest);
    const high = inWindow(h, p?.mostExpensive);
    return {
      key: h.startUtc,
      label: hourLabel(h.startUtc),
      title: hourRange(h.startUtc, h.endUtc),
      value: h.total ?? null,
      state: h.total == null ? 'missing' : 'complete',
      tone: low ? 'low' : high ? 'high' : isToday && Date.parse(h.endUtc) <= now ? 'past' : undefined,
      note: low ? 'billigste periode' : high ? 'dyreste periode' : undefined,
    };
  });
}

function Breakdown({ hour, fixed }: { hour: HourPrice; fixed: boolean }) {
  if (hour.total == null) return <p className="muted small">Der er ingen pris for {hourRange(hour.startUtc, hour.endUtc)} endnu.</p>;
  const rows: [string, number][] = [
    [fixed ? 'Fast pris' : 'Spotpris', hour.energy ?? 0],
    ['Elselskab', hour.supplier],
    ['Nettarif', hour.network],
    ['Afgifter', hour.tax],
    ['Moms', hour.vat ?? 0],
  ];
  return (
    <div className="price-breakdown" role="status">
      <p className="price-breakdown-head">
        {hourRange(hour.startUtc, hour.endUtc)} · <strong>{krPerKWh(hour.total)}</strong>
      </p>
      <dl className="stats stats-small">
        {rows.map(([label, value]) => (
          <div key={label}><dt>{label}</dt><dd>{number(value, 2)}</dd></div>
        ))}
      </dl>
    </div>
  );
}

export function PriceCard({ group, now }: { group: PriceGroup; now: number }) {
  const { view, names } = group;
  const [which, setWhich] = useState<'today' | 'tomorrow'>('today');
  // The hour the reader picked, per day. Until one is picked, today shows the hour in progress.
  const [picked, setPicked] = useState<Record<string, string>>({});

  const date = which === 'today' ? today(new Date(now)) : addDays(today(new Date(now)), 1);
  const day = view.days.find((d) => d.date === date);
  const isToday = which === 'today';
  const current = isToday ? day?.hours.find((h) => Date.parse(h.startUtc) <= now && now < Date.parse(h.endUtc)) : undefined;
  const selected = day?.hours.find((h) => h.startUtc === picked[date]) ?? current;
  const p = day ? periods(day) : null;
  const fixed = view.kind === 'Fixed';
  const priced = day?.hours.some((h) => h.total != null) ?? false;

  return (
    <section className="card price-card" aria-label={`Elpris for ${nameList(names)}`}>
      <div className="card-head">
        <h3>{nameList(names)}</h3>
        <div className="segmented" role="group" aria-label="Dag">
          {(['today', 'tomorrow'] as const).map((w) => (
            <button key={w} type="button" className={which === w ? 'on' : ''} aria-pressed={which === w} onClick={() => setWhich(w)}>
              {w === 'today' ? 'I dag' : 'I morgen'}
            </button>
          ))}
        </div>
      </div>

      {!day || !priced ? (
        <p className="note note-info">
          {isToday ? 'Dagens priser er ikke hentet endnu.' : 'Priserne for i morgen kommer normalt lidt efter kl. 13.'}
        </p>
      ) : (
        <>
          {isToday && current?.total != null ? (
            <>
              <p className="hero">{number(current.total, 2)}<span className="hero-unit"> kr./kWh</span></p>
              <p className="card-date">Nu, {hourRange(current.startUtc, current.endUtc)} · {view.area}</p>
            </>
          ) : day.average != null ? (
            <>
              <p className="hero">{number(day.average, 2)}<span className="hero-unit"> kr./kWh</span></p>
              <p className="card-date">Gennemsnit {longDate(day.date)} · {view.area}</p>
            </>
          ) : (
            <p className="card-date">{longDate(day.date)} · {view.area}</p>
          )}
          <p className="muted small">Ca. pris i kr./kWh inkl. elselskabets tillæg, tariffer, afgifter og moms.</p>

          <div className="price-chart">
            <BarChart
              bars={bars(day, now, isToday)}
              height={180}
              ariaLabel={`Elpris time for time ${longDate(day.date)}, kr. pr. kWh`}
              format={krPerKWh}
              selectedKey={selected?.startUtc ?? null}
              onSelect={(key) => setPicked((s) => ({ ...s, [date]: key }))}
            />
          </div>

          {p && (
            <ul className="legend price-periods">
              <li>
                <span className="swatch swatch-low" />
                {isToday ? 'Billigst resten af dagen' : 'Billigst'}: {hourRange(p.cheapest.fromUtc, p.cheapest.toUtc)} · gns. {krPerKWh(p.cheapest.average)}
              </li>
              <li>
                <span className="swatch swatch-high" />
                {isToday ? 'Dyrest resten af dagen' : 'Dyrest'}: {hourRange(p.mostExpensive.fromUtc, p.mostExpensive.toUtc)} · gns. {krPerKWh(p.mostExpensive.average)}
              </li>
            </ul>
          )}

          {selected && <Breakdown hour={selected} fixed={fixed} />}

          {day.average != null && day.min != null && day.max != null && (
            <dl className="stats">
              <div><dt>Gennemsnit</dt><dd>{number(day.average, 2)}</dd></div>
              <div><dt>Laveste time</dt><dd>{number(day.min, 2)}</dd></div>
              <div><dt>Højeste time</dt><dd>{number(day.max, 2)}</dd></div>
            </dl>
          )}

          {day.status === 'Partial' && <p className="note note-info">Nogle timer mangler en pris, så gennemsnit og billigste periode kan ikke beregnes.</p>}
          {day.negativeHours > 0 && (
            <p className="note note-ok">{day.negativeHours === 1 ? '1 time' : `${day.negativeHours} timer`} med negativ spotpris.</p>
          )}
          {fixed && <p className="hint">Fastprisaftale: kun tariffer og afgifter varierer over døgnet.</p>}
          {view.assumptions.includes('charges_missing') && (
            <p className="hint">Eloverblik har ikke sendt tariffer for målepunktet, så nettarif og afgifter er ikke med i prisen.</p>
          )}
          {view.assumptions.includes('supplier_markup_missing') && (
            <p className="hint">Elselskabets tillæg er ikke registreret under Indstillinger → Elaftale og regnes som 0.</p>
          )}
        </>
      )}
      <p className="card-foot">Priser hentet {dateTime(view.lastPriceSyncUtc)}</p>
    </section>
  );
}

/** Today's and tomorrow's price per kWh, hour by hour: one card per group of properties that pay the same. */
export function PriceCards({ properties }: { properties: { id: string; name: string }[] }) {
  const [now, setNow] = useState(() => Date.now());
  usePolling(() => setNow(Date.now()), 60_000);
  const day = today(new Date(now));
  // Danish hours start on the UTC hour, so this key changes exactly when a new hour begins.
  const hour = Math.floor(now / 3_600_000);
  const prices = useLoad(
    () => Promise.all(properties.map(async (p) => ({ name: p.name, view: await api.prices(p.id, day, addDays(day, 1)) }))),
    `prices|${properties.map((p) => p.id).join(',')}|${day}|${hour}`,
  );

  if (properties.length === 0) return null;
  const { groups, missing } = groupPrices(prices.data ?? []);
  return (
    <section>
      <h2 className="section-title">Elpriser</h2>
      {prices.error && <ErrorNote message={prices.error} onRetry={prices.reload} />}
      {prices.loading && !prices.data && <p className="muted">Henter…</p>}
      {groups.length > 0 && (
        <div className="cards">
          {groups.map((g) => <PriceCard key={g.names.join('|')} group={g} now={now} />)}
        </div>
      )}
      {missing.length > 0 && (
        <p className="note note-info">
          <span>
            {nameList(missing)} har ingen elaftale, så prisområdet er ukendt. Registrér den under{' '}
            <a href="#/indstillinger">Indstillinger</a> for at se elpriser.
          </span>
        </p>
      )}
    </section>
  );
}
