<#
.SYNOPSIS
    Build the zip of the Browser Link extension to send to the Chrome Web Store and Edge Add-ons.
.DESCRIPTION
    Writes artifacts/extension/ferry-browser-link-<version>.zip. The stores don't take the manifest's "key", which only fixes
    the ID of a copy loaded by hand, so it is left out; each store then gives the extension its own ID, which goes in
    BrowserLinkPlugin.StoreExtensionIds so the app lets it in. docs/extension-store.md has the rest of the steps.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'extensions\browser'
$out = New-Item -ItemType Directory -Force (Join-Path $root 'artifacts\extension')
$work = Join-Path $out 'staging'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Copy-Item -Recurse $source $work
$manifestPath = Join-Path $work 'manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.PSObject.Properties.Remove('key')
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
$zip = Join-Path $out "ferry-browser-link-$($manifest.version).zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
# entry by entry: Compress-Archive on Windows PowerShell writes paths with backslashes, which zip readers elsewhere can refuse
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -Recurse -File $work) {
        $name = $file.FullName.Substring($work.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Remove-Item -Recurse -Force $work
Write-Host $zip
