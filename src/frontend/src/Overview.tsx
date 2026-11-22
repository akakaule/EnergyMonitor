import { api, type Health, type PropertySummary } from './api';
import { BarChart } from './BarChart';
import { AlertList, ErrorNote, StatusBadge, useLoad } from './common';
import { addDays, age, dateTime, dayRange, kWh, longDate, percent, shortDate } from './format';
import { approxKr } from './Pricing';
import { PriceCards } from './Prices';

/** The approximate price of a span of days, or a dash while it loads or cannot be had. */
function Approx({ id, from, to }: { id: string; from: string; to: string }) {
  const estimate = useLoad(() => api.estimate(id, from, to), `estimate|${id}|${from}|${to}`);
  return <span>{estimate.data ? approxKr(estimate.data.total) : '–'}</span>;
}

function Deviation({ p }: { p: PropertySummary }) {
  if (!p.latestComplete) return null;
  if (!p.hasBaseline || p.baseline === null || p.deviationPercent === null) {
    return <p className="card-compare muted">Utilstrækkelig historik til sammenligning</p>;
  }
  // The arrow is drawn by the style sheet; the sign in the text already says the same in words.
  const direction = Math.round(p.deviationPercent) > 0 ? ' delta-up' : Math.round(p.deviationPercent) < 0 ? ' delta-down' : '';
  return (
    <p className={`card-compare${direction}`}>
      {percent(p.deviationPercent)} i forhold til median {kWh(p.baseline)}
    </p>
  );
}

export function PropertyCard({ p, pricing = false }: { p: PropertySummary; pricing?: boolean }) {
  const latest = p.latestComplete?.date;
  const cost = (days: number) =>
    pricing && latest ? <dd className="tile-cost"><Approx id={p.id} from={addDays(latest, -(days - 1))} to={latest} /></dd> : null;
  return (
    <a className="card card-link" href={`#/ejendom/${p.id}`} aria-label={`${p.name}: ${p.statusLabel}. Åbn detaljer`}>
      <div className="card-head">
        <h2>{p.name}</h2>
        <StatusBadge status={p.status} label={p.statusLabel} />
      </div>

      {p.latestComplete ? (
        <>
          <p className="hero">{kWh(p.latestComplete.kWh)}</p>
          {pricing && <p className="hero-cost"><Approx id={p.id} from={p.latestComplete.date} to={p.latestComplete.date} /></p>}
          <p className="card-date">Seneste komplette døgn · {longDate(p.latestComplete.date)}</p>
          <Deviation p={p} />
        </>
      ) : (
        <>
          <p className="hero hero-empty">–</p>
          <p className="card-date">
            {p.hasMeteringPoint ? 'Ingen komplette døgn modtaget endnu' : 'Intet målepunkt er tilknyttet'}
          </p>
        </>
      )}

      <dl className="stats stats-tiles">
        <div>
          <dt>Seneste 7 døgn</dt>
          <dd>{kWh(p.sum7)}</dd>
          {cost(7)}
        </div>
        <div>
          <dt>Seneste 30 døgn</dt>
          <dd>{kWh(p.sum30)}</dd>
          {cost(30)}
        </div>
      </dl>
      {p.latestComplete && (p.sum7 === null || p.sum30 === null) && (
        <p className="muted small">Summer vises kun, når alle døgn i perioden er komplette.</p>
      )}

      {p.sparkline.length > 0 && (
        <div className="spark">
          <BarChart
            compact
            height={44}
            ariaLabel={`Forbrug de seneste ${p.sparkline.length} komplette døgn, ${shortDate(p.sparkline[0].date)} til ${shortDate(p.sparkline[p.sparkline.length - 1].date)}`}
            bars={p.sparkline.map((d) => ({ key: d.date, label: '', title: longDate(d.date), value: d.kWh, state: 'complete' as const }))}
            // The latest day stands out, so the bar can be tied to the large number above.
            selectedKey={p.sparkline[p.sparkline.length - 1].date}
          />
          <p className="muted small">Seneste {p.sparkline.length} komplette døgn</p>
        </div>
      )}

      {p.pendingDays.length > 0 && (
        <p className="note note-info">Data for {dayRange(p.pendingDays)} er endnu ikke komplette.</p>
      )}
      {p.isStale && p.latestComplete && (
        <p className="note note-warn">Nyeste komplette måling er {age(p.dataAgeHours)}.</p>
      )}

      <p className="card-foot">
        Synkroniseret {dateTime(p.lastSuccessfulSyncUtc)}
        {p.latestComplete ? ` · nyeste komplette måling ${age(p.dataAgeHours)}` : ''}
        {!p.monitoringEnabled ? ' · overvågning på pause' : ''}
      </p>
    </a>
  );
}

function SourceNote({ health }: { health: Health }) {
  if (health.lastSync?.status === 'Failed') {
    return <p className="note note-warn">Seneste synkronisering fejlede ({health.lastSync.errorCode ?? 'ukendt fejl'}). Viste tal er fra {dateTime(health.lastSuccessfulSyncUtc)}.</p>;
  }
  if (health.token.state === 'expired') return <p className="note note-warn">Eloverblik-adgangsnøglen er udløbet og skal fornyes.</p>;
  if (health.token.state === 'expiring') return <p className="note note-info">Eloverblik-adgangsnøglen udløber om {health.token.daysLeft} dage.</p>;
  return null;
}

export function Overview() {
  const properties = useLoad(() => api.properties(), 'properties');
  const alerts = useLoad(() => api.alerts('open'), 'alerts');
  const health = useLoad(() => api.health(), 'health');
  const features = useLoad(() => api.features(), 'features');

  const open = alerts.data?.length ?? 0;

  return (
    <>
      <div className="page-head">
        <h1>Oversigt</h1>
        {alerts.data && (
          <p className={open > 0 ? 'page-status page-status-open' : 'page-status'}>
            {open === 0 ? 'Ingen aktive alarmer' : open === 1 ? '1 aktiv alarm' : `${open} aktive alarmer`}
          </p>
        )}
      </div>
      {properties.error && <ErrorNote message={properties.error} onRetry={properties.reload} />}
      {health.data && <SourceNote health={health.data} />}
      {properties.loading && !properties.data && <p className="muted">Henter…</p>}

      <div className="cards">
        {properties.data?.map((p) => <PropertyCard key={p.id} p={p} pricing={features.data?.pricing ?? false} />)}
      </div>
      {properties.data?.length === 0 && (
        <p className="note note-info">Der er ikke oprettet nogen ejendomme endnu. Opret dem under Indstillinger.</p>
      )}

      {features.data?.pricing && properties.data && <PriceCards properties={properties.data} />}

      <section>
        <h2 className="section-title">
          Aktive alarmer
          {open > 0 && <span className="count" aria-hidden="true">{open}</span>}
        </h2>
        {alerts.error ? (
          <ErrorNote message={alerts.error} onRetry={alerts.reload} />
        ) : (
          <AlertList
            alerts={alerts.data ?? []}
            showProperty
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
