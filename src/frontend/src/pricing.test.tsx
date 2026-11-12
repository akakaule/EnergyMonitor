// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { CostSummary, danishMidnightUtc, insertAgreement } from './Pricing';

afterEach(cleanup);
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
