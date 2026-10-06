using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Fluxus.Infrastructure.Landscape;

namespace Fluxus.Infrastructure.Persistence;

public sealed class AuctionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceId { get; set; } = "";
    public string Number { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public decimal? InitialPriceEur { get; set; }
    public decimal? LastBidEur { get; set; }
    public bool ContainsNonLandAssets { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string Mode { get; set; } = "Fixture";
    public List<AuctionItemEntity> Items { get; set; } = [];
}

public sealed class AuctionItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AuctionId { get; set; }
    public AuctionEntity Auction { get; set; } = null!;
    public string SourceKey { get; set; } = "";
    public Guid? ParcelId { get; set; }
    public ParcelEntity? Parcel { get; set; }
    public string? UniqueNumber { get; set; }
    public string? CadastralNumber { get; set; }
    public string? Address { get; set; }
    public decimal? TotalAreaHa { get; set; }
    public decimal? SoldAreaHa { get; set; }
    public string SoldAreaBasis { get; set; } = "unknown";
    public string? Share { get; set; }
    public string OfferType { get; set; } = "unknown";
    public string OwnershipKind { get; set; } = "unknown";
    public string? LandUse { get; set; }
    public decimal? ComponentPriceEur { get; set; }
    public string Issues { get; set; } = "[]";
    public string ResolutionStatus { get; set; } = "missing_identifier";
}

public sealed class ParcelEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UniqueNumber { get; set; }
    public string? CadastralNumber { get; set; }
    public MultiPolygon? Geometry { get; set; }
    public Point? MarkerPoint { get; set; }
    public string? MunicipalityCode { get; set; }
    public string? MunicipalityName { get; set; }
    public string ResolutionStatus { get; set; } = "not_found";
    public DateTimeOffset? GeometryFetchedAt { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string? LandscapeGeometryHash { get; set; }
    public string LandscapeStatus { get; set; } = "not_analyzed";
    public int? LandscapeScore { get; set; }
    public bool LandscapeIsStale { get; set; }
    public Guid? CurrentLandscapeAnalysisId { get; set; }
    public LandscapeAnalysisEntity? CurrentLandscapeAnalysis { get; set; }
    public Guid? CurrentLandscapeJobId { get; set; }
    public LandscapeJobEntity? CurrentLandscapeJob { get; set; }
}

public sealed class ImportRunEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Mode { get; set; } = "Fixture";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Outcome { get; set; } = "running";
    public int Auctions { get; set; }
    public int Items { get; set; }
    public int Mapped { get; set; }
    public int FailedAuctions { get; set; }
    public bool DiscoveryComplete { get; set; }
    public List<ImportIssueEntity> Issues { get; set; } = [];
}

public sealed class ImportIssueEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string SourceId { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class CatalogueDb(DbContextOptions<CatalogueDb> options) : DbContext(options)
{
    public DbSet<AuctionEntity> Auctions => Set<AuctionEntity>();
    public DbSet<AuctionItemEntity> Items => Set<AuctionItemEntity>();
    public DbSet<ParcelEntity> Parcels => Set<ParcelEntity>();
    public DbSet<ImportRunEntity> ImportRuns => Set<ImportRunEntity>();
    public DbSet<ImportIssueEntity> ImportIssues => Set<ImportIssueEntity>();
    public DbSet<LandscapeDatasetEntity> LandscapeDatasets => Set<LandscapeDatasetEntity>();
    public DbSet<LandscapeJobEntity> LandscapeJobs => Set<LandscapeJobEntity>();
    public DbSet<LandscapeAnalysisEntity> LandscapeAnalyses => Set<LandscapeAnalysisEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("postgis");
        model.Entity<AuctionEntity>(entity =>
        {
            entity.ToTable("auctions");
            entity.HasIndex(a => a.SourceId).IsUnique();
            entity.HasIndex(a => new { a.Status, a.EndsAt });
            entity.Property(a => a.InitialPriceEur).HasPrecision(18, 2);
            entity.Property(a => a.LastBidEur).HasPrecision(18, 2);
            entity.HasMany(a => a.Items).WithOne(i => i.Auction).HasForeignKey(i => i.AuctionId);
        });
        model.Entity<AuctionItemEntity>(entity =>
        {
            entity.ToTable("auction_items");
            entity.HasIndex(i => new { i.AuctionId, i.SourceKey }).IsUnique();
            entity.Property(i => i.TotalAreaHa).HasPrecision(18, 8);
            entity.Property(i => i.SoldAreaHa).HasPrecision(18, 8);
            entity.Property(i => i.ComponentPriceEur).HasPrecision(18, 2);
            entity.Property(i => i.Issues).HasColumnType("jsonb");
            entity.HasOne(i => i.Parcel).WithMany().HasForeignKey(i => i.ParcelId);
        });
        model.Entity<ParcelEntity>(entity =>
        {
            entity.ToTable("parcels");
            entity.HasIndex(p => p.UniqueNumber).IsUnique();
            entity.HasIndex(p => p.CadastralNumber).IsUnique();
            entity.HasIndex(p => p.MunicipalityCode);
            entity.Property(p => p.LandscapeStatus).HasDefaultValue("not_analyzed");
            entity.Property(p => p.Geometry).HasColumnType("geometry(MultiPolygon,4326)");
            entity.Property(p => p.MarkerPoint).HasColumnType("geometry(Point,4326)");
            entity.HasIndex(p => p.Geometry).HasMethod("gist");
        });
        model.Entity<ImportRunEntity>(entity =>
        {
            entity.ToTable("import_runs");
            entity.HasMany(r => r.Issues).WithOne().HasForeignKey(i => i.RunId);
        });
        model.Entity<ImportIssueEntity>().ToTable("import_issues");
        model.Entity<LandscapeDatasetEntity>(entity =>
        {
            entity.ToTable("landscape_datasets");
            entity.HasIndex(d => d.BundleHash).IsUnique();
            entity.Property(d => d.Manifest).HasColumnType("jsonb");
        });
        model.Entity<LandscapeJobEntity>(entity =>
        {
            entity.ToTable("landscape_jobs");
            entity.HasIndex(j => new { j.ParcelId, j.GeometryHash, j.DatasetId, j.AnalysisVersion }).IsUnique();
            entity.HasIndex(j => new { j.Status, j.AvailableAt });
            entity.HasOne(j => j.Dataset).WithMany().HasForeignKey(j => j.DatasetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ParcelEntity>().WithMany().HasForeignKey(j => j.ParcelId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<LandscapeAnalysisEntity>(entity =>
        {
            entity.ToTable("parcel_landscape_analyses");
            entity.HasIndex(a => a.JobId).IsUnique();
            entity.HasOne<LandscapeJobEntity>().WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(a => a.Dataset).WithMany().HasForeignKey(a => a.DatasetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ParcelEntity>().WithMany().HasForeignKey(a => a.ParcelId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(a => a.Metrics).HasColumnType("jsonb");
            entity.Property(a => a.Viewpoints).HasColumnType("jsonb");
            entity.Property(a => a.Limitations).HasColumnType("jsonb");
            entity.Property(a => a.VisibilityCoordinates).HasColumnType("jsonb");
        });
        model.Entity<ParcelEntity>().HasOne(p => p.CurrentLandscapeAnalysis).WithMany()
            .HasForeignKey(p => p.CurrentLandscapeAnalysisId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ParcelEntity>().HasOne(p => p.CurrentLandscapeJob).WithMany()
            .HasForeignKey(p => p.CurrentLandscapeJobId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ParcelEntity>().HasIndex(p => new { p.LandscapeStatus, p.LandscapeScore });
    }
}
