using System.Threading.Channels;

namespace GameClub.Agent.Services.Players;

// A hub message only wakes the authenticated HTTP reconciliation loop.
public sealed class StationSessionSyncSignal
{
    private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _pending.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan pollingInterval, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCancellation.CancelAfter(pollingInterval);
        try
        {
            await _pending.Reader.ReadAsync(waitCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
