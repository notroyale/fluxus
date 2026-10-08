import { inject, Injectable } from '@angular/core';
import { CatalogApi, ItemCard, parseItemCard } from './catalog-api';

export interface Observation {
  id: string;
  observedAt: string;
  fixtureMode: boolean;
  kind: 'added' | 'baseline' | 'changed';
  status: string;
  previousStatus: string | null;
  initialPriceEur: number | null;
  previousInitialPriceEur: number | null;
  lastBidEur: number | null;
  previousLastBidEur: number | null;
  endsAt: string | null;
  previousEndsAt: string | null;
  offerCount: number;
  priceChanged: boolean;
  bidChanged: boolean;
  statusChanged: boolean;
  deadlineChanged: boolean;
  offersChanged: boolean;
}
export interface AuctionHistory {
  auctionId: string;
  observations: Observation[];
  total: number;
  truncated: boolean;
}
export interface ShortlistResult {
  items: ItemCard[];
  missingIds: string[];
}
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Netinkama istorija.');
  return value as Record<string, unknown>;
}
export function parseHistory(value: unknown): AuctionHistory {
  const row = object(value);
  if (
    typeof row['auctionId'] !== 'string' ||
    !Array.isArray(row['observations']) ||
    row['observations'].length > 50 ||
    !Number.isInteger(row['total']) ||
    Number(row['total']) < row['observations'].length ||
    typeof row['truncated'] !== 'boolean'
  )
    throw new Error('Netinkama istorija.');
  for (const raw of row['observations']) {
    const entry = object(raw);
    for (const key of ['id', 'status', 'observedAt'])
      if (typeof entry[key] !== 'string') throw new Error('Netinkamas istorijos įrašas.');
    if (
      !Number.isFinite(Date.parse(String(entry['observedAt']))) ||
      !['added', 'baseline', 'changed'].includes(String(entry['kind'])) ||
      !Number.isInteger(entry['offerCount']) ||
      Number(entry['offerCount']) < 0
    )
      throw new Error('Netinkamas istorijos įrašas.');
    for (const key of ['previousStatus', 'endsAt', 'previousEndsAt'])
      if (
        entry[key] !== null &&
        (typeof entry[key] !== 'string' ||
          (key !== 'previousStatus' && !Number.isFinite(Date.parse(entry[key]))))
      )
        throw new Error('Netinkama istorijos data.');
    for (const key of [
      'initialPriceEur',
      'previousInitialPriceEur',
      'lastBidEur',
      'previousLastBidEur',
    ])
      if (entry[key] !== null && (typeof entry[key] !== 'number' || !Number.isFinite(entry[key])))
        throw new Error('Netinkama istorijos kaina.');
    for (const key of [
      'fixtureMode',
      'priceChanged',
      'bidChanged',
      'statusChanged',
      'deadlineChanged',
      'offersChanged',
    ])
      if (typeof entry[key] !== 'boolean') throw new Error('Netinkamas istorijos pakeitimas.');
  }
  return row as unknown as AuctionHistory;
}
export function parseShortlist(value: unknown): ShortlistResult {
  const row = object(value);
  if (
    !Array.isArray(row['items']) ||
    row['items'].length > 4 ||
    !Array.isArray(row['missingIds']) ||
    row['missingIds'].length > 4 ||
    !row['missingIds'].every((id) => typeof id === 'string')
  )
    throw new Error('Netinkamas išsaugotų sklypų sąrašas.');
  return { items: row['items'].map(parseItemCard), missingIds: row['missingIds'] as string[] };
}
@Injectable({ providedIn: 'root' })
export class OfferTrackingApi {
  private readonly api = inject(CatalogApi);
  async history(id: string, signal: AbortSignal): Promise<AuctionHistory> {
    const result = parseHistory(
      await this.api.get(`/api/auctions/${encodeURIComponent(id)}/history`, signal),
    );
    if (result.auctionId !== id) throw new Error('Istorija priklauso kitam aukcionui.');
    return result;
  }
  async shortlist(ids: string[], signal: AbortSignal): Promise<ShortlistResult> {
    const result = parseShortlist(
      await this.api.get(`/api/catalog/shortlist?ids=${encodeURIComponent(ids.join(','))}`, signal),
    );
    const returned = [...result.items.map((item) => item.id), ...result.missingIds];
    if (
      returned.length !== ids.length ||
      new Set(returned).size !== returned.length ||
      returned.some((id) => !ids.includes(id))
    )
      throw new Error('Išsaugotų sklypų atsakymas nesutampa.');
    return result;
  }
}
