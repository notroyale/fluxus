using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fluxus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AuctionChangeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OfferCount",
                table: "auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "PreviousInitialPriceEur",
                table: "auctions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousStatus",
                table: "auctions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceChangedAt",
                table: "auctions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "auctions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPresent",
                table: "auction_items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingSince",
                table: "auction_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "auction_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PreviousStatus = table.Column<string>(type: "text", nullable: true),
                    InitialPriceEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PreviousInitialPriceEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LastBidEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PreviousLastBidEur = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreviousEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OfferCount = table.Column<int>(type: "integer", nullable: false),
                    PriceChanged = table.Column<bool>(type: "boolean", nullable: false),
                    BidChanged = table.Column<bool>(type: "boolean", nullable: false),
                    StatusChanged = table.Column<bool>(type: "boolean", nullable: false),
                    DeadlineChanged = table.Column<bool>(type: "boolean", nullable: false),
                    OffersChanged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auction_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_auction_observations_auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auction_observations_AuctionId_ObservedAt",
                table: "auction_observations",
                columns: new[] { "AuctionId", "ObservedAt" });

            // Existing observations are a baseline, never invented additions or changes.
            migrationBuilder.Sql("""
                UPDATE auctions a SET "OfferCount" = (SELECT count(*) FROM auction_items i WHERE i."AuctionId" = a."Id");
                INSERT INTO auction_observations
                  ("Id", "AuctionId", "ObservedAt", "Mode", "Kind", "Status", "InitialPriceEur", "LastBidEur", "EndsAt", "OfferCount",
                   "PriceChanged", "BidChanged", "StatusChanged", "DeadlineChanged", "OffersChanged")
                SELECT gen_random_uuid(), "Id", "ObservedAt", "Mode", 'baseline', "Status", "InitialPriceEur", "LastBidEur", "EndsAt", "OfferCount",
                  false, false, false, false, false FROM auctions;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auction_observations");

            migrationBuilder.DropColumn(
                name: "OfferCount",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "PreviousInitialPriceEur",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "PreviousStatus",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "PriceChangedAt",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "IsPresent",
                table: "auction_items");

            migrationBuilder.DropColumn(
                name: "MissingSince",
                table: "auction_items");
        }
    }
}
