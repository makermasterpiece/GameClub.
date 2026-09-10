using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersAndPlayerAuthSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "security_audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    normalized_username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "player_auth_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_auth_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_player_auth_sessions_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_player_auth_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_user_id",
                table: "security_audit_events",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_player_auth_sessions_expires_at_utc",
                table: "player_auth_sessions",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_player_auth_sessions_station_id",
                table: "player_auth_sessions",
                column: "station_id",
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_player_auth_sessions_user_id",
                table: "player_auth_sessions",
                column: "user_id",
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_users_normalized_username",
                table: "users",
                column: "normalized_username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_auth_sessions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropIndex(
                name: "IX_security_audit_events_user_id",
                table: "security_audit_events");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "security_audit_events");
        }
    }
}
