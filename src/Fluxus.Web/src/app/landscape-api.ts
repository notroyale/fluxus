export interface LandscapeSummary {
  analysisId: string | null;
  status: 'not_analyzed' | 'queued' | 'processing' | 'ready' | 'partial' | 'unavailable' | 'failed';
  score: number | null;
  scoreProfile: string | null;
  quality: 'unknown' | 'low' | 'medium' | 'high';
  isStale: boolean;
  surveyYearFrom: number | null;
  surveyYearTo: number | null;
  computedAt: string | null;
}
export interface VisibilityOverlay {
  url: string;
  coordinates: [[number, number], [number, number], [number, number], [number, number]];
  observer: [number, number];
  eyeHeightM: number;
  radiusM: number;
}
export interface LandscapeDetail {
  id: string;
  parcelId: string | null;
  summary: LandscapeSummary;
  components: {
    views: number | null;
    terrain: number | null;
    nature: number | null;
    seclusion: number | null;
  } | null;
  metrics: {
    visibleAreaKm2: number | null;
    bestVisibleAreaKm2: number | null;
    distantDirectionFraction: number | null;
    visibleNaturalDiversity: number | null;
    reliefP90P10M: number | null;
    tpiStandardDeviationM: number | null;
    naturalCoverFraction: number | null;
    forestEdgeDistanceM: number | null;
    waterDistanceM: number | null;
    visibleWaterFraction: number | null;
    nearestBuildingDistanceM: number | null;
    buildingsWithin1Km: number | null;
    majorRoadDistanceM: number | null;
    gentleGroundAreaHa: number | null;
    largestGentlePatchHa: number | null;
    validViewpoints: number;
  } | null;
  viewpoints: {
    x: number;
    y: number;
    eyeHeightM: number;
    visibleAreaKm2: number;
    distantDirectionFraction: number;
    naturalDiversity: number | null;
    visibleWaterFraction: number | null;
  }[];
  limitations: string[];
  source: {
    provider: string;
    release: string;
    sourceUrl: string;
    licenseUrl: string;
    attribution: string;
    verticalDatum: string;
    preparation: string;
    groundResolutionM: number;
    surfaceResolutionM: number;
    analysisVersion: string;
  } | null;
  failureCode: string | null;
  visibility: VisibilityOverlay | null;
  preliminaryReport?: { url: string; createdAt: string; offerObservedAt: string } | null;
  terrainPreview?: {
    url: string;
    width: number;
    height: number;
    verticalExaggeration: number;
    meshResolutionM: number;
    surveyYearFrom: number;
    surveyYearTo: number;
    heightReferenceVerified: boolean;
    attribution: string;
  } | null;
}
function row(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Netinkamas kraštovaizdžio atsakymas.');
  return value as Record<string, unknown>;
}
const nullableString = (v: unknown) => v === null || typeof v === 'string';
const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const score = (v: unknown) =>
  v === null || (finite(v) && Number.isInteger(v) && v >= 0 && v <= 100);
