using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class PlayerAuthSessionConfiguration : IEntityTypeConfiguration<PlayerAuthSession>
{
    public void Configure(EntityTypeBuilder<PlayerAuthSession> builder)
    {
        builder.ToTable("player_auth_sessions");
        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id).HasColumnName("id");
        builder.Property(session => session.UserId).HasColumnName("user_id");
        builder.Property(session => session.StationId).HasColumnName("station_id");
        builder.Property(session => session.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.EndedAtUtc)
            .HasColumnName("ended_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne(session => session.User)
            .WithMany(user => user.PlayerAuthSessions)
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(session => session.Station)
            .WithMany(station => station.PlayerAuthSessions)
            .HasForeignKey(session => session.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(session => session.ExpiresAtUtc);
        builder.HasIndex(session => session.UserId)
            .IsUnique()
            .HasFilter("\"status\" = 'Active'");
        builder.HasIndex(session => session.StationId)
            .IsUnique()
            .HasFilter("\"status\" = 'Active'");
    }
}
