using Fluxus.Core.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Fluxus.Infrastructure.Landscape;

public sealed record LandscapeClaim(Guid JobId, Guid LeaseToken, Guid ParcelId, string GeometryHash,
    Guid DatasetId, LandscapeBundle Bundle, MultiPolygon Geometry);

public sealed class LandscapeStore(CatalogueDb db, TimeProvider clock)
{
    public async Task<Guid> RegisterAsync(string manifest, string hash, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO landscape_datasets ("Id", "BundleHash", "Manifest", "RegisteredAt")
            VALUES ({id}, {hash}, CAST({manifest} AS jsonb), {clock.GetUtcNow()})
            ON CONFLICT ("BundleHash") DO NOTHING
            """, ct);
        return await db.LandscapeDatasets.Where(d => d.BundleHash == hash).Select(d => d.Id).SingleAsync(ct);
    }

    public async Task<int> EnqueueAsync(Guid datasetId, IReadOnlyCollection<Guid> parcelIds, CancellationToken ct)
    {
        if (parcelIds.Count is < 1 or > 100) throw new ArgumentException("Select between 1 and 100 canonical parcels.");
        if (!await db.LandscapeDatasets.AnyAsync(d => d.Id == datasetId, ct)) throw new ArgumentException("Unknown dataset.");
        var count = 0;
        foreach (var id in parcelIds.Distinct())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockProjectionAsync(ct);
            // The row lock also prevents publishing against a concurrently changed geometry.
            var parcel = await db.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (parcel?.Geometry is null || parcel.ResolutionStatus != "resolved_exact")
                throw new ArgumentException($"Parcel {id} has no accepted geometry.");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE parcels SET \"LandscapeGeometryHash\" = md5(ST_AsEWKB(ST_Normalize(\"Geometry\"))) WHERE \"Id\" = {id}", ct);
            await db.Entry(parcel).ReloadAsync(ct);
            var job = await db.LandscapeJobs.SingleOrDefaultAsync(j => j.ParcelId == id &&
                j.GeometryHash == parcel.LandscapeGeometryHash && j.DatasetId == datasetId &&
                j.AnalysisVersion == LandscapeScore.AnalysisVersion, ct);
            if (job is null)
            {
                job = new() { ParcelId = id, DatasetId = datasetId, GeometryHash = parcel.LandscapeGeometryHash!,
                    AnalysisVersion = LandscapeScore.AnalysisVersion, CreatedAt = clock.GetUtcNow(), AvailableAt = clock.GetUtcNow() };
                db.LandscapeJobs.Add(job);
                count++;
            }
            if (parcel.CurrentLandscapeJobId != job.Id)
            {
                var analysis = await db.LandscapeAnalyses.SingleOrDefaultAsync(a => a.JobId == job.Id, ct);
                parcel.CurrentLandscapeJobId = job.Id;
                parcel.LandscapeStatus = job.Status;
                parcel.LandscapeIsStale = parcel.CurrentLandscapeAnalysisId != null;
                parcel.LandscapeScore = null;
                if (analysis is not null)
                {
                    parcel.CurrentLandscapeAnalysisId = analysis.Id;
                    parcel.LandscapeStatus = analysis.Status;
                    parcel.LandscapeScore = analysis.Score;
                    parcel.LandscapeIsStale = false;
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
        return count;
    }

    public async Task<LandscapeClaim?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockProjectionAsync(ct);
        // Crash recovery is bounded by attempts. Claims are serialized only for the selected row.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE landscape_jobs SET "Status" = 'failed', "FailureCode" = 'attempts_exhausted',
              "FinishedAt" = {now}, "LeaseToken" = NULL, "LeaseUntil" = NULL
            WHERE "Status" = 'processing' AND "LeaseUntil" < {now} AND "Attempts" >= 3
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE parcels p SET "LandscapeStatus" = 'failed'
            FROM landscape_jobs j WHERE p."CurrentLandscapeJobId" = j."Id" AND j."Status" = 'failed'
              AND p."LandscapeStatus" = 'processing'
            """, ct);
        var jobs = await db.LandscapeJobs.FromSqlInterpolated($"""
            SELECT * FROM landscape_jobs WHERE "Attempts" < 3 AND
              (("Status" = 'queued' AND "AvailableAt" <= {now}) OR
               ("Status" = 'processing' AND "LeaseUntil" < {now}))
            ORDER BY "CreatedAt", "Id" FOR UPDATE SKIP LOCKED LIMIT 1
            """).ToListAsync(ct);
        var job = jobs.SingleOrDefault();
        if (job is null) { await tx.CommitAsync(ct); return null; }
        var parcel = await db.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE \"Id\" = {job.ParcelId} FOR UPDATE").SingleAsync(ct);
        if (parcel.Geometry is null || parcel.LandscapeGeometryHash != job.GeometryHash)
        {
            job.Status = "unavailable"; job.FailureCode = "parcel_geometry_changed"; job.FinishedAt = now;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear(); return null;
        }
        job.Status = "processing"; job.Attempts++; job.LeaseToken = Guid.NewGuid();
        job.LeaseUntil = now.AddMinutes(20); job.FailureCode = null;
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE parcels SET \"LandscapeStatus\" = 'processing' WHERE \"CurrentLandscapeJobId\" = {job.Id}", ct);
        var manifest = await db.LandscapeDatasets.Where(d => d.Id == job.DatasetId).Select(d => d.Manifest).SingleAsync(ct);
        var geometry = await db.Database.SqlQuery<byte[]>($"SELECT ST_AsBinary(ST_Transform(\"Geometry\", 3346)) AS \"Value\" FROM parcels WHERE \"Id\" = {job.ParcelId}").SingleAsync(ct);
        var result = new LandscapeClaim(job.Id, job.LeaseToken.Value, job.ParcelId, job.GeometryHash,
            job.DatasetId, LandscapeJson.Read<LandscapeBundle>(manifest), (MultiPolygon)new WKBReader().Read(geometry));
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    public async Task<bool> PublishAsync(LandscapeClaim claim, LandscapeAnalysisEntity analysis, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockProjectionAsync(ct);
        var job = await db.LandscapeJobs.FromSqlInterpolated($"SELECT * FROM landscape_jobs WHERE \"Id\" = {claim.JobId} FOR UPDATE").SingleAsync(ct);
        if (job.LeaseToken != claim.LeaseToken || job.Status != "processing" || job.LeaseUntil <= clock.GetUtcNow())
        { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return false; }
        var parcel = await db.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE \"Id\" = {claim.ParcelId} FOR UPDATE").SingleAsync(ct);
        analysis.ParcelId = claim.ParcelId; analysis.JobId = claim.JobId; analysis.DatasetId = claim.DatasetId;
        analysis.GeometryHash = claim.GeometryHash; analysis.AnalysisVersion = LandscapeScore.AnalysisVersion;
        analysis.ScoreProfile = LandscapeScore.Profile; analysis.ComputedAt = clock.GetUtcNow();
        db.LandscapeAnalyses.Add(analysis);
        job.Status = analysis.Status; job.FinishedAt = clock.GetUtcNow(); job.LeaseToken = null; job.LeaseUntil = null;
        if (parcel.LandscapeGeometryHash == claim.GeometryHash && parcel.CurrentLandscapeJobId == job.Id)
        {
            // An incomplete replacement must not destroy the last good published result.
            if (analysis.Status == "ready" || parcel.CurrentLandscapeAnalysisId is null)
            {
                parcel.CurrentLandscapeAnalysis = analysis;
                parcel.LandscapeIsStale = false;
                parcel.LandscapeScore = analysis.Score;
            }
            parcel.LandscapeStatus = analysis.Status;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }

    public async Task FailAsync(LandscapeClaim claim, string code, bool retryable, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockProjectionAsync(ct);
        var job = await db.LandscapeJobs.FromSqlInterpolated($"SELECT * FROM landscape_jobs WHERE \"Id\" = {claim.JobId} FOR UPDATE").SingleAsync(ct);
        if (job.LeaseToken == claim.LeaseToken && job.Status == "processing" && job.LeaseUntil > clock.GetUtcNow())
        {
            job.Status = retryable && job.Attempts < 3 ? "queued" : retryable ? "failed" : "unavailable";
            job.FailureCode = code; job.AvailableAt = clock.GetUtcNow().AddMinutes(Math.Pow(2, job.Attempts));
            job.FinishedAt = job.Status == "queued" ? null : clock.GetUtcNow();
            job.LeaseToken = null; job.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE parcels SET \"LandscapeStatus\" = {job.Status} WHERE \"CurrentLandscapeJobId\" = {job.Id}", ct);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    // The short projection transactions touch both sides of cyclic parcel/job FKs. A common
    // transaction lock avoids lock-order deadlocks; raster processing never holds this lock.
    private Task<int> LockProjectionAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(738410063)", ct);
}