export function parseLandscapeSummary(value: unknown): LandscapeSummary {
  const r = row(value);
  if (
    !['not_analyzed', 'queued', 'processing', 'ready', 'partial', 'unavailable', 'failed'].includes(
      String(r['status']),
    ) ||
    !['unknown', 'low', 'medium', 'high'].includes(String(r['quality'])) ||
    !score(r['score']) ||
    typeof r['isStale'] !== 'boolean' ||
    !nullableString(r['analysisId']) ||
    !nullableString(r['scoreProfile']) ||
    !nullableString(r['computedAt']) ||
    (r['computedAt'] !== null && !Number.isFinite(Date.parse(String(r['computedAt']))))
  )
    throw new Error('Netinkamas kraštovaizdžio vertinimas.');
  for (const key of ['surveyYearFrom', 'surveyYearTo'])
    if (
      r[key] !== null &&
      (!finite(r[key]) ||
        !Number.isInteger(r[key]) ||
        Number(r[key]) < 2000 ||
        Number(r[key]) > 2100)
    )
      throw new Error('Netinkamas duomenų laikotarpis.');
  if (
    r['score'] !== null &&
    (r['status'] !== 'ready' || r['isStale'] || !r['analysisId'] || !r['scoreProfile'])
  )
    throw new Error('Nepatvirtintas kraštovaizdžio balas.');
  return r as unknown as LandscapeSummary;
}
export function parseLandscapeDetail(value: unknown): LandscapeDetail {
  const r = row(value);
  if (
    typeof r['id'] !== 'string' ||
    !nullableString(r['parcelId']) ||
    !nullableString(r['failureCode']) ||
    !Array.isArray(r['limitations']) ||
    !r['limitations'].every((v) => typeof v === 'string')
  )
    throw new Error('Netinkami kraštovaizdžio duomenys.');
  parseLandscapeSummary(r['summary']);
  if (r['components'] !== null) {
    const components = row(r['components']);
    for (const key of ['views', 'terrain', 'nature', 'seclusion'])
      if (!score(components[key])) throw new Error('Netinkamas vertinimo komponentas.');
  }
  if (r['metrics'] !== null) {
    const metrics = row(r['metrics']);
    for (const key of [
      'visibleAreaKm2',
      'bestVisibleAreaKm2',
      'distantDirectionFraction',
      'visibleNaturalDiversity',
      'reliefP90P10M',
      'tpiStandardDeviationM',
      'naturalCoverFraction',
      'forestEdgeDistanceM',
      'waterDistanceM',
      'visibleWaterFraction',
      'nearestBuildingDistanceM',
      'buildingsWithin1Km',
      'majorRoadDistanceM',
      'gentleGroundAreaHa',
      'largestGentlePatchHa',
    ])
      if (metrics[key] !== null && (!finite(metrics[key]) || Number(metrics[key]) < 0))
        throw new Error('Netinkamas matavimas.');
    for (const key of [
      'distantDirectionFraction',
      'visibleNaturalDiversity',
      'naturalCoverFraction',
      'visibleWaterFraction',
    ])
      if (metrics[key] !== null && Number(metrics[key]) > 1)
        throw new Error('Netinkama matavimo dalis.');
    if (
      !finite(metrics['validViewpoints']) ||
      !Number.isInteger(metrics['validViewpoints']) ||
      metrics['validViewpoints'] < 0 ||
      metrics['validViewpoints'] > 16
    )
      throw new Error('Netinkamas stebėjimo taškų skaičius.');
  }
  if (!Array.isArray(r['viewpoints']) || r['viewpoints'].length > 16)
    throw new Error('Netinkami stebėjimo taškai.');
  for (const point of r['viewpoints']) {
    const p = row(point);
    for (const key of ['x', 'y', 'eyeHeightM', 'visibleAreaKm2', 'distantDirectionFraction'])
      if (!finite(p[key])) throw new Error('Netinkamas stebėjimo taškas.');
    for (const key of ['naturalDiversity', 'visibleWaterFraction'])
      if (p[key] !== null && (!finite(p[key]) || Number(p[key]) < 0 || Number(p[key]) > 1))
        throw new Error('Netinkama matoma danga.');
  }
  if (r['source'] !== null) {
    const source = row(r['source']);
    for (const key of [
      'provider',
      'release',
      'sourceUrl',
      'licenseUrl',
      'attribution',
      'verticalDatum',
      'preparation',
      'analysisVersion',
    ])
      if (typeof source[key] !== 'string') throw new Error('Netinkamas duomenų šaltinis.');
    for (const key of ['sourceUrl', 'licenseUrl'])
      if (new URL(String(source[key])).protocol !== 'https:')
        throw new Error('Netinkama šaltinio nuoroda.');
    for (const key of ['groundResolutionM', 'surfaceResolutionM'])
      if (!finite(source[key]) || Number(source[key]) <= 0)
        throw new Error('Netinkama šaltinio raiška.');
  }
  if (r['visibility'] !== null) {
    const visibility = row(r['visibility']);
    const coordinate = (v: unknown) =>
      Array.isArray(v) &&
      v.length === 2 &&
      v.every(finite) &&
      Math.abs(v[0]) <= 180 &&
      Math.abs(v[1]) <= 85.1;
    if (
      typeof visibility['url'] !== 'string' ||
      !/^\/api\/landscape\/analyses\/[0-9a-f-]{36}\/visibility\.png$/.test(visibility['url']) ||
      !Array.isArray(visibility['coordinates']) ||
      visibility['coordinates'].length !== 4 ||
      !visibility['coordinates'].every(coordinate) ||
      !coordinate(visibility['observer']) ||
      visibility['eyeHeightM'] !== 1.7 ||
      visibility['radiusM'] !== 3000
    )
      throw new Error('Netinkamas matomumo sluoksnis.');
  }
  if (r['preliminaryReport'] != null) {
    const report = row(r['preliminaryReport']);
    const path =
      typeof report['url'] === 'string'
        ? /^\/api\/auction-parcels\/([0-9a-f-]{36})\/preliminary-report\/[0-9a-f]{64}\.pdf$/.exec(
            report['url'],
          )
        : null;
    if (!path || path[1] !== r['id']) throw new Error('Netinkama ataskaitos nuoroda.');
    for (const key of ['createdAt', 'offerObservedAt'])
      if (typeof report[key] !== 'string' || !Number.isFinite(Date.parse(report[key])))
        throw new Error('Netinkama ataskaitos data.');
    if (Date.parse(String(report['createdAt'])) < Date.parse(String(report['offerObservedAt'])))
      throw new Error('Netinkama ataskaitos data.');
  }
  if (r['terrainPreview'] != null) {
    const preview = row(r['terrainPreview']);
    const path =
      typeof preview['url'] === 'string'
        ? /^\/api\/landscape\/parcels\/([0-9a-f-]{36})\/terrain\/[0-9a-f]{64}\.png$/.exec(
            preview['url'],
          )
        : null;
    if (
      !path ||
      path[1] !== r['parcelId'] ||
      typeof preview['attribution'] !== 'string' ||
      !preview['attribution'].trim() ||
      typeof preview['heightReferenceVerified'] !== 'boolean'
    )
      throw new Error('Netinkama reljefo peržiūra.');
    for (const key of ['width', 'height'])
      if (
        !finite(preview[key]) ||
        !Number.isInteger(preview[key]) ||
        preview[key] < 1 ||
        preview[key] > 2048
      )
        throw new Error('Netinkamas reljefo vaizdo dydis.');
    if (
      !finite(preview['verticalExaggeration']) ||
      preview['verticalExaggeration'] < 1 ||
      preview['verticalExaggeration'] > 50 ||
      !finite(preview['meshResolutionM']) ||
      preview['meshResolutionM'] <= 0 ||
      preview['meshResolutionM'] > 100 ||
      !finite(preview['surveyYearFrom']) ||
      !Number.isInteger(preview['surveyYearFrom']) ||
      preview['surveyYearFrom'] < 2000 ||
      !finite(preview['surveyYearTo']) ||
      !Number.isInteger(preview['surveyYearTo']) ||
      preview['surveyYearTo'] > 2100 ||
      preview['surveyYearTo'] < preview['surveyYearFrom']
    )
      throw new Error('Netinkami reljefo vaizdo duomenys.');
  }
  return r as unknown as LandscapeDetail;
}
export function landscapeLabel(value: LandscapeSummary): string {
  if (value.isStale) return 'Vertinimas atnaujinamas';
  if (value.score !== null) return `Kraštovaizdis ${value.score}/100`;
  return {
    not_analyzed: 'Dar nevertintas',
    queued: 'Laukia vertinimo',
    processing: 'Vertinama',
    ready: 'Vertinimas paruoštas',
    partial: 'Duomenys nepilni',
    unavailable: 'Trūksta duomenų',
    failed: 'Vertinimas nepavyko',
  }[value.status];
}
