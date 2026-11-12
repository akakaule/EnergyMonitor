import { useState } from 'react';
import { PricingSettings } from './Pricing';
import { api, errorText, type Health, type Me, type MeteringPoint, type NotificationSettings, type PropertySummary, type Rule } from './api';
import { ErrorNote, useLoad } from './common';
import { dateTime } from './format';

type Result = { ok: boolean; text: string } | null;

function ResultNote({ result }: { result: Result }) {
  if (!result) return null;
  return <p className={result.ok ? 'note note-ok' : 'note note-error'} role="status">{result.text}</p>;
}

/** Runs an action and reports what happened next to the button that started it. */
function useAction(): [Result, boolean, (action: () => Promise<unknown>, done: string) => Promise<boolean>] {
  const [result, setResult] = useState<Result>(null);
  const [busy, setBusy] = useState(false);
  const run = async (action: () => Promise<unknown>, done: string) => {
    setBusy(true);
    setResult(null);
    try {
      await action();
      setResult({ ok: true, text: done });
      return true;
    } catch (e) {
      setResult({ ok: false, text: errorText(e) });
      return false;
    } finally {
      setBusy(false);
    }
  };
  return [result, busy, run];
}

const TOKEN: Record<Health['token']['state'], string> = {
  missing: 'Ikke registreret',
  unknown: 'Registreret · udløbsdato ukendt (forny manuelt inden for et år)',
  expired: 'Udløbet · skal fornyes',
  expiring: 'Udløber snart',
  ok: 'Gyldig',
};

function Operations({ admin }: { admin: boolean }) {
  const health = useLoad(() => api.health(), 'health');
  const [result, busy, run] = useAction();
  const h = health.data;

  return (
    <section className="card">
      <h2>Drift</h2>
      {health.error && <ErrorNote message={health.error} onRetry={health.reload} />}
      {h && (
        <dl className="facts">
          <div><dt>Seneste synkronisering</dt><dd>{h.lastSync ? `${dateTime(h.lastSync.finishedAtUtc ?? h.lastSync.startedAtUtc)} · ${h.lastSync.status === 'Succeeded' ? 'gennemført' : h.lastSync.status === 'Failed' ? 'fejlet' : 'kører'}` : 'Ingen endnu'}</dd></div>
          <div><dt>Seneste vellykkede</dt><dd>{dateTime(h.lastSuccessfulSyncUtc)}</dd></div>
          <div><dt>Seneste fejl</dt><dd>{h.lastError ? `${h.lastError.errorCode ?? 'ukendt'} · ${dateTime(h.lastError.at)}` : 'Ingen'}</dd></div>
          <div>
            <dt>Eloverblik-adgangsnøgle</dt>
            <dd>
              {TOKEN[h.token.state]}
              {h.token.daysLeft !== null && h.token.daysLeft >= 0 ? ` · ${h.token.daysLeft} dage tilbage` : ''}
            </dd>
          </div>
          <div><dt>Notifikationer</dt><dd>{h.notifications.configured ? 'Telegram er sat op' : 'Telegram mangler bot-nøgle eller modtagere'}{h.notifications.pending > 0 ? ` · ${h.notifications.pending} besked(er) afventer afsendelse` : ''}</dd></div>
        </dl>
      )}
      {admin && (
        <>
          <button
            type="button"
            className="button"
            disabled={busy || h?.syncRunning}
            onClick={async () => {
              if (await run(() => api.sync(), 'Synkronisering er startet. Det kan tage et par minutter.')) {
                window.setTimeout(health.reload, 4000);
              }
            }}
          >
            Synkronisér nu
          </button>
          <ResultNote result={result} />
        </>
      )}
    </section>
  );
}

