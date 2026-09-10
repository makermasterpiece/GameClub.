param()

# Run interactively on the project PC. Git Credential Manager handles GitHub login.
# Uses a separate checkout; never resets this workspace or force-pushes history.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRemote = 'https://github.com/makermasterpiece/GameClub..git'
$taskCheckout = Join-Path $taskRoot ('work\github-publish-' + [Guid]::NewGuid().ToString('N'))

function Invoke-TaskGit {
    param([string[]] $Arguments)
    & git -c http.sslBackend=openssl @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git failed. Nothing is force-pushed; resolve the displayed error before retrying.' }
}

Push-Location $taskRoot
try {
    $taskFiles = @(Invoke-TaskGit -Arguments @('ls-files', '--cached', '--others', '--exclude-standard') | Sort-Object -Unique)
    if ($taskFiles -notcontains 'GameClub.sln') { throw 'GameClub solution not found.' }
    foreach ($taskFile in $taskFiles) {
        if ($taskFile -match '(^|/)(work|bin|obj|node_modules|dist|\.git|\.vs)/|(^|/)\.env($|\.(?!example$))|\.(pfx|p12|key|clixml|db|dump|backup)$|private.*\.pem$|credentials\.dat$') {
            throw "Excluded or sensitive file in publication set: $taskFile"
        }
        if ($taskFile -match '(^|/)\.\./|^[A-Za-z]:|^/') { throw 'Invalid source path.' }
        $taskSource = Join-Path $taskRoot $taskFile
        if ((Get-Item -LiteralPath $taskSource).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symbolic links require manual review.' }
        if ($taskFile -notmatch '\.(png|jpg|jpeg|ico)$') {
            if (Select-String -LiteralPath $taskSource -Quiet -Pattern '(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.)') {
                throw "Possible secret found; review before publishing: $taskFile"
            }
        }
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskCheckout) -Force | Out-Null
    Invoke-TaskGit -Arguments @('clone', '--branch', 'main', $taskRemote, $taskCheckout)
    Push-Location $taskCheckout
    try {
        # Preserve remote-only files and all prior commits. Refuse to overwrite existing application code.
        $taskRemoteFiles = @(Invoke-TaskGit -Arguments @('ls-files'))
        $taskOverlap = @($taskRemoteFiles | Where-Object { $_ -in $taskFiles -and $_ -notin @('README.md', '.gitignore') })
        if ($taskOverlap.Count -gt 0) { throw 'Remote already contains overlapping project files. Review them before publishing.' }
        foreach ($taskFile in $taskFiles) {
            $taskDestination = Join-Path $taskCheckout $taskFile
            New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $taskRoot $taskFile) -Destination $taskDestination
        }
        $taskName = @(Invoke-TaskGit -Arguments @('log', '-1', '--format=%an'))[0]
        $taskEmail = @(Invoke-TaskGit -Arguments @('log', '-1', '--format=%ae'))[0]
        if ([string]::IsNullOrWhiteSpace($taskName) -or [string]::IsNullOrWhiteSpace($taskEmail)) { throw 'Missing author identity in the initial commit.' }
        Invoke-TaskGit -Arguments @('config', '--local', 'user.name', $taskName)
        Invoke-TaskGit -Arguments @('config', '--local', 'user.email', $taskEmail)
        Invoke-TaskGit -Arguments @('add', '--all')
        Invoke-TaskGit -Arguments @('commit', '-m', 'Add GameClub MVP through stage 10 with documentation and screenshots')
        Write-Host 'GitHub may ask you to sign in. Choose makermasterpiece. Do not paste tokens into chat.'
        Invoke-TaskGit -Arguments @('-c', 'credential.username=makermasterpiece', 'push', 'origin', 'HEAD:main')
        $taskLocalHash = @(Invoke-TaskGit -Arguments @('rev-parse', 'HEAD'))[0]
        $taskRemoteHash = (@(Invoke-TaskGit -Arguments @('ls-remote', 'origin', 'refs/heads/main'))[0] -split '\s+')[0]
        if ($taskLocalHash -ne $taskRemoteHash) { throw 'Remote verification failed. Inspect GitHub before retrying.' }
        Write-Host 'Published and verified: https://github.com/makermasterpiece/GameClub.'
        Write-Host "Publication checkout retained at: $taskCheckout"
    }
    finally { Pop-Location }
}
finally { Pop-Location }
