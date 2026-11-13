// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, type Estimate, type PropertySummary } from './api';
import { PropertyCard } from './Overview';
import { CostSummary, danishMidnightUtc, EstimateCard, insertAgreement } from './Pricing';

const sommerhus: PropertySummary = {
  id: 'sommerhus', name: 'Sommerhus', monitoringEnabled: true, hasMeteringPoint: true, meteringPoint: '…0028',
  status: 'Normal', statusLabel: 'Normal', hasBaseline: true,
  latestComplete: { date: '2026-11-06', kWh: 1.2, quality: 'Complete', expectedIntervals: 24, receivedIntervals: 24 },
  baseline: 7.4, deviationKWh: -6.2, deviationPercent: -83.8, sum7: 45.6, sum30: 300,
  pendingDays: [], isStale: false, dataAgeHours: 32.1, lastSuccessfulSyncUtc: '2026-11-08T07:05:00Z',
  openAlerts: 0, sparkline: [],
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
it('keeps a partial known subtotal distinct and labels estimation independently', () => {
  render(<CostSummary result={{ total: null, knownSubtotal: 12.345, status: 'Partial', isEstimated: true, consumptionCoverage: 0.5, priceCoverage: 0.25, knownKWh: 3, missing: ['spot'], sources: ['Energinet'], components: { Energy: 10, Vat: 2.345 } }} />);
  expect(screen.getByText(/Kendt delbeløb/)).toBeTruthy();
  expect(screen.getByText(/Estimeret fordeling/)).toBeTruthy();
  expect(screen.getByText(/Spotpriser/)).toBeTruthy();
  expect(screen.queryByText('I alt')).toBeNull();
});
it('preserves a negative complete amount', () => {
  render(<CostSummary result={{ total: -2, knownSubtotal: -2, status: 'Calculated', isEstimated: false, consumptionCoverage: 1, priceCoverage: 1, knownKWh: 3, missing: [], sources: [], components: { Energy: -2 } }} />);
  expect(screen.getByText(/I alt/)).toBeTruthy();
  expect(screen.getAllByText(/-2,00/).length).toBeGreaterThan(0);
});

it('uses the offset at midnight on both DST transition days', () => {
  expect(danishMidnightUtc('2026-03-29')).toBe('2026-03-28T23:00:00.000Z');
  expect(danishMidnightUtc('2026-10-25')).toBe('2026-10-24T22:00:00.000Z');
});
it('replaces equal instants with different ISO formats and preserves future terms', () => {
  const a = { fromUtc: '2026-01-01T00:00:00+00:00', toUtc: '2026-06-01T00:00:00+00:00', area: 'DK1', kind: 'Spot', fixedPrice: null, supplierMarkup: 0, dailySubscription: 0, settlementMinutes: 15, vatRate: 0.25 };
  const future = { ...a, fromUtc: '2026-06-01T00:00:00+00:00', toUtc: null };
  const result = insertAgreement([a, future], { ...a, fromUtc: '2026-01-01T00:00:00.000Z', supplierMarkup: 0.1 });
  expect(result).toHaveLength(2);
  expect(result[0].toUtc).toBe(future.fromUtc);
  expect(result[1]).toEqual(future);
});

describe('Ca. pris', () => {
  const estimate: Estimate = {
    fromUtc: '2026-10-13T22:00:00Z', toUtc: '2026-11-12T23:00:00Z', total: 1950.4, kWh: 704.3, consumptionCoverage: 1,
    components: { Energy: 1086, Supplier: 82.4, Network: 289, Tax: 5.6, Subscription: 76.6, Vat: 410.3 },
    assumptions: ['current_tariffs'],
  };

  it('shows the total incl. VAT, the price per kWh, the breakdown and what is assumed', async () => {
    vi.spyOn(api, 'estimate').mockResolvedValue(estimate);
    render(<EstimateCard id="hus" from="2026-10-14" to="2026-11-12" />);

    expect(await screen.findByText('ca. 1.950 kr')).toBeTruthy();
    expect(screen.getByText(/704 kWh · ca\. 2,77 kr\/kWh/)).toBeTruthy();
    expect(screen.getByText('Spotpris')).toBeTruthy();
    expect(screen.getByText('Nettarif')).toBeTruthy();
    expect(screen.getByText(/Eloverbliks aktuelle satser/)).toBeTruthy();
    expect(screen.queryByText(/Elselskabets tillæg er ikke registreret/)).toBeNull();
  });

  it('says when the supplier terms are missing', async () => {
    vi.spyOn(api, 'estimate').mockResolvedValue({ ...estimate, assumptions: ['current_tariffs', 'supplier_markup_missing', 'supplier_subscription_missing'] });
    render(<EstimateCard id="hus" from="2026-10-14" to="2026-11-12" />);

    expect(await screen.findByText(/Elselskabets tillæg og abonnement er ikke registreret/)).toBeTruthy();
  });

  it('puts the approximate amounts on the overview card', async () => {
    vi.spyOn(api, 'estimate').mockImplementation(async (_id, from) => ({ ...estimate, total: from === '2026-11-06' ? 24.2 : from === '2026-10-31' ? 450 : 1950 }));
    render(<PropertyCard p={sommerhus} pricing />);

    expect(await screen.findByText('ca. 24 kr')).toBeTruthy();
    expect(await screen.findByText('ca. 450 kr')).toBeTruthy();
    expect(await screen.findByText('ca. 1.950 kr')).toBeTruthy();
  });
});
