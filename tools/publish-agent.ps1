<#
.SYNOPSIS
    Build the Linux agents (ferry for linux-x64 and linux-arm64) on Windows, for server mode in a build of your own.
.DESCRIPTION
    Writes artifacts/cli/ferry-linux-x64 and ferry-linux-arm64, which the app's build copies into agent\.
    These are self-contained single files, not trimmed, as tools/publish-cli.sh makes the Linux builds for
    a release: they cross-build, and run on older glibc than a Native AOT build would. The agent has to be the same
    build as the PC, so build them again after changing the code.
#>
param(
    [string[]]$Rid = @('linux-x64', 'linux-arm64'),
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = New-Item -ItemType Directory -Force (Join-Path $root 'artifacts\cli')

foreach ($r in $Rid) {
    $out = Join-Path $root "artifacts\publish\agent-$r"
    dotnet publish (Join-Path $root 'src\Ferry.Agent\Ferry.Agent.csproj') -c Release -r $r -o $out `
        -p:ServerAgent=true -p:SelfContained=true -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true @(if ($Version) { "-p:Version=$Version" })
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $target = Join-Path $artifacts "ferry-$r"
    Copy-Item (Join-Path $out 'ferry') $target -Force
    Write-Host ("{0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
}
