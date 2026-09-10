using GameClub.Domain.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

internal sealed class AgentCommandConfiguration : IEntityTypeConfiguration<AgentCommand>
{
    public void Configure(EntityTypeBuilder<AgentCommand> builder)
    {
        builder.ToTable("agent_commands");

        builder.HasKey(command => command.Id);

        builder.Property(command => command.Id).HasColumnName("id");
        builder.Property(command => command.StationId).HasColumnName("station_id");
        builder.Property(command => command.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(command => command.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
        builder.Property(command => command.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(command => command.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.SentAtUtc).HasColumnName("sent_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.AcknowledgedAtUtc).HasColumnName("acknowledged_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.FailedAtUtc).HasColumnName("failed_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(command => command.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);

        builder.HasIndex(command => command.StationId);
        builder.HasIndex(command => command.Status);
        builder.HasIndex(command => command.CreatedAtUtc);

        builder
            .HasOne(command => command.Station)
            .WithMany()
            .HasForeignKey(command => command.StationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
