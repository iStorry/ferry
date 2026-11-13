<#
.SYNOPSIS
    Publish Ferry for Windows: self-contained, ReadyToRun, ready for the installer or a portable zip.
.DESCRIPTION
    Writes artifacts/publish/app-<rid>/ with Ferry.exe, the ferry CLI in cli\, the Linux agent builds in agent\ and the
    OpenSSH client in openssh\ (fetch-openssh.ps1). The app, CLI and agents are built here from the same source
    (publish-cli.ps1, publish-agent.ps1), since the PC and the agent have to be the same build. -Prebuilt uses the ones
    already in artifacts/cli instead (the release workflow puts its own builds there); it fails if the agents aren't there.
    -SmokeTest starts the published app with throwaway settings and checks its window comes up.
#>
param(
    [string]$Rid = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }),
    [string]$Version = '',
    [switch]$Prebuilt,
    [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish\app-$Rid"
$versionArgs = @(if ($Version) { "-p:Version=$Version" })

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

# the Linux agents for server mode, which the app's build copies into agent\
if (-not $Prebuilt) {
    & (Join-Path $PSScriptRoot 'publish-agent.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
foreach ($agentRid in 'linux-x64', 'linux-arm64') {
    if (-not (Test-Path (Join-Path $root "artifacts\cli\ferry-$agentRid"))) {
        Write-Error "artifacts\cli\ferry-$agentRid is missing: server mode would have no agent to upload."
        exit 1
    }
}

dotnet publish (Join-Path $root 'src\Ferry.App\Ferry.App.csproj') -c Release -r $Rid --self-contained `
    -p:PublishReadyToRun=true -o $out @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# the CLI, next to the app (the installer can put this folder on PATH)
$cli = Join-Path $root "artifacts\cli\ferry-$Rid.exe"
if (-not $Prebuilt -or -not (Test-Path $cli)) {
    & (Join-Path $PSScriptRoot 'publish-cli.ps1') -Rid $Rid -Version $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
New-Item -ItemType Directory -Force (Join-Path $out 'cli') | Out-Null
Copy-Item $cli (Join-Path $out 'cli\ferry.exe') -Force

# the OpenSSH client server mode runs, in openssh\ (found there by the app and by cli\ferry.exe): a pinned release,
# checked by its SHA-256
& (Join-Path $PSScriptRoot 'fetch-openssh.ps1') -Rid $Rid -Destination (Join-Path $out 'openssh')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# what the app is under, and what it carries along
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -Force

# symbols aren't shipped
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$agents = @(Get-ChildItem (Join-Path $out 'agent') -ErrorAction SilentlyContinue).Count
Write-Host ("{0}: {1:N1} MB, {2} agent build(s)" -f $out, $size, $agents)

if ($SmokeTest) {
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "ferry-smoke-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $env:FERRY_CONFIG_PATH = Join-Path $scratch 'settings.json'
    $env:FERRY_CHECK_UPDATES = 'false'
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process (Join-Path $out 'Ferry.exe') -PassThru
    try {
        while ($watch.Elapsed.TotalSeconds -lt 30 -and -not $app.HasExited) {
            $app.Refresh()
            if ($app.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw "Smoke test failed: no window after $([int]$watch.Elapsed.TotalSeconds) s" }
        Write-Host ("Smoke test: window up after {0:N1} s" -f $watch.Elapsed.TotalSeconds)
        Start-Sleep -Seconds 3
        if ($app.HasExited) { throw 'Smoke test failed: the app exited' }
    }
    finally {
        if (-not $app.HasExited) { $app.Kill() }
        Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
        Remove-Item Env:FERRY_CONFIG_PATH, Env:FERRY_CHECK_UPDATES -ErrorAction SilentlyContinue
    }
}
