using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStationSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timestamp_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    source_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "station_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protected_secret = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_credentials", x => x.id);
                    table.ForeignKey(
                        name: "FK_station_credentials_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "station_enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_enrollment_tokens", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_station_id",
                table: "security_audit_events",
                column: "station_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_timestamp_utc",
                table: "security_audit_events",
                column: "timestamp_utc");

            migrationBuilder.CreateIndex(
                name: "IX_station_credentials_secret_hash",
                table: "station_credentials",
                column: "secret_hash");

            migrationBuilder.CreateIndex(
                name: "IX_station_credentials_station_id",
                table: "station_credentials",
                column: "station_id",
                unique: true,
                filter: "revoked_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_station_enrollment_tokens_expires_at_utc",
                table: "station_enrollment_tokens",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_station_enrollment_tokens_token_hash",
                table: "station_enrollment_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_audit_events");

            migrationBuilder.DropTable(
                name: "station_credentials");

            migrationBuilder.DropTable(
                name: "station_enrollment_tokens");
        }
    }
}
