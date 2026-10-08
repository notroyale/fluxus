import { Injectable } from '@angular/core';

export interface RefreshStatus {
  enabled: boolean;
  maxAuctions: number;
  id: string | null;
  status: 'idle' | 'queued' | 'running' | 'completed' | 'failed';
  phase: 'listing' | 'details' | 'geometry' | 'saving' | null;
  startedAt: string | null;
  finishedAt: string | null;
  retryAt: string | null;
  auctions: number;
  items: number;
  mapped: number;
  failedAuctions: number;
  discoveryComplete: boolean | null;
  errorCode: string | null;
}

export function parseRefreshStatus(value: unknown): RefreshStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Netinkamas atnaujinimo atsakymas.');
  const row = value as Record<string, unknown>;
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (
    typeof row['enabled'] !== 'boolean' ||
    !['idle', 'queued', 'running', 'completed', 'failed'].includes(String(row['status'])) ||
    ![null, 'listing', 'details', 'geometry', 'saving'].includes(row['phase'] as string | null) ||
    (row['id'] !== null && (typeof row['id'] !== 'string' || !uuid.test(row['id']))) ||
    (row['status'] !== 'idle' && row['id'] === null) ||
    (row['discoveryComplete'] !== null && typeof row['discoveryComplete'] !== 'boolean') ||
    ![null, 'no_auctions', 'import_busy', 'interrupted', 'timed_out', 'source_failed'].includes(
      row['errorCode'] as string | null,
    )
  )
    throw new Error('Netinkama atnaujinimo būsena.');
  for (const key of ['maxAuctions', 'auctions', 'items', 'mapped', 'failedAuctions'])
    if (typeof row[key] !== 'number' || !Number.isInteger(row[key]) || Number(row[key]) < 0)
      throw new Error('Netinkami atnaujinimo skaičiai.');
  if (Number(row['maxAuctions']) < 1 || Number(row['mapped']) > Number(row['items']))
    throw new Error('Netinkami atnaujinimo skaičiai.');
  for (const key of ['startedAt', 'finishedAt', 'retryAt'])
    if (
      row[key] !== null &&
      (typeof row[key] !== 'string' || !Number.isFinite(Date.parse(row[key])))
    )
      throw new Error('Netinkama atnaujinimo data.');
  if (
    (row['status'] !== 'idle' && row['startedAt'] === null) ||
    (['completed', 'failed'].includes(String(row['status'])) && row['finishedAt'] === null) ||
    (row['status'] === 'completed' && typeof row['discoveryComplete'] !== 'boolean')
  )
    throw new Error('Nepilni atnaujinimo duomenys.');
  return row as unknown as RefreshStatus;
}

@Injectable({ providedIn: 'root' })
export class CatalogRefreshApi {
  async status(signal: AbortSignal): Promise<RefreshStatus> {
    const response = await fetch('/api/catalog/refresh', { signal, cache: 'no-store' });
    if (!response.ok) throw new Error('Atnaujinimo būsenos gauti nepavyko.');
    return parseRefreshStatus(await response.json());
  }

  async start(signal: AbortSignal): Promise<RefreshStatus> {
    const response = await fetch('/api/catalog/refresh', {
      method: 'POST',
      headers: { 'X-Fluxus-Refresh': '1' },
      signal,
    });
    if (!response.ok) {
      if (response.status === 429) throw new Error('Prieš naują atnaujinimą palaukite minutę.');
      if (response.status === 403)
        throw new Error('Šioje aplinkoje duomenų atnaujinimas neleidžiamas.');
      throw new Error('Atnaujinimo pradėti nepavyko. Bandykite dar kartą.');
    }
    return parseRefreshStatus(await response.json());
  }
}
