import { describe, expect, it } from 'vitest';
import { isRecentlyAdded } from './offer-recency';

describe('Offer addition recency', () => {
  const now = Date.parse('2026-10-08T12:00:00Z');
  it('includes the rolling five-day boundary and excludes older, future and unknown dates', () => {
    expect(isRecentlyAdded('2026-10-08T12:00:00Z', now)).toBe(true);
    expect(isRecentlyAdded('2026-10-03T12:00:00Z', now)).toBe(true);
    expect(isRecentlyAdded('2026-10-03T11:59:59.999Z', now)).toBe(false);
    expect(isRecentlyAdded('2026-10-08T12:00:00.001Z', now)).toBe(false);
    expect(isRecentlyAdded(null, now)).toBe(false);
    expect(isRecentlyAdded('invalid', now)).toBe(false);
  });
  it('handles equivalent offsets and expires an offer as time moves past five days', () => {
    const added = '2026-10-03T15:00:00+03:00';
    expect(isRecentlyAdded(added, now)).toBe(true);
    expect(isRecentlyAdded(added, now + 60_000)).toBe(false);
  });
});
