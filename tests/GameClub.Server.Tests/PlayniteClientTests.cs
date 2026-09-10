using GameClub.Client.Services.Playnite;
using GameClub.Contracts.Client;
using GameClub.Contracts.Games;
using GameClub.Contracts.Gaming;
using Xunit;

namespace GameClub.Server.Tests;

public sealed class PlayniteClientTests
{
    private static readonly Guid Game = Guid.NewGuid(), Playnite = Guid.NewGuid();
    private sealed class Clock : TimeProvider { public DateTime Now = DateTime.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Config(bool close = true) : IPlayniteConfigurationReader
    { public PlayniteLocalConfiguration Read() => new(@"C:\Games\Playnite.FullscreenApp.exe", @"C:\Games\library.json", close, [new(Game, Playnite)]); }
    private sealed class Process : IPlayniteProcessAdapter
    { public int Starts, Closes; public void Execute(string executable, Guid? game) => Starts++; public void CloseManaged() => Closes++; }
    private static ClientStateMessage State(Clock clock)
    {
        var user = Guid.NewGuid();
        return new(ClientShellState.SessionActive, "PC", clock.Now, null, 1, user, "user", null, Guid.NewGuid(),
            new GamingSessionSnapshot(Guid.NewGuid(), user, Guid.NewGuid(), "Active", clock.Now, clock.Now.AddMinutes(10), 600, clock.Now, 0, Guid.NewGuid()));
    }
    private static PlayniteActionRequest Request(Clock clock, ClientStateMessage state) =>
        new(Guid.NewGuid(), PlayniteActionType.OpenFullscreen, state.GamingSession!.Id, clock.Now.AddSeconds(10));

    [Fact] public void Active_paid_session_opens_and_replay_does_not_execute_twice()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state); var request = Request(clock, state);
        Assert.True(bridge.Execute(request).Success); Assert.False(bridge.Execute(request).Success); Assert.Equal(1, process.Starts);
    }
    [Fact] public void Wrong_allowlist_pair_cannot_launch()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state);
        Assert.False(bridge.Execute(Request(clock, state) with { Action = PlayniteActionType.LaunchGame, GameId = Game, PlayniteGameId = Guid.NewGuid() }).Success);
        Assert.Equal(0, process.Starts);
    }
    [Fact] public void Expired_or_wrong_session_authorization_is_rejected()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state);
        Assert.False(bridge.Execute(Request(clock, state) with { ExpiresAtUtc = clock.Now }).Success);
        Assert.False(bridge.Execute(Request(clock, state) with { GamingSessionId = Guid.NewGuid() }).Success);
        Assert.Equal(0, process.Starts);
    }
    [Fact] public void Exact_allowed_pair_launches_but_menu_with_game_fields_is_rejected()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state);
        Assert.True(bridge.Execute(Request(clock, state) with { Action = PlayniteActionType.LaunchGame, GameId = Game, PlayniteGameId = Playnite }).Success);
        Assert.False(bridge.Execute(Request(clock, state) with { GameId = Game }).Success);
        Assert.Equal(1, process.Starts);
    }
    [Fact] public void Login_without_paid_session_cannot_open_games()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state with { GamingSession = state.GamingSession! with { TariffId = null, PackageId = null } });
        Assert.False(bridge.Execute(Request(clock, state)).Success); Assert.Equal(0, process.Starts);
    }
    [Fact] public void Different_active_session_revokes_previous_launcher()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state); bridge.Execute(Request(clock, state));
        bridge.UpdateState(State(clock)); Assert.Equal(1, process.Closes);
    }
    [Theory] [InlineData(true)] [InlineData(false)] public void Disconnect_restores_shell_and_respects_close_policy(bool close)
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(close), process, clock);
        var changes = new List<bool>(); bridge.FullscreenChanged += changes.Add;
        var state = State(clock); bridge.UpdateState(state); Assert.True(bridge.Execute(Request(clock, state)).Success);
        bridge.UpdateState(null); Assert.Equal(close ? 1 : 0, process.Closes); Assert.Equal(new[] { true, false }, changes);
    }
    [Fact] public void Paused_or_transferred_session_revokes_owned_launcher()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state); bridge.Execute(Request(clock, state));
        bridge.UpdateState(state with { GamingSession = state.GamingSession! with { Status = "Paused" } });
        Assert.Equal(1, process.Closes);
        Assert.False(bridge.Execute(Request(clock, state)).Success);
    }
    [Fact] public void Stale_state_revokes_even_without_pipe_disconnect()
    {
        var clock = new Clock(); var process = new Process(); var bridge = new PlayniteSessionBridge(new Config(), process, clock);
        var state = State(clock); bridge.UpdateState(state); bridge.Execute(Request(clock, state));
        clock.Now = clock.Now.AddSeconds(21); bridge.Refresh(); Assert.Equal(1, process.Closes);
    }
    [Theory] [InlineData(@"\\server\Playnite.FullscreenApp.exe")] [InlineData(@"C:\Games\cmd.exe")]
    [InlineData(@"C:\Games\..\Playnite.FullscreenApp.exe")] [InlineData(@"C:\Games\Playnite.FullscreenApp.exe:evil")]
    public void Unsafe_executable_path_is_rejected(string path) => Assert.Throws<ArgumentException>(() => PlayniteConfigurationReader.ValidatePath(path, true));
}
