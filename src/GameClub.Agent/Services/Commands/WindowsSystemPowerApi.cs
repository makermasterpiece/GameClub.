using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameClub.Agent.Services.Commands;

/// <summary>
/// Fixed local Win32 shutdown, never a shell command. See Microsoft InitiateSystemShutdownExW and
/// AdjustTokenPrivileges documentation; the latter may return true with ERROR_NOT_ALL_ASSIGNED.
/// </summary>
public sealed class WindowsSystemPowerApi : IWindowsSystemPowerApi
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    // SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_MAINTENANCE | SHTDN_REASON_FLAG_PLANNED.
    private const uint PlannedApplicationMaintenance = 0x80040001;

    public bool IsWindows => OperatingSystem.IsWindows();

    public IDisposable AcquireShutdownPrivilege()
    {
        if (!IsWindows) throw new PlatformNotSupportedException("Station power commands require Windows.");
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
            throw NativeFailure("Opening the process token");
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
                throw NativeFailure("Looking up the shutdown privilege");
            var requested = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            var adjusted = AdjustTokenPrivileges(token, false, ref requested,
                (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _);
            var error = Marshal.GetLastPInvokeError();
            if (!adjusted || error != 0)
                throw new Win32Exception(error, $"Enabling the shutdown privilege failed (Windows error {error}).");
            return new ShutdownPrivilege(token, previous);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    public void InitiateLocalShutdown(bool restart, uint graceSeconds, bool forceAppsClosed)
    {
        if (!IsWindows) throw new PlatformNotSupportedException("Station power commands require Windows.");
        if (graceSeconds != WindowsSystemPowerService.GracePeriodSeconds || forceAppsClosed)
            throw new ArgumentException("Only a 30-second graceful local power request is supported.");
        // null machine name is intentionally constant: no remote machine can be supplied through this API.
        if (!InitiateSystemShutdownExW(null, "GameClub: scheduled station power operation.",
                graceSeconds, false, restart, PlannedApplicationMaintenance))
            throw NativeFailure("Requesting the local power operation");
    }

    private static Win32Exception NativeFailure(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed (Windows error {error}).");
    }

    private sealed class ShutdownPrivilege(SafeAccessTokenHandle token, TokenPrivileges previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (previous.PrivilegeCount == 0) return; // It was already enabled; preserve that initial state.
                var restored = AdjustTokenPrivileges(token, false, ref previous,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out _, out _);
                var error = Marshal.GetLastPInvokeError();
                if (!restored || error != 0)
                    throw new Win32Exception(error, $"Restoring the shutdown privilege failed (Windows error {error}).");
            }
            finally { token.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState,
        uint bufferLength, out TokenPrivileges previousState, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(string? machineName, string message, uint timeout,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown, uint reason);
}
