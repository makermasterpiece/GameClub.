using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionExtensionsTransfersAndStationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PackageDurationMinutesSnapshot",
                table: "gaming_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PackagePriceSnapshot",
                table: "gaming_sessions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "session_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GamingSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_operations_gaming_sessions_GamingSessionId",
                        column: x => x.GamingSessionId,
                        principalTable: "gaming_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "station_session_segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GamingSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_session_segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_station_session_segments_gaming_sessions_GamingSessionId",
                        column: x => x.GamingSessionId,
                        principalTable: "gaming_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_station_session_segments_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_operations_GamingSessionId",
                table: "session_operations",
                column: "GamingSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_station_session_segments_GamingSessionId",
                table: "station_session_segments",
                column: "GamingSessionId",
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_station_session_segments_GamingSessionId_StartedAtUtc",
                table: "station_session_segments",
                columns: new[] { "GamingSessionId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_station_session_segments_StationId",
                table: "station_session_segments",
                column: "StationId");

            // Stage 7 had no transfers. Preserve the original station's entire residency,
            // including pauses; do not reconstruct billable time from these segments.
            migrationBuilder.Sql("""
                INSERT INTO station_session_segments ("Id", "GamingSessionId", "StationId", "StartedAtUtc", "EndedAtUtc")
                SELECT gen_random_uuid(), "Id", "StationId", "StartedAtUtc",
                    CASE WHEN "Status" IN ('Active', 'Paused') THEN NULL
                         ELSE COALESCE("EndedAtUtc", "LastStateChangedAtUtc") END
                FROM gaming_sessions WHERE "StartedAtUtc" IS NOT NULL;

                UPDATE gaming_sessions AS session
                SET "PackagePriceSnapshot" = COALESCE(session."InitialPrice", package."Price"),
                    "PackageDurationMinutesSnapshot" = package."DurationMinutes"
                FROM tariff_packages AS package
                WHERE session."PackageId" = package."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_operations");

            migrationBuilder.DropTable(
                name: "station_session_segments");

            migrationBuilder.DropColumn(
                name: "PackageDurationMinutesSnapshot",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "PackagePriceSnapshot",
                table: "gaming_sessions");
        }
    }
}
