namespace GameClub.Server.Security.Employees;

public sealed class EmployeeConnectionRevalidator(EmployeeConnectionRegistry connections, IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<EmployeeConnectionRevalidator> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RevalidateOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RevalidateOnceAsync(CancellationToken ct)
    {
        foreach (var connection in connections.Snapshot())
        {
            ct.ThrowIfCancellationRequested();
            var valid = false;
            try
            {
                if (connection.Identity.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var validator = scope.ServiceProvider.GetRequiredService<EmployeePrincipalValidator>();
                    using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    checkTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    valid = await validator.ValidateAsync(connection.Identity.CreatePrincipal(), checkTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Outbound data must not continue flowing when current authorization cannot be established.
                logger.LogWarning("Employee connection authorization check failed ({ErrorType}); closing connection",
                    exception.GetType().Name);
                foreach (var pending in connections.Snapshot()) connections.TryAbort(pending);
                return;
            }

            if (!valid && connections.TryAbort(connection))
                logger.LogInformation("Closed revoked or expired admin connection for employee {EmployeeId}",
                    connection.Identity.EmployeeId);
        }
    }
}
