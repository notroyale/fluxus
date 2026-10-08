import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuctionHistory } from './auction-history';
import { OfferTrackingApi, parseHistory, parseShortlist } from './offer-tracking-api';
import { OfferChanges } from './offer-changes';
import { SaveOffer } from './save-offer';
import { readShortlist, shortlistKey, ShortlistStore } from './shortlist-store';
import { offer } from './test-data';

const history = {
  auctionId: offer.auctionId,
  total: 1,
  truncated: false,
  observations: [
    {
      id: 'change-1',
      observedAt: '2026-10-08T10:00:00Z',
      fixtureMode: false,
      kind: 'changed',
      status: 'ended',
      previousStatus: 'running',
      initialPriceEur: 8000,
      previousInitialPriceEur: 10000,
      lastBidEur: null,
      previousLastBidEur: null,
      endsAt: offer.endsAt,
      previousEndsAt: offer.endsAt,
      offerCount: 1,
      priceChanged: true,
      bidChanged: false,
      statusChanged: true,
      deadlineChanged: false,
      offersChanged: false,
    },
  ],
};
describe('Offer tracking', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('validates history dates, old prices and change flags before rendering', () => {
    expect(parseHistory(history).observations[0].previousInitialPriceEur).toBe(10000);
    expect(() =>
      parseHistory({
        ...history,
        observations: [{ ...history.observations[0], observedAt: 'invalid' }],
      }),
    ).toThrow();
    expect(() =>
      parseHistory({
        ...history,
        observations: [{ ...history.observations[0], previousInitialPriceEur: '10000' }],
      }),
    ).toThrow();
    expect(() =>
      parseHistory({
        ...history,
        observations: [{ ...history.observations[0], statusChanged: 'yes' }],
      }),
    ).toThrow();
    expect(() =>
      parseShortlist({ items: [{ ...offer, isPresent: 'yes' }], missingIds: [] }),
    ).toThrow();
  });

  it('rejects a shortlist response with unexpected or duplicate IDs', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ items: [offer], missingIds: [offer.id] }))),
    );
    TestBed.configureTestingModule({});
    const api = TestBed.inject(OfferTrackingApi);
    await expect(api.shortlist([offer.id], new AbortController().signal)).rejects.toThrow(
      'nesutampa',
    );
  });

  it('reads corrupt storage safely and validates, deduplicates and limits saved IDs', () => {
    expect(readShortlist('{bad')).toEqual([]);
    expect(readShortlist('{}')).toEqual([]);
    expect(
      readShortlist(
        JSON.stringify(['one', 'one', null, '<script>', 'two', 'three', 'four', 'five']),
      ),
    ).toEqual(['one', 'two', 'three', 'four']);
  });

  it('updates saved selections from another tab and keeps a session usable when storage is blocked', async () => {
    localStorage.clear();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        const ids = new URL(url, location.origin).searchParams.get('ids')!.split(',');
        return new Response(JSON.stringify({ items: [], missingIds: ids }));
      }),
    );
    TestBed.configureTestingModule({});
    const fixture = TestBed.createComponent(SaveOffer);
    fixture.componentRef.setInput('card', offer);
    await fixture.whenStable();
    const store = TestBed.inject(ShortlistStore);
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: shortlistKey,
        storageArea: localStorage,
        newValue: '["other-tab"]',
      }),
    );
    expect(store.ids()).toEqual(['other-tab']);
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    store.toggle('second');
    await fixture.whenStable();
    expect(store.ids()).toEqual(['other-tab', 'second']);
    expect(store.message()).toContain('Naršyklė neleidžia');
    write.mockRestore();
  });

  it('renders previous prices and status changes when history is opened, with a retry after failure', async () => {
    let failing = true;
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(history), { status: failing ? 503 : 200 })),
    );
    const fixture = TestBed.createComponent(AuctionHistory);
    fixture.componentRef.setInput('auctionId', offer.auctionId);
    fixture.componentRef.setInput('observedAt', offer.observedAt);
    await fixture.whenStable();
    expect(fetch).not.toHaveBeenCalled();
    const details = fixture.nativeElement.querySelector('details') as HTMLDetailsElement;
    details.open = true;
    details.dispatchEvent(new Event('toggle'));
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[role=alert]')).not.toBeNull();
    failing = false;
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Vyksta → Baigta');
    expect(fixture.nativeElement.textContent).toContain('Pradinė kaina:');
    expect(fixture.nativeElement.querySelector('li strong')?.textContent).toContain('→');
  });

  it('shows expired deadlines separately from a confirmed ended status', async () => {
    const fixture = TestBed.createComponent(OfferChanges);
    fixture.componentRef.setInput('card', {
      ...offer,
      status: 'running',
      endsAt: '2026-10-01T00:00:00Z',
    });
    fixture.componentRef.setInput('now', Date.parse('2026-10-08T12:00:00Z'));
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Terminas praėjo');
    expect(fixture.nativeElement.textContent).not.toContain('Aukcionas baigtas');
    fixture.componentRef.setInput('card', {
      ...offer,
      status: 'ended',
      statusChangedAt: '2026-10-08T10:00:00Z',
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Aukcionas baigtas');
    expect(fixture.nativeElement.textContent).not.toContain('Terminas praėjo');
  });
});
