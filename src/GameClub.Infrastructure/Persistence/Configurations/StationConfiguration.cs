using GameClub.Domain.Stations;
using GameClub.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class StationConfiguration : IEntityTypeConfiguration<Station>
{
    public void Configure(EntityTypeBuilder<Station> builder)
    {
        builder.ToTable("stations");

        builder.HasKey(station => station.Id);

        builder.Property(station => station.Id).HasColumnName("id");
        builder.Property(station => station.StationGroupId).HasColumnName("station_group_id")
            .HasDefaultValue(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        builder.HasOne<StationGroup>().WithMany().HasForeignKey(station => station.StationGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(station => station.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(station => station.MachineName).HasColumnName("machine_name").HasMaxLength(255).IsRequired();
        builder.Property(station => station.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(station => station.AgentVersion).HasColumnName("agent_version").HasMaxLength(50).IsRequired();
        builder.Property(station => station.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(station => station.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(station => station.LastSeenAtUtc).HasColumnName("last_seen_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(station => station.ClientConnected).HasColumnName("client_connected").HasDefaultValue(false);
        builder.Property(station => station.ClientState).HasColumnName("client_state").HasMaxLength(20).HasDefaultValue("Offline").IsRequired();
        builder.Property(station => station.ClientLastSeenAtUtc).HasColumnName("client_last_seen_at_utc").HasColumnType("timestamp with time zone");

        builder.HasIndex(station => station.MachineName).IsUnique();
    }
}
