using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameCatalogAndStationInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlayniteGameId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    Executable = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CoverUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "station_games",
                columns: table => new
                {
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Installed = table.Column<bool>(type: "boolean", nullable: false),
                    LastDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_games", x => new { x.StationId, x.GameId });
                    table.ForeignKey(
                        name: "FK_station_games_games_GameId",
                        column: x => x.GameId,
                        principalTable: "games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_station_games_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_games_PlayniteGameId",
                table: "games",
                column: "PlayniteGameId",
                unique: true,
                filter: "\"PlayniteGameId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_station_games_GameId",
                table: "station_games",
                column: "GameId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "station_games");

            migrationBuilder.DropTable(
                name: "games");
        }
    }
}
