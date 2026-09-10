using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GameClub.Contracts.Client;
using GameClub.Contracts.Gaming;
using GameClub.Contracts.Games;
using GameClub.Client.Services.Playnite;

namespace GameClub.Client;

public partial class MainWindow : Window
{
    private string _stationName = "Игровая станция";
    private Guid? _pendingLoginRequestId;
    private Guid? _pendingLogoutRequestId;
    private readonly Stopwatch _gamingClock = new();
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GamingSessionSnapshot? _gamingSnapshot;
    private ClientStateMessage? _state;
    private Guid? _pendingGames;
    private DateTime _gamesRequestedAt;

    public MainWindow()
    {
        InitializeComponent();
        _displayTimer.Tick += (_, _) => UpdateGamingTime();
        _displayTimer.Start();
        Closed += (_, _) => _displayTimer.Stop();
        ShowOffline();
    }

    public event Func<PlayerLoginRequest, Task>? LoginRequested;
    public event Func<PlayerLogoutRequest, Task>? LogoutRequested;
    public event Func<GamesMenuRequest, Task>? GamesRequested;

    public void SetPlayniteForeground(bool active)
    {
        Topmost = !active;
        if (!active) { Show(); WindowState = WindowState.Maximized; Activate(); }
        else WindowState = WindowState.Minimized;
    }

