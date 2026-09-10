using GameClub.Domain.Commands;
using GameClub.Domain.Stations;
using GameClub.Domain.Security;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Infrastructure.Persistence;

public sealed class GameClubDbContext(DbContextOptions<GameClubDbContext> options) : DbContext(options)
{
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<GameClub.Domain.Games.Game> Games => Set<GameClub.Domain.Games.Game>();
    public DbSet<GameClub.Domain.Games.StationGame> StationGames => Set<GameClub.Domain.Games.StationGame>();

    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();

    public DbSet<StationEnrollmentToken> StationEnrollmentTokens => Set<StationEnrollmentToken>();

    public DbSet<StationCredential> StationCredentials => Set<StationCredential>();

    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();

    public DbSet<User> Users => Set<User>();

    public DbSet<PlayerAuthSession> PlayerAuthSessions => Set<PlayerAuthSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GameClubDbContext).Assembly);
    }
}