function RuleRow({ propertyId, rule, onSaved }: { propertyId: string; rule: Rule; onSaved: () => void }) {
  const [draft, setDraft] = useState(rule);
  const [result, busy, run] = useAction();

  const set = (patch: Partial<Rule['params']>) => setDraft({ ...draft, params: { ...draft.params, ...patch } });
  const title = rule.type === 'LowConsumption' ? 'Lavt forbrug' : rule.type === 'HighConsumption' ? 'Højt forbrug' : 'Manglende data';
  const numeric = (value: string) => (value === '' ? 0 : Number(value));

  return (
    <fieldset className="rule">
      <legend>{title}</legend>
      <label className="check">
        <input type="checkbox" checked={draft.enabled} onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} />
        Aktiv
      </label>
      {rule.type === 'MissingData' ? (
        <label>
          Timer uden nyt komplet døgn
          <input type="number" min={24} max={720} value={draft.params.missingHours} onChange={(e) => set({ missingHours: numeric(e.target.value) })} />
        </label>
      ) : (
        <>
          <label>
            {rule.type === 'LowConsumption' ? 'Alarm under (% af median)' : 'Alarm over (% af median)'}
            <input type="number" min={0} max={1000} value={draft.params.thresholdPercent} onChange={(e) => set({ thresholdPercent: numeric(e.target.value) })} />
          </label>
          <label>
            Mindste median (kWh/døgn)
            <input type="number" min={0} max={1000} step="0.5" value={draft.params.minBaselineKWh} onChange={(e) => set({ minBaselineKWh: numeric(e.target.value) })} />
          </label>
          {rule.type === 'LowConsumption' && (
            <label>
              Alarm efter (døgn i træk)
              <input type="number" min={1} max={30} value={draft.params.escalateAfterDays} onChange={(e) => set({ escalateAfterDays: numeric(e.target.value) })} />
            </label>
          )}
        </>
      )}
      <button type="button" className="button button-small" disabled={busy} onClick={async () => (await run(() => api.saveRule(propertyId, draft), 'Gemt.')) && onSaved()}>
        Gem regel
      </button>
      <ResultNote result={result} />
    </fieldset>
  );
}

function PropertySettings({ property, points, onSaved }: { property: PropertySummary; points: MeteringPoint[] | null; onSaved: () => void }) {
  const rules = useLoad(() => api.rules(property.id), `rules|${property.id}`);
  const [name, setName] = useState(property.name);
  const [monitoring, setMonitoring] = useState(property.monitoringEnabled);
  const [point, setPoint] = useState('');
  const [result, busy, run] = useAction();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleteResult, deleting, runDelete] = useAction();

  return (
    <section className="card">
      <h2>{property.name}</h2>
      <div className="form">
        <label>
          Navn
          <input type="text" maxLength={40} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Målepunkt
          <select value={point} onChange={(e) => setPoint(e.target.value)}>
            <option value="">{property.hasMeteringPoint ? `Behold nuværende (${property.meteringPoint})` : 'Ikke tilknyttet'}</option>
            {points?.map((p) => (
              <option key={p.meteringPointId} value={p.meteringPointId}>{p.address || 'Uden adresse'} ({p.masked})</option>
            ))}
          </select>
        </label>
        {point !== '' && property.hasMeteringPoint && (
          <p className="note note-warn">Når målepunktet skiftes, slettes ejendommens hidtidige forbrugsdata og alarmer, og historikken hentes igen.</p>
        )}
        <label className="check">
          <input type="checkbox" checked={monitoring} onChange={(e) => setMonitoring(e.target.checked)} />
          Overvågning aktiv (fjern markeringen for pause)
        </label>
        <button
          type="button"
          className="button"
          disabled={busy}
          onClick={async () => {
            if (await run(() => api.saveProperty(property.id, name, point || null, monitoring), 'Gemt.')) {
              setPoint('');
              onSaved();
            }
          }}
        >
          Gem ejendom
        </button>
        <ResultNote result={result} />
      </div>

      <h3>Alarmgrænser</h3>
      <p className="muted small">Grænserne er startværdier til kalibrering. Et lavt forbrug er kun en indikator på mulig fejl.</p>
      {rules.error && <ErrorNote message={rules.error} onRetry={rules.reload} />}
      {rules.data?.map((r) => <RuleRow key={r.id + JSON.stringify(r)} propertyId={property.id} rule={r} onSaved={rules.reload} />)}

      <h3>Data</h3>
      {confirmDelete ? (
        <div className="confirm">
          <p>Slet alle forbrugsdata og alarmer for {property.name}? Historikken hentes igen ved næste synkronisering.</p>
          <button type="button" className="button button-danger" disabled={deleting} onClick={async () => { await runDelete(() => api.deleteData(property.id), 'Data er slettet.'); setConfirmDelete(false); onSaved(); }}>
            Ja, slet data
          </button>
          <button type="button" className="button button-small" onClick={() => setConfirmDelete(false)}>Fortryd</button>
        </div>
      ) : (
        <button type="button" className="button button-small" onClick={() => setConfirmDelete(true)}>Slet forbrugsdata…</button>
      )}
      <ResultNote result={deleteResult} />
    </section>
  );
}

function NewProperty({ points, onSaved }: { points: MeteringPoint[] | null; onSaved: () => void }) {
  const [name, setName] = useState('');
  const [point, setPoint] = useState('');
  const [result, busy, run] = useAction();
  return (
    <section className="card">
      <h2>Ny ejendom</h2>
      <div className="form">
        <label>
          Navn
          <input type="text" maxLength={40} value={name} placeholder="f.eks. Sommerhus" onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Målepunkt
          <select value={point} onChange={(e) => setPoint(e.target.value)}>
            <option value="">Vælg senere</option>
            {points?.map((p) => (
              <option key={p.meteringPointId} value={p.meteringPointId}>{p.address || 'Uden adresse'} ({p.masked})</option>
            ))}
          </select>
        </label>
        <button
          type="button"
          className="button"
          disabled={busy || name.trim() === ''}
          onClick={async () => {
            if (await run(() => api.createProperty(name, point || null), 'Ejendommen er oprettet.')) {
              setName('');
              setPoint('');
              onSaved();
            }
          }}
        >
          Opret ejendom
        </button>
        <ResultNote result={result} />
      </div>
    </section>
  );
}

