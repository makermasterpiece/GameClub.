using GameClub.Domain.Games;
using GameClub.Domain.Stations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

public sealed class GameConfiguration : IEntityTypeConfiguration<Game>
{
    public void Configure(EntityTypeBuilder<Game> b)
    {
        b.ToTable("games");
        b.HasKey(g => g.Id);
        b.Property(g => g.Name).HasMaxLength(200);
        b.Property(g => g.PlayniteGameId).HasMaxLength(36);
        b.Property(g => g.Executable).HasMaxLength(1024);
        b.Property(g => g.CoverUrl).HasMaxLength(2048);
        b.HasIndex(g => g.PlayniteGameId).IsUnique().HasFilter("\"PlayniteGameId\" IS NOT NULL");
    }
}

public sealed class StationGameConfiguration : IEntityTypeConfiguration<StationGame>
{
    public void Configure(EntityTypeBuilder<StationGame> b)
    {
        b.ToTable("station_games");
        b.HasKey(g => new { g.StationId, g.GameId });
        b.HasOne<Station>().WithMany().HasForeignKey(g => g.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Game>().WithMany().HasForeignKey(g => g.GameId).OnDelete(DeleteBehavior.Restrict);
    }
}
