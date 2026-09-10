using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using GameClub.Server.Services.Players;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GameClub.Server.Tests.Players;

public sealed class UserProvisioningDatabaseErrorTests
{
    [Fact]
    public async Task UsernameUniqueViolation_IsMappedToDuplicateUsername()
    {
        var error = CreateDatabaseError(PostgresErrorCodes.UniqueViolation, "IX_users_normalized_username");
        await using var db = CreateContext(error);

        var result = await CreateService(db).CreateAsync(
            "nur", "StrongPassword123!", null, null, null, default);

        Assert.Equal(CreateUserError.DuplicateUsername, result.Error);
        Assert.Empty(db.ChangeTracker.Entries<User>());
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "PK_users")]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "IX_users_normalized_username")]
    [InlineData(PostgresErrorCodes.SerializationFailure, null)]
    public async Task UnrelatedDatabaseError_IsNotMisreportedAsDuplicateUsername(
        string sqlState,
        string? constraintName)
    {
        var error = CreateDatabaseError(sqlState, constraintName);
        await using var db = CreateContext(error);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => CreateService(db).CreateAsync(
            "nur", "StrongPassword123!", null, null, null, default));

        Assert.Same(error, thrown);
    }

    private static DbUpdateException CreateDatabaseError(string sqlState, string? constraintName) =>
        new("Synthetic database failure", new PostgresException(
            "Synthetic database failure", "ERROR", "ERROR", sqlState, constraintName: constraintName));

    private static GameClubDbContext CreateContext(DbUpdateException exception) =>
        new(new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new FailingSaveInterceptor(exception))
            .Options);

    private static UserProvisioningService CreateService(GameClubDbContext db) =>
        new(db, new AspNetPasswordHasherService(new PasswordHasher<User>()),
            TimeProvider.System, NullLogger<UserProvisioningService>.Instance);

    private sealed class FailingSaveInterceptor(DbUpdateException exception) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw exception;
    }
}
