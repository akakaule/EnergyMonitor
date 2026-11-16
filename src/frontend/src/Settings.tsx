import { useEffect, useId, useRef, useState, type InputHTMLAttributes, type ReactNode } from 'react';
import { PricingSettings } from './Pricing';
import { api, codeText, errorText, type HeatPumpDevice, type HeatPumpStatus, type Health, type Me, type MeteringPoint, type NotificationSettings, type PropertySummary, type Rule } from './api';
import { ErrorNote, StatusBadge, Switch, useLoad, type Loaded } from './common';
import { dateTime, fullDate } from './format';

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

/** The settings screen shows one section at a time; the section is part of the address. */
const DRIFT = 'drift';
const NOTIFICATIONS = 'notifikationer';
const NEW_PROPERTY = 'ny-ejendom';
const HEAT_PUMP = 'varmepumpe';
const propertySection = (id: string) => `ejendom/${id}`;
const sectionHref = (section: string) => (section === DRIFT ? '#/indstillinger' : `#/indstillinger/${section}`);

type Tone = 'ok' | 'warn' | 'error' | 'neutral';
interface Fact { value: string; detail?: string; tone: Tone }

// Status is always an icon and a word, never colour alone.
const TONE_ICON: Record<Tone, string> = { ok: '✓', warn: '!', error: '×', neutral: '·' };

const TOKEN: Record<Health['token']['state'], Fact> = {
  missing: { value: 'Ikke registreret', tone: 'error' },
  unknown: { value: 'Registreret', detail: 'Udløbsdato ukendt (forny manuelt inden for et år)', tone: 'warn' },
  expired: { value: 'Udløbet', detail: 'Skal fornyes', tone: 'error' },
  expiring: { value: 'Udløber snart', tone: 'warn' },
  ok: { value: 'Gyldig', tone: 'ok' },
};

const details = (...parts: (string | undefined | false)[]) => parts.filter(Boolean).join(' · ') || undefined;

function facts(h: Health): { label: string; fact: Fact }[] {
  const sync = h.lastSync;
  const token = TOKEN[h.token.state];
  return [
    {
      label: 'Seneste synkronisering',
      fact: !sync
        ? { value: 'Ingen endnu', tone: 'neutral' }
        : {
            value: sync.status === 'Succeeded' ? 'Gennemført' : sync.status === 'Failed' ? 'Fejlet' : 'Kører',
            detail: dateTime(sync.finishedAtUtc ?? sync.startedAtUtc),
            tone: sync.status === 'Succeeded' ? 'ok' : sync.status === 'Failed' ? 'error' : 'neutral',
          },
    },
    {
      label: 'Seneste vellykkede',
      fact: { value: dateTime(h.lastSuccessfulSyncUtc), tone: h.lastSuccessfulSyncUtc ? 'ok' : 'warn' },
    },
    {
      label: 'Seneste fejl',
      fact: h.lastError
        ? { value: h.lastError.errorCode ?? 'ukendt', detail: dateTime(h.lastError.at), tone: 'warn' }
        : { value: 'Ingen', tone: 'ok' },
    },
    {
      label: 'Eloverblik-adgangsnøgle',
      fact: {
        ...token,
        detail: details(token.detail, h.token.daysLeft !== null && h.token.daysLeft >= 0 && `${h.token.daysLeft} dage tilbage`),
      },
    },
    {
      label: 'Notifikationer',
      fact: {
        value: h.notifications.configured ? 'Telegram er sat op' : 'Ikke sat op',
        detail: details(
          !h.notifications.configured && 'Telegram mangler bot-nøgle eller modtagere',
          h.notifications.pending > 0 && `${h.notifications.pending} besked(er) afventer afsendelse`,
        ),
        tone: h.notifications.configured ? 'ok' : 'warn',
      },
    },
  ];
}

