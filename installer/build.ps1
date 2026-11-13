<#
.SYNOPSIS
    Build Ferry's setup with Inno Setup 6: artifacts/installer/Ferry-<version>-setup.exe.
.DESCRIPTION
    Publishes the app first unless -SkipPublish (then it uses artifacts/publish/app-<rid> as it is). Needs Inno
    Setup 6 (ISCC.exe in Program Files, on PATH, or given with -Iscc); CI installs it with Chocolatey.
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Rid = 'win-x64',
    [string]$Iscc = '',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SkipPublish) {
    & (Join-Path $root 'tools\publish-app.ps1') -Rid $Rid -Version $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$app = Join-Path $root "artifacts\publish\app-$Rid"
if (-not (Test-Path (Join-Path $app 'Ferry.exe'))) { throw "No published app in $app" }

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found; install it, or pass -Iscc' }

& $Iscc "/DAppVersion=$Version" "/DAppDir=$app" (Join-Path $PSScriptRoot 'ferry.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item (Join-Path $root "artifacts\installer\Ferry-$Version-setup.exe") | ForEach-Object { '{0} ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
