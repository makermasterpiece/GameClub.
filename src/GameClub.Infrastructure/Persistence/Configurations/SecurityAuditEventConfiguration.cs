using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class SecurityAuditEventConfiguration : IEntityTypeConfiguration<SecurityAuditEvent>
{
    public void Configure(EntityTypeBuilder<SecurityAuditEvent> builder)
    {
        builder.ToTable("security_audit_events");
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Id).HasColumnName("id");
        builder.Property(audit => audit.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        builder.Property(audit => audit.StationId).HasColumnName("station_id");
        builder.Property(audit => audit.TimestampUtc).HasColumnName("timestamp_utc").HasColumnType("timestamp with time zone");
        builder.Property(audit => audit.SourceIp).HasColumnName("source_ip").HasMaxLength(45);
        builder.Property(audit => audit.Details).HasColumnName("details").HasMaxLength(1000);
        builder.Property(audit => audit.UserId).HasColumnName("user_id");
        builder.HasIndex(audit => audit.TimestampUtc);
        builder.HasIndex(audit => audit.StationId);
        builder.HasIndex(audit => audit.UserId);
    }
}
