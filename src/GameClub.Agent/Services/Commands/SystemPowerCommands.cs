namespace GameClub.Agent.Services.Commands;

/// <summary>Only the two fixed local power operations; no command line, remote host or executable input.</summary>
public interface ISystemPowerService
{
    void ScheduleRestart();
    void ScheduleShutdown();
}

public sealed class RestartStationCommandHandler(ISystemPowerService power)
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        power.ScheduleRestart();
        return Task.CompletedTask;
    }
}

public sealed class ShutdownStationCommandHandler(ISystemPowerService power)
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        power.ScheduleShutdown();
        return Task.CompletedTask;
    }
}

/// <summary>Injectable boundary: tests replace every native call and never request an OS shutdown.</summary>
public interface IWindowsSystemPowerApi
{
    bool IsWindows { get; }
    IDisposable AcquireShutdownPrivilege();
    void InitiateLocalShutdown(bool restart, uint graceSeconds, bool forceAppsClosed);
}

public sealed class WindowsSystemPowerService(IWindowsSystemPowerApi native,
    ILogger<WindowsSystemPowerService> logger) : ISystemPowerService
{
    public const uint GracePeriodSeconds = 30;
    private static readonly object PrivilegeGate = new();

    public void ScheduleRestart() => Schedule(restart: true);
    public void ScheduleShutdown() => Schedule(restart: false);

    private void Schedule(bool restart)
    {
        if (!native.IsWindows)
            throw new PlatformNotSupportedException("Station power commands require Windows.");

        // The process-token privilege is enabled only around the synchronous request and restored before returning.
        lock (PrivilegeGate)
        {
            var privilege = native.AcquireShutdownPrivilege();
            var accepted = false;
            try
            {
                native.InitiateLocalShutdown(restart, GracePeriodSeconds, forceAppsClosed: false);
                accepted = true;
            }
            finally
            {
                try { privilege.Dispose(); }
                catch (Exception exception) when (accepted)
                {
                    // Do not claim a rejected request (or release the server lease) after Windows already accepted it.
                    logger.LogCritical("Power request was accepted but privilege restoration failed ({ErrorType})",
                        exception.GetType().Name);
                }
            }
        }
        // Success means Windows accepted a delayed request, not that the physical restart/shutdown completed.
    }
}
