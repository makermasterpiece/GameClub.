using GameClub.Agent.Configuration;
using GameClub.Agent.Services.Commands;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class SqliteProcessedCommandStoreTests
{
    [Fact]
    public void DuplicateCommand_RemainsBlockedAfterStoreRestart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"processed-{Guid.NewGuid():N}.db");
        try
        {
            var options = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
            var commandId = Guid.NewGuid();
            const string nonce = "persistent-nonce";
            var firstProcess = new SqliteProcessedCommandStore(options, TimeProvider.System);
            Assert.True(firstProcess.TryBegin(commandId, nonce));
            firstProcess.Set(commandId, ProcessedCommandStatus.Completed);

            var restartedProcess = new SqliteProcessedCommandStore(options, TimeProvider.System);

            Assert.False(restartedProcess.TryBegin(commandId, nonce));
            Assert.True(restartedProcess.TryGet(commandId, out var state));
            Assert.Equal(ProcessedCommandStatus.Completed, state.Status);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
