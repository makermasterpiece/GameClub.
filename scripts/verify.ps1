param(
    [string]$Dotnet = 'dotnet',
    [string]$PackageSource
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $taskRoot
try {
    $restoreArgs = @('restore', 'GameClub.sln')
    if ($PackageSource) { $restoreArgs += @('--source', $PackageSource) }
    & $Dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }
    & $Dotnet build GameClub.sln --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
    & $Dotnet test GameClub.sln --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
} finally {
    Pop-Location
}
