<#
.SYNOPSIS
    Publish the ferry CLI as one Native AOT binary for win-x64 or win-arm64 (either builds on an x64 machine).
.DESCRIPTION
    Writes artifacts/cli/ferry-<rid>.exe. Native AOT needs the MSVC linker (Visual Studio or its Build Tools with
    "Desktop development with C++"); the Visual Studio Installer folder is put on PATH so the build can find vswhere.
#>
param(
    [string]$Rid = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }),
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$installer;$env:PATH" }

$out = Join-Path $root "artifacts\publish\cli-$Rid"
dotnet publish (Join-Path $root 'src\Ferry.Agent\Ferry.Agent.csproj') -c Release -r $Rid -o $out @(if ($Version) { "-p:Version=$Version" })
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$artifacts = New-Item -ItemType Directory -Force (Join-Path $root 'artifacts\cli')
$target = Join-Path $artifacts "ferry-$Rid.exe"
Copy-Item (Join-Path $out 'ferry.exe') $target -Force
$size = (Get-Item $target).Length / 1MB
Write-Host ("{0} ({1:N1} MB)" -f $target, $size)
