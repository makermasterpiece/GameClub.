using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class StationCredentialConfiguration : IEntityTypeConfiguration<StationCredential>
{
    public void Configure(EntityTypeBuilder<StationCredential> builder)
    {
        builder.ToTable("station_credentials");
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Id).HasColumnName("id");
        builder.Property(credential => credential.StationId).HasColumnName("station_id");
        builder.Property(credential => credential.SecretHash).HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
        builder.Property(credential => credential.ProtectedSecret).HasColumnName("protected_secret").IsRequired();
        builder.Property(credential => credential.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(credential => credential.RevokedAtUtc).HasColumnName("revoked_at_utc").HasColumnType("timestamp with time zone").IsConcurrencyToken();
        builder.Property(credential => credential.LastUsedAtUtc).HasColumnName("last_used_at_utc").HasColumnType("timestamp with time zone");
        builder.Ignore(credential => credential.IsActive);
        builder.HasIndex(credential => credential.StationId)
            .IsUnique()
            .HasFilter("revoked_at_utc IS NULL");
        builder.HasIndex(credential => credential.SecretHash);
        builder.HasOne(credential => credential.Station)
            .WithMany()
            .HasForeignKey(credential => credential.StationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
