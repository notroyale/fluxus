# Initial source validation

Captured 6 October 2026 at 17:27:40 UTC / 20:27:40 Europe/Vilnius. Machine-readable evidence is in [`tests/fixtures/validation.json`](../tests/fixtures/validation.json).

The filtered active-land listing returned 20 auctions on page zero, a next page of 1, and pagination links reaching page 30. Ten selected detail pages yielded eleven land offers, all with exact cadastral matches. They resolve to ten distinct parcels: auction 337250 contains two source sections offering separate halves of the same cadastral parcel. This sample demonstrates matching, not catalogue-wide coverage.

| Auction number | Land offers | Distinct parcels | Important case |
| --- | ---: | ---: | --- |
| 337123 | 1 | 1 | 3/4 share; exact sold area 199.5 a = 1.995 ha, despite rounded 2 ha text |
| 337139 | 1 | 1 | Full 20.22 ha parcel |
| 337147 | 1 | 1 | Buildings and land; 6/48 land share |
| 337138 | 1 | 1 | 282/1127 share |
| 337120 | 1 | 1 | 1/2 share |
| 337153 | 1 | 1 | Full 0.4563 ha parcel |
| 337154 | 1 | 1 | Full 0.14 ha parcel |
| 337170 | 1 | 1 | Observed bid 15,200 EUR; auction price 15,120 EUR |
| 337179 | 1 | 1 | Urban mixed bundle; 11/1597 share, sold area 0.0022 ha |
| 337250 | 2 | 1 | Separate 1/2 offers, separate item IDs, shared canonical geometry |

The Geoportal layer is `geoportal_teikeju/RC_sklypai_opendata/MapServer/0`. Queries use exact `unikalus_nr` and/or normalized `kadastro_nr`, request `outSR=4326`, and accept only a single feature whose returned identifiers agree with the source. The service returns ArcGIS JSON rings, not GeoJSON. A 20-second trial had timed out during planning; a 60-second request budget succeeded for this sample. Geometry is validated as a Lithuanian EPSG:4326 polygon and stored as MultiPolygon. PostGIS generates points on surface. Geometry conversion tests separately exercise holes, multiple polygon parts, and rejection of projected coordinates.

Sanitized detail fixtures preserve stable estate section IDs and allowlisted auction/land fields. They omit owners, payment details, galleries and free-form descriptions. Consequently the captured demo does not retain land-use descriptions; the live parser can extract a purpose line without storing the rest of the description.

The wider real-estate listing was fetched only for its first page. Different ordering and pagination mean a first-page ID comparison cannot establish that the land filter includes every mixed bundle. Full category comparison, moving-pagination detection, and at least one auction with genuinely distinct cadastral parcels remain outstanding. Two sections for one parcel do not satisfy that last case.

Browser verification exercises the published Angular build, country map, selected boundary, urban mixed bundle, two same-parcel offers, filter empty states, and mobile list/map switching. These are rendering and behavior checks. They are not an independent survey or cadastral-boundary certification.

The previous successful data survives failed detail imports. Unresolved offers remain in the list. The initial importer does not remove records missing from a later listing or infer an auction's terminal status. Catalogue completeness remains explicitly false for the default bounded ten-auction import.
