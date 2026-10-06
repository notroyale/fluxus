import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { FeatureCollection, MultiPolygon, Point } from 'geojson';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './app';
import { AuctionMap } from './auction-map';
import type { ItemCard } from './catalog-api';
import type { VisibilityOverlay } from './landscape-api';
import { catalog, detail } from './test-data';

@Component({ selector: 'app-auction-map', template: '' })
class TestMap {
  readonly features = input.required<FeatureCollection<Point, ItemCard>>();
  readonly selectedId = input<string | null>(null);
  readonly boundary = input<MultiPolygon | null>(null);
  readonly visibility = input<VisibilityOverlay | null>(null);
  readonly selected = output<string>();
}

describe('Auction browsing', () => {
  let failCatalog = false;
  let failLandscape = false;
  const requests: string[] = [];
  beforeEach(async () => {
    history.replaceState(null, '', '/');
    requests.length = 0;
    failCatalog = false;
    failLandscape = false;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        requests.push(url);
        if (url.startsWith('/api/catalog/map'))
          return new Response(JSON.stringify(catalog), { status: failCatalog ? 503 : 200 });
        if (url === '/api/catalog/filters')
          return new Response(JSON.stringify([{ code: '73', name: 'Rokiškio rajono' }]));
        if (url.startsWith('/api/auctions/')) return new Response(JSON.stringify(detail));
        if (url.endsWith('/landscape'))
          return new Response(
            JSON.stringify({
              id: url.split('/')[3],
              parcelId: 'parcel-1',
              summary: catalog.items[0].landscape,
              components: null,
              metrics: null,
              viewpoints: [],
              source: null,
              limitations: ['source_data_not_loaded'],
              failureCode: null,
              visibility: null,
            }),
            { status: failLandscape ? 503 : 200 },
          );
        return new Response(
          JSON.stringify({
            id: url.split('/')[3],
            resolutionStatus: 'resolved_exact',
            geometry: null,
            geometryFetchedAt: null,
            provider: 'Geoportal / Registrų centras',
          }),
        );
      }),
    );
    TestBed.configureTestingModule({ imports: [App] });
    TestBed.overrideComponent(App, {
      remove: { imports: [AuctionMap] },
      add: { imports: [TestMap] },
    });
    await TestBed.compileComponents();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    history.replaceState(null, '', '/');
  });

  it('groups the offers by auction and clearly labels the captured sample', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('.auction-card')).toHaveLength(1);
    expect(root.textContent).toContain('Pavyzdiniai duomenys');
    expect(root.textContent).toContain('Tai nėra aktualus aukcionų sąrašas');
    expect(root.textContent).toContain('2 pasiūlymai');
  });

  it('explains absent terrain data without inventing a landscape score', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('.auction-card') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(root.querySelector('app-landscape-details')?.textContent).toContain('Dar nevertintas');
    expect(root.querySelector('app-landscape-details')?.textContent).toContain(
      'dar neįkelti patikrinti',
    );
    expect(root.querySelector('.visibility-toggle')).toBeNull();
  });

  it('shares landscape filters and sorting and clears them on reset', async () => {
    history.replaceState(
      null,
      '',
      '/?minLandscapeScore=70&landscapeStatus=ready&sort=landscape_desc',
    );
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector<HTMLInputElement>('#landscape-min')!.value).toBe('70');
    expect(root.querySelector<HTMLSelectElement>('#landscape-status')!.value).toBe('ready');
    expect(root.querySelector<HTMLSelectElement>('#sort')!.value).toBe('landscape_desc');
    const min = root.querySelector<HTMLInputElement>('#landscape-min')!;
    min.value = '101';
    min.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(root.textContent).toContain('sveikasis skaičius');
    min.value = '80';
    min.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('minLandscapeScore')).toBe('80');
    expect(
      requests.some(
        (url) => url.includes('minLandscapeScore=80') && url.includes('sort=landscape_desc'),
      ),
    ).toBe(true);
    (root.querySelector('.text-button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).has('minLandscapeScore')).toBe(false);
    expect(root.querySelector<HTMLSelectElement>('#landscape-status')!.value).toBe('all');
  });

  it('keeps the auction detail usable when landscape data fails and lets the user retry', async () => {
    failLandscape = true;
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('.auction-card') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(root.textContent).toContain('Kraštovaizdžio duomenų įkelti nepavyko');
    expect(root.querySelector('.source-link')).not.toBeNull();
    failLandscape = false;
    const retry = [...root.querySelectorAll<HTMLButtonElement>('.detail-panel button')].find(
      (button) => button.textContent?.includes('Bandyti dar kartą'),
    )!;
    retry.click();
    await fixture.whenStable();
    expect(root.querySelector('app-landscape-details')?.textContent).toContain('Dar nevertintas');
  });

  it('keeps both ownership offers selectable and explains the bundle price', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('.auction-card') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(root.textContent).toContain('Kaina apima visą turto komplektą');
    expect(root.textContent).toContain('Viso aukciono pradinė kaina');
    const buttons = root.querySelectorAll<HTMLButtonElement>('.parcel-selector button');
    expect(buttons).toHaveLength(2);
    buttons[1].click();
    await fixture.whenStable();
    expect(location.search).toContain('parcel=offer-2');
    expect(root.querySelector('.parcel-selector button.chosen')).toBe(buttons[1]);
    expect(requests).toContain('/api/auction-parcels/offer-2/geometry');
    expect(root.querySelector<HTMLAnchorElement>('.source-link')?.hostname).toBe(
      'www.evarzytynes.lt',
    );
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();
    expect(root.querySelector('.detail-panel')).toBeNull();
  });

  it('rejects an inverted range and shares valid filters in the URL', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const min = root.querySelector<HTMLInputElement>('[aria-label="Kaina nuo"]')!;
    const max = root.querySelector<HTMLInputElement>('[aria-label="Kaina iki"]')!;
    min.value = '100';
    min.dispatchEvent(new Event('input'));
    max.value = '50';
    max.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(root.textContent).toContain('negali būti didesnė');
    expect(requests.filter((url) => url.startsWith('/api/catalog/map'))).toHaveLength(1);
    max.value = '200';
    max.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('maxPriceEur')).toBe('200');
    expect(
      requests.some((url) => url.includes('minPriceEur=100') && url.includes('maxPriceEur=200')),
    ).toBe(true);
  });

  it('lets the user retry an unavailable API', async () => {
    failCatalog = true;
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
    failCatalog = false;
    (root.querySelector('.empty button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(root.querySelectorAll('.auction-card')).toHaveLength(1);
  });

  it('shows a municipality restored from the URL after its options load', async () => {
    history.replaceState(null, '', '/?municipalityCode=73');
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector<HTMLSelectElement>('#municipality')!.value).toBe('73');
    expect(requests.some((url) => url.includes('municipalityCode=73'))).toBe(true);
  });

  it('applies sale ownership filters and clears ownership when switching to rent', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const ownership = root.querySelector<HTMLSelectElement>('#ownership-kind')!;
    const type = root.querySelector<HTMLSelectElement>('#offer-type')!;
    expect(type.value).toBe('sale');
    ownership.value = 'partial';
    ownership.dispatchEvent(new Event('change'));
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('ownershipKind')).toBe('partial');
    expect(
      requests.some(
        (url) => url.includes('offerType=sale') && url.includes('ownershipKind=partial'),
      ),
    ).toBe(true);
    type.value = 'rent';
    type.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(ownership.disabled).toBe(true);
    expect(ownership.value).toBe('all');
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('offerType')).toBe('rent');
    expect(new URLSearchParams(location.search).has('ownershipKind')).toBe(false);
    (root.querySelector('.text-button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(type.value).toBe('sale');
    expect(ownership.disabled).toBe(false);
  });

  it('changes price sorting immediately, retains it after filtering and restores it from the URL', async () => {
    history.replaceState(null, '', '/?sort=price_desc&ownershipKind=full');
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const sort = root.querySelector<HTMLSelectElement>('#sort')!;
    expect(sort.value).toBe('price_desc');
    expect(root.querySelector<HTMLSelectElement>('#ownership-kind')!.value).toBe('full');
    sort.value = 'price_asc';
    sort.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('sort')).toBe('price_asc');
    expect(
      requests.some((url) => url.includes('sort=price_asc') && url.includes('ownershipKind=full')),
    ).toBe(true);
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    expect(new URLSearchParams(location.search).get('sort')).toBe('price_asc');
  });
});
