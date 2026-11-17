<#
.SYNOPSIS
  Install / update the WebErp.HostSync agent (Windows Service, LocalSystem) that syncs tenant domains to IIS site WebErp.
.DESCRIPTION
  ASCII only (the elevated console on the VPS cannot show Vietnamese). Safe to run many times.
  Run elevated, e.g.:
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File C:\WebErp\setup\setup-hostsync.ps1'

  What it does:
    1. Checks the new build in <Root>\tools\hostsync-staging (put there by deploy.ps1) against its SHA256SUMS.txt and prints
       the manifest hash. Compare it with "HostSync ... manifest SHA256" in the GitHub CI run summary of the same commit.
       Pass -ExpectedManifestHash to make the script stop on mismatch.
    2. Registers Event Log source WebErp.HostSync.
    3. Stops service WebErpHostSync (if any), copies the build to -InstallDir (Program Files, NOT writable by the deploy runner).
       appsettings.Production.json in -InstallDir (local overrides) is kept.
    4. Creates / updates the service: LocalSystem, automatic start, restart on failure.
    5. Adopts the domains that were configured by hand (default erp + tinphong) into table managed_host.
    6. Starts the service and prints the agent status.

  It never touches other IIS sites / app pools, never runs iisreset, never changes win-acme renewals.
  Logs: <Root>\setup\logs\setup-hostsync-<time>.log (no secrets: the agent never reads connection strings).
#>
param(
    [string]$Root = $(if ($env:WebErp__Root) { $env:WebErp__Root } else { 'C:\WebErp' }),
    [string]$Source = '',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WebErp\HostSync'),
    [string[]]$Adopt = @('erp.finora.io.vn', 'tinphong.finora.io.vn'),
    [string]$ExpectedManifestHash = '',
    [switch]$SkipAdopt
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'WebErpHostSync'
$EventSource = 'WebErp.HostSync'
if (-not $Source) { $Source = Join-Path $Root 'tools\hostsync-staging' }

$logDir = Join-Path $Root 'setup\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$log = Join-Path $logDir ('setup-hostsync-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Start-Transcript -Path $log | Out-Null

function Step([string]$text) { Write-Host ''; Write-Host "=== $text" }

# Must be identical to the manifest computation in deploy/package.ps1.
function Get-Manifest([string]$dir) {
    # Base path from Get-Item and list from that same string (avoid 8.3 short-name mismatch).
    $dirFull = (Get-Item -LiteralPath $dir).FullName.TrimEnd('\')
    $base = $dirFull + '\'
    $rels = @(Get-ChildItem -LiteralPath $dirFull -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object { $_.FullName.Substring($base.Length) })
    [Array]::Sort($rels, [StringComparer]::Ordinal)
    $sb = New-Object Text.StringBuilder
    foreach ($r in $rels) {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $base $r)).Hash.ToLowerInvariant()
        [void]$sb.Append("$h  $($r.Replace('\', '/'))`n")
    }
    return $sb.ToString()
}

function Get-TextHash([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

$exitCode = 0
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script elevated (Start-Process powershell -Verb RunAs ...).'
    }

    Step "1. Check build in $Source"
    $exe = Join-Path $Source 'WebErp.HostSync.exe'
    if (-not (Test-Path $exe)) { throw "Not found: $exe. Deploy first (deploy.ps1 copies the agent to tools\hostsync-staging)." }
    $sumsFile = Join-Path $Source 'SHA256SUMS.txt'
    if (-not (Test-Path $sumsFile)) { throw "Not found: $sumsFile" }
    $manifest = Get-Manifest $Source
    $expectedSums = [IO.File]::ReadAllText($sumsFile)
    if ($manifest -ne $expectedSums) { throw 'Files in staging do not match SHA256SUMS.txt (changed after packaging). Stop.' }
    $manifestHash = Get-TextHash $manifest
    $version = if (Test-Path (Join-Path $Source 'version.txt')) { (Get-Content (Join-Path $Source 'version.txt') -Raw).Trim() } else { '?' }
    Write-Host "Version (commit): $version"
    Write-Host "Manifest SHA256 : $manifestHash"
    Write-Host 'Compare with "HostSync <commit> manifest SHA256" in the GitHub Actions CI summary of that commit.'
    if ($ExpectedManifestHash -and $ExpectedManifestHash.Trim().ToLowerInvariant() -ne $manifestHash) {
        throw "Manifest hash mismatch: expected $ExpectedManifestHash. Stop."
    }

    Step '2. Event Log source'
    if (-not [Diagnostics.EventLog]::SourceExists($EventSource)) {
        New-EventLog -LogName Application -Source $EventSource
        Write-Host "Created source $EventSource."
    } else { Write-Host "Source $EventSource exists." }

    Step "3. Copy build to $InstallDir"
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        Write-Host 'Service stopped.'
    }
    if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
    robocopy $Source $InstallDir /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
    # appsettings.Production.json (local) is excluded from the check.
    $check = (Get-Manifest $InstallDir) -split "`n" | Where-Object { $_ -and $_ -notmatch '  appsettings\.Production\.json$' }
    if ((($check -join "`n") + "`n") -ne $manifest) { throw 'Installed files differ from staging after copy.' }
    Write-Host 'Copied and verified.'

    Step "4. Service $ServiceName"
    $bin = '"' + (Join-Path $InstallDir 'WebErp.HostSync.exe') + '"'
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName 'WebErp HostSync (IIS domains)' -StartupType Automatic `
            -Description 'Syncs WebErp tenant domains (config.db) to IIS site WebErp: bindings, Lets Encrypt certificates (win-acme).' | Out-Null
        Write-Host 'Service created (LocalSystem).'
    } else {
        & sc.exe config $ServiceName binPath= $bin start= auto obj= LocalSystem | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc config failed ($LASTEXITCODE)." }
        Write-Host 'Service updated.'
    }
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc failure failed ($LASTEXITCODE)." }
    $global:LASTEXITCODE = 0

    Step '5. Adopt existing domains'
    $agent = Join-Path $InstallDir 'WebErp.HostSync.exe'
    if ($SkipAdopt -or $Adopt.Count -eq 0) { Write-Host 'Skipped.' }
    elseif (-not (Test-Path (Join-Path $Root 'data\config.db'))) { Write-Warning 'config.db not found: open the admin site once first, then run this script again.' }
    else {
        & $agent adopt @Adopt
        if ($LASTEXITCODE -ne 0) { Write-Warning "adopt returned $LASTEXITCODE (see lines above). The agent will also adopt bound domains on its own." }
        $global:LASTEXITCODE = 0
    }

    Step '6. Start service'
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Host 'Service running. Waiting 20 s for the first sync round...'
    Start-Sleep -Seconds 20
    & $agent status
    $global:LASTEXITCODE = 0
    Write-Host ''
    Write-Host "Done. Version $version, manifest $manifestHash. Log: $log"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    $exitCode = 1
} finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
