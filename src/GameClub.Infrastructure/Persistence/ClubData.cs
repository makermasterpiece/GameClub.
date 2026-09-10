using System.Data;
using GameClub.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameClub.Infrastructure.Persistence;

public sealed class ClubData(GameClubDbContext db) : IClubData
{
    public IQueryable<T> Query<T>() where T : class => db.Set<T>();
    public void Add<T>(T entity) where T : class => db.Set<T>().Add(entity);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);

    public async Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("All writes must be made inside their unit of work.");
        // SERIALIZABLE protects database reads, not EF's identity map from a previous operation.
        db.ChangeTracker.Clear();
        for (var attempt = 0; ; attempt++)
        {
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    var result = await operation(ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch (Exception ex) when (IsSerializationFailure(ex) && attempt < 4)
                {
                    db.ChangeTracker.Clear();
                    // PostgreSQL can report 40001 during COMMIT, completing the transaction.
                    // Explicit RollbackAsync would then mask the original retryable failure.
                    // Dispose rolls back only a transaction that is still active, before retry.
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    db.ChangeTracker.Clear();
                    throw new ClubException("CONCURRENT_CONFLICT");
                }
                catch
                {
                    db.ChangeTracker.Clear();
                    throw;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
        }
    }

    private static bool IsSerializationFailure(Exception exception)
    {
        // Npgsql's default EF strategy can wrap DbUpdateException in InvalidOperationException.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        return false;
    }
}
