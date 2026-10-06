import { describe, expect, it } from 'vitest';
import { landscapeLabel, parseLandscapeDetail, parseLandscapeSummary } from './landscape-api';
import { offer } from './test-data';

describe('Landscape response boundary', () => {
  it('keeps missing data unscored and gives it a useful state', () => {
    expect(parseLandscapeSummary(offer.landscape).score).toBeNull();
    expect(landscapeLabel(offer.landscape)).toBe('Dar nevertintas');
    expect(landscapeLabel({ ...offer.landscape, status: 'queued', isStale: true })).toBe(
      'Vertinimas atnaujinamas',
    );
  });
  it('rejects invalid or stale scores instead of displaying them as current', () => {
    for (const score of [-1, 101, 12.5, Number.NaN, '80'])
      expect(() => parseLandscapeSummary({ ...offer.landscape, score })).toThrow();
    expect(() =>
      parseLandscapeSummary({ ...offer.landscape, score: 80, status: 'ready', isStale: true }),
    ).toThrow();
    expect(() => parseLandscapeSummary({ ...offer.landscape, status: 'invented' })).toThrow();
  });
  it('accepts zero as a real published score', () => {
    const summary = {
      ...offer.landscape,
      analysisId: 'analysis-1',
      score: 0,
      status: 'ready',
      scoreProfile: 'balanced-retreat-v1',
    };
    expect(parseLandscapeSummary(summary).score).toBe(0);
  });
  it('rejects remote overlay URLs and malformed map coordinates', () => {
    const detail = {
      id: offer.id,
      parcelId: 'parcel-1',
      summary: offer.landscape,
      components: null,
      metrics: null,
      viewpoints: [],
      source: null,
      limitations: [],
      failureCode: null,
      visibility: null,
    };
    expect(parseLandscapeDetail(detail).visibility).toBeNull();
    expect(() =>
      parseLandscapeDetail({
        ...detail,
        visibility: {
          url: 'https://example.org/image.png',
          coordinates: [
            [25, 55],
            [26, 55],
            [26, 54],
            [25, 54],
          ],
          observer: [25, 55],
          eyeHeightM: 1.7,
          radiusM: 3000,
        },
      }),
    ).toThrow();
  });
});
