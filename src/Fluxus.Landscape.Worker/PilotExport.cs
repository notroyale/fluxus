using System.Text.Json;
using Fluxus.Infrastructure.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Fluxus.Landscape.Worker;

public static class PilotExport
{
    public static async Task<string> ExportAsync(CatalogueDb db, Guid parcelId, string outputRoot, string destination, CancellationToken ct)
    {
        var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == parcelId && p.Geometry != null && p.ResolutionStatus == "resolved_exact", ct)
            ?? throw new ArgumentException("A canonical parcel with accepted geometry is required.");
        var wkb = await db.Database.SqlQuery<byte[]>($"SELECT ST_AsBinary(ST_Transform(\"Geometry\",3346)) AS \"Value\" FROM parcels WHERE \"Id\" = {parcelId}").SingleAsync(ct);
        var geometry = new WKBReader().Read(wkb); var bounds = geometry.EnvelopeInternal;
        if (bounds.Width > 3000 || bounds.Height > 3000) throw new LandscapeInputException("parcel_budget_exceeded");
        var raster = new[] { Math.Floor((bounds.MinX - 3100) / 10) * 10, Math.Floor((bounds.MinY - 3100) / 10) * 10,
            Math.Ceiling((bounds.MaxX + 3100) / 10) * 10, Math.Ceiling((bounds.MaxY + 3100) / 10) * 10 };
        var context = new[] { bounds.MinX - 1500, bounds.MinY - 1500, bounds.MaxX + 1500, bounds.MaxY + 1500 };
        LidarPreparation.ValidateBounds(raster);
        var order = await db.Database.SqlQuery<string>($"SELECT ST_AsGeoJSON(ST_Transform(ST_MakeEnvelope({raster[0]}, {raster[1]}, {raster[2]}, {raster[3]},3346),4326)) AS \"Value\"").SingleAsync(ct);
        var pilot = new PilotArea(parcelId, parcel.CadastralNumber, parcel.UniqueNumber, parcel.LandscapeGeometryHash!, raster, context,
            JsonDocument.Parse(new GeoJsonWriter().Write(geometry)).RootElement.Clone(), JsonDocument.Parse(order).RootElement.Clone());
        var output = LandscapeBundleReader.ConfinedPath(outputRoot, destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new LandscapeInputException("preparation_output_exists");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "pilot-area.json"), LandscapeJson.Write(pilot), ct);
        await File.WriteAllTextAsync(Path.Combine(output, "geoportal-order-area.geojson"), LandscapeJson.Write(new
        {
            type = "FeatureCollection", features = new[] { new { type = "Feature", geometry = pilot.OrderGeometryWgs84,
                properties = new { parcelId, note = "Request every intersecting classified LiDAR tile, including all edges of this buffered area." } } }
        }), ct);
        await File.WriteAllTextAsync(Path.Combine(output, "ORDER.md"), $"""
            # Geoportal LiDAR pilot order

            Canonical parcel: {parcelId}; cadastral number: {parcel.CadastralNumber}; unique number: {parcel.UniqueNumber}.
            EPSG:3346 request envelope: [{string.Join(", ", raster.Select(x => x.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)))}].
            This includes the actual parcel envelope plus 3.1 km, to cover all viewpoints and raster cell margins.

            Use `geoportal-order-area.geojson` (WGS84) to identify the order area. Geoportal requires a registered account and an area order.
            Select the latest classified LAZ release covering the complete envelope. Request all intersecting sheets, including seams.
            Save the provider's download links, release, actual survey years, reuse/derivative licence terms and vertical datum evidence.
            Keep original LAS/LAZ files and calculate SHA-256 for every tile; do not alter CRS labels or guess a height datum.
            Then create a preparation request following `docs/LANDSCAPE_RUNBOOK.md` and run `--prepare`.

            Official workflow: https://nzt.lrv.lt/lt/naujienos/kaip-gauti-valstybinius-erdvinius-duomenis-CkKP/
            Catalogue: https://www.geoportal.lt/geoportal/
            This export creates no order, accepts no terms, and changes no catalogue data.
            """, ct);
        return output;
    }
}
