import { useState } from 'react';
import { api, codeText, type HeatingDay, type HeatingView } from './api';
import { ErrorNote, useLoad } from './common';
import { celsius, dateTime, kWh, longDate, number, percent } from './format';
import { ScatterChart } from './ScatterChart';

const PERIODS: [number, string][] = [[90, '3 mdr.'], [180, '6 mdr.'], [365, '1 år']];

/** Below this share of explained variation the figures say little. */
const WEAK = 0.3;

const WEATHER_ERRORS: Record<string, string> = {
  weather_rate_limited: 'Vejrtjenesten begrænser antallet af kald lige nu. Der prøves igen ved næste synkronisering.',
  weather_unreachable: 'Der er ingen forbindelse til vejrtjenesten. Der prøves igen ved næste synkronisering.',
};

function dayLabel(d: HeatingDay): string {
  return `${longDate(d.date)}: ${kWh(d.kWh)} ved ${celsius(d.outdoorC)} ude${d.indoorC != null ? ` og ${celsius(d.indoorC)} inde` : ''}`;
}

function Latest({ day }: { day: HeatingDay }) {
  if (day.expectedKWh == null) return null;
  const deviation = day.expectedKWh > 0 ? ` (${percent(((day.kWh - day.expectedKWh) / day.expectedKWh) * 100)})` : '';
  return (
    <p className="card-compare">
      {dayLabel(day)} · forventet {kWh(day.expectedKWh)}{deviation}
    </p>
  );
}

function Result({ view }: { view: HeatingView }) {
  const model = view.model;
  if (view.status !== 'Ok' || !model) {
    return (
      <p className="note note-info">
        {view.lastWeatherSyncUtc
          ? `Der er endnu ikke nok døgn med både komplet forbrug og udetemperatur til en sammenligning (${view.days.length} af mindst 14).`
          : 'Udetemperaturen er ikke hentet endnu. Den hentes ved næste synkronisering.'}
      </p>
    );
  }
  const latest = view.latest ?? null;
  const weak = model.rSquared < WEAK || model.kWhPerDegreeDay <= 0;
  return (
    <>
      <dl className="stats">
        <div><dt>Pr. grad koldere</dt><dd>{number(model.kWhPerDegreeDay, 2)} kWh/døgn</dd></div>
        <div><dt>Grundforbrug</dt><dd>{number(model.baseKWhPerDay, 1)} kWh/døgn</dd></div>
        <div><dt>Forklaret af kulden</dt><dd>{Math.round(model.rSquared * 100)} %</dd></div>
      </dl>
      {latest && <Latest day={latest} />}
      {weak && <p className="note note-info">Forbruget følger ikke udetemperaturen ret meget i perioden, så tallene er usikre.</p>}
      <ScatterChart
        ariaLabel={`Forbrug pr. døgn mod graddage, ${view.days.length} døgn`}
        xLabel={view.usesIndoor ? 'Grader koldere ude end inde' : `Grader under ${number(view.baseTemperatureC, 0)} °C ude`}
        yLabel="kWh pr. døgn"
        points={view.days.map((d) => ({ key: d.date, x: d.degreeDays, y: d.kWh, label: dayLabel(d) }))}
        line={{ intercept: model.baseKWhPerDay, slope: model.kWhPerDegreeDay }}
        highlightKey={latest?.date}
      />
      <ul className="legend">
        <li><span className="swatch swatch-dot" />Et døgn</li>
        <li><span className="swatch swatch-dot swatch-dot-highlight" />Seneste døgn</li>
        <li><span className="swatch swatch-line" />Forventet forbrug</li>
      </ul>
      <p className="hint">
        {view.usesIndoor
          ? 'Hvert døgn er sat efter forskellen mellem varmepumpens målte indetemperatur og udetemperaturen.'
          : `Hvert døgn er sat efter, hvor mange grader udetemperaturen lå under ${number(view.baseTemperatureC, 0)} °C.`}{' '}
        Forbruget er hele målepunktets, ikke varmepumpens alene: alt andet ligger i grundforbruget.
      </p>
    </>
  );
}

/** How the property's daily consumption follows the cold, over the chosen period. */
export function HeatingCard({ id, located }: { id: string; located: boolean }) {
  const [days, setDays] = useState(90);
  const heating = useLoad(() => (located ? api.heating(id, days) : Promise.resolve(null)), `heating|${id}|${days}|${located}`);
  const view = heating.data;

  return (
    <section className="card">
      <div className="chart-head">
        <h2>Forbrug og udetemperatur</h2>
        {located && (
          <div className="segmented" role="group" aria-label="Periode for udetemperatur">
            {PERIODS.map(([value, label]) => (
              <button key={value} type="button" className={days === value ? 'on' : ''} aria-pressed={days === value} onClick={() => setDays(value)}>
                {label}
              </button>
            ))}
          </div>
        )}
      </div>
      {!located || view?.status === 'NoLocation' ? (
        <p className="hint">
          Angiv ejendommens position under <a href={`#/indstillinger/ejendom/${id}`}>Indstillinger</a> for at se, hvor meget af forbruget der følger udetemperaturen.
        </p>
      ) : (
        <>
          {heating.error && <ErrorNote message={heating.error} onRetry={heating.reload} />}
          {!view && !heating.error && <p className="muted">Henter…</p>}
          {view && <Result view={view} />}
          {view?.weatherErrorCode && <p className="note note-warn">{WEATHER_ERRORS[view.weatherErrorCode] ?? codeText(view.weatherErrorCode)}</p>}
          {view && (
            <p className="card-foot">
              Udetemperatur hentet {dateTime(view.lastWeatherSyncUtc)} · Vejrdata fra <a href="https://open-meteo.com/" target="_blank" rel="noreferrer">Open-Meteo.com</a>
            </p>
          )}
        </>
      )}
    </section>
  );
}
