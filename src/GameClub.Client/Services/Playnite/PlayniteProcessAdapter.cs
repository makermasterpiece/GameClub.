using System.Diagnostics;
using System.IO;

namespace GameClub.Client.Services.Playnite;

public interface IPlayniteProcessAdapter
{
    void Execute(string executable, Guid? playniteGameId);
    void CloseManaged();
}

// Runs exclusively in the interactive WPF process, never Windows Service session 0.
public sealed class PlayniteProcessAdapter : IPlayniteProcessAdapter
{
    private Process? _owned;
    private string? _executable;
    private DateTime _started;

    public void Execute(string executable, Guid? playniteGameId)
    {
        if (!File.Exists(executable)) throw new IOException("Playnite is not installed.");
        if (_owned is not null && !IsOwned()) { _owned.Dispose(); _owned = null; }
        if (_owned is null)
        {
            if (FindInteractiveInstances().Length != 0) throw new IOException("An unmanaged Playnite instance is running.");
            // A game launch first requires the player to open the owned Fullscreen instance.
            // No delayed game start may outlive the short server authorization lease.
            if (playniteGameId.HasValue) throw new IOException("Open Playnite Fullscreen first.");
            _owned = Start(executable, "--startfullscreen");
            _executable = executable;
            _started = _owned.StartTime;
            if (!IsOwned()) throw new IOException("Playnite ownership could not be established.");
        }
        else
        {
            if (!string.Equals(_executable, executable, StringComparison.OrdinalIgnoreCase) || !HasExclusiveOwnership())
                throw new IOException("Playnite ownership changed.");
            using var command = playniteGameId is { } game
                ? Start(executable, "--start", game.ToString("D"))
                : Start(executable, "--startfullscreen");
        }
    }

    public void CloseManaged()
    {
        // --shutdown is global to Playnite: use it only while the sole local instance is ours.
        if (_owned is null || !IsOwned() || !HasExclusiveOwnership()) return;
        using var command = Start(_executable!, "--shutdown");
    }

    private bool IsOwned()
    {
        try { return _owned is { HasExited: false } && _owned.SessionId == Process.GetCurrentProcess().SessionId
            && _owned.StartTime == _started
            && string.Equals(_owned.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private bool HasExclusiveOwnership() => FindInteractiveInstances().All(id => id == _owned!.Id);

    private static int[] FindInteractiveInstances()
    {
        var ids = new List<int>();
        foreach (var name in new[] { "Playnite.FullscreenApp", "Playnite.DesktopApp" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
                if (process.SessionId == Process.GetCurrentProcess().SessionId) ids.Add(process.Id);
        }
        return ids.ToArray();
    }

    private static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Playnite did not start.");
    }
}
