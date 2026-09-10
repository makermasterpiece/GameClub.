# Runs inside Playnite's documented PowerShell extension host, never on the Server.
$script:worker = $null
$script:runspace = $null
$script:stop = $null
$script:pending = $null
$script:export = {
    param($api)
    try {
        $target = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'GameClub\Playnite\Inventory\library.json'
        # Installation creates this dedicated directory and grants the Playnite account Modify.
        # Do not create directories or change ACLs from the unprivileged exporter.
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($target))) { return }
        $games = @($api.Database.Games | Select-Object -First 501 | ForEach-Object {
            @{ id = $_.Id.ToString('D'); name = $_.Name; isInstalled = [bool]$_.IsInstalled }
        })
        if ($games.Count -gt 500) { return }
        $json = @{ schemaVersion = 1; exportedAtUtc = [DateTime]::UtcNow.ToString('o'); games = $games } | ConvertTo-Json -Depth 4 -Compress
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        if ($bytes.Length -gt 524288) { return }
        $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllBytes($temporary, $bytes)
            # Windows PowerShell 5.1 converts $null to an empty string for this overload.
            if ([IO.File]::Exists($target)) { [IO.File]::Replace($temporary, $target, [Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($temporary, $target) }
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    } catch {
        # No credentials, paths or library data in logs. Agent fails closed when export is stale.
    }
}

function OnApplicationStarted {
    param($eventArgs)
    & $script:export $PlayniteApi
    if ($null -eq $script:worker) {
        # An idle embedded PowerShell host need not pump Register-ObjectEvent jobs.
        # Keep a dedicated bounded pipeline alive; no dependency on Playnite calling another event.
        $script:stop = New-Object Threading.ManualResetEvent($false)
        $script:runspace = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
        $script:runspace.Open()
        $script:runspace.SessionStateProxy.SetVariable('gameClubApi', $PlayniteApi)
        $script:runspace.SessionStateProxy.SetVariable('gameClubStop', $script:stop)
        $script:runspace.SessionStateProxy.SetVariable('gameClubExportSource', $script:export.ToString())
        $script:worker = [Management.Automation.PowerShell]::Create()
        $script:worker.Runspace = $script:runspace
        [void]$script:worker.AddScript(@'
$exportLibrary = [ScriptBlock]::Create($gameClubExportSource)
while (-not $gameClubStop.WaitOne(20000)) {
    & $exportLibrary $gameClubApi
}
'@)
        $script:pending = $script:worker.BeginInvoke()
    }
}
function OnLibraryUpdated { param($eventArgs) & $script:export $PlayniteApi }
function OnGameInstalled { param($eventArgs) & $script:export $PlayniteApi }
function OnGameUninstalled { param($eventArgs) & $script:export $PlayniteApi }
function OnApplicationStopped {
    param($eventArgs)
    if ($null -ne $script:worker) {
        [void]$script:stop.Set()
        try {
            if (-not $script:pending.AsyncWaitHandle.WaitOne(3000)) { $script:worker.Stop() }
            $script:worker.EndInvoke($script:pending)
        } catch { }
        $script:worker.Dispose()
        $script:runspace.Dispose()
        $script:stop.Dispose()
        $script:worker = $null
        $script:runspace = $null
        $script:stop = $null
        $script:pending = $null
    }
}
Export-ModuleMember -Function OnApplicationStarted, OnLibraryUpdated, OnGameInstalled, OnGameUninstalled, OnApplicationStopped
