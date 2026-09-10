using GameClub.Agent.Services.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameClub.Server.Tests.Commands;

// Every native operation is a fake. These tests must never call WindowsSystemPowerApi or affect the test host.
public sealed class SystemPowerServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypedHandler_RequestsOnlyLocalGracefulPower_WithTemporaryPrivilege(bool restart)
    {
        var native = new FakeNative();
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        if (restart) await new RestartStationCommandHandler(power).ExecuteAsync(default);
        else await new ShutdownStationCommandHandler(power).ExecuteAsync(default);

        Assert.Equal(new[] { "acquire", "request", "restore" }, native.Calls);
        Assert.Equal(restart, native.Restart);
        Assert.Equal(30u, native.GraceSeconds);
        Assert.False(native.ForceAppsClosed);
    }

    [Fact]
    public void UnsupportedPlatform_DoesNotAcquirePrivilegeOrRequestPower()
    {
        var native = new FakeNative { IsWindows = false };
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        Assert.Throws<PlatformNotSupportedException>(power.ScheduleRestart);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void MissingShutdownPrivilege_DoesNotRequestPower()
    {
        var native = new FakeNative { PrivilegeDenied = true };
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        Assert.Throws<UnauthorizedAccessException>(power.ScheduleShutdown);
        Assert.Equal(new[] { "acquire" }, native.Calls);
    }

    [Fact]
    public void FailedNativeRequest_RestoresPrivilege()
    {
        var native = new FakeNative { RequestRejected = true };
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        Assert.Throws<InvalidOperationException>(power.ScheduleShutdown);
        Assert.Equal(new[] { "acquire", "request", "restore" }, native.Calls);
    }

    [Fact]
    public void AcceptedNativeRequest_IsNotMisreportedAsRejected_WhenPrivilegeCleanupFails()
    {
        var native = new FakeNative { RestoreRejected = true };
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        power.ScheduleShutdown();
        Assert.Equal(new[] { "acquire", "request", "restore" }, native.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledTypedHandler_DoesNotTouchNativeBoundary(bool restart)
    {
        var native = new FakeNative();
        var power = new WindowsSystemPowerService(native, NullLogger<WindowsSystemPowerService>.Instance);
        var token = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restart
            ? new RestartStationCommandHandler(power).ExecuteAsync(token)
            : new ShutdownStationCommandHandler(power).ExecuteAsync(token));
        Assert.Empty(native.Calls);
    }

    private sealed class FakeNative : IWindowsSystemPowerApi
    {
        public bool IsWindows { get; init; } = true;
        public bool PrivilegeDenied { get; init; }
        public bool RequestRejected { get; init; }
        public bool RestoreRejected { get; init; }
        public List<string> Calls { get; } = [];
        public bool Restart { get; private set; }
        public uint GraceSeconds { get; private set; }
        public bool ForceAppsClosed { get; private set; }

        public IDisposable AcquireShutdownPrivilege()
        {
            Calls.Add("acquire");
            if (PrivilegeDenied) throw new UnauthorizedAccessException("Test privilege unavailable.");
            return new FakeLease(this);
        }

        public void InitiateLocalShutdown(bool restart, uint graceSeconds, bool forceAppsClosed)
        {
            Assert.Equal(new[] { "acquire" }, Calls);
            Calls.Add("request");
            Restart = restart;
            GraceSeconds = graceSeconds;
            ForceAppsClosed = forceAppsClosed;
            if (RequestRejected) throw new InvalidOperationException("Test OS request rejected.");
        }

        private sealed class FakeLease(FakeNative owner) : IDisposable
        {
            public void Dispose()
            {
                owner.Calls.Add("restore");
                if (owner.RestoreRejected) throw new InvalidOperationException("Test privilege restoration failed.");
            }
        }
    }
}
