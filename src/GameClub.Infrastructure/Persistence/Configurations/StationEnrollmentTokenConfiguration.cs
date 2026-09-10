using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class StationEnrollmentTokenConfiguration : IEntityTypeConfiguration<StationEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<StationEnrollmentToken> builder)
    {
        builder.ToTable("station_enrollment_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).HasColumnName("id");
        builder.Property(token => token.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(token => token.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(token => token.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(token => token.UsedAtUtc).HasColumnName("used_at_utc").HasColumnType("timestamp with time zone").IsConcurrencyToken();
        builder.Property(token => token.Revoked).HasColumnName("revoked").IsConcurrencyToken();
        builder.Property(token => token.Description).HasColumnName("description").HasMaxLength(500);
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => token.ExpiresAtUtc);
    }
}
