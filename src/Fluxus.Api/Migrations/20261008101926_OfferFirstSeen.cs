using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fluxus.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferFirstSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstSeenAt",
                table: "auction_items",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "auction_items");
        }
    }
}
