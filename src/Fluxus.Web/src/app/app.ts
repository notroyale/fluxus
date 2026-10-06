import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  resource,
  signal,
  viewChild,
} from '@angular/core';
import { AuctionMap } from './auction-map';
import { CatalogApi, defaultFilters, Filters, ItemCard } from './catalog-api';
import { landscapeLabel } from './landscape-api';
import { LandscapeDetails } from './landscape-details';

@Component({
  selector: 'app-root',
  imports: [AuctionMap, LandscapeDetails],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  private readonly api = inject(CatalogApi);
  private readonly destroy = inject(DestroyRef);
  private readonly detailPanel = viewChild<ElementRef<HTMLElement>>('detailPanel');
  private readonly selectedAuctionId = signal<string | undefined>(undefined);
  private lastTrigger?: HTMLElement;
  protected readonly filters = signal<Filters>(this.readFilters());
  protected readonly draft = signal<Filters>(this.filters());
  protected readonly selectedId = signal<string | null>(
    new URLSearchParams(location.search).get('parcel'),
  );
  protected readonly mobileView = signal<'map' | 'list'>('map');
  protected readonly filterError = signal<string | null>(null);
  protected readonly landscapeLabel = landscapeLabel;
  protected readonly showVisibility = signal(false);
  protected readonly landscape = resource({
    params: () => this.selectedId() ?? undefined,
    loader: ({ params, abortSignal }) => this.api.landscape(params, abortSignal),
  });
  protected readonly visibility = computed(() =>
    this.showVisibility() && this.landscape.hasValue() ? this.landscape.value().visibility : null,
  );
  protected readonly catalog = resource({
    params: () => this.filters(),
    loader: ({ params, abortSignal }) => this.api.catalog(params, abortSignal),
  });
  protected readonly municipalities = resource({
    loader: ({ abortSignal }) => this.api.municipalities(abortSignal),
  });
  private readonly auctionId = computed(() =>
    this.selectedId()
      ? ((this.catalog.hasValue()
          ? this.catalog.value().items.find((i) => i.id === this.selectedId())?.auctionId
          : undefined) ?? this.selectedAuctionId())
      : undefined,
  );
  protected readonly detail = resource({
    params: () => this.auctionId(),
    loader: ({ params, abortSignal }) => this.api.detail(params, abortSignal),
  });
  protected readonly selectedCard = computed(
    () =>
      (this.catalog.hasValue()
        ? this.catalog.value().items.find((i) => i.id === this.selectedId())
        : undefined) ??
      (this.detail.hasValue()
        ? this.detail.value().items.find((i) => i.card.id === this.selectedId())?.card
        : undefined),
  );
  protected readonly geometry = resource({
    params: () => this.selectedId() ?? undefined,
    loader: ({ params, abortSignal }) => this.api.geometry(params, abortSignal),
  });
  protected readonly boundary = computed(() =>
    this.geometry.hasValue() ? this.geometry.value().geometry : null,
  );
  protected readonly groups = computed(() => {
    const groups = new Map<string, ItemCard[]>();
    for (const item of this.catalog.hasValue() ? this.catalog.value().items : [])
      groups.set(item.auctionId, [...(groups.get(item.auctionId) ?? []), item]);
    return [...groups.entries()].map(([id, items]) => ({
      id,
      item: items[0],
      items,
      offerTypes: [...new Set(items.map((item) => item.offerType))],
      ownershipKinds: [...new Set(items.map((item) => item.ownershipKind))],
      unresolved: items.every((i) => i.resolutionStatus !== 'resolved_exact'),
    }));
  });
  private readonly now = signal(Date.now());
  protected readonly stale = computed(
    () =>
      this.catalog.hasValue() &&
      this.catalog.value().meta.observedAt !== null &&
      this.now() - Date.parse(this.catalog.value().meta.observedAt!) > 2 * 60 * 60 * 1000,
  );

  constructor() {
    const onPopState = () => {
      this.filters.set(this.readFilters());
      this.draft.set(this.filters());
      this.selectedAuctionId.set(undefined);
      this.selectedId.set(new URLSearchParams(location.search).get('parcel'));
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && this.selectedId()) this.closeDetails();
    };
    window.addEventListener('popstate', onPopState);
    window.addEventListener('keydown', onKey);
    const clock = setInterval(() => this.now.set(Date.now()), 60_000);
    effect(() => {
      if (this.selectedId()) this.detailPanel()?.nativeElement.focus({ preventScroll: true });
    });
    effect(() => {
      this.selectedId();
      this.showVisibility.set(false);
    });
    this.destroy.onDestroy(() => {
      window.removeEventListener('popstate', onPopState);
      window.removeEventListener('keydown', onKey);
      clearInterval(clock);
    });
  }
  protected updateFilter(key: keyof Filters, value: string) {
    this.draft.update((current) => ({
      ...current,
      [key]: value,
      ...(key === 'offerType' && value === 'rent' ? { ownershipKind: 'all' } : {}),
    }));
  }
  protected changeSort(value: string) {
    this.filters.update((current) => ({ ...current, sort: value }));
    this.draft.update((current) => ({ ...current, sort: value }));
    this.writeUrl();
  }
  protected applyFilters(event: Event) {
    event.preventDefault();
    const filters = this.draft();
    if (
      filters.minLandscapeScore &&
      (!Number.isInteger(Number(filters.minLandscapeScore)) ||
        Number(filters.minLandscapeScore) < 0 ||
        Number(filters.minLandscapeScore) > 100)
    ) {
      this.filterError.set('Kraštovaizdžio balas turi būti sveikasis skaičius nuo 0 iki 100.');
      return;
    }
    const ranges = [
      ['minPriceEur', 'maxPriceEur'],
      ['minSoldAreaHa', 'maxSoldAreaHa'],
    ] as const;
    if (
      ranges.some(
        ([min, max]) => filters[min] && filters[max] && Number(filters[min]) > Number(filters[max]),
      )
    ) {
      this.filterError.set('Reikšmė „nuo“ negali būti didesnė už reikšmę „iki“.');
      return;
    }
    this.filterError.set(null);
    this.filters.set({ ...filters });
    this.selectedId.set(null);
    this.selectedAuctionId.set(undefined);
    this.writeUrl();
  }
  protected resetFilters() {
    this.filterError.set(null);
    this.draft.set({ ...defaultFilters });
    this.filters.set({ ...defaultFilters });
    this.selectedId.set(null);
    this.selectedAuctionId.set(undefined);
    this.writeUrl();
  }
  protected select(id: string) {
    const card =
      (this.catalog.hasValue() ? this.catalog.value().items.find((i) => i.id === id) : undefined) ??
      (this.detail.hasValue()
        ? this.detail.value().items.find((i) => i.card.id === id)?.card
        : undefined);
    this.lastTrigger =
      document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    this.selectedAuctionId.set(card?.auctionId);
    this.selectedId.set(id);
    this.mobileView.set('map');
    this.writeUrl();
  }
  protected closeDetails() {
    this.selectedId.set(null);
    this.selectedAuctionId.set(undefined);
    this.writeUrl();
    this.lastTrigger?.focus({ preventScroll: true });
  }
  protected money(value: number | null) {
    return value === null
      ? 'Nenurodyta'
      : new Intl.NumberFormat('lt-LT', {
          style: 'currency',
          currency: 'EUR',
          maximumFractionDigits: 2,
        }).format(value);
  }
  protected area(value: number | null) {
    return value === null
      ? 'Nenurodyta'
      : `${new Intl.NumberFormat('lt-LT', { maximumFractionDigits: 4 }).format(value)} ha`;
  }
  protected date(value: string | null) {
    return value
      ? new Intl.DateTimeFormat('lt-LT', {
          month: 'short',
          day: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
          timeZone: 'Europe/Vilnius',
        }).format(new Date(value))
      : 'Nenurodyta';
  }
  protected status(value: string) {
    return (
      (
        {
          running: 'Vyksta',
          announced: 'Paskelbta',
          ended: 'Baigta',
          cancelled: 'Atšaukta',
        } as Record<string, string>
      )[value] ?? 'Nežinoma'
    );
  }
  protected offerType(value: string) {
    return (
      (
        { sale: 'Parduodama', rent: 'Nuomojama', unknown: 'Tipas nenurodytas' } as Record<
          string,
          string
        >
      )[value] ?? 'Tipas nenurodytas'
    );
  }
  protected ownership(value: string) {
    return (
      (
        {
          full: 'Visa nuosavybė',
          partial: 'Nuosavybės dalis',
          unknown: 'Dalis nenurodyta',
          not_applicable: 'Nuosavybė neparduodama',
        } as Record<string, string>
      )[value] ?? 'Dalis nenurodyta'
    );
  }
  protected resolution(value: string) {
    return (
      (
        {
          resolved_exact: 'Ribos pagal kadastro duomenis',
          not_found: 'Sklypo duomenų rinkinyje nerasta',
          missing_identifier: 'Trūksta sklypo numerio',
          provider_unavailable: 'Kadastro paslauga nepasiekiama',
          identifier_conflict: 'Sklypo numeriai nesutampa',
          invalid_geometry: 'Sklypo ribų patvirtinti nepavyko',
        } as Record<string, string>
      )[value] ?? 'Vietos nustatyti nepavyko'
    );
  }
  protected errorMessage(value: unknown) {
    return value instanceof Error ? value.message : 'Duomenų gauti nepavyko.';
  }
  private readFilters(): Filters {
    const params = new URLSearchParams(location.search);
    return Object.fromEntries(
      Object.entries(defaultFilters).map(([key, fallback]) => [key, params.get(key) ?? fallback]),
    ) as unknown as Filters;
  }
  private writeUrl() {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(this.filters()))
      if (value && value !== defaultFilters[key as keyof Filters]) params.set(key, value);
    if (this.selectedId()) params.set('parcel', this.selectedId()!);
    const query = params.toString();
    history.pushState(null, '', `${location.pathname}${query ? `?${query}` : ''}`);
  }
}
