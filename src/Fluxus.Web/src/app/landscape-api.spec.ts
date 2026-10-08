import { describe, expect, it } from 'vitest';
import { landscapeLabel, parseLandscapeDetail, parseLandscapeSummary } from './landscape-api';
import { offer } from './test-data';

describe('Landscape response boundary', () => {
  it('allows a preliminary PDF without introducing a score and rejects another offer report', () => {
    const id = '0d4cf142-b4ce-43e7-933d-d77c73c42a44';
    const preliminaryReport = {
      url: `/api/auction-parcels/${id}/preliminary-report/${'b'.repeat(64)}.pdf`,
      createdAt: '2026-10-08T09:24:00Z',
      offerObservedAt: '2026-10-06T18:54:00Z',
    };
    const detail = {
      id,
      parcelId: 'b0795d8b-c0a5-4898-9902-a36aa5e1d846',
      summary: offer.landscape,
      components: null,
      metrics: null,
      viewpoints: [],
      source: null,
      limitations: [],
      failureCode: null,
      visibility: null,
      preliminaryReport,
    };
    expect(parseLandscapeDetail(detail).preliminaryReport).toEqual(preliminaryReport);
    expect(parseLandscapeDetail(detail).summary.score).toBeNull();
    expect(() => parseLandscapeDetail({ ...detail, id: 'another-offer' })).toThrow();
    for (const change of [
      { url: 'https://example.org/report.pdf' },
      { createdAt: 'invalid' },
      { createdAt: '2026-10-01T00:00:00Z' },
    ])
      expect(() =>
        parseLandscapeDetail({ ...detail, preliminaryReport: { ...preliminaryReport, ...change } }),
      ).toThrow();
  });
  it('accepts a parcel-bound inspection image while keeping the offer unscored', () => {
    const parcelId = 'b0795d8b-c0a5-4898-9902-a36aa5e1d846';
    const terrainPreview = {
      url: `/api/landscape/parcels/${parcelId}/terrain/${'a'.repeat(64)}.png`,
      width: 1440,
      height: 960,
      verticalExaggeration: 20,
      meshResolutionM: 20,
      surveyYearFrom: 2019,
      surveyYearTo: 2022,
      heightReferenceVerified: false,
      attribution: 'Test source',
    };
    const detail = {
      id: offer.id,
      parcelId,
      summary: offer.landscape,
      components: null,
      metrics: null,
      viewpoints: [],
      source: null,
      limitations: [],
      failureCode: null,
      visibility: null,
      terrainPreview,
    };
    expect(parseLandscapeDetail(detail).terrainPreview?.heightReferenceVerified).toBe(false);
    expect(parseLandscapeDetail(detail).summary.score).toBeNull();
    for (const change of [
      { url: 'https://example.org/terrain.png' },
      { width: 3000 },
      { verticalExaggeration: 51 },
      { surveyYearTo: 2018 },
      { meshResolutionM: -1 },
      { heightReferenceVerified: 'yes' },
    ])
      expect(() =>
        parseLandscapeDetail({ ...detail, terrainPreview: { ...terrainPreview, ...change } }),
      ).toThrow();
    expect(() => parseLandscapeDetail({ ...detail, parcelId: 'another-parcel' })).toThrow();
  });
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
  it('retains verified visibility while allowing unknown visible cover in partial results', () => {
    const detail = {
      id: offer.id,
      parcelId: 'parcel-1',
      summary: { ...offer.landscape, status: 'partial' },
      components: { views: null, terrain: 45, nature: null, seclusion: 60 },
      metrics: null,
      viewpoints: [
        {
          x: 500000,
          y: 6100000,
          eyeHeightM: 1.7,
          visibleAreaKm2: 12,
          distantDirectionFraction: 0.6,
          naturalDiversity: null,
          visibleWaterFraction: null,
        },
      ],
      source: null,
      limitations: ['visible_cover_incomplete'],
      failureCode: null,
      visibility: null,
    };
    expect(parseLandscapeDetail(detail).viewpoints[0].visibleAreaKm2).toBe(12);
    expect(parseLandscapeDetail(detail).summary.score).toBeNull();
    expect(() =>
      parseLandscapeDetail({
        ...detail,
        viewpoints: [{ ...detail.viewpoints[0], naturalDiversity: 1.1 }],
      }),
    ).toThrow();
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
