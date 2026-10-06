using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fluxus.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfferType",
                table: "auction_items",
                type: "text",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "OwnershipKind",
                table: "auction_items",
                type: "text",
                nullable: false,
                defaultValue: "unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfferType",
                table: "auction_items");

            migrationBuilder.DropColumn(
                name: "OwnershipKind",
                table: "auction_items");
        }
    }
}

