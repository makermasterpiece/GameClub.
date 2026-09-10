using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGamingSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gaming_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TariffId = table.Column<Guid>(type: "uuid", nullable: true),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpectedEndAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PurchasedMinutes = table.Column<int>(type: "integer", nullable: true),
                    AddedMinutes = table.Column<int>(type: "integer", nullable: false),
                    InitialPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    FinalPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AccumulatedTicks = table.Column<long>(type: "bigint", nullable: false),
                    LastResumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStateChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gaming_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_gaming_sessions_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gaming_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GamingSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_events_gaming_sessions_GamingSessionId",
                        column: x => x.GamingSessionId,
                        principalTable: "gaming_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gaming_sessions_StationId",
                table: "gaming_sessions",
                column: "StationId",
                unique: true,
                filter: "\"Status\" IN ('Active','Paused')");

            migrationBuilder.CreateIndex(
                name: "IX_gaming_sessions_Status_ExpectedEndAtUtc",
                table: "gaming_sessions",
                columns: new[] { "Status", "ExpectedEndAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_gaming_sessions_UserId",
                table: "gaming_sessions",
                column: "UserId",
                unique: true,
                filter: "\"Status\" IN ('Active','Paused')");

            migrationBuilder.CreateIndex(
                name: "IX_session_events_GamingSessionId_CreatedAtUtc",
                table: "session_events",
                columns: new[] { "GamingSessionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "gaming_sessions");
        }
    }
}
