# Fluxus

A working first slice of the Lithuanian land-auction map: ASP.NET Core (.NET 10), Angular 22, PostgreSQL/PostGIS, and MapLibre. It includes a Lithuanian map/list UI, filters, auction details, exact cadastral boundaries, a manual importer, and an optional scheduler.

The default demo uses a sanitized capture from **6 October 2026, 20:27 Europe/Vilnius**: **10 auctions, 11 land offers, 10 distinct parcels**. Two offers sell separate halves of the same parcel. All 11 offers resolved by exact identifiers in this sample. This is a historical sample, not a complete or current national catalogue.

## Run the complete demo with Docker

From the repository root, with Docker running:

```powershell
docker compose -f deploy/compose.yaml --profile app build api
docker compose -f deploy/compose.yaml --profile app run --rm -e Source__Mode=Fixture api --migrate --import-once
docker compose -f deploy/compose.yaml --profile app up -d
```

Open [http://localhost:8080](http://localhost:8080). The database persists in the `fluxus_postgres` volume. `docker compose -f deploy/compose.yaml --profile app stop` stops the app and database while keeping their data.

The seed command uses captured fixtures. The running Compose app enables live manual refreshes; **Atnaujinti duomenis** in the header starts a bounded scrape of up to 100 active-listing auction attempts. No scrape starts merely by opening the page. `SOURCE_MODE=Fixture` keeps the running app in fixture mode and disables the button.

The development database listens only on `127.0.0.1:15432`; the demo app listens only on `127.0.0.1:8080`. Compose supplies a local development password. For another environment, supply `POSTGRES_PASSWORD` and an appropriate `ConnectionStrings__Catalogue`; migrations are an explicit command, not an automatic web-host startup action.

## Develop locally

Requirements: .NET SDK 10.0.301 (see `global.json`), Node 24.15+ within the 24.x release line (tested with 24.19.0), npm, and Docker. Node 25 is outside this project's supported range.

Start PostGIS and import the fixtures:

```powershell
docker compose -f deploy/compose.yaml up -d --wait db
dotnet restore Fluxus.slnx --locked-mode
./scripts/import.ps1
```

Run the backend in one terminal:

```powershell
dotnet run --project src/Fluxus.Api --no-launch-profile
```

Run Angular in another:

```powershell
cd src/Fluxus.Web
npm ci
npm start
```

Open [http://localhost:4200](http://localhost:4200). Angular proxies `/api` and `/health` to the API at port 5286. MapLibre's module worker and shared module are copied by Angular's asset configuration; keep these assets when publishing the frontend.

## Import and configuration

The default import is deterministic and makes no requests to the auction or cadastral services. Repeating it updates existing auctions and offers rather than duplicating them.

```powershell
./scripts/import.ps1                     # captured sample
./scripts/import.ps1 -Live -MaxAuctions 10 # bounded current-source import
./scripts/import.ps1 -Live -MaxAuctions 100 # larger current-source catalogue
```

A live import reads the active land listing and its detail pages, then queries the Geoportal cadastral layer. Requests are sequential with a two-second delay. Up to 20 exact parcel identifiers are queried per geometry request; each returned parcel is matched and validated independently. Geometry calls can take tens of seconds; the timeout is 60 seconds. Accepted geometry is cached for 30 days. Not-found results have a 24-hour retry interval; other unresolved results have a one-hour retry interval. Stored geometry survives a failed refresh. Import runs and per-auction failures are persisted. The limit counts auction attempts, so multiple land sections can produce more offers than auctions.

To import live data using the built Docker image while the app remains available:

```powershell
docker compose -f deploy/compose.yaml --profile app run --rm -e Source__Mode=Live -e Source__MaxAuctions=100 api --import-once
```

Reload the browser after the command finishes to display the expanded catalogue.

For the running local Docker demo, use **Atnaujinti duomenis** instead. The button starts a background job, reports discovery/detail/geometry/save progress and shows saved-auction, offer, mapped-offer and failure counts. It reloads the list, map, municipalities and selected parcel/report when the job finishes, retaining filters and selection. Another browser tab can reopen the page and observe the current job. Duplicate requests reuse that job; a one-minute cooldown follows completion or failure. A separate CLI/scheduled import is excluded by the existing database lock. Requests remain sequential and delayed.

This refresh revisits auctions discovered in the bounded active listing; it does not recheck every historical auction. Existing records absent from that listing retain their captured state, and the catalogue keeps its oldest-observation timestamp/stale warning. A partial result does not claim complete discovery. Accepted records survive source failures; refreshed listing timestamps invalidate older offer-bound preliminary PDF links. LiDAR processing and report generation are separate operations.

Manual refresh endpoints are `GET /api/catalog/refresh` for status and `POST /api/catalog/refresh` with `X-Fluxus-Refresh: 1` to start. The API default is disabled and permits manual refresh only in Live mode. Compose enables it for its loopback-only prototype. Keep it disabled or protect it with operator authorization before exposing the API publicly. Cross-site form submissions are rejected and no CORS policy is enabled. The queue/status is in memory and stops with the API; persisted import outcomes remain in the database. The worker has a 45-minute deadline and does not depend on the initiating HTTP request staying open.

Use a separate database when you need to keep a fixture demonstration and a live catalogue independent. Importing fixtures into an existing live catalogue updates the matching source auctions with captured values, and the UI labels any catalogue containing fixture rows as a sample.

Settings are in `src/Fluxus.Api/appsettings.json` and can be overridden with environment variables:

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__Catalogue` | Local PostGIS connection | Database connection; use provider-required TLS outside the local network |
| `Source__Mode` | `Fixture` | `Fixture` or `Live` |
| `Source__MaxAuctions` | `10` | Maximum auctions attempted in each import |
| `Source__MaxPages` | `40` | Listing pagination limit |
| `Source__DelayMilliseconds` | `2000` | Delay before external requests |
| `Source__ScheduledImportsEnabled` | `false` | Enable live scheduled imports |
| `Source__ManualImportsEnabled` | `false` | Enable the browser refresh action in Live mode; enabled in local Compose |
| `Source__IntervalMinutes` | `60` | Import interval, minimum 30 minutes |

The scheduler runs only in Live mode. It uses the same bounded importer and a PostgreSQL advisory lock. A run that reaches a limit or encounters a source failure is marked partial. Missing auctions are **not** inferred to have ended or deleted. A malformed listing aborts the run; a malformed detail leaves the previous auction intact.

## Verify

```powershell
docker compose -f deploy/compose.yaml up -d --wait db
dotnet test Fluxus.slnx --verbosity minimal
cd src/Fluxus.Web
npm test -- --watch=false
npm run build
npm run format:check
```

Backend integration tests create and remove a uniquely named `fluxus_test_<guid>` database. They never reset the demo database. The test connection can be overridden with `FLUXUS_TEST_CONNECTION` and needs permission to create test databases. CI runs the same checks against a PostGIS service. Tests cover parsing captured HTML, shares and rounded areas, mixed bundles, exact geometry matching, holes/multipart geometry, stable imports, concurrent import exclusion, API filters, and saved-data preservation after a failed detail read.

Refresh checks cover disabled fixture mode, rejected cross-site requests, duplicate starts, background completion, partial summaries, source failures with retained records, an already-running external import, empty listings and cooldown recovery. Angular checks cover progress/disabled states, failed starts, retrying status reads, malformed job responses and automatic catalogue/selected-report reload while preserving filters.

The browser button was tested against the live source on 8 October, 12:45–12:52 Europe/Vilnius. It saved **99 auctions / 113 offers / 112 mapped offers** from 100 attempts; one detail failed parsing (`FormatException`, source ID `338446`). The run correctly reported partial discovery and retained accepted records. The catalogue then contained **128 auctions / 143 offers / 142 mapped offers**, including older retained entries, and kept its 6 October oldest-observation warning. Page reload reconnected to the running job, the completion summary appeared, the catalogue reloaded automatically, and no browser errors were observed. A local database backup was taken before this run.

Offers first added within the last five days (a rolling 120-hour window) use lighter green map dots and a matching legend. Each offer stores a nullable `FirstSeenAt`; later imports preserve it, independently of the auction start date and latest observation. Unknown historical dates stay dark. Clusters containing only recent offers are light with dark labels; mixed clusters stay dark, and selection remains amber. An open map checks expiry every minute.

Existing databases need the `OfferFirstSeen` migration via the explicit `--migrate` command. It leaves historical dates null. For this local demo, comparison with the pre-refresh backup proved 28 offers were added during the saved 8 October run; their dates were recovered from their original observations. The other 115 dates remain unknown. The guarded backfill, evidence and database backups are retained under ignored `.local/`. Browser verification confirmed both marker colors, the light cluster labels and legend without console errors. Tests cover the five-day boundary, expiry, time offsets, malformed dates and preservation of old/unknown dates across refreshes.

To recapture a small live validation sample explicitly:

```powershell
dotnet run --project src/Fluxus.Api --no-launch-profile -- --validate-sources
```

This command rewrites `tests/fixtures` and its validation report. Review the changes before retaining them. Saved detail fixtures omit owner/contact/payment information and free-form descriptions. The importer does not persist raw source HTML.

Verified locally on 8 October 2026: 80 backend and 27 frontend tests pass, along with the solution/production frontend builds and formatting. Docker and GDAL/PDAL mechanical checks are documented in the landscape validation record. Tests also cover an actual rental auction, sale versus rental classification, full/partial/unknown ownership, matching list and map filters, both price sort orders, missing prices, and URL/reset behavior. Batch checks cover reordered responses, missing parcels, duplicate/conflicting matches, transfer limits, and request-size limits.

## Structure and API

| Location | Responsibility |
| --- | --- |
| `src/Fluxus.Core` | Parsed auction/offer models, normalization rules, import orchestration and ports |
| `src/Fluxus.Infrastructure` | HTML adapter, Geoportal adapter, geometry conversion, PostGIS persistence |
| `src/Fluxus.Api` | HTTP endpoints, EF migrations, commands, scheduler |
| `src/Fluxus.Web` | Standalone, zoneless Angular UI using signals, resource, and native fetch |
| `tests/Fluxus.Tests` | Parser, geometry, database and API tests |
| `tests/fixtures` | Sanitized source examples and exact provider responses |

| Endpoint | Result |
| --- | --- |
| `GET /api/catalog/map` | Cluster-ready point GeoJSON, offer cards including unresolved offers, and freshness/sample metadata |
| `GET /api/auctions/{id}` | Auction totals, source link, and all land offers |
| `GET /api/auction-parcels/{id}/geometry` | Selected cadastral MultiPolygon or an explicit unresolved state |
| `GET /api/catalog/filters` | Available municipalities |
| `GET /api/catalog/status` | Latest import outcome |
| `GET /health/live`, `GET /health/ready` | Host and database connectivity |

Map filters: `status` (comma-separated), `municipalityCode`, `minPriceEur`, `maxPriceEur`, `minSoldAreaHa`, `maxSoldAreaHa`, `endsAfter`, and `endsBefore` (ISO timestamps), plus `offerType` (`all`, `sale`, `rent`, `unknown`) and `ownershipKind` (`all`, `full`, `partial`, `unknown`). An ownership filter selects sale offers only. `sort` supports `ending` (default), `price_asc`, and `price_desc`; missing prices come last with stable auction/item tie breaks. API results are bounded at 5,000 offers and reject invalid ranges and unsupported choices. The UI defaults to sales and exposes rentals or all offers separately. Selected offers, filters, and sorting are shareable through the URL.

Offer type comes from each source property section's sale/rental heading and structured fields, not a lease mentioned in a sale description. Rental shares and areas use the source's rental fields. Only a valid explicit fraction establishes full or partial ownership; missing/invalid shares remain unknown, and rental offers do not transfer ownership. Existing database rows start as unknown after the `OfferTerms` migration; re-import their source auctions to classify them.

Auction prices always describe the whole auction. A building-and-land bundle is explicitly labeled. Rentals retain the source's starting auction price; check its rental period and pricing conditions in the official listing. The offered area uses exact source units, checks the stated fraction, and excludes contradictory or unknown areas from numeric filtering. The API retains the existing `soldAreaHa` field name for both sale and rental offered areas. A marker is a PostGIS point on the cadastral polygon; the displayed outline is the whole cadastral parcel, not a spatial allocation of an ownership share.

## Remaining implementation

The landscape-potential implementation includes a separate GDAL worker, versioned per-parcel scores and durable jobs, API/UI explanations, score filters/sorting and a representative visibility image. It also includes raw LAS/LAZ preparation with PDAL, pilot-area order exports and bounded public GRPK context acquisition. The selected parcel's real context snapshot and 56 ordered LAZ tiles are available locally. Preparation supports a verified provider projection declaration when tile CRS metadata is absent, and explicit inspection previews for unverified heights. Height-reference/source validation and the 20-parcel calibration pilot remain pending; the live catalogue has no invented landscape scores. See the [runbook](docs/LANDSCAPE_RUNBOOK.md), [validation record](docs/LANDSCAPE_SOURCE_VALIDATION.md) and original [target plan](docs/LANDSCAPE_POTENTIAL_IMPLEMENTATION_PLAN.md).

See the [implementation plan](docs/PROTOTYPE_IMPLEMENTATION_PLAN.md) and [validation record](docs/SOURCE_VALIDATION.md). The next stage is full discovery validation against the wider property listing, end-time controls, missing-auction reconciliation, per-auction refresh cadence/retries, paged list responses, snapshot/version consistency, broader failure journeys, and final operational checks. The default limits and sample label deliberately keep this first slice's coverage explicit. Source guidance and a suitable basemap provider must be settled before a public scheduled launch; the local prototype uses attributed OpenStreetMap raster tiles.
