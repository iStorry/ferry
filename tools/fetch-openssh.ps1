<#
.SYNOPSIS
    Put the OpenSSH client the app carries into a folder: ssh.exe, ssh-keygen.exe, the DLLs beside them, and the licence.
.DESCRIPTION
    Server mode runs the OpenSSH client (docs/openssh-plan.md). The app carries its own, so every user gets a known
    version (8.4 or newer, which askpass needs) and Windows 10 works without the optional feature. It is a pinned
    release of Microsoft's Win32-OpenSSH, downloaded once into artifacts\openssh\ and refused unless its SHA-256 is the
    one below (the digest GitHub publishes for the release asset). To move to a newer release, change $Release and
    both digests together, from: gh api repos/PowerShell/Win32-OpenSSH/releases/tags/<tag> --jq '.assets[] | {name, digest}'
#>
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'

# released 2025-10-27; 10.4.0.0p1 (2026-10-20) was too new to pin when this was chosen
$Release = '10.0.0.0p2-Preview'
$Zips = @{
    'win-x64'   = @{ Name = 'OpenSSH-Win64.zip'; Sha256 = '23f50f3458c4c5d0b12217c6a5ddfde0137210a30fa870e98b29827f7b43aba5' }
    'win-arm64' = @{ Name = 'OpenSSH-ARM64.zip'; Sha256 = '698c6aec31c1dd0fb996206e8741f4531a97355686b5431ef347d531b07fcd42' }
}
$zip = $Zips[$Rid]
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root "artifacts\openssh\$Release"
New-Item -ItemType Directory -Force $cache | Out-Null

$file = Join-Path $cache $zip.Name
if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $zip.Sha256) {
    $url = "https://github.com/PowerShell/Win32-OpenSSH/releases/download/$Release/$($zip.Name)"
    Write-Host "Downloading $url"
    # GitHub's downloads fail now and then (a 504 cost a release once): a few tries, further apart each time
    $tries = 4
    for ($try = 1; ; $try++) {
        try {
            Invoke-WebRequest $url -OutFile "$file.download" -UseBasicParsing
            break
        }
        catch {
            Remove-Item "$file.download" -Force -ErrorAction SilentlyContinue
            if ($try -ge $tries) { throw }
            $wait = 15 * $try
            Write-Host "Try $try of $tries failed ($($_.Exception.Message)); again in $wait s"
            Start-Sleep -Seconds $wait
        }
    }
    $hash = (Get-FileHash "$file.download" -Algorithm SHA256).Hash
    if ($hash -ne $zip.Sha256) {
        Remove-Item "$file.download" -Force
        throw "$($zip.Name) of OpenSSH $Release has SHA-256 $hash, not the pinned $($zip.Sha256): not used."
    }
    Move-Item "$file.download" $file -Force
}

$unpacked = Join-Path $cache "unpacked-$Rid"
if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
Expand-Archive $file -DestinationPath $unpacked
$from = (Get-ChildItem $unpacked -Recurse -Filter ssh.exe | Select-Object -First 1).DirectoryName
if (-not $from -or -not (Test-Path (Join-Path $from 'ssh-keygen.exe'))) {
    throw "$($zip.Name) of OpenSSH $Release has no ssh.exe with ssh-keygen.exe beside it."
}

if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
New-Item -ItemType Directory -Force $Destination | Out-Null
# only the client side: no sshd, no agent service, no install scripts
foreach ($name in 'ssh.exe', 'ssh-keygen.exe', 'LICENSE.txt', 'NOTICE.txt') {
    $source = Join-Path $from $name
    if (Test-Path $source) { Copy-Item $source $Destination }
}
Get-ChildItem $from -Filter *.dll | Copy-Item -Destination $Destination
Remove-Item -Recurse -Force $unpacked
Write-Host "OpenSSH $Release ($Rid) in $Destination"
