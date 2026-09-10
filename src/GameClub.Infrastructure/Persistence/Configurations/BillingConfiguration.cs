using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

public sealed class StationGroupConfiguration : IEntityTypeConfiguration<StationGroup>
{
    public void Configure(EntityTypeBuilder<StationGroup> b)
    {
        b.ToTable("station_groups"); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.NormalizedName).HasMaxLength(100);
        b.HasIndex(x => x.NormalizedName).IsUnique();
        b.HasData(new StationGroup(Guid.Parse("10000000-0000-0000-0000-000000000001"), "STANDARD"),
            new StationGroup(Guid.Parse("10000000-0000-0000-0000-000000000002"), "VIP"),
            new StationGroup(Guid.Parse("10000000-0000-0000-0000-000000000003"), "BOOTCAMP"));
    }
}

public sealed class TariffConfiguration : IEntityTypeConfiguration<Tariff>
{
    public void Configure(EntityTypeBuilder<Tariff> b)
    {
        b.ToTable("tariffs"); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.HourlyPrice).HasPrecision(18, 2);
        b.HasOne<StationGroup>().WithMany().HasForeignKey(x => x.StationGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StationGroupId, x.IsActive });
    }
}

public sealed class TariffPackageConfiguration : IEntityTypeConfiguration<TariffPackage>
{
    public void Configure(EntityTypeBuilder<TariffPackage> b)
    {
        b.ToTable("tariff_packages"); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Price).HasPrecision(18, 2);
        b.HasOne<StationGroup>().WithMany().HasForeignKey(x => x.StationGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StationGroupId, x.IsActive });
    }
}

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> b)
    {
        b.ToTable("wallets"); b.HasKey(x => x.Id);
        b.Property(x => x.Balance).HasPrecision(18, 2);
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> b)
    {
        b.ToTable("wallet_transactions"); b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.BalanceAfter).HasPrecision(18, 2);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ReferenceType).HasMaxLength(50);
        b.HasIndex(x => x.OperationId).IsUnique();
        b.HasIndex(x => new { x.WalletId, x.CreatedAtUtc });
        b.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WalletReservationConfiguration : IEntityTypeConfiguration<WalletReservation>
{
    public void Configure(EntityTypeBuilder<WalletReservation> b)
    {
        b.ToTable("wallet_reservations"); b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.HasIndex(x => x.GamingSessionId).IsUnique();
        b.HasIndex(x => x.WalletId).HasFilter("\"ReleasedAtUtc\" IS NULL");
        b.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GamingSession>().WithMany().HasForeignKey(x => x.GamingSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}
