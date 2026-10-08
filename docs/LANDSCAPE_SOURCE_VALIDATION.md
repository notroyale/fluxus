# Landscape potential: validation and current scope

Updated on 8 October 2026. **Prototype processing, preparation and presentation are implemented; the first real LiDAR pilot has been downloaded.** Height-reference verification and the broader source/calibration pilot remain pending. No synthetic result has been inserted into the live catalogue.

## Source access gate

The official [2025 LiDAR announcement](https://www.geoportal.lt/geoportal/naujienos/-/asset_publisher/sUiF20Ur705t/content/id/3167123) describes classified LAZ, survey dates of May–October, and an area-order workflow that generates download links. The inspected page does not provide an anonymous tile-download endpoint. It describes the complete archive as 2.3 TB; nationwide acquisition is inappropriate for this pilot.

The [2025 metadata](https://www.geoportal.lt/metadata-catalog/catalog/search/resource/details.page?uuid=%7B35934016-7D5B-4014-8B7E-00FCBFB740C4%7D) identifies EPSG:3346 and classified ground, vegetation, buildings and noise. Its legal fields indicate copyright/licence without identifying the exact reusable licence. Vertical datum compatibility, actual parcel coverage, source-file readability and permission to redistribute derivatives remain unverified. The user has no existing download links or dataset.

[NŽT's official access instructions](https://nzt.lrv.lt/lt/naujienos/kaip-gauti-valstybinius-erdvinius-duomenis-CkKP/) explicitly require a registered Geoportal account and an area order for LiDAR. A historical anonymous tile URL cited in a government study returned 404.

On 7 October, the user's existing authenticated session allowed a bounded order for the [combined 2019–2026 catalogue](https://www.geoportal.lt/metadata-catalog/catalog/search/resource/details.page?uuid=%7B3C0D5E47-49CA-4132-88C1-EC3D1E4610B4%7D). The four selected sheets are **44/31, 44/32, 45/31 and 45/32**, with a portal-confirmed area of **100 km²**. The order requests an ESRI Shapefile index in LKS-94 containing per-sheet LAZ links; it does not directly deliver the point clouds. The separate 2025 coverage map does not cover the Varpučiai pilot.

On 8 October, after the user completed signing, order **LIDAR_DRLT_recent-2336** reported **“Ordered geoproduct is ready.”** The 2,613-byte index was downloaded from the authenticated session. The portal specifies a **12-month agreement**, acceptance after identity verification through Elektroniniai valdžios vartai, and execution only after signing. The [general conditions](https://www.geoportal.lt/geoportal/en/web/en/general-conditions-for-the-use-of-spatial-data) require attribution, restrict redistribution of ordered data and access URLs, and require use to stop when the agreement expires. The Lithuanian wording prevails; actual dataset use limitations and derivative/publication permissions still require review. Do not commit private service/download URLs or identity data. This is not evidence of unrestricted open-data rights.

The ordered CSV contained four actual archive links. Bounded ZIP directory/header range reads inspected their 100 one-kilometre LAZ tiles. Only **56 tiles** intersecting the pilot envelope plus interpolation margin were retrieved: **3,041,108,634 bytes**, with **723,796,588 header points**. Every downloaded entry passed its provider ZIP CRC32, and SHA-256 is retained in the local acquisition report. Original files are unchanged. Raw data and reports are under `.local/landscape-inputs/selected-parcel-lidar/`; the original ordered index is under `.local/landscape-inputs/geoportal-pilot-index/`.

The LAS 1.2 files lack embedded projection VLRs. The original index `.prj` resolves to **EPSG:3346** using GDAL. Preparation now permits a checksum-verified provider declaration only for missing CRS and rejects conflicting embedded CRS. It does not rewrite the originals. PDAL reads the actual compressed files. A bounded sample of 1,000 points per tile (56,000 total) contains classes 1–7 and GPS times spanning **23 September 2019 to 25 September 2022**; this sampling does not prove the complete acquisition range or class proportions. Neither the ordered documentation nor the inspected files identifies a vertical datum. An explicit `previewOnly` preparation request permits local terrain inspection; it does not authorize registering verified landscape results.

**M0 is not complete.** The first area's acquisition is complete. Verify compatible heights and derivative-use conditions, inspect prepared terrain/coverage, and obtain two further representative areas before the 20-parcel pilot.

### First real terrain preview

The bounded 56-tile preparation completed successfully in about ten minutes on the configured two-CPU container. The aligned EPSG:3346 ground and surface rasters each contain **3,180 × 3,295 cells at 2 m**. Ground has **99.0467% valid cells**; surface has **99.9549%**. Surface never falls below interpolated ground at their shared valid cells. These checks establish readability, alignment and coverage, not the accuracy of the classification or height reference.

Gaps are locally significant: the nearest missing ground cell is about **188 m** from the parcel centroid, and the nearest missing surface cell about **501 m**. Within 3 km of that centroid there are 72,556 missing ground cells and 3,128 missing surface cells. The current strict completeness rules would withhold dependent results; high whole-envelope coverage is not evidence of a complete viewshed. Do not fill these cells merely to obtain a score. Classification errors, mixed survey vintages and interpolation choices need further review.

Outputs are `.local/landscape-prepared/selected-parcel-preview/ground.tif`, `surface.tif` and `terrain-preparation.json`. Local quality measurements and a relative terrain image are in `.local/landscape-prepared/selected-parcel-inspection/`. The initial converter report's generic prose called supplied datum evidence operator-verified; its explicit `datumVerified=false` and `previewOnly=true` fields identify this run as unverified. The prose is corrected in subsequent builds, and the inspection report records this caveat. Nothing was registered or published to the live catalogue.

An additional **static 3D ground preview** is now visible in the selected offer's landscape report. It uses a 20 m mesh averaged from complete blocks of the actual 2 m raster, with holes preserved and an explicit 20× vertical exaggeration. Relative elevation colors are not land-cover textures. The red outline follows the parcel boundary. Its 1440 × 960 PNG and sidecar are local under `.local/terrain-previews/b0795d8b-c0a5-4898-9902-a36aa5e1d846/`. This is an inspection illustration independent of analyses: it does not introduce a score, establish a verified datum, or model trees/buildings. The API checks geometry/content hashes and serves the bounded PNG through a read-only mount; browser verification confirms the image loads and its limitation/scale labels are displayed. Reusable offline rendering steps are in the runbook. There is no automatic render backfill yet.

## Real context snapshot

The public [GRPK map service](https://www.geoportal.lt/arcgis/rest/services/NZT/GRPK/MapServer) was inspected for actual schema and class labels. A bounded snapshot was prepared for canonical parcel `b0795d8b-c0a5-4898-9902-a36aa5e1d846` (`3960/0004:0206`), matching the currently selected demo offer. Its 3.1 km buffered request envelope is `[420020,6055500,426380,6062090]` in EPSG:3346.

Files are local at `.local/landscape-prepared/selected-parcel-context/`; original layer responses and metadata are retained. Layers 30/27/21/19/23/24/22/11 provide coverage, land cover, wetlands, water, built-up areas, buildings and roads. IDs-first queries fetch every ID in batches of 200, reject transfer limits/missing features/duplicates, enforce EPSG:3346, and recheck ID sets. Coverage geometry covers the requested raster envelope. These safeguards cannot establish a single atomic release across multiple queries.

There are **89 unique building footprints** within the 1.5 km context envelope. All 73 road features were fetched; none has major-road TIPAS 1/2. Road presence is not an access-rights claim. Cover is rasterized at 5 m using documented precedence and source labels. Known forest, natural meadow/sand, wetlands, water, cultivation/orchards/damaged ground and built-up polygons are mapped explicitly. `sd15` (trees/shrubs), `sd4` (unused land), new classes and geometric gaps remain unknown: GDAL reports **92.55% valid cover**, about 7.45% NoData. Thus this snapshot does not establish complete nature/view-composition inputs. No national context archive was downloaded.

The matching pilot order files are `.local/landscape-prepared/selected-parcel-request/ORDER.md`, `pilot-area.json` and `geoportal-order-area.geojson`. Export/fetch/preparation commands do not write analyses to the catalogue.

## Implemented slice

- Versioned `balanced-retreat-v1`: views 35%, terrain 20%, nature 30%, seclusion 15%; physical anchors from the plan, missing components withhold the overall score, weights uncalibrated.
- Additive dataset/job/immutable-analysis/current-result schema; canonical parcel reuse, content and geometry hashes, leases, three bounded attempts, staleness and last-good preservation.
- Manual registration of 1–100 canonical parcels. Geometry changes invalidate scores; an operator enqueues new geometry with a verified bundle. Price/share changes do not trigger analysis. Auto-enqueue and release discovery are deferred.
- Separate .NET worker: one job at a time, 15-minute whole-job deadline, 20-minute lease; brief projection transactions use a shared advisory lock to avoid cyclic-FK deadlocks. HTTP requests do no terrain computation.
- Local GeoTIFF ground/surface/cover plus projected context vectors; registration/processing checksum checks, actual raster CRS/georeferencing/band/resolution validation.
- Separate digest-pinned PDAL preparation container, streaming classified LAS/LAZ, merged tile seams, embedded or verified provider-declared CRS, hash/input/output budgets, explicit height-verification/preview states and immutable outputs. Ground class 2 IDW and surface classes 2–6 maximum use a 2 m grid/radius 2√2 m; no fallback gap filling. Withheld/noise/unclassified points are excluded.
- 5 m visibility/terrain grids, 2 m gentle-area grids. Maximum surface aggregation preserves obstructions; independent minimum validity masks prevent resampling from hiding source NoData. Each visibility point needs complete 3 km disks plus a cell margin.
- Deterministic points inside holes/multiparts, up to 16 at least 10 m apart. At least three complete points, except parcels below 400 m² with a low-quality sparse-point allowance.
- Ground-relative 1.7 m eyes on the obstruction surface, refraction/curvature 0.85714, median view measurements and a separate best visible area. Below-canopy observers never start on treetops.
- Ground P90–P10 relief, TPI variation, natural cover, forest edge, water proximity/visibility, building distance/count, major-road distance; gentle ground ≤8° and largest four-connected patch.
- `gdal-grid-v2` retains measured visibility/terrain/seclusion and an image when cover is incomplete. Cover-dependent measurements/components and the overall score remain null; unknown cover never becomes a verified zero.
- Validated offer detail and catalogue score/status filters, null-last score sorts and consistent list/map results. Angular cancellable resources, URL controls, per-parcel explanations and selected visibility overlay retain sale/rental/ownership semantics.

The initial overlay is a **1024×1024 PNG in Web Mercator**, served by immutable analysis ID with checksum validation and a read-only artifact mount. The planned XYZ tile pyramid is deferred. It depicts visible surface cells, including canopy/building surfaces, rather than guaranteed views of underlying features.

Current TPI uses a square local window with 500 m half-width. Terrain/cover use a 1 km disk around the parcel point on surface. Distances saturate at 1 km for forest/water/buildings and 1.5 km for major roads; UI labels saturation “≥”. Prepared vector coverage/provenance is an operator declaration to audit; empty lists are meaningful only for a verified complete area.

## Validation

GDAL **3.11.4** uses the official OSGeo Ubuntu image, pinned to `sha256:4b94b5e76e8b5616a4d5dc38e6e20def10ab70c3487eeb923d90547372e70a03`. Runtime is .NET 10.0.9. [GDAL's viewshed documentation](https://gdal.org/en/stable/programs/gdal_viewshed.html) describes projected-CRS and input-NoData limitations; the worker checks these before processing.

`--self-test` generates explicitly synthetic inputs in its own temporary directory, runs the actual GDAL processor, never connects to a database, and deletes only its temporary files.

| Synthetic scenario | Outcome | Median visible area | Points |
| --- | --- | --- | --- |
| Flat unobstructed ground | Ready | 28.257125 km² | 16 |
| 80 m intervening ridge | Ready; views reduced | 17.135350 km² | 16 |
| 20 m canopy over ground-level observer | Ready; distant views blocked | 0.000225 km² | 16 |
| Valley with 80 m surrounding rise | Ready; relief and screening detected | 18.2390625 km² | 16 |
| 50 m building screen | Ready; distant views reduced | 18.9454125 km² | 16 |
| Missing surface cells | Partial; score null | Not calculated | 0 |
| Missing cover cells | Partial; dependent components/score null, terrain visibility retained | 28.257125 km² | 16 |

These checks validate mechanics, not Lithuanian source files or the beauty ranking. Source-quality flags are heuristics, not statistical confidence.

The preparation self-test runs the actual image, generating two classified LAZ tiles in an owned temporary directory. It checks seam continuity, flat ground beneath a building, a 50 m building surface, exclusion of class-7/withheld outliers, preserved survey holes, checksum rejection and refusal to overwrite output. The upstream `pdal/pdal:2.9.2` image is pinned to `sha256:18d73101500cd673052ee4ead8a4b716a9dad09c8711e88e7a37abed2496c469`; its actual binaries report **PDAL 2.8.4 / GDAL 3.11.3**. Reports retain these executable versions.

85 backend and 36 frontend tests pass on 8 October, along with the solution build (zero warnings/errors), production Angular build/formatting and API Docker build using Node 24.19.0. The preview checks cover changed geometry, corrupt images, malformed metadata, excessive image dimensions/scales, parcel-bound image URLs, and unverified images remaining unscored. API/worker/preparation Docker builds, the seven-scenario GDAL self-test and the classified-LAZ preparation self-test pass. The latter now exercises a missing embedded CRS, rejection without a declaration, and successful fallback to a checksum-verified provider PRJ. Checks cover scoring/missing inputs/quality, holes/multipart sampling, hidden-water handling, gentle-area NoData, canonical/job reuse, expired-lease rejection, retries/last-good results, score filtering/sorting/list-map agreement, bounded/truncated/changing context queries, preparation evidence/budgets, artifact quota/abandoned-output cleanup, URL/reset behavior, and independent landscape failures.

Before the 8 October browser refresh, the local demo contained **100 auctions / 115 offers / 114 mapped offers / zero landscape scores**. It reported 114 `not_analyzed` summaries and one `unavailable` summary for the unresolved offer. The database was backed up before migration; no synthetic scores or source fixtures were imported into it. Manual catalogue refresh now updates live listing records independently of terrain preparation and scoring; current catalogue counts can change without creating landscape analyses.

## Preliminary report

On 8 October 2026, a two-page English preliminary report was generated for offer `0d4cf142-b4ce-43e7-933d-d77c73c42a44` and visually checked on both rendered pages. It includes the real ground preview, captured 3/4 ownership offer and EUR 10,800 starting price, full-envelope source coverage and outstanding checks. The 6 October listing snapshot scheduled its end for 7 October; the report explicitly leaves current availability and auction outcome unverified.

The local parcel panel exposes the report through a content-hash PDF URL. Verification returned HTTP 200 with `application/pdf` and matching SHA-256. The API binds the PDF to the offer, observed listing timestamp and current parcel geometry; reports become unavailable after snapshot changes and cannot leak between different ownership offers. Backend/frontend checks cover this behavior and malformed metadata. No analysis rows or scores were created; the report is an inspection artifact, with unverified heights and unresolved local gaps.

## Remaining work

The first area's LiDAR acquisition and raster preparation are complete. Height-reference verification, a justified strategy for actual survey gaps, detailed classification/seam checks, two further source areas, the 20-parcel preference review, per-parcel performance benchmarks and backfill remain pending. Valley/building/hidden-water/seam mechanics have controlled checks; they still need confirmation on real terrain.

Container limits: 2 CPUs, 4 GiB; four million cells per analysis grid, 3 km parcel-envelope dimensions, 16 points, context vectors ≤64 MiB/200,000 features, image reads ≤4 MiB. Inputs are read-only. Larger jobs remain unavailable. The worker has a 10 GiB artifact quota, 1 GiB working reserve, a processing switch and six-hour abandoned-output cleanup with a two-day grace period; tests verify published history, recent files, active leases and operator folders survive cleanup. All published analyses are retained. Native free-space checks can be affected by Docker Desktop's virtual filesystem; the bounded artifact quota also limits growth. Rich telemetry, source-release discovery, automatic geometry-change enqueue, published-history archival policy and XYZ tiling remain deferred. Stop the optional worker to stop processing while catalogue browsing continues. Do not destructively roll back the schema or assign synthetic scores to real offers.
