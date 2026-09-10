# Run with Windows PowerShell 5.1. Uses a mock SDK object and workspace-only files.
$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path $PSScriptRoot ('../../work/playnite-exporter-test-' + [Guid]::NewGuid().ToString('N'))
$testDirectory = (New-Item -ItemType Directory -Path $testDirectory).FullName
$target = Join-Path $testDirectory 'library.json'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'GameClub.LibraryExporter/GameClub.LibraryExporter.psm1') -Raw
# Test-only in-memory redirection; production extension exposes no configurable output argument.
$original = '$target = Join-Path ([Environment]::GetFolderPath(''CommonApplicationData'')) ''GameClub\Playnite\Inventory\library.json'''
if (-not $source.Contains($original)) { throw 'Exporter target line changed; update test.' }
$source = $source.Replace($original, ('$target = ''' + $target.Replace("'", "''") + "'"))
$source = $source.Replace('# No credentials, paths or library data in logs. Agent fails closed when export is stale.', '[IO.File]::WriteAllText($target + ''.error'', $_.ToString())')
$id = [Guid]::NewGuid()
$mock = [pscustomobject]@{ Database = [pscustomobject]@{ Games = @([pscustomobject]@{ Id=$id; Name='Synthetic game'; IsInstalled=$true }) } }
$hostRunspace = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
$hostRunspace.Open()
$hostRunspace.SessionStateProxy.SetVariable('mockApi', $mock)
$hostRunspace.SessionStateProxy.SetVariable('moduleSource', $source)
$hostPipeline = [Management.Automation.PowerShell]::Create()
$hostPipeline.Runspace = $hostRunspace
try {
    [void]$hostPipeline.AddScript('$global:PlayniteApi=$mockApi; $module=New-Module -ScriptBlock ([ScriptBlock]::Create($moduleSource)); Import-Module $module; OnApplicationStarted')
    [void]$hostPipeline.Invoke()
    if ($hostPipeline.HadErrors) { throw ($hostPipeline.Streams.Error | Out-String) }
    $first = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
    # Crucially, no PowerShell invocation/event pump in the simulated Playnite host during this wait.
    [Threading.Thread]::Sleep(22000)
    $second = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
    if ($second.exportedAtUtc -le $first.exportedAtUtc) {
        if (Test-Path -LiteralPath ($target + '.error')) { Get-Content -LiteralPath ($target + '.error') }
        throw 'Idle host did not refresh export.'
    }
    if ($second.games.Count -ne 1 -or $second.games[0].id -ne $id.ToString('D') -or -not $second.games[0].isInstalled) { throw 'SDK export mismatch.' }
    'PASS: idle embedded host refreshes the SDK export; payload identity/Installed are correct.'
} finally {
    $hostPipeline.Commands.Clear()
    [void]$hostPipeline.AddScript('OnApplicationStopped')
    [void]$hostPipeline.Invoke()
    $hostPipeline.Dispose()
    $hostRunspace.Dispose()
}