    public void ApplyGamesResult(GamesMenuResult result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ApplyGamesResult(result)); return; }
        if (_pendingGames != result.RequestId) return;
        _pendingGames = null;
        GamesStatusText.Text = result.Success ? string.Empty : "Игры недоступны. Проверьте активную сессию и настройку Playnite у оператора.";
        GamesButton.IsEnabled = true;
    }

    private async void GamesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PlayniteSessionBridge.CanPlay(_state, DateTime.UtcNow) || GamesRequested is null) return;
        var request = new GamesMenuRequest(Guid.NewGuid());
        _pendingGames = request.RequestId;
        _gamesRequestedAt = DateTime.UtcNow;
        GamesButton.IsEnabled = false;
        GamesStatusText.Text = "Запуск Playnite…";
        try { await GamesRequested(request); }
        catch { ApplyGamesResult(new(request.RequestId, false, "AGENT_UNAVAILABLE")); }
    }

    public void ApplyState(ClientStateMessage state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyState(state));
            return;
        }

        _state = state;
        _stationName = string.IsNullOrWhiteSpace(state.StationName)
            ? _stationName
            : state.StationName;
        StationNameText.Text = _stationName;
        LoginPanel.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Collapsed;
        ApplyGamingSnapshot(state.State == ClientShellState.SessionActive ? state.GamingSession : null);
        if (state.State != ClientShellState.Available)
        {
            _pendingLoginRequestId = null;
        }

        if (state.State != ClientShellState.SessionActive)
        {
            _pendingLogoutRequestId = null;
        }

        switch (state.State)
        {
            case ClientShellState.Locked:
                StateTitleText.Text = "Станция заблокирована";
                StateMessageText.Text = state.Message ?? "Обратитесь к оператору";
                break;
            case ClientShellState.Available:
                StateTitleText.Text = "Станция свободна";
                StateMessageText.Text = string.Empty;
                LoginPanel.Visibility = Visibility.Visible;
                SetLoginBusy(_pendingLoginRequestId.HasValue);
                break;
            case ClientShellState.SessionActive:
                StateTitleText.Text = string.Empty;
                StateMessageText.Text = string.Empty;
                SessionPanel.Visibility = Visibility.Visible;
                var playerName = string.IsNullOrWhiteSpace(state.DisplayName)
                    ? state.Username ?? "Игрок"
                    : state.DisplayName;
                WelcomeText.Text = $"Добро пожаловать,\n{playerName}";
                SetLogoutBusy(_pendingLogoutRequestId.HasValue);
                _pendingLoginRequestId = null;
                UsernameInput.Clear();
                PasswordInput.Clear();
                break;
            case ClientShellState.Maintenance:
                StateTitleText.Text = "Техническое обслуживание";
                StateMessageText.Text = state.Message ?? "Станция временно недоступна";
                break;
            case ClientShellState.Offline:
                ShowOffline(state.Message);
                break;
            default:
                ShowOffline();
                break;
        }
    }

    public void ApplyLoginResult(PlayerLoginResultMessage result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyLoginResult(result));
            return;
        }

        if (_pendingLoginRequestId != result.RequestId)
        {
            return;
        }

        if (result.Success)
        {
            LoginStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            LoginStatusText.Text = "Вход выполнен";
            return;
        }

        SetLoginBusy(false);
        _pendingLoginRequestId = null;
        LoginStatusText.Text = result.ErrorCode switch
        {
            "ACCOUNT_DISABLED" => "Учётная запись отключена",
            "ACCOUNT_BANNED" => "Учётная запись заблокирована",
            "USER_ALREADY_LOGGED_IN" => "Пользователь уже вошёл на другой станции",
            "STATION_ALREADY_LOGGED_IN" => "На станции уже выполнен вход",
            "RATE_LIMITED" => "Слишком много попыток. Повторите через минуту",
            "SERVER_UNAVAILABLE" or "SERVER_ERROR" or "AGENT_AUTHENTICATION_FAILED" =>
                "Сервис входа временно недоступен",
            "STATION_UNAVAILABLE" => "Станция сейчас недоступна для входа",
            _ => "Неверный логин или пароль"
        };
        PasswordInput.Focus();
    }

    public void ApplyLogoutResult(PlayerLogoutResultMessage result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyLogoutResult(result));
            return;
        }

        if (_pendingLogoutRequestId != result.RequestId)
        {
            return;
        }

        if (!result.Success)
        {
            _pendingLogoutRequestId = null;
            SetLogoutBusy(false);
            LogoutStatusText.Text = "Не удалось завершить сессию. Повторите попытку";
        }
    }

    public void ShowOffline(string? message = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowOffline(message));
            return;
        }

        StationNameText.Text = _stationName;
        StateTitleText.Text = "Нет связи с системой";
        _state = null;
        _pendingGames = null;
        GamesButton.Visibility = Visibility.Collapsed;
        StateMessageText.Text = message ?? "Повторное подключение выполняется автоматически";
        LoginPanel.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Collapsed;
        _pendingLoginRequestId = null;
        _pendingLogoutRequestId = null;
        PasswordInput.Clear();
        ApplyGamingSnapshot(null);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        var password = PasswordInput.Password;
        if (username.Length is < 3 or > 32 || password.Length is 0 or > 128)
        {
            LoginStatusText.Text = "Введите логин и пароль";
            PasswordInput.Clear();
            return;
        }

        var request = new PlayerLoginRequest(Guid.NewGuid(), username, password);
        PasswordInput.Clear();
        _pendingLoginRequestId = request.RequestId;
        SetLoginBusy(true);
        LoginStatusText.Text = "Выполняется вход...";

        try
        {
            if (LoginRequested is null)
            {
                throw new InvalidOperationException("Login transport is unavailable.");
            }

            await LoginRequested(request);
        }
        catch
        {
            _pendingLoginRequestId = null;
            SetLoginBusy(false);
            LoginStatusText.Text = "Нет связи с локальным Agent";
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var request = new PlayerLogoutRequest(Guid.NewGuid());
        _pendingLogoutRequestId = request.RequestId;
        SetLogoutBusy(true);
        LogoutStatusText.Text = "Завершение сессии...";

        try
        {
            if (LogoutRequested is null)
            {
                throw new InvalidOperationException("Logout transport is unavailable.");
            }

            await LogoutRequested(request);
        }
        catch
        {
            _pendingLogoutRequestId = null;
            SetLogoutBusy(false);
            LogoutStatusText.Text = "Нет связи с локальным Agent";
        }
    }

    private void SetLoginBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        UsernameInput.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
        if (!busy)
        {
            LoginStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
        }
    }

    private void SetLogoutBusy(bool busy)
    {
        LogoutButton.IsEnabled = !busy;
        if (!busy)
        {
            LogoutStatusText.Text = string.Empty;
        }
    }

    private void ApplyGamingSnapshot(GamingSessionSnapshot? snapshot)
    {
        if (_gamingSnapshot != snapshot)
        {
            _gamingSnapshot = snapshot;
            _gamingClock.Restart();
        }

        GamingTimerText.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        GamingElapsedText.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateGamingTime();
    }

    private void UpdateGamingTime()
    {
        GamesButton.Visibility = PlayniteSessionBridge.CanPlay(_state, DateTime.UtcNow) ? Visibility.Visible : Visibility.Collapsed;
        if (_pendingGames.HasValue && DateTime.UtcNow - _gamesRequestedAt > TimeSpan.FromSeconds(15))
            ApplyGamesResult(new(_pendingGames.Value, false, "TIMEOUT"));
        if (_gamingSnapshot is null)
        {
            GamingStatusText.Text = "Вход выполнен. Ожидание игровой сессии";
            return;
        }

        var display = GamingSessionTimeProjection.FromSnapshot(_gamingSnapshot, _gamingClock.Elapsed);
        GamingStatusText.Text = _gamingSnapshot.Status switch
        {
            "Active" => display.RemainingSeconds == 0
                ? "Ожидание подтверждения завершения сервером"
                : "Игровая сессия активна",
            "Paused" => "Игровая сессия приостановлена",
            _ => "Ожидание начала игровой сессии"
        };
        GamingTimerText.Text = display.RemainingSeconds is { } remaining
            ? FormatSeconds(remaining)
            : "Без лимита времени";
        GamingElapsedText.Text = $"Прошло: {FormatSeconds(display.ElapsedSeconds)}";
    }

    private static string FormatSeconds(long seconds) =>
        $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
}
