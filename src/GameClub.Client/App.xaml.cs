using System.Windows;
using GameClub.Client.Services;
using GameClub.Client.Services.Playnite;

namespace GameClub.Client;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private ClientPipeConnection? _connection;
    private PlayniteSessionBridge? _playnite;
    private readonly System.Windows.Threading.DispatcherTimer _playniteTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;

        _playnite = new PlayniteSessionBridge(new PlayniteConfigurationReader(), new PlayniteProcessAdapter(), TimeProvider.System);
        _playnite.FullscreenChanged += active => window.Dispatcher.BeginInvoke(new Action(() => window.SetPlayniteForeground(active)));
        _playniteTimer.Tick += (_, _) => _playnite.Refresh();
        _playniteTimer.Start();
        _connection = new ClientPipeConnection { Playnite = _playnite };
        _connection.StateReceived += window.ApplyState;
        _connection.LoginResultReceived += window.ApplyLoginResult;
        _connection.LogoutResultReceived += window.ApplyLogoutResult;
        _connection.GamesMenuResultReceived += window.ApplyGamesResult;
        _connection.AgentUnavailable += () => window.ShowOffline();
        window.LoginRequested += request => _connection.SendLoginAsync(request, _shutdown.Token);
        window.LogoutRequested += request => _connection.SendLogoutAsync(request, _shutdown.Token);
        window.GamesRequested += request => _connection.SendOpenGamesAsync(request, _shutdown.Token);

        window.Show();
        _ = _connection.RunAsync(_shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _playniteTimer.Stop();
        _playnite?.UpdateState(null);
        _shutdown.Dispose();
        base.OnExit(e);
    }
}
