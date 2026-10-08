# Running landscape analyses

The auction demo currently has no LiDAR-backed scores. The worker consumes **prepared, verified** bundles. Raw LAS/LAZ preparation and bounded GRPK context downloads are available; Geoportal account orders remain manual.

## Request and prepare a real pilot

The current selected offer maps to canonical parcel `b0795d8b-c0a5-4898-9902-a36aa5e1d846`, cadastral number `3960/0004:0206`. Its request is already exported at `.local/landscape-prepared/selected-parcel-request/`: `ORDER.md`, `pilot-area.json` and WGS84 `geoportal-order-area.geojson`. The buffered EPSG:3346 envelope is `[420020,6055500,426380,6062090]`, about 41.9 km².

The official [NŽT workflow](https://nzt.lrv.lt/lt/naujienos/kaip-gauti-valstybinius-erdvinius-duomenis-CkKP/) requires registration, selecting a dataset/area, submitting an order and downloading prepared files. Request all intersecting classified LAZ sheets. Retain survey years, the exact licence/derivative conditions and height-datum documentation.

Build the preparation container and validate mechanics independently of the database:

```powershell
docker compose -f deploy/compose.yaml --profile landscape-preparation build landscape-preparation
docker run --rm --memory=4g --cpus=2 --tmpfs /tmp:size=128m fluxus-landscape-preparation --preparation-self-test
```

Its upstream image is digest-pinned. Despite the image tag `2.9.2`, the inspected binaries report **PDAL 2.8.4 / GDAL 3.11.3**; reports record executable versions. The scoring worker separately uses GDAL 3.11.4.

Export another accepted canonical parcel, then fetch public GRPK context:

```powershell
docker compose -f deploy/compose.yaml --profile landscape-preparation run --rm landscape-preparation --pilot-plan CANONICAL_UUID --output pilot-request
```

Copy the request folder under `.local/landscape-inputs/` (the preparation input mount is read-only), then:

```powershell
docker compose -f deploy/compose.yaml --profile landscape-preparation run --rm landscape-preparation --fetch-context /inputs/pilot-request/pilot-area.json --output pilot-context
```

The selected parcel's real context snapshot already exists at `.local/landscape-prepared/selected-parcel-context/`: 89 building footprints, a complete query of 73 road features with no TIPAS 1/2 major roads, and a 5 m cover raster. Coverage is verified against the GRPK coverage polygon. All spatial-query IDs are fetched in batches of 200 and rechecked; truncated/missing/conflicting responses fail. Snapshots can span service edits and are not an atomic provider release.

Cover precedence is land cover → wetlands → water → built-up areas. `ms0` is forest; `sd2`/`sd42` and wetlands are open natural; water is class 3; `ms4`/`sd11`/`sd7` and built-up areas are class 0. `sd15`, `sd4`, new codes and gaps remain NoData. The selected snapshot has **92.55% classified coverage**, so it cannot alone establish a complete nature score. Inspect the source classes and survey alignment before registration; do not silently relabel unknown cover as non-natural.

Put original tiles under `.local/landscape-inputs/pilot-raw/`. Create `request.json` alongside them:

```json
{
  "tiles": [{ "path": "actual-tile.laz", "sha256": "ACTUAL_64_HEX_SHA256", "resolutionM": 0 }],
  "bounds": [420020, 6055500, 426380, 6062090],
  "verticalDatum": "VERIFIED_COMMON_HEIGHT_DATUM_IN_METRES",
  "datumEvidence": "Exact provider document and section verifying heights",
  "sourceUrl": "https://provider.example/actual-release",
  "licenseUrl": "https://provider.example/exact-reuse-conditions",
  "surveyYearFrom": 2025,
  "surveyYearTo": 2026,
  "datumVerified": true,
  "previewOnly": false
}
```

Use actual years and all tiles; placeholders are not provenance. Compute SHA-256 with `Get-FileHash`. Run:

```powershell
docker compose -f deploy/compose.yaml --profile landscape-preparation run --rm landscape-preparation --prepare /inputs/pilot-raw/request.json --output pilot-terrain
```

The converter verifies hashes and EPSG:3346, then streams merged tiles into aligned 2 m grids. If original tiles omit CRS metadata, add `horizontalCrsDeclaration` as a `.prj` asset with its actual SHA-256 and `horizontalCrsEvidence` identifying the provider declaration. GDAL must resolve that declaration to EPSG:3346. PDAL uses `default_srs` only for missing CRS; a conflicting embedded CRS fails instead of being overridden. Ground uses class 2 IDW; surface uses the maximum of classes 2–6. Withheld, noise and unclassified points are excluded. The radius is 2√2 m; fallback gap filling is disabled. NoData remains missing.

Set `datumVerified: true` only with evidence for compatible heights. Unverified heights require `previewOnly: true` and an explicit description of what remains unknown. Such terrain is for local inspection and must not be registered as a verified scoring bundle. A horizontal `.prj` does not establish height units or a vertical datum. The current selected pilot uses this preview mode with the original ordered index declaration.

Limits: 64 tiles, 8 GiB input, 800 million header points, 25 million 2 m output cells, 30-minute preparation deadline and 2 GiB free-space check. Output folders must be new; successful outputs are moved from owned staging folders, failures remove only their own staging. Files plus `terrain-preparation.json` are written under `.local/landscape-prepared/`. No preparation command registers a bundle, enqueues a job or inserts a score.

After reviewing both reports, copy the terrain/context assets into a new immutable folder under `.local/landscape-inputs/`, construct the scoring manifest below with real hashes and all source attribution, and register only parcels whose coverage/datum/reuse checks pass.

## Offline 3D report preview

A terrain illustration can be shown independently of a landscape analysis. The selected pilot already has a **1440 × 960 PNG**, generated from its actual 2 m ground raster using a **20 m display mesh** and a labelled **20× vertical exaggeration**. Colors represent relative ground elevation; the red outline is the actual projected parcel boundary. It models bare ground, not trees/buildings or photographic appearance. Missing cells remain gaps. Height reference is explicitly unverified, and the image does not establish a score.

The reusable scripts are `scripts/export-terrain-mesh.py` (GDAL runtime) and `scripts/render-terrain-preview.py` (Python/Matplotlib). Generate the bounded mesh with the existing worker image:

```powershell
$preparedRoot = (Resolve-Path .local/landscape-prepared).Path
$renderScripts = (Resolve-Path scripts).Path
docker run --rm --memory=1g --cpus=1 --entrypoint python3 --mount "type=bind,src=$preparedRoot,dst=/prepared" --mount "type=bind,src=$renderScripts,dst=/scripts,readonly" fluxus-landscape-worker /scripts/export-terrain-mesh.py /prepared/selected-parcel-preview/ground.tif /prepared/selected-parcel-inspection/terrain-mesh.npz
```

The exporter accepts a north-up one-band EPSG:3346 raster at ≤2 m and ≤25 million cells, creates at most 360 rows/columns, averages complete blocks, and retains any block containing missing input as missing. It records the original ground SHA-256 and refuses to overwrite a mesh file. The render has its own mesh rather than claiming display resolution equals source resolution.

Install the optional offline plotting dependencies in a local environment, then render into a **new** directory:

```powershell
python -m venv .local/terrain-render-env
& .local/terrain-render-env/Scripts/python.exe -m pip install -r scripts/terrain-render-requirements.txt
& .local/terrain-render-env/Scripts/python.exe scripts/render-terrain-preview.py .local/landscape-prepared/selected-parcel-inspection/terrain-mesh.npz .local/landscape-prepared/selected-parcel-request/pilot-area.json .local/new-terrain-preview/b0795d8b-c0a5-4898-9902-a36aa5e1d846 --exaggeration 20 --year-from 2019 --year-to 2022 --attribution "Geoportal / NŽT Lidar_DR_LT · order LIDAR_DRLT_recent-2336"
```

Inspect the PNG and source metadata before placing `terrain.png` and `preview.json` under `.local/terrain-previews/CANONICAL_UUID/`. The selected pilot is already there. Compose mounts only this preview directory read-only into the API. Native runs use `Landscape__PreviewRoot` pointing to its absolute path. No plotting/GDAL runtime is needed for serving the image, and rendering never writes analysis rows.

The report returns optional `terrainPreview` metadata and a content-hash image URL. The API verifies canonical parcel identity, current geometry hash, PNG dimensions/header, SHA-256, metadata and file-size bounds; missing, changed or invalid previews are omitted. It refuses linked files/directories and serves only a fixed PNG filename beneath the configured root. Changed geometry hides the old image. The Angular report displays an expandable image with scale, mesh resolution, source years, attribution and verification status, and handles image failures independently. There is no automatic image generation for other parcels yet.

## Preliminary pilot PDF

The selected Varpučiai offer now has a two-page English inspection report, created on 8 October 2026. It combines the captured auction terms, terrain image, measured coverage, context snapshot and outstanding verification work. Its captured auction end was 7 October; the report does not claim current availability or provide an overall landscape score.

`scripts/create-preliminary-report.py` uses ReportLab and Windows Segoe UI fonts. This is a template for the current pilot, including its reviewed survey dates and road summary; review those statements before adapting it to another area. With the captured inputs, generate a new output filename:

```powershell
python scripts/create-preliminary-report.py --pilot .local/landscape-prepared/selected-parcel-request/pilot-area.json --auction .local/preliminary-report-offer.json --offer-id 0d4cf142-b4ce-43e7-933d-d77c73c42a44 --terrain-quality .local/landscape-prepared/selected-parcel-inspection/raster-inspection.json --context .local/landscape-prepared/selected-parcel-context/context-preparation.json --cover-quality .local/preliminary-report-cover.json --preparation .local/landscape-prepared/selected-parcel-preview/terrain-preparation.json --preview-directory .local/terrain-previews/b0795d8b-c0a5-4898-9902-a36aa5e1d846 --output output/pdf/varpuciai-preliminary-landscape-report-v2.pdf
```

The auction input is a captured API auction detail response; the cover quality input contains the valid percentage and cell count calculated from the prepared cover raster. The builder checks accepted, unscored offer identity, matching parcel/geometry evidence and preview integrity. It refuses to overwrite the PDF. Inspect both rendered pages before attaching a new version.

To serve a reviewed report locally, place it at `preliminary-report.pdf` beside the parcel's `terrain.png` and copy its generated JSON sidecar into `preview.json` as the `preliminaryReport` object. Generated PDFs and source files stay ignored. The API serves at most 8 MiB, verifies the PDF header and SHA-256, and binds it to the exact offer ID and auction observation timestamp. Changed geometry, a refreshed offer snapshot, another ownership offer, corrupt/missing files or invalid metadata hide the link. There is no automatic report regeneration.

The detail response exposes optional `preliminaryReport` metadata. `GET /api/auction-parcels/{offerId}/preliminary-report/{sha256}.pdf` returns the verified PDF. The Angular panel displays its creation and listing-observation dates beside the PDF link, while keeping the analysis state and score unchanged.

## Setup and mechanical check

```powershell
docker compose -f deploy/compose.yaml up -d --wait db
dotnet build Fluxus.slnx
dotnet src/Fluxus.Api/bin/Debug/net10.0/Fluxus.Api.dll --migrate
docker compose -f deploy/compose.yaml --profile app build api
docker compose -f deploy/compose.yaml --profile app up -d api
docker compose -f deploy/compose.yaml --profile landscape build landscape-worker
docker run --rm --memory=4g --cpus=2 --tmpfs /tmp:size=512m fluxus-landscape-worker --self-test
```

The self-test runs isolated synthetic scenarios and **never accesses the catalogue**. Its larger temporary mount is needed only while generating fixtures.

## Bundle contract

Place files below `.local/landscape-inputs/pilot/`, mounted as `/inputs/pilot/`. Register in the same execution environment as processing; container registration stores container paths and native registration stores native paths.

All inputs use EPSG:3346/metres. Ground and surface elevations must share a **verified vertical datum** and metre units. Record the oldest/newest survey years across all inputs. Do not upsample coarse sources to claim finer resolution.

| File | Content |
| --- | --- |
| `ground.tif` | One-band ground elevation, native resolution ≤2 m, valid georeferencing and NoData metadata |
| `surface.tif` | One-band obstruction surface including ground, vegetation/buildings, native ≤5 m, matching height datum |
| `cover.tif` | One-band categorical cover: 0 verified human/other non-natural, 1 forest, 2 verified open natural, 3 water. Unknown classes are NoData, never zero. Record class precedence. |
| `buildings.geojson` | Projected EPSG:3346 FeatureCollection, deduplicated footprints, one feature per building |
| `major-roads.geojson` | Projected EPSG:3346 line FeatureCollection with documented major-road class selection |

Projected GeoJSON is internal interchange, **not RFC 7946 web-map GeoJSON**. The API converts displayed observer/image coordinates to WGS84. Provide the parcel envelope plus at least 3.1 km raster margin and 1.5 km complete building/road coverage. Missing cells withhold scores. Empty context collections require verified complete observations. Record interpolation/substitutions and audit coverage; a national bounding box does not prove local completeness.

`manifest.json` fields (replace placeholders, every hash and the illustrative bounds):

```json
{
  "provider": "Actual provider",
  "release": "Actual release/version",
  "sourceUrl": "https://provider.example/dataset",
  "licenseUrl": "https://provider.example/exact-license",
  "attribution": "Required attribution for all datasets",
  "surveyYearFrom": 2025,
  "surveyYearTo": 2026,
  "verticalDatum": "Verified common datum; metre elevation units",
  "epsg": 3346,
  "substituted": false,
  "preparation": "Tools/versions, point classes, interpolation, masks, cover precedence, road selection, building deduplication, coverage checks",
  "ground": { "path": "ground.tif", "sha256": "REPLACE_WITH_64_HEX_DIGITS", "resolutionM": 2 },
  "surface": { "path": "surface.tif", "sha256": "REPLACE_WITH_64_HEX_DIGITS", "resolutionM": 5 },
  "cover": { "path": "cover.tif", "sha256": "REPLACE_WITH_64_HEX_DIGITS", "resolutionM": 5 },
  "buildings": { "path": "buildings.geojson", "sha256": "REPLACE_WITH_64_HEX_DIGITS", "resolutionM": 0 },
  "majorRoads": { "path": "major-roads.geojson", "sha256": "REPLACE_WITH_64_HEX_DIGITS", "resolutionM": 0 },
  "contextBounds": [496900, 6096900, 503100, 6103100]
}
```

Use `Get-FileHash -Algorithm SHA256`. Source/licence links must use HTTPS. Keep source files immutable below the configured input root. The converter is mechanically tested against synthetic classified LAZ and has prepared the selected pilot's 56 actual Lithuanian LAZ tiles. Those preview rasters retain source gaps and unverified height-reference status; see the validation record before attempting registration.

## Register and process

List canonical parcel IDs; offer IDs in catalogue cards are different:

```powershell
docker compose -f deploy/compose.yaml --profile landscape run --rm landscape-worker --list-parcels
```

Select a small pilot with verified coverage. The offer landscape endpoint also exposes `parcelId`.

```powershell
docker compose -f deploy/compose.yaml --profile landscape run --rm landscape-worker --register /inputs/pilot/manifest.json --parcels "CANONICAL_UUID_1,CANONICAL_UUID_2"
docker compose -f deploy/compose.yaml --profile landscape run --rm landscape-worker --once
docker compose -f deploy/compose.yaml --profile landscape up -d landscape-worker
```

`--once` processes one eligible job. `--watch`, the container default, polls every 10 seconds. Same bundle/geometry/version reuses work; changed content or version creates a new job. Duplicate registration does not silently restart terminal jobs. Transient failures retry with a bounded delay, up to three claims. The job deadline is 15 minutes and crash-recovery lease 20 minutes.

Stop processing without removing results:

```powershell
docker compose -f deploy/compose.yaml --profile landscape stop landscape-worker
```

Published results remain available through the API's read-only artifact mount. The worker checks a 10 GiB artifact quota and reserves 1 GiB working space before claiming a job. Quota/free-space failures wait without consuming attempts. Every six hours it removes only outputs older than two days that have no analysis row, and expired staging directories without an active job lease. It retains all published analyses, including historical results, recent files and operator folders, and rejects linked directories during cleanup.

`LANDSCAPE_MAX_ARTIFACT_BYTES` overrides the Compose quota. `LANDSCAPE_PROCESSING_ENABLED=false` stops job processing on startup; manual source preparation/registration commands remain available. The worker profile is optional. Retention of published history is intentional; an operator must expand storage or establish a separate archival policy when that history reaches the quota.

## Results and API

Open [the prototype](http://127.0.0.1:8080/) and select an offer. **Kraštovaizdžio potencialas** displays state, four components, facts and source quality. Complete results expose **Galimi vaizdai** for the representative 1.7 m/3 km view. Sale/rental/ownership conditions remain separate.

- `GET /api/auction-parcels/{offerId}/landscape`: summary, metrics/components/provenance, optional visibility image.
- `GET /api/landscape/analyses/{analysisId}/visibility.png`: immutable checksum-verified PNG; no on-demand processing.
- Catalogue: `minLandscapeScore=0..100`, `landscapeStatus=all|ready|not_analyzed|queued|processing|partial|unavailable|failed`, `sort=landscape_desc|landscape_asc`.

Minimum-score filters exclude stale/unscored results even at zero. Either score sort puts them last. Offers sharing a canonical parcel reuse its physical analysis. Multi-parcel auctions have no averaged score. Analysis version `gdal-grid-v2` preserves measured terrain visibility and its image when cover is incomplete, but withholds cover-dependent components and the overall score.

Read [the validation record](LANDSCAPE_SOURCE_VALIDATION.md) before interpreting scores. Real-source verification and human preference calibration remain required.