function Notifications() {
  const settings = useLoad(() => api.notificationSettings(), 'notification-settings');
  if (settings.error) return <section className="card"><h2>Notifikationer</h2><ErrorNote message={settings.error} onRetry={settings.reload} /></section>;
  // The form starts from the stored settings, so it is only mounted once they are known.
  return settings.data ? <NotificationForm initial={settings.data} /> : null;
}

function NotificationForm({ initial }: { initial: NotificationSettings }) {
  const [daily, setDaily] = useState(initial.dailyReportEnabled);
  const [recipients, setRecipients] = useState(initial.recipients.join(', '));
  const [result, busy, run] = useAction();
  const [testResult, testing, runTest] = useAction();
  const [preview, setPreview] = useState<string | null>(null);

  return (
    <section className="card">
      <h2>Notifikationer</h2>
      <div className="form">
        <label className="check">
          <input type="checkbox" checked={daily} onChange={(e) => setDaily(e.target.checked)} />
          Send daglig statusrapport kl. 09:00
        </label>
        <label>
          Modtagere (Telegram chat-id’er, adskilt med komma)
          <input type="text" inputMode="text" value={recipients} onChange={(e) => setRecipients(e.target.value)} />
        </label>
        <div className="row">
          <button
            type="button"
            className="button"
            disabled={busy}
            onClick={() => run(() => api.saveNotificationSettings({ dailyReportEnabled: daily, recipients: recipients.split(',').map((r) => r.trim()).filter(Boolean) }), 'Gemt.')}
          >
            Gem
          </button>
          <button type="button" className="button button-secondary" disabled={testing} onClick={() => runTest(() => api.testNotification(), 'Testbeskeden er sendt.')}>
            Send testbesked
          </button>
          <button type="button" className="button button-secondary" onClick={async () => setPreview((await api.reportPreview().catch(() => ({ text: 'Rapporten kunne ikke hentes.' }))).text)}>
            Vis dagens rapport
          </button>
        </div>
        <ResultNote result={result} />
        <ResultNote result={testResult} />
        {preview && <pre className="preview">{preview}</pre>}
      </div>
    </section>
  );
}

export function Settings({ me }: { me: Me }) {
  const features = useLoad(() => api.features(), 'features');
  const admin = me.role === 'admin';
  const properties = useLoad(() => api.properties(), 'properties');
  const [points, setPoints] = useState<MeteringPoint[] | null>(null);
  const [pointsResult, loadingPoints, runPoints] = useAction();

  return (
    <>
      <h1>Indstillinger</h1>
      <Operations admin={admin} />
      {!admin && <p className="note note-info">Du er logget ind som læser. Kun administratorer kan ændre indstillinger.</p>}
      {admin && (
        <>
          <section className="card">
            <h2>Målepunkter fra Eloverblik</h2>
            <p className="muted small">Hent listen for at knytte et målepunkt til en ejendom.</p>
            <button type="button" className="button button-secondary" disabled={loadingPoints} onClick={() => runPoints(async () => setPoints(await api.meteringPoints()), 'Målepunkter er hentet.')}>
              Hent målepunkter
            </button>
            <ResultNote result={pointsResult} />
            {points && (
              <ul className="plain">
                {points.map((p) => <li key={p.meteringPointId}>{p.address || 'Uden adresse'} · {p.masked}{p.readingOccurrence ? ` · ${p.readingOccurrence}` : ''}</li>)}
              </ul>
            )}
          </section>

          {properties.error && <ErrorNote message={properties.error} onRetry={properties.reload} />}
          {properties.data?.map((p) => <PropertySettings key={p.id} property={p} points={points} onSaved={properties.reload} />)}
          {features.data?.pricing && properties.data?.map(p => <PricingSettings key={p.id} id={p.id} name={p.name} />)}
          <NewProperty points={points} onSaved={properties.reload} />
          <Notifications />

          <section className="card">
            <h2>Eksport</h2>
            <p className="muted small">Henter konfiguration, alarmer og al forbrugshistorik som én JSON-fil (backup).</p>
            <a className="button button-secondary" href="/api/admin/export" download="energy-monitor-export.json">Hent eksport</a>
          </section>
        </>
      )}
    </>
  );
}