function Tiles({ items }: { items: { label: string; fact: Fact }[] }) {
  return (
    <dl className="tiles">
      {items.map(({ label, fact }) => (
        <div key={label} className="tile">
          <dt>{label}</dt>
          <dd>
            <span className={`tone tone-${fact.tone}`} aria-hidden="true">{TONE_ICON[fact.tone]}</span>
            <span className="tile-value">{fact.value}</span>
            {fact.detail && <span className="tile-detail">{fact.detail}</span>}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function PanelHead({ title, about, children }: { title: string; about?: ReactNode; children?: ReactNode }) {
  return (
    <header className="panel-head">
      <div>
        <h2>{title}</h2>
        {about && <p className="muted">{about}</p>}
      </div>
      {children}
    </header>
  );
}

function Operations({ admin, health, hidden }: { admin: boolean; health: Loaded<Health>; hidden: boolean }) {
  const [result, busy, run] = useAction();
  const h = health.data;

  return (
    <section className="panel" hidden={hidden}>
      <PanelHead title="Drift" about="Synkronisering med Eloverblik og status for notifikationer.">
        {admin && (
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
        )}
      </PanelHead>
      <ResultNote result={result} />
      {health.error && <ErrorNote message={health.error} onRetry={health.reload} />}
      {h && <Tiles items={facts(h)} />}
      {admin && (
        <div className="card">
          <h3>Eksport</h3>
          <p className="hint">Henter konfiguration, alarmer og al forbrugshistorik som én JSON-fil (backup).</p>
          <div className="actions">
            <a className="button button-secondary" href="/api/admin/export" download="energy-monitor-export.json">Hent eksport</a>
          </div>
        </div>
      )}
    </section>
  );
}

/** The list of metering points is fetched from Eloverblik on demand and shared by every form that picks one. */
interface Points {
  list: MeteringPoint[] | null;
  busy: boolean;
  error: string | null;
  load: () => void;
}

function MeteringPointField({ value, onChange, empty, points }: { value: string; onChange: (value: string) => void; empty: string; points: Points }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>Målepunkt</label>
      <div className="field-row">
        <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
          <option value="">{empty}</option>
          {points.list?.map((p) => (
            <option key={p.meteringPointId} value={p.meteringPointId}>
              {p.address || 'Uden adresse'} ({p.masked}){p.readingOccurrence ? ` · ${p.readingOccurrence}` : ''}
            </option>
          ))}
        </select>
        <button type="button" className="button button-secondary" disabled={points.busy} onClick={points.load}>
          Hent målepunkter
        </button>
      </div>
      {points.error ? (
        <p className="note note-error" role="status">{points.error}</p>
      ) : (
        <p className="hint">
          {points.list ? `${points.list.length} målepunkt(er) hentet fra Eloverblik.` : 'Hent listen fra Eloverblik for at vælge et målepunkt.'}
        </p>
      )}
    </div>
  );
}

function NumberField({ label, unit, ...input }: { label: string; unit: string } & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="field">
      {label}
      <span className="unit">
        <input type="number" {...input} />
        {unit}
      </span>
    </label>
  );
}

const RULES: Record<Rule['type'], { title: string; about: string }> = {
  LowConsumption: { title: 'Lavt forbrug', about: 'Når et døgns forbrug ligger under grænsen.' },
  HighConsumption: { title: 'Højt forbrug', about: 'Når et døgns forbrug ligger over grænsen.' },
  MissingData: { title: 'Manglende data', about: 'Når der ikke kommer nye komplette døgn.' },
  IndoorTooCold: {
    title: 'For koldt inde',
    about: 'Når varmepumpen måler under grænsen inde. Først Observer, derefter Alarm, hvis det varer ved; lukkes, når det er 1 °C over grænsen. Som udgangspunkt en frostalarm under 5 °C med Alarm efter 120 min.',
  },
  HeatPumpOffline: { title: 'Varmepumpe offline', about: 'Når varmepumpen ikke har meldt sig hos Sensibo. Som udgangspunkt efter 60 min.' },
};

// Starting values for rules a server without heat-pump support has not sent parameters for.
const HEAT_PUMP_DEFAULTS = { minTemperatureC: 5, sustainedMinutes: 120, offlineMinutes: 60 };

function RuleFields({ type, params, set }: { type: Rule['type']; params: Rule['params']; set: (patch: Partial<Rule['params']>) => void }) {
  const numeric = (value: string) => (value === '' ? 0 : Number(value));
  switch (type) {
    case 'MissingData':
      return <NumberField label="Uden nyt komplet døgn i" unit="timer" min={24} max={720} value={params.missingHours} onChange={(e) => set({ missingHours: numeric(e.target.value) })} />;
    case 'IndoorTooCold':
      return (
        <>
          <NumberField label="Alarm under" unit="°C" min={-10} max={25} step="0.5" value={params.minTemperatureC ?? HEAT_PUMP_DEFAULTS.minTemperatureC} onChange={(e) => set({ minTemperatureC: numeric(e.target.value) })} />
          <NumberField label="Alarm efter" unit="min under grænsen" min={0} max={1440} value={params.sustainedMinutes ?? HEAT_PUMP_DEFAULTS.sustainedMinutes} onChange={(e) => set({ sustainedMinutes: numeric(e.target.value) })} />
        </>
      );
    case 'HeatPumpOffline':
      return <NumberField label="Offline i" unit="min" min={15} max={1440} value={params.offlineMinutes ?? HEAT_PUMP_DEFAULTS.offlineMinutes} onChange={(e) => set({ offlineMinutes: numeric(e.target.value) })} />;
    default:
      return (
        <>
          <NumberField
            label={type === 'LowConsumption' ? 'Alarm under' : 'Alarm over'}
            unit="% af median"
            min={0}
            max={1000}
            value={params.thresholdPercent}
            onChange={(e) => set({ thresholdPercent: numeric(e.target.value) })}
          />
          <NumberField label="Mindste median" unit="kWh/døgn" min={0} max={1000} step="0.5" value={params.minBaselineKWh} onChange={(e) => set({ minBaselineKWh: numeric(e.target.value) })} />
          {type === 'LowConsumption' && (
            <NumberField label="Alarm efter" unit="døgn i træk" min={1} max={30} value={params.escalateAfterDays} onChange={(e) => set({ escalateAfterDays: numeric(e.target.value) })} />
          )}
        </>
      );
  }
}

function RuleRow({ propertyId, rule, onSaved }: { propertyId: string; rule: Rule; onSaved: () => void }) {
  const [draft, setDraft] = useState(rule);
  const [result, busy, run] = useAction();
  const titleId = useId();

  const set = (patch: Partial<Rule['params']>) => setDraft({ ...draft, params: { ...draft.params, ...patch } });
  const { title, about } = RULES[rule.type];

  return (
    <div className={draft.enabled ? 'rule' : 'rule rule-off'} role="group" aria-labelledby={titleId}>
      <div className="rule-head">
        <h4 id={titleId}>{title}</h4>
        <Switch checked={draft.enabled} onChange={(enabled) => setDraft({ ...draft, enabled })}>Aktiv</Switch>
      </div>
      <p className="hint">{about}</p>
      <div className="rule-fields">
        <RuleFields type={rule.type} params={draft.params} set={set} />
      </div>
      <div className="actions">
        <button type="button" className="button button-small" disabled={busy} onClick={async () => (await run(() => api.saveRule(propertyId, draft), 'Gemt.')) && onSaved()}>
          Gem regel
        </button>
        <ResultNote result={result} />
      </div>
    </div>
  );
}

function PropertySettings({ property, points, pricing, rulesVersion, hidden, onSaved }: { property: PropertySummary; points: Points; pricing: boolean; rulesVersion: number; hidden: boolean; onSaved: () => void }) {
  // Linking a heat pump adds rules, so the list is fetched again when links change.
  const rules = useLoad(() => api.rules(property.id), `rules|${property.id}|${rulesVersion}`);
  const [name, setName] = useState(property.name);
  const [monitoring, setMonitoring] = useState(property.monitoringEnabled);
  const [point, setPoint] = useState('');
  const [result, busy, run] = useAction();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleteResult, deleting, runDelete] = useAction();

  return (
    <section className="panel" hidden={hidden}>
      <PanelHead
        title={property.name}
        about={
          <>
            {property.hasMeteringPoint ? `Målepunkt ${property.meteringPoint}` : 'Intet målepunkt er tilknyttet'} · <a href={`#/ejendom/${property.id}`}>Vis forbrug</a>
          </>
        }
      >
        <StatusBadge status={property.status} label={property.statusLabel} />
      </PanelHead>

      <div className="card">
        <h3>Ejendom</h3>
        <div className="fields">
          <label className="field">
            Navn
            <input type="text" maxLength={40} value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          <MeteringPointField
            value={point}
            onChange={setPoint}
            empty={property.hasMeteringPoint ? `Behold nuværende (${property.meteringPoint})` : 'Ikke tilknyttet'}
            points={points}
          />
        </div>
        {point !== '' && property.hasMeteringPoint && (
          <p className="note note-warn">Når målepunktet skiftes, slettes ejendommens hidtidige forbrugsdata og alarmer, og historikken hentes igen.</p>
        )}
        <div className="switch-row">
          <Switch checked={monitoring} onChange={setMonitoring}>Overvågning aktiv</Switch>
          <p className="hint">Slå fra for at sætte overvågningen på pause.</p>
        </div>
        <div className="actions">
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
      </div>

      <div className="card">
        <h3>Alarmgrænser</h3>
        <p className="hint">Grænserne er startværdier til kalibrering. Et lavt forbrug er kun en indikator på mulig fejl.</p>
        {rules.error && <ErrorNote message={rules.error} onRetry={rules.reload} />}
        <div className="rules">
          {rules.data?.map((r) => <RuleRow key={r.id + JSON.stringify(r)} propertyId={property.id} rule={r} onSaved={rules.reload} />)}
        </div>
      </div>

      {pricing && <PricingSettings id={property.id} />}

      <div className="card danger-zone">
        <h3>Farezone</h3>
        {confirmDelete ? (
          <div className="confirm">
            <p>Slet alle forbrugsdata og alarmer for {property.name}? Historikken hentes igen ved næste synkronisering.</p>
            <button type="button" className="button button-danger" disabled={deleting} onClick={async () => { await runDelete(() => api.deleteData(property.id), 'Data er slettet.'); setConfirmDelete(false); onSaved(); }}>
              Ja, slet data
            </button>
            <button type="button" className="button button-secondary" onClick={() => setConfirmDelete(false)}>Fortryd</button>
          </div>
        ) : (
          <div className="danger-row">
            <p className="hint">Sletter alle forbrugsdata og alarmer for {property.name}. Historikken hentes igen ved næste synkronisering.</p>
            <button type="button" className="button button-danger-outline" onClick={() => setConfirmDelete(true)}>Slet forbrugsdata…</button>
          </div>
        )}
        <ResultNote result={deleteResult} />
      </div>
    </section>
  );
}

function NewProperty({ points, hidden, onCreated }: { points: Points; hidden: boolean; onCreated: (id: string) => void }) {
  const [name, setName] = useState('');
  const [point, setPoint] = useState('');
  const [result, busy, run] = useAction();
  return (
    <section className="panel" hidden={hidden}>
      <PanelHead title="Ny ejendom" about="Opret en ejendom og knyt den til et målepunkt fra Eloverblik." />
      <div className="card">
        <div className="fields">
          <label className="field">
            Navn
            <input type="text" maxLength={40} value={name} placeholder="f.eks. Sommerhus" onChange={(e) => setName(e.target.value)} />
          </label>
          <MeteringPointField value={point} onChange={setPoint} empty="Vælg senere" points={points} />
        </div>
        <div className="actions">
          <button
            type="button"
            className="button"
            disabled={busy || name.trim() === ''}
            onClick={async () => {
              let id = '';
              if (await run(async () => { id = (await api.createProperty(name, point || null)).id; }, 'Ejendommen er oprettet.')) {
                setName('');
                setPoint('');
                onCreated(id);
              }
            }}
          >
            Opret ejendom
          </button>
          <ResultNote result={result} />
        </div>
      </div>
    </section>
  );
}

function Notifications({ hidden }: { hidden: boolean }) {
  const settings = useLoad(() => api.notificationSettings(), 'notification-settings');
  return (
    <section className="panel" hidden={hidden}>
      <PanelHead title="Notifikationer" about="Beskeder sendes via Telegram." />
      {settings.error && <ErrorNote message={settings.error} onRetry={settings.reload} />}
      {/* The form starts from the stored settings, so it is only mounted once they are known. */}
      {settings.data ? <NotificationForm initial={settings.data} /> : !settings.error && <p className="muted">Henter…</p>}
    </section>
  );
}

function NotificationForm({ initial }: { initial: NotificationSettings }) {
  const [daily, setDaily] = useState(initial.dailyReportEnabled);
  const [recipients, setRecipients] = useState(initial.recipients.join(', '));
  const [result, busy, run] = useAction();
  const [testResult, testing, runTest] = useAction();
  const [preview, setPreview] = useState<string | null>(null);

  return (
    <>
      <div className="card">
        <h3>Modtagere og rapport</h3>
        <div className="fields">
          <label className="field field-narrow">
            Modtagere
            <input type="text" inputMode="text" value={recipients} onChange={(e) => setRecipients(e.target.value)} />
            <span className="hint">Telegram chat-id’er, adskilt med komma.</span>
          </label>
        </div>
        <div className="switch-row">
          <Switch checked={daily} onChange={setDaily}>Send daglig statusrapport kl. 09:00</Switch>
        </div>
        <div className="actions">
          <button
            type="button"
            className="button"
            disabled={busy}
            onClick={() => run(() => api.saveNotificationSettings({ dailyReportEnabled: daily, recipients: recipients.split(',').map((r) => r.trim()).filter(Boolean) }), 'Gemt.')}
          >
            Gem
          </button>
          <ResultNote result={result} />
        </div>
      </div>

      <div className="card">
        <h3>Afprøvning</h3>
        <p className="hint">Send en testbesked til modtagerne, eller se den rapport, der sendes i dag.</p>
        <div className="actions">
          <button type="button" className="button button-secondary" disabled={testing} onClick={() => runTest(() => api.testNotification(), 'Testbeskeden er sendt.')}>
            Send testbesked
          </button>
          <button type="button" className="button button-secondary" onClick={async () => setPreview((await api.reportPreview().catch(() => ({ text: 'Rapporten kunne ikke hentes.' }))).text)}>
            Vis dagens rapport
          </button>
          <ResultNote result={testResult} />
        </div>
        {preview && <pre className="preview">{preview}</pre>}
      </div>
    </>
  );
}

function heatPumpFacts(h: HeatPumpStatus): { label: string; fact: Fact }[] {
  const count = h.devices.length === 1 ? '1 varmepumpe' : `${h.devices.length} varmepumper`;
  return [
    {
      label: 'Forbindelse',
      fact: !h.configured
        ? { value: 'Ikke sat op', detail: 'Serveren mangler en Sensibo API-nøgle', tone: 'warn' }
        : h.lastError
          ? { value: 'Fejl', detail: details(codeText(h.lastError), h.lastAttemptAtUtc !== null && `Seneste forsøg ${dateTime(h.lastAttemptAtUtc)}`), tone: 'error' }
          : h.lastSuccessAtUtc
            ? { value: 'Forbundet', detail: count, tone: 'ok' }
            : { value: 'Venter på første måling', tone: 'neutral' },
    },
    {
      label: 'Sidste måling',
      fact: h.lastSuccessAtUtc
        ? { value: dateTime(h.lastSuccessAtUtc), detail: `Hentes hvert ${h.intervalMinutes}. minut`, tone: 'ok' }
        : { value: 'Ingen endnu', tone: 'neutral' },
    },
    {
      label: 'Historik siden',
      fact: h.oldestUtc
        ? { value: fullDate(new Date(h.oldestUtc).getTime()), detail: 'Uge, måned og år kan vises', tone: 'ok' }
        : { value: 'Ingen endnu', detail: 'Kun Sensibos seneste døgn kan vises', tone: 'neutral' },
    },
  ];
}

function HeatPumpLinks({ devices, properties, onSaved }: { devices: HeatPumpDevice[]; properties: PropertySummary[]; onSaved: () => void }) {
  const [links, setLinks] = useState<Record<string, string>>(() => Object.fromEntries(devices.map((d) => [d.id, d.propertyId ?? ''])));
  const [result, busy, run] = useAction();
  const save = () => api.saveHeatpumpLinks(devices.map((d) => ({ deviceId: d.id, propertyId: links[d.id] || null })));

  return (
    <div className="card">
      <h3>Ejendomme</h3>
      <p className="hint">
        En varmepumpe, der hører til en ejendom, vises på ejendommens side, og ejendommen får alarmerne For koldt inde og Varmepumpe offline.
      </p>
      {devices.length === 0 ? (
        <p className="empty">Ingen varmepumper fundet endnu.</p>
      ) : (
        <div className="fields">
          {devices.map((d) => (
            <label key={d.id} className="field">
              {d.name}
              <select value={links[d.id] ?? ''} onChange={(e) => setLinks({ ...links, [d.id]: e.target.value })}>
                <option value="">Ingen</option>
                {properties.map((p) => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </label>
          ))}
        </div>
      )}
      <div className="actions">
        <button type="button" className="button" disabled={busy || devices.length === 0} onClick={async () => (await run(save, 'Gemt.')) && onSaved()}>
          Gem tilknytning
        </button>
        <ResultNote result={result} />
      </div>
    </div>
  );
}

function HeatPumpSettings({ status, properties, hidden, onLinked }: { status: Loaded<HeatPumpStatus | null>; properties: PropertySummary[]; hidden: boolean; onLinked: () => void }) {
  const [result, busy, run] = useAction();
  const h = status.data;
  return (
    <section className="panel" hidden={hidden}>
      <PanelHead title="Varmepumpe" about="Målinger fra Sensibo, og hvilken ejendom hver varmepumpe hører til.">
        <button
          type="button"
          className="button"
          disabled={busy || !h?.configured}
          onClick={async () => {
            if (await run(() => api.collectHeatpump(), 'Målingerne hentes nu.')) window.setTimeout(status.reload, 4000);
          }}
        >
          Hent nu
        </button>
      </PanelHead>
      <ResultNote result={result} />
      {status.error && <ErrorNote message={status.error} onRetry={status.reload} />}
      {h && <Tiles items={heatPumpFacts(h)} />}
      {h && (
        <HeatPumpLinks
          // A device that appears later gets a fresh form; saving keeps the form and its result.
          key={h.devices.map((d) => d.id).join('|')}
          devices={h.devices}
          properties={properties}
          onSaved={() => {
            status.reload();
            onLinked();
          }}
        />
      )}
    </section>
  );
}

const ICONS = {
  drift: <path d="M3 12h4l2.5-6 5 12 2.5-6H21" />,
  bell: (
    <>
      <path d="M6 16v-5a6 6 0 0 1 12 0v5l1.5 2h-15z" />
      <path d="M10 20.5a2.2 2.2 0 0 0 4 0" />
    </>
  ),
  house: (
    <>
      <path d="M4 11.5 12 4.5l8 7" />
      <path d="M6.5 9.8V19.5h11V9.8" />
      <path d="M10.2 19.5v-5h3.6v5" />
    </>
  ),
  plus: <path d="M12 6v12M6 12h12" />,
  heatpump: (
    <>
      <rect x="3" y="5" width="18" height="12" rx="2" />
      <circle cx="10" cy="11" r="3.5" />
      <path d="M16.5 8.5v5M6 17v2.5M18 17v2.5" />
    </>
  ),
};

/** The coloured dot repeats what the second line says in words, so colour is never the only signal. */
interface MenuState { text: string; tone?: Tone }

function MenuLink({ section, active, icon, label, state }: { section: string; active: string; icon: keyof typeof ICONS; label: string; state?: MenuState }) {
  return (
    <a href={sectionHref(section)} aria-current={section === active ? 'page' : undefined}>
      <span className={icon === 'plus' ? 'nav-icon nav-icon-add' : 'nav-icon'} aria-hidden="true">
        <svg viewBox="0 0 24 24">{ICONS[icon]}</svg>
        {state?.tone && <span className={`nav-dot nav-dot-${state.tone}`} />}
      </span>
      <span className="nav-text">
        <span className="nav-label">{label}</span>
        {state && <>{' '}<span className="nav-sub">{state.text}</span></>}
      </span>
    </a>
  );
}

function driftState(h: Health | null): MenuState | undefined {
  if (!h) return undefined;
  if (h.token.state === 'missing' || h.token.state === 'expired') return { text: 'Adgangsnøglen skal fornyes', tone: 'error' };
  if (h.syncRunning || h.lastSync?.status === 'Running') return { text: 'Synkroniserer…', tone: 'neutral' };
  if (h.lastSync?.status === 'Failed') return { text: 'Synkronisering fejlede', tone: 'error' };
  if (h.token.state === 'expiring') return { text: `Adgangsnøglen udløber om ${h.token.daysLeft} dage`, tone: 'warn' };
  return { text: h.lastSuccessfulSyncUtc ? `Synkroniseret ${dateTime(h.lastSuccessfulSyncUtc)}` : 'Ikke synkroniseret endnu' };
}

function notificationState(h: Health | null): MenuState | undefined {
  if (!h) return undefined;
  if (!h.notifications.configured) return { text: 'Ikke sat op', tone: 'warn' };
  if (h.notifications.pending > 0) return { text: `${h.notifications.pending} afventer afsendelse`, tone: 'warn' };
  return { text: 'Telegram er sat op' };
}

function heatPumpState(h: HeatPumpStatus | null): MenuState | undefined {
  if (!h) return undefined;
  if (!h.configured) return { text: 'Ikke sat op', tone: 'warn' };
  if (h.lastError) return { text: 'Hentning fejlede', tone: 'error' };
  return { text: h.lastSuccessAtUtc ? `Målt ${dateTime(h.lastSuccessAtUtc)}` : 'Ingen målinger endnu' };
}

const STATUS_TONE: Record<PropertySummary['status'], Tone> = { Normal: 'ok', Observer: 'warn', Alarm: 'error', DataMissing: 'neutral' };

function propertyState(p: PropertySummary): MenuState {
  if (!p.monitoringEnabled) return { text: 'På pause', tone: 'neutral' };
  if (!p.hasMeteringPoint) return { text: 'Intet målepunkt', tone: 'neutral' };
  return { text: `${p.statusLabel} · ${p.meteringPoint}`, tone: STATUS_TONE[p.status] };
}

export function Settings({ me, section }: { me: Me; section?: string }) {
  const features = useLoad(() => api.features(), 'features');
  const heatpumpOn = features.data?.heatpump ?? false;
  const heatpump = useLoad(() => (heatpumpOn ? api.heatpump() : Promise.resolve(null)), `heatpump|${heatpumpOn}`);
  const [linksSaved, setLinksSaved] = useState(0);
  const admin = me.role === 'admin';
  const properties = useLoad(() => api.properties(), 'properties');
  // Loaded once here: the Drift panel shows it in full, the menu summarises it.
  const health = useLoad(() => api.health(), 'health');
  const [pointList, setPointList] = useState<MeteringPoint[] | null>(null);
  const [pointsResult, loadingPoints, runPoints] = useAction();
  // A property that was just created is not in the list until it has been fetched again.
  const [created, setCreated] = useState<string | null>(null);

  const points: Points = {
    list: pointList,
    busy: loadingPoints,
    error: pointsResult && !pointsResult.ok ? pointsResult.text : null,
    load: () => void runPoints(async () => setPointList(await api.meteringPoints()), ''),
  };

  const list = properties.data;
  const known = (wanted: string) =>
    list ? list.some((p) => propertySection(p.id) === wanted) || wanted === propertySection(created ?? '') : !properties.error;
  const active =
    admin &&
    section &&
    (section === NOTIFICATIONS ||
      section === NEW_PROPERTY ||
      (section === HEAT_PUMP && (heatpumpOn || features.loading)) ||
      (section.startsWith('ejendom/') && known(section)))
      ? section
      : DRIFT;
  const waiting =
    (active.startsWith('ejendom/') && !list?.some((p) => propertySection(p.id) === active)) || (active === HEAT_PUMP && !heatpumpOn);

  // On a phone the menu is a row that scrolls sideways; keep the current section in view.
  const menu = useRef<HTMLElement | null>(null);
  const count = list?.length ?? 0;
  useEffect(() => {
    const nav = menu.current;
    const current = nav?.querySelector<HTMLElement>('[aria-current="page"]');
    if (nav && current) nav.scrollLeft = current.offsetLeft - (nav.clientWidth - current.offsetWidth) / 2;
  }, [active, count]);

  return (
    <>
      <h1>Indstillinger</h1>
      <div className={admin ? 'settings' : undefined}>
        {admin && (
          <nav className="settings-nav" aria-label="Indstillinger" ref={menu}>
            <p className="nav-group">System</p>
            <MenuLink section={DRIFT} active={active} icon="drift" label="Drift" state={driftState(health.data)} />
            <MenuLink section={NOTIFICATIONS} active={active} icon="bell" label="Notifikationer" state={notificationState(health.data)} />
            {heatpumpOn && <MenuLink section={HEAT_PUMP} active={active} icon="heatpump" label="Varmepumpe" state={heatPumpState(heatpump.data)} />}
            <p className="nav-group">Ejendomme</p>
            {list?.map((p) => (
              <MenuLink key={p.id} section={propertySection(p.id)} active={active} icon="house" label={p.name} state={propertyState(p)} />
            ))}
            <MenuLink section={NEW_PROPERTY} active={active} icon="plus" label="Ny ejendom" />
          </nav>
        )}
        <div className="settings-body">
          {admin && properties.error && <ErrorNote message={properties.error} onRetry={properties.reload} />}
          <Operations admin={admin} health={health} hidden={active !== DRIFT} />
          {!admin && <p className="note note-info">Du er logget ind som læser. Kun administratorer kan ændre indstillinger.</p>}
          {admin && (
            <>
              {waiting && <p className="muted">Henter…</p>}
              {list?.map((p) => (
                <PropertySettings
                  key={p.id}
                  property={p}
                  points={points}
                  pricing={features.data?.pricing ?? false}
                  rulesVersion={linksSaved}
                  hidden={active !== propertySection(p.id)}
                  onSaved={properties.reload}
                />
              ))}
              <NewProperty
                points={points}
                hidden={active !== NEW_PROPERTY}
                onCreated={(id) => {
                  setCreated(id);
                  properties.reload();
                  window.location.hash = sectionHref(propertySection(id));
                }}
              />
              <Notifications hidden={active !== NOTIFICATIONS} />
              {heatpumpOn && (
                <HeatPumpSettings
                  status={heatpump}
                  properties={list ?? []}
                  hidden={active !== HEAT_PUMP}
                  onLinked={() => setLinksSaved((n) => n + 1)}
                />
              )}
            </>
          )}
        </div>
      </div>
    </>
  );
}
