using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Fluxus.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "auctions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InitialPriceEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LastBidEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ContainsNonLandAssets = table.Column<bool>(type: "boolean", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auctions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Auctions = table.Column<int>(type: "integer", nullable: false),
                    Items = table.Column<int>(type: "integer", nullable: false),
                    Mapped = table.Column<int>(type: "integer", nullable: false),
                    FailedAuctions = table.Column<int>(type: "integer", nullable: false),
                    DiscoveryComplete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "parcels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UniqueNumber = table.Column<string>(type: "text", nullable: true),
                    CadastralNumber = table.Column<string>(type: "text", nullable: true),
                    Geometry = table.Column<MultiPolygon>(type: "geometry(MultiPolygon,4326)", nullable: true),
                    MarkerPoint = table.Column<Point>(type: "geometry(Point,4326)", nullable: true),
                    MunicipalityCode = table.Column<string>(type: "text", nullable: true),
                    MunicipalityName = table.Column<string>(type: "text", nullable: true),
                    ResolutionStatus = table.Column<string>(type: "text", nullable: false),
                    GeometryFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parcels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_issues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_issues_import_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "import_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "auction_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uuid", nullable: true),
                    UniqueNumber = table.Column<string>(type: "text", nullable: true),
                    CadastralNumber = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    TotalAreaHa = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    SoldAreaHa = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    SoldAreaBasis = table.Column<string>(type: "text", nullable: false),
                    Share = table.Column<string>(type: "text", nullable: true),
                    LandUse = table.Column<string>(type: "text", nullable: true),
                    ComponentPriceEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Issues = table.Column<string>(type: "jsonb", nullable: false),
                    ResolutionStatus = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_auction_items_auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_auction_items_parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalTable: "parcels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_auction_items_AuctionId_SourceKey",
                table: "auction_items",
                columns: new[] { "AuctionId", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auction_items_ParcelId",
                table: "auction_items",
                column: "ParcelId");

            migrationBuilder.CreateIndex(
                name: "IX_auctions_SourceId",
                table: "auctions",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auctions_Status_EndsAt",
                table: "auctions",
                columns: new[] { "Status", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_import_issues_RunId",
                table: "import_issues",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_CadastralNumber",
                table: "parcels",
                column: "CadastralNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parcels_Geometry",
                table: "parcels",
                column: "Geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_MunicipalityCode",
                table: "parcels",
                column: "MunicipalityCode");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_UniqueNumber",
                table: "parcels",
                column: "UniqueNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auction_items");

            migrationBuilder.DropTable(
                name: "import_issues");

            migrationBuilder.DropTable(
                name: "auctions");

            migrationBuilder.DropTable(
                name: "parcels");

            migrationBuilder.DropTable(
                name: "import_runs");
        }
    }
}
