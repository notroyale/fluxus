using Fluxus.Infrastructure.Persistence;

namespace Fluxus.Infrastructure.Landscape;

public sealed class LandscapeDatasetEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BundleHash { get; set; } = "";
    public string Manifest { get; set; } = "{}";
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class LandscapeJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParcelId { get; set; }
    public Guid DatasetId { get; set; }
    public LandscapeDatasetEntity Dataset { get; set; } = null!;
    public string GeometryHash { get; set; } = "";
    public string AnalysisVersion { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? FailureCode { get; set; }
}

public sealed class LandscapeAnalysisEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParcelId { get; set; }
    public Guid JobId { get; set; }
    public Guid DatasetId { get; set; }
    public LandscapeDatasetEntity Dataset { get; set; } = null!;
    public string GeometryHash { get; set; } = "";
    public string AnalysisVersion { get; set; } = "";
    public string ScoreProfile { get; set; } = "";
    public string Status { get; set; } = "partial";
    public string Quality { get; set; } = "unknown";
    public int? Score { get; set; }
    public int? Views { get; set; }
    public int? Terrain { get; set; }
    public int? Nature { get; set; }
    public int? Seclusion { get; set; }
    public string Metrics { get; set; } = "{}";
    public string Viewpoints { get; set; } = "[]";
    public string Limitations { get; set; } = "[]";
    public string? VisibilityArtifact { get; set; }
    public string? VisibilitySha256 { get; set; }
    public string? VisibilityCoordinates { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}
