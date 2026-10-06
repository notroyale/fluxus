using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fluxus.Api.Migrations
{
    /// <inheritdoc />
    public partial class LandscapeAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentLandscapeAnalysisId",
                table: "parcels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentLandscapeJobId",
                table: "parcels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LandscapeGeometryHash",
                table: "parcels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LandscapeIsStale",
                table: "parcels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LandscapeScore",
                table: "parcels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LandscapeStatus",
                table: "parcels",
                type: "text",
                nullable: false,
                defaultValue: "not_analyzed");

            migrationBuilder.CreateTable(
                name: "landscape_datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleHash = table.Column<string>(type: "text", nullable: false),
                    Manifest = table.Column<string>(type: "jsonb", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_landscape_datasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "landscape_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryHash = table.Column<string>(type: "text", nullable: false),
                    AnalysisVersion = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_landscape_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_landscape_jobs_landscape_datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "landscape_datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_landscape_jobs_parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalTable: "parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parcel_landscape_analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryHash = table.Column<string>(type: "text", nullable: false),
                    AnalysisVersion = table.Column<string>(type: "text", nullable: false),
                    ScoreProfile = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Quality = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    Views = table.Column<int>(type: "integer", nullable: true),
                    Terrain = table.Column<int>(type: "integer", nullable: true),
                    Nature = table.Column<int>(type: "integer", nullable: true),
                    Seclusion = table.Column<int>(type: "integer", nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: false),
                    Viewpoints = table.Column<string>(type: "jsonb", nullable: false),
                    Limitations = table.Column<string>(type: "jsonb", nullable: false),
                    VisibilityArtifact = table.Column<string>(type: "text", nullable: true),
                    VisibilitySha256 = table.Column<string>(type: "text", nullable: true),
                    VisibilityCoordinates = table.Column<string>(type: "jsonb", nullable: true),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parcel_landscape_analyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_parcel_landscape_analyses_landscape_datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "landscape_datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_parcel_landscape_analyses_landscape_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "landscape_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_parcel_landscape_analyses_parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalTable: "parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_parcels_CurrentLandscapeAnalysisId",
                table: "parcels",
                column: "CurrentLandscapeAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_CurrentLandscapeJobId",
                table: "parcels",
                column: "CurrentLandscapeJobId");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_LandscapeStatus_LandscapeScore",
                table: "parcels",
                columns: new[] { "LandscapeStatus", "LandscapeScore" });

            migrationBuilder.CreateIndex(
                name: "IX_landscape_datasets_BundleHash",
                table: "landscape_datasets",
                column: "BundleHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_landscape_jobs_DatasetId",
                table: "landscape_jobs",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_landscape_jobs_ParcelId_GeometryHash_DatasetId_AnalysisVers~",
                table: "landscape_jobs",
                columns: new[] { "ParcelId", "GeometryHash", "DatasetId", "AnalysisVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_landscape_jobs_Status_AvailableAt",
                table: "landscape_jobs",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_parcel_landscape_analyses_DatasetId",
                table: "parcel_landscape_analyses",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_parcel_landscape_analyses_JobId",
                table: "parcel_landscape_analyses",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parcel_landscape_analyses_ParcelId",
                table: "parcel_landscape_analyses",
                column: "ParcelId");

            migrationBuilder.AddForeignKey(
                name: "FK_parcels_landscape_jobs_CurrentLandscapeJobId",
                table: "parcels",
                column: "CurrentLandscapeJobId",
                principalTable: "landscape_jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_parcels_parcel_landscape_analyses_CurrentLandscapeAnalysisId",
                table: "parcels",
                column: "CurrentLandscapeAnalysisId",
                principalTable: "parcel_landscape_analyses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("UPDATE parcels SET \"LandscapeGeometryHash\" = md5(ST_AsEWKB(ST_Normalize(\"Geometry\"))) WHERE \"Geometry\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_parcels_landscape_jobs_CurrentLandscapeJobId",
                table: "parcels");

            migrationBuilder.DropForeignKey(
                name: "FK_parcels_parcel_landscape_analyses_CurrentLandscapeAnalysisId",
                table: "parcels");

            migrationBuilder.DropTable(
                name: "parcel_landscape_analyses");

            migrationBuilder.DropTable(
                name: "landscape_jobs");

            migrationBuilder.DropTable(
                name: "landscape_datasets");

            migrationBuilder.DropIndex(
                name: "IX_parcels_CurrentLandscapeAnalysisId",
                table: "parcels");

            migrationBuilder.DropIndex(
                name: "IX_parcels_CurrentLandscapeJobId",
                table: "parcels");

            migrationBuilder.DropIndex(
                name: "IX_parcels_LandscapeStatus_LandscapeScore",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "CurrentLandscapeAnalysisId",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "CurrentLandscapeJobId",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "LandscapeGeometryHash",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "LandscapeIsStale",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "LandscapeScore",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "LandscapeStatus",
                table: "parcels");
        }
    }
}
