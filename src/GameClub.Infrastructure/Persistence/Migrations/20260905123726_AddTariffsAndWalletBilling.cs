using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GameClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTariffsAndWalletBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "station_group_id",
                table: "stations",
                type: "uuid",
                nullable: true,
                defaultValue: new Guid("10000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                table: "gaming_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FundingLimitSeconds",
                table: "gaming_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HourlyPriceSnapshot",
                table: "gaming_sessions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PrepaidCharged",
                table: "gaming_sessions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseFingerprint",
                table: "gaming_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "station_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallets_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tariff_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AvailableFrom = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    AvailableUntil = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tariff_packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tariff_packages_station_groups_StationGroupId",
                        column: x => x.StationGroupId,
                        principalTable: "station_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tariffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    HourlyPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tariffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tariffs_station_groups_StationGroupId",
                        column: x => x.StationGroupId,
                        principalTable: "station_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    GamingSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_reservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallet_reservations_gaming_sessions_GamingSessionId",
                        column: x => x.GamingSessionId,
                        principalTable: "gaming_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wallet_reservations_wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReferenceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallet_transactions_wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "station_groups",
                columns: new[] { "Id", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "STANDARD", "STANDARD" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "VIP", "VIP" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "BOOTCAMP", "BOOTCAMP" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_stations_station_group_id",
                table: "stations",
                column: "station_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_gaming_sessions_PackageId",
                table: "gaming_sessions",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_gaming_sessions_TariffId",
                table: "gaming_sessions",
                column: "TariffId");

            migrationBuilder.CreateIndex(
                name: "IX_station_groups_NormalizedName",
                table: "station_groups",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tariff_packages_StationGroupId_IsActive",
                table: "tariff_packages",
                columns: new[] { "StationGroupId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_tariffs_StationGroupId_IsActive",
                table: "tariffs",
                columns: new[] { "StationGroupId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_wallet_reservations_GamingSessionId",
                table: "wallet_reservations",
                column: "GamingSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_reservations_WalletId",
                table: "wallet_reservations",
                column: "WalletId",
                filter: "\"ReleasedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_transactions_OperationId",
                table: "wallet_transactions",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_transactions_WalletId_CreatedAtUtc",
                table: "wallet_transactions",
                columns: new[] { "WalletId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_wallets_UserId",
                table: "wallets",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_gaming_sessions_tariff_packages_PackageId",
                table: "gaming_sessions",
                column: "PackageId",
                principalTable: "tariff_packages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_gaming_sessions_tariffs_TariffId",
                table: "gaming_sessions",
                column: "TariffId",
                principalTable: "tariffs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_stations_station_groups_station_group_id",
                table: "stations",
                column: "station_group_id",
                principalTable: "station_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_gaming_sessions_tariff_packages_PackageId",
                table: "gaming_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_gaming_sessions_tariffs_TariffId",
                table: "gaming_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_stations_station_groups_station_group_id",
                table: "stations");

            migrationBuilder.DropTable(
                name: "tariff_packages");

            migrationBuilder.DropTable(
                name: "tariffs");

            migrationBuilder.DropTable(
                name: "wallet_reservations");

            migrationBuilder.DropTable(
                name: "wallet_transactions");

            migrationBuilder.DropTable(
                name: "station_groups");

            migrationBuilder.DropTable(
                name: "wallets");

            migrationBuilder.DropIndex(
                name: "IX_stations_station_group_id",
                table: "stations");

            migrationBuilder.DropIndex(
                name: "IX_gaming_sessions_PackageId",
                table: "gaming_sessions");

            migrationBuilder.DropIndex(
                name: "IX_gaming_sessions_TariffId",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "station_group_id",
                table: "stations");

            migrationBuilder.DropColumn(
                name: "BillingMode",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "FundingLimitSeconds",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "HourlyPriceSnapshot",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "PrepaidCharged",
                table: "gaming_sessions");

            migrationBuilder.DropColumn(
                name: "PurchaseFingerprint",
                table: "gaming_sessions");
        }
    }
}
