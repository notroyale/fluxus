import { afterEach, describe, expect, it, vi } from 'vitest';
import { CatalogApi, parseCatalog } from './catalog-api';
import { catalog, detail } from './test-data';

describe('API boundary validation', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('rejects projected coordinates supplied as a nonfinite point', () => {
    const malformed = structuredClone(catalog);
    malformed.features[0].geometry.coordinates[0] = Number.NaN;
    expect(() => parseCatalog(malformed)).toThrow('Netinkamos koordinatės');
  });
  it('accepts an unknown addition date and rejects malformed dates in cards and map features', () => {
    const value = structuredClone(catalog);
    value.items[0].firstSeenAt = null;
    expect(parseCatalog(value).items[0].firstSeenAt).toBeNull();
    value.features[0].properties.firstSeenAt = 'invalid';
    expect(() => parseCatalog(value)).toThrow('pridėjimo data');
  });
  it('rejects a source link outside the official auction site', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ ...detail, sourceUrl: 'https://example.com/fake' })),
      ),
    );
    await expect(
      new CatalogApi().detail('auction-1', new AbortController().signal),
    ).rejects.toThrow('Netinkama aukciono nuoroda');
  });
});
