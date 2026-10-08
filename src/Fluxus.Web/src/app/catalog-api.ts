import { Injectable } from '@angular/core';
import type { FeatureCollection, MultiPolygon, Point } from 'geojson';
import {
  LandscapeSummary,
  LandscapeDetail,
  parseLandscapeSummary,
  parseLandscapeDetail,
} from './landscape-api';

export interface ItemCard {
  id: string;
  auctionId: string;
  number: string;
  status: string;
  address: string | null;
  municipalityCode: string | null;
  municipalityName: string | null;
  initialPriceEur: number | null;
  lastBidEur: number | null;
  endsAt: string | null;
  totalAreaHa: number | null;
  soldAreaHa: number | null;
  soldAreaBasis: string;
  share: string | null;
  containsNonLandAssets: boolean;
  resolutionStatus: string;
  offerType: 'sale' | 'rent' | 'unknown';
  ownershipKind: 'full' | 'partial' | 'unknown' | 'not_applicable';
  landscape: LandscapeSummary;
  firstSeenAt: string | null;
  observedAt: string;
  isPresent: boolean;
  missingSince: string | null;
  priceChangedAt: string | null;
  previousInitialPriceEur: number | null;
  statusChangedAt: string | null;
  previousStatus: string | null;
  pricePerHaEur: number | null;
  componentPriceEur: number | null;
  auctionOfferCount: number;
}
export interface Catalog extends FeatureCollection<Point, ItemCard> {
  items: ItemCard[];
  meta: {
    auctions: number;
    items: number;
    mapped: number;
    unresolved: number;
    fixtureMode: boolean;
    observedAt: string | null;
    lastImportAt: string | null;
    lastImportOutcome: string | null;
    discoveryComplete: boolean;
  };
}
export interface Filters {
  offerType: string;
  ownershipKind: string;
  sort: string;
  municipalityCode: string;
  minPriceEur: string;
  maxPriceEur: string;
  minSoldAreaHa: string;
  maxSoldAreaHa: string;
  status: string;
  minLandscapeScore: string;
  landscapeStatus: string;
}
export const defaultFilters: Filters = {
  municipalityCode: '',
  minPriceEur: '',
  maxPriceEur: '',
  minSoldAreaHa: '',
  maxSoldAreaHa: '',
  status: 'announced,running',
  offerType: 'sale',
  ownershipKind: 'all',
  sort: 'ending',
  minLandscapeScore: '',
  landscapeStatus: 'all',
};
export interface AuctionDetail {
  id: string;
  number: string;
  sourceUrl: string;
  status: string;
  startsAt: string | null;
  endsAt: string | null;
  initialPriceEur: number | null;
  lastBidEur: number | null;
  containsNonLandAssets: boolean;
  observedAt: string;
  fixtureMode: boolean;
  items: {
    card: ItemCard;
    uniqueNumber: string | null;
    cadastralNumber: string | null;
    landUse: string | null;
    componentPriceEur: number | null;
  }[];
}
export interface GeometryResponse {
  id: string;
  resolutionStatus: string;
  geometry: MultiPolygon | null;
  geometryFetchedAt: string | null;
  provider: string;
}
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Netinkamas serverio atsakymas.');
  return value as Record<string, unknown>;
}
function nullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}
function nullableNumber(value: unknown): value is number | null {
  return value === null || (typeof value === 'number' && Number.isFinite(value));
}
export function parseItemCard(value: unknown): ItemCard {
  const row = object(value);
  for (const key of ['missingSince', 'priceChangedAt', 'statusChangedAt'])
    if (
      row[key] !== null &&
      (typeof row[key] !== 'string' || !Number.isFinite(Date.parse(row[key])))
    )
      throw new Error('Netinkama pakeitimo data.');
  if (
    typeof row['observedAt'] !== 'string' ||
    !Number.isFinite(Date.parse(row['observedAt'])) ||
    typeof row['isPresent'] !== 'boolean' ||
    !nullableString(row['previousStatus']) ||
    !Number.isInteger(row['auctionOfferCount']) ||
    Number(row['auctionOfferCount']) < 0
  )
    throw new Error('Netinkama pasiūlymo istorija.');
  if (
    row['firstSeenAt'] !== null &&
    (typeof row['firstSeenAt'] !== 'string' || !Number.isFinite(Date.parse(row['firstSeenAt'])))
  )
    throw new Error('Netinkama pasiūlymo pridėjimo data.');
  parseLandscapeSummary(row['landscape']);
  if (
    !['sale', 'rent', 'unknown'].includes(String(row['offerType'])) ||
    !['full', 'partial', 'unknown', 'not_applicable'].includes(String(row['ownershipKind']))
  )
    throw new Error('Netinkamas žemės pasiūlymo tipas.');
  for (const key of ['id', 'auctionId', 'number', 'status', 'soldAreaBasis', 'resolutionStatus'])
    if (typeof row[key] !== 'string') throw new Error('Netinkami aukciono duomenys.');
  for (const key of ['address', 'municipalityCode', 'municipalityName', 'endsAt', 'share'])
    if (!nullableString(row[key])) throw new Error('Netinkami sklypo duomenys.');
  for (const key of [
    'initialPriceEur',
    'lastBidEur',
    'totalAreaHa',
    'soldAreaHa',
    'previousInitialPriceEur',
    'pricePerHaEur',
    'componentPriceEur',
  ])
    if (!nullableNumber(row[key])) throw new Error('Netinkami sklypo dydžiai.');
  if (typeof row['containsNonLandAssets'] !== 'boolean')
    throw new Error('Netinkami turto duomenys.');
  return row as unknown as ItemCard;
}
export function parseCatalog(value: unknown): Catalog {
  const catalog = object(value);
  if (
    catalog['type'] !== 'FeatureCollection' ||
    !Array.isArray(catalog['features']) ||
    !Array.isArray(catalog['items'])
  )
    throw new Error('Netinkamas žemėlapio atsakymas.');
  catalog['items'].forEach(parseItemCard);
  for (const raw of catalog['features']) {
    const feature = object(raw);
    const geometry = object(feature['geometry']);
    if (
      feature['type'] !== 'Feature' ||
      typeof feature['id'] !== 'string' ||
      geometry['type'] !== 'Point' ||
      !Array.isArray(geometry['coordinates']) ||
      geometry['coordinates'].length !== 2 ||
      !geometry['coordinates'].every((n) => typeof n === 'number' && Number.isFinite(n))
    )
      throw new Error('Netinkamos koordinatės.');
    parseItemCard(feature['properties']);
  }
  const meta = object(catalog['meta']);
  for (const key of ['auctions', 'items', 'mapped', 'unresolved'])
    if (typeof meta[key] !== 'number' || !Number.isInteger(meta[key]) || Number(meta[key]) < 0)
      throw new Error('Netinkami skaičiai.');
  if (
    typeof meta['fixtureMode'] !== 'boolean' ||
    typeof meta['discoveryComplete'] !== 'boolean' ||
    !nullableString(meta['observedAt']) ||
    !nullableString(meta['lastImportAt']) ||
    !nullableString(meta['lastImportOutcome'])
  )
    throw new Error('Netinkama atnaujinimo informacija.');
  return catalog as unknown as Catalog;
}
@Injectable({ providedIn: 'root' })
export class CatalogApi {
  async landscape(id: string, signal: AbortSignal): Promise<LandscapeDetail> {
    return parseLandscapeDetail(
      await this.get(`/api/auction-parcels/${encodeURIComponent(id)}/landscape`, signal),
    );
  }
  async get(url: string, signal: AbortSignal): Promise<unknown> {
    const response = await fetch(url, { signal });
    if (!response.ok) {
      if (response.status === 400) throw new Error('Patikrinkite filtrų reikšmes ir intervalus.');
      throw new Error(
        'Duomenų gauti nepavyko. Patikrinkite, ar veikia API, ir bandykite dar kartą.',
      );
    }
    return response.json() as Promise<unknown>;
  }
  async catalog(filters: Filters, signal: AbortSignal): Promise<Catalog> {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value);
    return parseCatalog(await this.get(`/api/catalog/map?${query}`, signal));
  }
  async municipalities(signal: AbortSignal): Promise<{ code: string; name: string }[]> {
    const value = await this.get('/api/catalog/filters', signal);
    if (!Array.isArray(value)) throw new Error('Netinkamas savivaldybių sąrašas.');
    return value.map((raw) => {
      const row = object(raw);
      if (typeof row['code'] !== 'string' || typeof row['name'] !== 'string')
        throw new Error('Netinkama savivaldybė.');
      return { code: row['code'], name: row['name'] };
    });
  }
  async detail(id: string, signal: AbortSignal): Promise<AuctionDetail> {
    const row = object(await this.get(`/api/auctions/${encodeURIComponent(id)}`, signal));
    if (
      typeof row['id'] !== 'string' ||
      typeof row['number'] !== 'string' ||
      typeof row['status'] !== 'string' ||
      typeof row['sourceUrl'] !== 'string' ||
      !Array.isArray(row['items']) ||
      !nullableString(row['startsAt']) ||
      !nullableString(row['endsAt']) ||
      !nullableNumber(row['initialPriceEur']) ||
      !nullableNumber(row['lastBidEur']) ||
      typeof row['observedAt'] !== 'string' ||
      typeof row['containsNonLandAssets'] !== 'boolean' ||
      typeof row['fixtureMode'] !== 'boolean'
    )
      throw new Error('Netinkamas aukciono atsakymas.');
    const url = new URL(row['sourceUrl']);
    if (
      url.protocol !== 'https:' ||
      url.hostname !== 'www.evarzytynes.lt' ||
      url.pathname !== '/evs/pages/auction.do'
    )
      throw new Error('Netinkama aukciono nuoroda.');
    for (const raw of row['items']) {
      const section = object(raw);
      parseItemCard(section['card']);
      for (const key of ['uniqueNumber', 'cadastralNumber', 'landUse'])
        if (!nullableString(section[key])) throw new Error('Netinkami sklypo identifikatoriai.');
      if (!nullableNumber(section['componentPriceEur'])) throw new Error('Netinkama sklypo kaina.');
    }
    return row as unknown as AuctionDetail;
  }
  async geometry(id: string, signal: AbortSignal): Promise<GeometryResponse> {
    const row = object(
      await this.get(`/api/auction-parcels/${encodeURIComponent(id)}/geometry`, signal),
    );
    if (
      typeof row['id'] !== 'string' ||
      typeof row['resolutionStatus'] !== 'string' ||
      !nullableString(row['geometryFetchedAt']) ||
      typeof row['provider'] !== 'string'
    )
      throw new Error('Netinkamas ribų atsakymas.');
    if (row['geometry'] !== null) {
      const geometry = object(row['geometry']);
      if (
        geometry['type'] !== 'MultiPolygon' ||
        !Array.isArray(geometry['coordinates']) ||
        geometry['coordinates'].length === 0
      )
        throw new Error('Netinkamos sklypo ribos.');
      for (const polygon of geometry['coordinates']) {
        if (!Array.isArray(polygon) || polygon.length === 0)
          throw new Error('Netinkamas daugiakampis.');
        for (const ring of polygon) {
          if (
            !Array.isArray(ring) ||
            ring.length < 4 ||
            !ring.every(
              (p) =>
                Array.isArray(p) &&
                p.length >= 2 &&
                p.every((n) => typeof n === 'number' && Number.isFinite(n)),
            )
          )
            throw new Error('Netinkamos ribų koordinatės.');
        }
      }
    }
    return row as unknown as GeometryResponse;
  }
}
