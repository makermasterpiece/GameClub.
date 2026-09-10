using GameClub.Domain.Gaming;
using GameClub.Domain.Billing;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

public sealed class GamingSessionConfiguration : IEntityTypeConfiguration<GamingSession>
{
    public void Configure(EntityTypeBuilder<GamingSession> b)
    {
        b.ToTable("gaming_sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.InitialPrice).HasPrecision(18, 2);
        b.Property(s => s.FinalPrice).HasPrecision(18, 2);
        b.Property(s => s.BillingMode).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.HourlyPriceSnapshot).HasPrecision(18, 2);
        b.Property(s => s.PrepaidCharged).HasPrecision(18, 2);
        b.Property(s => s.PackagePriceSnapshot).HasPrecision(18, 2);
        b.Property(s => s.PurchaseFingerprint).HasMaxLength(64);
        b.HasOne<Tariff>().WithMany().HasForeignKey(s => s.TariffId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TariffPackage>().WithMany().HasForeignKey(s => s.PackageId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(s => s.AccumulatedSeconds);
        b.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Station>().WithMany().HasForeignKey(s => s.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(s => s.UserId).IsUnique().HasFilter("\"Status\" IN ('Active','Paused')");
        b.HasIndex(s => s.StationId).IsUnique().HasFilter("\"Status\" IN ('Active','Paused')");
        b.HasIndex(s => new { s.Status, s.ExpectedEndAtUtc });
    }
}

public sealed class SessionOperationConfiguration : IEntityTypeConfiguration<SessionOperation>
{
    public void Configure(EntityTypeBuilder<SessionOperation> b)
    {
        b.ToTable("session_operations");
        b.HasKey(s => s.Id);
        b.Property(s => s.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.Fingerprint).HasMaxLength(64);
        b.HasOne<GamingSession>().WithMany().HasForeignKey(s => s.GamingSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StationSessionSegmentConfiguration : IEntityTypeConfiguration<StationSessionSegment>
{
    public void Configure(EntityTypeBuilder<StationSessionSegment> b)
    {
        b.ToTable("station_session_segments");
        b.HasKey(s => s.Id);
        b.HasOne<GamingSession>().WithMany().HasForeignKey(s => s.GamingSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Station>().WithMany().HasForeignKey(s => s.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(s => s.GamingSessionId).IsUnique().HasFilter("\"EndedAtUtc\" IS NULL");
        b.HasIndex(s => new { s.GamingSessionId, s.StartedAtUtc });
    }
}

public sealed class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
    public void Configure(EntityTypeBuilder<SessionEvent> b)
    {
        b.ToTable("session_events");
        b.HasKey(s => s.Id);
        b.Property(s => s.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(s => s.Details).HasMaxLength(2000);
        b.HasOne<GamingSession>().WithMany().HasForeignKey(s => s.GamingSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(s => new { s.GamingSessionId, s.CreatedAtUtc });
    }
}
