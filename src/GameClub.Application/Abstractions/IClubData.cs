namespace GameClub.Application.Abstractions;

/// <summary>Scoped unit of work. The provider owns transactions and bounded serialization retries.</summary>
public interface IClubData
{
    IQueryable<T> Query<T>() where T : class;
    void Add<T>(T entity) where T : class;
    Task SaveAsync(CancellationToken cancellationToken);
    Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

public sealed class ClubException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IClubEvents
{
    Task StationChangedAsync(Guid stationId, CancellationToken cancellationToken);
}
