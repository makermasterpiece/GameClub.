using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id");
        builder.Property(user => user.Username)
            .HasColumnName("username")
            .HasMaxLength(User.MaximumUsernameLength)
            .IsRequired();
        builder.Property(user => user.NormalizedUsername)
            .HasColumnName("normalized_username")
            .HasMaxLength(User.MaximumUsernameLength)
            .IsRequired();
        builder.Property(user => user.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(512)
            .IsRequired();
        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(User.MaximumDisplayNameLength);
        builder.Property(user => user.Email).HasColumnName("email").HasMaxLength(User.MaximumEmailLength);
        builder.Property(user => user.Phone).HasColumnName("phone").HasMaxLength(User.MaximumPhoneLength);
        builder.Property(user => user.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(user => user.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.LastLoginAtUtc)
            .HasColumnName("last_login_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(user => user.NormalizedUsername).IsUnique();
    }
}
