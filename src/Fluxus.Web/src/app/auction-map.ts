import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { FeatureCollection, MultiPolygon, Point } from 'geojson';
import type { GeoJSONSource, Map, MapMouseEvent } from 'maplibre-gl';
import type { ItemCard } from './catalog-api';
import type { VisibilityOverlay } from './landscape-api';
import { isRecentlyAdded } from './offer-recency';

@Component({
  selector: 'app-auction-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="map-canvas" #canvas aria-label="Lietuvos aukcionų žemėlapis"></div>
    @for (label of clusterLabels(); track label.id) {
      <span
        class="cluster-label"
        [class.recent]="label.recent"
        aria-hidden="true"
        [style.left.px]="label.x"
        [style.top.px]="label.y"
        >{{ label.count }}</span
      >
    }
    @if (error(); as message) {
      <div class="map-warning" role="status">{{ message }} Aukcionus rasite sąraše.</div>
    }
    @if (visibilityError(); as message) {
      <div class="map-warning" role="status">{{ message }}</div>
    }
    @if (choices().length) {
      <div class="map-choices" aria-label="Aukcionai šioje vietoje">
        <button class="choice-close" (click)="choices.set([])" aria-label="Uždaryti pasirinkimą">
          ×
        </button>
        <strong>Aukcionai šioje vietoje</strong>
        @for (item of choices(); track item.id) {
          <button (click)="choose(item.id)">
            Nr. {{ item.number }} · {{ item.share ?? 'Dalis nenurodyta' }}
          </button>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      position: relative;
      height: 100%;
      min-height: 340px;
    }
    .map-canvas {
      position: absolute;
      inset: 0;
      background: #e7eee9;
    }
    .map-warning {
      position: absolute;
      top: 16px;
      left: 16px;
      right: 68px;
      background: #fff8e4;
      border: 1px solid #d6c086;
      padding: 12px 16px;
      border-radius: 8px;
      font-size: 13px;
    }
    .cluster-label {
      position: absolute;
      transform: translate(-50%, -50%);
      color: #fff;
      font: 700 12px system-ui;
      pointer-events: none;
    }
    .map-choices {
      position: absolute;
      bottom: 35px;
      left: 16px;
      padding: 18px;
      background: #fff;
      border-radius: 12px;
      box-shadow: 0 8px 35px #172e2330;
      min-width: 240px;
    }
    .cluster-label.recent {
      color: #123f2e;
    }
    .map-choices button {
      display: block;
      margin-top: 10px;
    }
    .map-choices .choice-close {
      float: right;
      margin: -12px -6px 4px 12px;
    }
  `,
})
export class AuctionMap {
  readonly features = input.required<FeatureCollection<Point, ItemCard>>();
  readonly selectedId = input<string | null>(null);
  readonly boundary = input<MultiPolygon | null>(null);
  readonly visibility = input<VisibilityOverlay | null>(null);
  readonly selected = output<string>();
  private readonly canvas = viewChild.required<ElementRef<HTMLDivElement>>('canvas');
  private readonly destroy = inject(DestroyRef);
  private readonly ready = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly visibilityError = signal<string | null>(null);
  protected readonly choices = signal<ItemCard[]>([]);
  protected readonly clusterLabels = signal<
    { id: number; count: number; x: number; y: number; recent: boolean }[]
  >([]);
  private map?: Map;
  private resizeObserver?: ResizeObserver;
  private lastBoundary: MultiPolygon | null = null;
  private readonly now = signal(Date.now());
  private readonly points = computed(() => ({
    type: 'FeatureCollection' as const,
    features: this.features().features.map((feature) => ({
      ...feature,
      properties: {
        ...feature.properties,
        recentlyAdded: isRecentlyAdded(feature.properties.firstSeenAt, this.now()),
      },
    })),
  }));
  constructor() {
    const clock = setInterval(() => this.now.set(Date.now()), 60_000);
    afterNextRender(() => {
      void this.initialize();
    });
    effect(() => {
      const points = this.points();
      const selected = this.selectedId();
      const boundary = this.boundary();
      if (!this.ready() || !this.map) return;
      (this.map.getSource('offers') as GeoJSONSource).setData(points);
      this.map.setFilter('selected-offer', ['==', ['get', 'id'], selected ?? '']);
      (this.map.getSource('boundary') as GeoJSONSource).setData({
        type: 'FeatureCollection',
        features: boundary ? [{ type: 'Feature', geometry: boundary, properties: {} }] : [],
      });
      if (boundary && boundary !== this.lastBoundary) {
        this.fitBoundary(boundary, 650);
      }
      this.lastBoundary = boundary;
    });
    this.destroy.onDestroy(() => {
      clearInterval(clock);
      this.resizeObserver?.disconnect();
      this.map?.remove();
    });
    effect((onCleanup) => {
      const visibility = this.visibility();
      if (!this.ready() || !this.map) return;
      this.visibilityError.set(null);
      if (!visibility) return;
      const map = this.map;
      const controller = new AbortController();
      let imageUrl: string | undefined;
      void (async () => {
        try {
          const response = await fetch(visibility.url, { signal: controller.signal });
          if (!response.ok) throw new Error('Visibility image unavailable');
          const blob = await response.blob();
          if (blob.size > 4 * 1024 * 1024 || blob.type !== 'image/png')
            throw new Error('Invalid visibility image');
          if (controller.signal.aborted) return;
          imageUrl = URL.createObjectURL(blob);
          map.addSource('landscape-image', {
            type: 'image',
            url: imageUrl,
            coordinates: visibility.coordinates,
          });
          map.addLayer(
            {
              id: 'landscape-image',
              type: 'raster',
              source: 'landscape-image',
              paint: { 'raster-opacity': 0.65, 'raster-fade-duration': 0 },
            },
            'boundary-fill',
          );
          map.addSource('landscape-observer', {
            type: 'geojson',
            data: {
              type: 'Feature',
              properties: {},
              geometry: { type: 'Point', coordinates: visibility.observer },
            },
          });
          map.addLayer({
            id: 'landscape-observer',
            type: 'circle',
            source: 'landscape-observer',
            paint: {
              'circle-radius': 5,
              'circle-color': '#f4be54',
              'circle-stroke-color': '#fff',
              'circle-stroke-width': 2,
            },
          });
        } catch {
          if (!controller.signal.aborted)
            this.visibilityError.set(
              'Matomumo sluoksnio įkelti nepavyko. Sklypo vertinimas lieka prieinamas.',
            );
        }
      })();
      onCleanup(() => {
        controller.abort();
        for (const id of ['landscape-image', 'landscape-observer']) {
          if (map.getLayer(id)) map.removeLayer(id);
          if (map.getSource(id)) map.removeSource(id);
        }
        if (imageUrl) URL.revokeObjectURL(imageUrl);
      });
    });
  }
  protected choose(id: string) {
    this.choices.set([]);
    this.selected.emit(id);
  }
  private async initialize() {
    try {
      const maplibre = await import('maplibre-gl');
      if (this.destroy.destroyed) return;
      maplibre.setWorkerUrl(new URL('maplibre/maplibre-gl-worker.mjs', document.baseURI).href);
      this.map = new maplibre.Map({
        container: this.canvas().nativeElement,
        center: [23.9, 55.2],
        zoom: 6.7,
        minZoom: 5,
        maxZoom: 18,
        style: {
          version: 8,
          sources: {
            osm: {
              type: 'raster',
              tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
              tileSize: 256,
              maxzoom: 19,
              attribution:
                '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors · Kadastro duomenys: Registrų centras / Geoportal',
            },
          },
          layers: [
            {
              id: 'basemap',
              type: 'raster',
              source: 'osm',
              paint: { 'raster-saturation': -0.6, 'raster-opacity': 0.85 },
            },
          ],
        },
      });
      this.map.addControl(new maplibre.NavigationControl({ showCompass: false }), 'top-right');
      this.resizeObserver = new ResizeObserver(() => {
        this.map?.resize();
        const boundary = this.boundary();
        if (this.ready() && boundary) this.fitBoundary(boundary, 0);
      });
      this.resizeObserver.observe(this.canvas().nativeElement);
      this.map.on('error', () => this.error.set('Žemėlapio sluoksnio įkelti nepavyko.'));
      this.map.on('load', () => {
        const map = this.map!;
        map.addSource('offers', {
          type: 'geojson',
          data: this.points(),
          cluster: true,
          clusterRadius: 42,
          clusterMaxZoom: 15,
          clusterProperties: { recentCount: ['+', ['case', ['get', 'recentlyAdded'], 1, 0]] },
        });
        map.addSource('boundary', {
          type: 'geojson',
          data: { type: 'FeatureCollection', features: [] },
        });
        map.addLayer({
          id: 'boundary-fill',
          type: 'fill',
          source: 'boundary',
          paint: { 'fill-color': '#137e62', 'fill-opacity': 0.22 },
        });
        map.addLayer({
          id: 'boundary-line',
          type: 'line',
          source: 'boundary',
          paint: { 'line-color': '#095f49', 'line-width': 3 },
        });
        map.addLayer({
          id: 'clusters',
          type: 'circle',
          source: 'offers',
          filter: ['has', 'point_count'],
          paint: {
            'circle-color': [
              'case',
              ['==', ['get', 'recentCount'], ['get', 'point_count']],
              '#77c79d',
              '#145e4c',
            ],
            'circle-radius': ['step', ['get', 'point_count'], 18, 10, 24, 50, 30],
            'circle-stroke-width': 4,
            'circle-stroke-color': '#ffffff',
            'circle-stroke-opacity': 0.85,
          },
        });
        map.addLayer({
          id: 'offers',
          type: 'circle',
          source: 'offers',
          filter: ['!', ['has', 'point_count']],
          paint: {
            'circle-color': ['case', ['get', 'recentlyAdded'], '#77c79d', '#167b5b'],
            'circle-radius': 8,
            'circle-stroke-width': 3,
            'circle-stroke-color': '#fff',
          },
        });
        map.addLayer({
          id: 'selected-offer',
          type: 'circle',
          source: 'offers',
          filter: ['==', ['get', 'id'], ''],
          paint: {
            'circle-color': '#e4a445',
            'circle-radius': 11,
            'circle-stroke-width': 3,
            'circle-stroke-color': '#fff',
          },
        });
        map.on('click', 'clusters', (event) => {
          void this.expandCluster(event);
        });
        map.on('click', 'offers', (event) => {
          const candidates = map.queryRenderedFeatures(event.point, { layers: ['offers'] });
          const ids = new Set(candidates.map((f) => String(f.properties['id'])));
          const choices = this.features()
            .features.filter((f) => ids.has(String(f.id)))
            .map((f) => f.properties);
          if (choices.length > 1) this.choices.set(choices);
          else if (choices[0]) this.choose(choices[0].id);
        });
        for (const layer of ['offers', 'clusters']) {
          map.on('mouseenter', layer, () => {
            map.getCanvas().style.cursor = 'pointer';
          });
          map.on('mouseleave', layer, () => {
            map.getCanvas().style.cursor = '';
          });
        }
        map.on('render', () => {
          const seen = new Set<number>();
          const labels: { id: number; count: number; x: number; y: number; recent: boolean }[] = [];
          for (const feature of map.queryRenderedFeatures({ layers: ['clusters'] })) {
            if (feature.geometry.type !== 'Point') continue;
            const id = Number(feature.properties['cluster_id']);
            if (seen.has(id)) continue;
            seen.add(id);
            const point = map.project(feature.geometry.coordinates as [number, number]);
            labels.push({
              id,
              count: Number(feature.properties['point_count']),
              x: Math.round(point.x),
              y: Math.round(point.y),
              recent:
                Number(feature.properties['recentCount']) ===
                Number(feature.properties['point_count']),
            });
          }
          const previous = this.clusterLabels();
          if (
            labels.length !== previous.length ||
            labels.some(
              (label, i) =>
                label.id !== previous[i]?.id ||
                label.count !== previous[i]?.count ||
                label.recent !== previous[i]?.recent ||
                label.x !== previous[i]?.x ||
                label.y !== previous[i]?.y,
            )
          )
            this.clusterLabels.set(labels);
        });
        this.ready.set(true);
      });
    } catch {
      this.error.set('Šioje naršyklėje žemėlapis nepasiekiamas.');
    }
  }
  private fitBoundary(boundary: MultiPolygon, duration: number) {
    const coordinates = boundary.coordinates.flat(2);
    const xs = coordinates.map((p) => p[0]);
    const ys = coordinates.map((p) => p[1]);
    const mobile = window.matchMedia('(max-width: 760px)').matches;
    const padding = mobile
      ? {
          top: 145,
          bottom: this.canvas().nativeElement.clientHeight * 0.52 + 35,
          left: 30,
          right: 30,
        }
      : { top: 145, bottom: 65, left: 45, right: 400 };
    this.map!.fitBounds(
      [
        [Math.min(...xs), Math.min(...ys)],
        [Math.max(...xs), Math.max(...ys)],
      ],
      { padding, maxZoom: 16, duration },
    );
  }
  private async expandCluster(event: MapMouseEvent) {
    const map = this.map!;
    const feature = map.queryRenderedFeatures(event.point, { layers: ['clusters'] })[0];
    if (!feature || feature.geometry.type !== 'Point') return;
    const source = map.getSource('offers') as GeoJSONSource;
    const zoom = await source.getClusterExpansionZoom(Number(feature.properties['cluster_id']));
    if (this.destroy.destroyed) return;
    if (zoom > 16) {
      const leaves = await source.getClusterLeaves(
        Number(feature.properties['cluster_id']),
        100,
        0,
      );
      const ids = new Set(leaves.map((f) => String(f.properties?.['id'])));
      this.choices.set(
        this.features()
          .features.filter((f) => ids.has(String(f.id)))
          .map((f) => f.properties),
      );
    } else map.easeTo({ center: feature.geometry.coordinates as [number, number], zoom });
  }
}
