import { useState } from 'react';
import { api, errorText } from './api';
import { ErrorNote, useLoad } from './common';
import { today } from './format';

export function danishMidnightUtc(date: string): string {
  const midnight = new Date(`${date}T00:00:00Z`);
  const offset = Number(new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Copenhagen', hour: 'numeric', hourCycle: 'h23' }).format(midnight));
  return new Date(midnight.getTime() - offset * 3_600_000).toISOString();
}

export function insertAgreement(existing: Agreement[], agreement: Agreement): Agreement[] {
  const start = Date.parse(agreement.fromUtc);
  const future = existing.filter(a => Date.parse(a.fromUtc) > start).sort((a, b) => Date.parse(a.fromUtc) - Date.parse(b.fromUtc));
  const earlier = existing.filter(a => Date.parse(a.fromUtc) < start).map(a => ({ ...a, toUtc: a.toUtc === null || Date.parse(a.toUtc) > start ? agreement.fromUtc : a.toUtc }));
  return [...earlier, { ...agreement, toUtc: future[0]?.fromUtc ?? null }, ...future];
}

export interface CostResult {
  total: number | null;
  knownSubtotal: number;
  knownKWh: number;
  status: string;
  isEstimated: boolean;
  consumptionCoverage: number;
  priceCoverage: number;
  components: Record<string, number>;
  missing: string[];
  sources: string[];
}
export interface Agreement {
  fromUtc: string; toUtc: string | null; area: string; kind: string;
  fixedPrice: number | null; supplierMarkup: number | null; dailySubscription: number | null;
  settlementMinutes: number; vatRate: number;
}
export interface TariffRate {
  code: string; owner: string; component: string; fromUtc: string; toUtc: string;
  price: number | null; unit: string; vat: string; source: string; fromHour: number; toHour: number;
}
export interface PricingConfiguration {
  tariffMappings?: { code: string; owner: string; type: string; component: string; unit: string; vat: string; fromUtc: string; toUtc: string }[];
  agreements: Agreement[]; rates: TariffRate[]; requiredTariffCodes: string[]; tariffsConfirmed: boolean;
}
const money = (n: number) => new Intl.NumberFormat('da-DK', { style: 'currency', currency: 'DKK' }).format(n);
const labels: Record<string, string> = { Energy: 'Energi', Supplier: 'Leverandørtillæg', Network: 'Net', Tax: 'Afgifter', Vat: 'Moms', Subscription: 'Abonnementer' };
const missingLabels: Record<string, string> = { spot: 'Spotpriser', agreement: 'Elaftale', supplier: 'Leverandørtillæg', subscription: 'Abonnement', consumption: 'Forbrug', tariff_inventory: 'Bekræftede tariffer og afgifter', vat: 'Momsbehandling' };
export function CostSummary({ result: r }: { result: CostResult }) {
  return <section className="card">
    <h2>Omkostninger · {{ Calculated: 'Beregnet', Estimated: 'Estimeret', Partial: 'Delvis', Unavailable: 'Utilgængelig' }[r.status] ?? r.status}</h2>
    <p><strong>{r.total === null ? 'Kendt delbeløb' : 'I alt'}: {money(r.total ?? r.knownSubtotal)}</strong></p>
    <p className="small">Kendt forbrug: {r.knownKWh.toLocaleString('da-DK')} kWh · Forbrugsdækning: {Math.round(r.consumptionCoverage * 100)} % · Energiprisdækning: {Math.round(r.priceCoverage * 100)} %</p>
    <dl className="stats">{Object.entries(r.components).map(([key, value]) => <div key={key}><dt>{labels[key] ?? key}</dt><dd>{money(value)}</dd></div>)}</dl>
    {r.isEstimated && <p className="note note-info">Estimeret fordeling: grovere målinger er fordelt jævnt på prisintervaller, eller kilden har estimeret forbruget.</p>}
    {r.missing.length > 0 && <p className="note note-info">Mangler: {r.missing.map(key => missingLabels[key] ?? key.replace('tariff:', 'Tarif ')).join(', ')}. En fuld total kan ikke beregnes.</p>}
    <p className="muted small">Efter registrerede vilkår; ikke afstemt med en faktura. Abonnementer fordeles pr. dansk kalenderdag. Afrunding sker ved visning.</p>
    <p className="muted small">Kilder: {r.sources.join(', ') || 'Ingen'}</p>
  </section>;
}
export function PropertyCosts({ id, from, to }: { id: string; from: string; to: string }) {
  const costs = useLoad(() => api.costs(id, from, to), `${id}|${from}|${to}`);
  const status = useLoad(() => api.priceStatus(id, from, to), `${id}|${from}|${to}`);
  if (costs.error) return <ErrorNote message={costs.error} onRetry={costs.reload} />;
  return <>{costs.data ? <CostSummary result={costs.data} /> : <p>Henter omkostninger…</p>}
    {status.data?.sources.map(s => <p className="muted small" key={s.source}>{s.source}: senest hentet {s.lastSuccessUtc ? new Date(s.lastSuccessUtc).toLocaleString('da-DK', { timeZone: 'Europe/Copenhagen' }) : 'aldrig'}{s.errorCode ? ` · ${s.errorCode}` : ''}</p>)}
    {status.error && <ErrorNote message={status.error} onRetry={status.reload} />}</>;
}

export function PricingSettings({ id, name }: { id: string; name: string }) {
  const config = useLoad(() => api.pricing(id), id);
  return <section className="card"><h2>Elaftale · {name}</h2>
    {config.error && <ErrorNote message={config.error} onRetry={config.reload} />}
    {config.data && <AgreementEditor key={JSON.stringify(config.data)} id={id} initial={config.data} saved={config.reload} />}
  </section>;
}
function AgreementEditor({ id, initial, saved }: { id: string; initial: PricingConfiguration; saved: () => void }) {
  const [from, setFrom] = useState(today());
  const [area, setArea] = useState(initial.agreements.at(-1)?.area ?? 'DK1');
  const [kind, setKind] = useState('Spot');
  const [fixed, setFixed] = useState('');
  const [markup, setMarkup] = useState('');
  const [subscription, setSubscription] = useState('');
  const [settlement, setSettlement] = useState(15);
  const [rates, setRates] = useState(JSON.stringify(initial.rates, null, 2));
  const [mappings, setMappings] = useState(JSON.stringify(initial.tariffMappings ?? [], null, 2));
  const [codes, setCodes] = useState(initial.requiredTariffCodes.join(', '));
  const [confirmed, setConfirmed] = useState(initial.tariffsConfirmed);
  const [result, setResult] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setResult('');
    try {
      const fromUtc = danishMidnightUtc(from);
      const agreements = insertAgreement(initial.agreements, { fromUtc, toUtc: null, area, kind, fixedPrice: kind === 'Fixed' && fixed !== '' ? Number(fixed) : null, supplierMarkup: markup === '' ? null : Number(markup), dailySubscription: subscription === '' ? null : Number(subscription), settlementMinutes: settlement, vatRate: 0.25 });
      await api.savePricing(id, { agreements, rates: JSON.parse(rates) as TariffRate[], tariffMappings: JSON.parse(mappings) as PricingConfiguration['tariffMappings'], requiredTariffCodes: codes.split(',').map(s => s.trim()).filter(Boolean), tariffsConfirmed: confirmed });
      setResult('Gemt. Tidligere vilkår bevares frem til gyldighedsdatoen.'); saved();
    } catch (e) { setResult(e instanceof SyntaxError ? 'Tariffer skal være gyldig JSON.' : errorText(e)); }
    finally { setBusy(false); }
  };
  return <div className="form">
    <p className="muted small">Beløb er DKK ekskl. 25 % moms. Tomme tillæg og abonnementer forbliver ukendte. Registrér ændringer fra deres faktiske gyldighedsdato.</p>
    <ul>{initial.agreements.map(a => <li key={a.fromUtc}>{a.fromUtc.slice(0, 10)} – {a.toUtc?.slice(0, 10) ?? 'fortsat'} · {a.area} · {a.kind}</li>)}</ul>
    <label>Gyldig fra (dansk dato)<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
    <label>Prisområde<select value={area} onChange={e => setArea(e.target.value)}><option>DK1</option><option>DK2</option></select></label>
    <label>Aftale<select value={kind} onChange={e => setKind(e.target.value)}><option value="Spot">Spotpris</option><option value="Fixed">Fastpris</option></select></label>
    {kind === 'Fixed' && <label>Fastpris DKK/kWh<input type="number" step="any" value={fixed} onChange={e => setFixed(e.target.value)} /></label>}
    <label>Leverandørtillæg DKK/kWh<input type="number" step="any" value={markup} onChange={e => setMarkup(e.target.value)} /></label>
    <label>Leverandørabonnement DKK pr. dag<input type="number" min="0" step="any" value={subscription} onChange={e => setSubscription(e.target.value)} /></label>
    <label>Afregningsinterval<select value={settlement} onChange={e => setSettlement(Number(e.target.value))}><option value={15}>Kvarter</option><option value={60}>Time</option><option value={1440}>Døgn</option></select></label>
    <details><summary>Tariffer og afgifter med historik</summary>
      <p className="small">Registrér verificerede satser fra netoperatør eller Eloverblik. Én kode pr. afgift på tværs af kilder; gyldighed til er eksklusiv. Enhed: kWh eller day. Moms: Excluded, Included, Exempt eller Unknown. Tidsbånd: fromHour/toHour i dansk tid.</p>
      <label>Påkrævede tarifkoder (kommasepareret)<input value={codes} onChange={e => setCodes(e.target.value)} /></label>
      <label>Verificeret fortolkning af Eloverblik-koder (JSON)<textarea rows={6} value={mappings} onChange={e => setMappings(e.target.value)} /></label>
      <p className="small">Felter: code, owner, type (som i kilden), component (Network/Tax/Subscription), unit (kWh/day), vat (Excluded/Included/Exempt), fromUtc og toUtc. Efter gem kræves synkronisering.</p>
      <label>Tarifsatser (JSON)<textarea rows={12} value={rates} onChange={e => setRates(e.target.value)} /></label>
      <p className="small">Eksempel: {JSON.stringify({ code: 'net', owner: 'netselskab', component: 'Network', fromUtc: '2026-01-01T00:00:00Z', toUtc: '2027-01-01T00:00:00Z', price: 0.2, unit: 'kWh', vat: 'Excluded', source: 'manual', fromHour: 0, toHour: 24 })}</p>
      <label><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />Alle relevante tariffer, afgifter og abonnementer er verificeret og registreret</label>
    </details>
    <button className="button" type="button" disabled={busy || !from} onClick={save}>Gem elaftale og tariffer</button>
    {result && <p role="status">{result}</p>}
  </div>;
}
