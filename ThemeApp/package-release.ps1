#requires -Version 7
<#
.SYNOPSIS
    Builds the store package: dist/PPSA90210.zip (rooted at PPSA90210/) and the catalog entry
    dist/PPSA90210.json for blackbearreloaded/ps5-homebrew-catalog.
.DESCRIPTION
    Runs build.ps1 -Output Folder, stages out/module as PPSA90210/, zips it and records the SHA-256.
    Nothing is uploaded. The release tag is the contentVersion from sce_sys/param.json (01.000.000), as the
    catalog recommends, so the record's version, the tag and what consoles compare are one number. The
    record follows the catalog's metadata format (docs/metadata.md there): twelve fields, a one-sentence
    description of at most 200 characters, and an icon URL pinned to the tag.
#>
param(
    [string]$SourceRepo = "https://github.com/Darkatek7/ProsperoThemes",
    [string]$ArtifactUrl = "",
    [string]$IconUrl = "",
    [string]$Author = "Darkatek7",
    [string]$License = "GPL-3.0",
    [string]$Category = "utilities",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$titleId = "PPSA90210"

if (-not $SkipBuild) {
    & (Join-Path $here "build.ps1") -Output Folder
    if ($LASTEXITCODE) { throw "build.ps1 failed." }
}

$module = Join-Path $here "out/module"
# A missing font atlas stops the app at startup, so every atlas the renderer loads is checked here.
$fonts = "inter-22", "inter-28", "inter-28-semibold", "inter-36", "inter-48", "inter-76" | ForEach-Object { "sce_sys/prospero-themes/$_.p5font" }
foreach ($required in @("eboot.bin", "sce_sys/param.json", "sce_sys/icon0.png", "sce_module/libc.prx",
                        "sce_sys/prospero-themes/ThemeHelper.elf", "sce_sys/prospero-themes/blue-hour.p5theme") + $fonts) {
    if (-not (Test-Path (Join-Path $module $required))) { throw "Package is missing $required." }
}

$param = Get-Content (Join-Path $module "sce_sys/param.json") -Raw | ConvertFrom-Json
if ($param.titleId -ne $titleId) { throw "param.json titleId is $($param.titleId), expected $titleId." }
# The tag and the record's version: the contentVersion itself, e.g. "01.000.000".
$version = $param.contentVersion
if ($version -notmatch '^\d{2}\.\d{3}\.\d{3}$') { throw "contentVersion '$version' is not in the NN.NNN.NNN format." }

$dist = Join-Path $here "../dist"
$stage = Join-Path $dist "stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage $titleId) | Out-Null
# Only what the app is: out/module can hold leftovers from older builds.
foreach ($part in "eboot.bin", "sce_module", "sce_sys") {
    Copy-Item (Join-Path $module $part) (Join-Path $stage $titleId) -Recurse -Force
}

$zip = Join-Path $dist "$titleId.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# PPSA90210/ at the top of the archive, entries with forward slashes.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $stage $titleId), $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item $stage -Recurse -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

if (-not $ArtifactUrl) { $ArtifactUrl = "$SourceRepo/releases/download/$version/$titleId.zip" }
if (-not $IconUrl) {
    # Pinned to the tag; next to it the catalog also finds sce_sys/param.json for the contentVersion.
    $ownerRepo = $SourceRepo -replace '^https://github\.com/', ''
    $IconUrl = "https://raw.githubusercontent.com/$ownerRepo/$version/ThemeApp/sce_sys/icon0.png"
}
$description = "Theme manager for the PS5 home screen: download community themes, preview and apply .p5theme " +
               "packs, import them from USB and restore the originals."
if ($description.Length -gt 200) { throw "The catalog allows 200 characters of description, this one has $($description.Length)." }

$entry = [ordered]@{
    titleid      = $titleId
    name         = "Prospero Themes"
    kind         = "app"
    category     = $Category
    description  = $description
    license      = $License
    author       = $Author
    version      = $version
    source_repo  = $SourceRepo
    artifact_url = $ArtifactUrl
    sha256       = $sha
    icon_url     = $IconUrl
}
$entry | ConvertTo-Json | Set-Content (Join-Path $dist "$titleId.json") -Encoding utf8NoBOM

Write-Host "Package: $zip"
Write-Host "SHA-256: $sha"
Write-Host "Version: $version"
Write-Host "Catalog: $(Join-Path $dist "$titleId.json")"
