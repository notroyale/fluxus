using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fluxus.Infrastructure.Landscape;

public static class LandscapeArtifactStorage
{
    public const long DefaultQuotaBytes = 10L * 1024 * 1024 * 1024;
    public const long WorkingReserveBytes = 1024L * 1024 * 1024;

    public static long CheckBudget(string root, long quotaBytes = DefaultQuotaBytes, long reserveBytes = WorkingReserveBytes)
    {
        if (quotaBytes <= reserveBytes || reserveBytes < 0) throw new ArgumentException("Invalid artifact storage budget.");
        Directory.CreateDirectory(root);
        long used = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            used += new FileInfo(file).Length;
            if (used > quotaBytes - reserveBytes) throw new LandscapeInputException("artifact_quota_exceeded");
        }
        // On Unix the full mount path is needed to check its filesystem rather than '/'.
        var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(root))! : Path.GetFullPath(root));
        if (drive.AvailableFreeSpace < reserveBytes) throw new LandscapeInputException("artifact_disk_space_low");
        return used;
    }

    public static async Task<int> CleanupAbandonedAsync(CatalogueDb db, string root, DateTimeOffset now, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) return 0;
        var count = 0; var cutoff = now.AddDays(-2).UtcDateTime;
        foreach (var directory in Directory.EnumerateDirectories(fullRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id) || !IsOldOwnedDirectory(directory, cutoff)) continue;
            // Retain every published analysis, including historical results. Only abandoned
            // filesystem outputs without an analysis row are disposable after the grace period.
            if (await db.LandscapeAnalyses.AnyAsync(a => a.Id == id, ct)) continue;
            Directory.Delete(LandscapeBundleReader.ConfinedPath(fullRoot, directory), true); count++;
        }
        var staging = Path.Combine(fullRoot, ".staging");
        if (!Directory.Exists(staging) || (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0) return count;
        foreach (var jobDirectory in Directory.EnumerateDirectories(staging))
        {
            if (!Guid.TryParseExact(Path.GetFileName(jobDirectory), "D", out var jobId) ||
                (File.GetAttributes(jobDirectory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var leaseDirectory in Directory.EnumerateDirectories(jobDirectory))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(leaseDirectory), "D", out _) || !IsOldOwnedDirectory(leaseDirectory, cutoff)) continue;
                if (await db.LandscapeJobs.AnyAsync(j => j.Id == jobId && j.Status == "processing" && j.LeaseUntil > now, ct)) continue;
                Directory.Delete(LandscapeBundleReader.ConfinedPath(fullRoot, leaseDirectory), true); count++;
            }
        }
        return count;
    }

    private static bool IsOldOwnedDirectory(string path, DateTime cutoff)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Directory.GetLastWriteTimeUtc(path) >= cutoff) return false;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(entry) >= cutoff) return false;
        return true;
    }
}
