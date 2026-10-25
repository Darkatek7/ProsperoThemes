#requires -Version 5.1
<#
.SYNOPSIS
    Builds ProsperoThemes into an installable package.
.DESCRIPTION
    Delegates to the SDK's shared build pipeline (as the SDK's own app sample does).

    The one thing that needs arranging first: an application module imports libc.prx, which the system
    does not publish, so the application has to carry its own copy. Drop one in third_party/modules, or
    point -Modules (or the PROSPERO_MODULES environment variable) at any folder that has it. These files
    come from a game package; they are gitignored and not part of the source repository. The release ZIP
    carries the libc.prx it was built with, because the app does not start without it.
.PARAMETER Modules
    Folder holding the modules the application has to carry (needs at least libc.prx).
.PARAMETER Output
    Package produces a .pkg; Folder stops after the module and its metadata are gathered, which is what
    you want while iterating because it skips packaging.
#>
param(
    [string]$SdkRoot = "",
    [string]$Configuration = "Release",
    [ValidateSet("Package", "Folder")][string]$Output = "Package",
    [string]$Modules = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SdkRoot) { $SdkRoot = $env:SHARPPROSPERO_ROOT }
if (-not $SdkRoot -or -not (Test-Path (Join-Path $SdkRoot "build/build-app.ps1"))) {
    throw "Set SHARPPROSPERO_ROOT to the SharpProspero SDK folder, or pass -SdkRoot. See the SDK's doctor.ps1."
}

if (-not $Modules) { $Modules = $env:PROSPERO_MODULES }
if (-not $Modules) {
    # In the public repository the modules sit at its root (../); in the work collection, ProsperoThemes is a
    # subfolder and they sit at the collection's root (../../).
    foreach ($candidate in @((Join-Path $here "third_party/modules"),
                             (Join-Path $here "../third_party/modules"),
                             (Join-Path $here "../../third_party/modules"))) {
        if (Test-Path $candidate) { $Modules = $candidate; break }
    }
}
if (-not $Modules -or -not (Test-Path $Modules)) {
    throw ("No module folder. The application imports libc.prx, which the system does not publish, so it " +
        "must carry its own copy - put one in third_party/modules or pass -Modules.")
}
if (-not (Get-ChildItem $Modules -File -Filter "libc.prx")) {
    throw "The module folder '$Modules' has no libc.prx."
}
$env:PROSPERO_MODULES = (Resolve-Path $Modules).Path
Write-Host "Modules: $env:PROSPERO_MODULES"

& (Join-Path $SdkRoot "build/build-app.ps1") `
    -ProjectPath (Join-Path $here "ThemeApp.csproj") `
    -Output Folder `
    -OutputFolder (Join-Path $here "out") `
    -Configuration $Configuration `
    -SdkRoot $SdkRoot

# The carried modules and the need entries decide whether the app starts at all; check them before staging.
& (Join-Path $here 'tools/check-needed-modules.ps1') -Module (Join-Path $here 'out/module/eboot.bin') `
    -SdkRoot $SdkRoot -Configuration $Configuration

# The SDK gathers only sce_sys, sce_module and strings. Stage the app-owned assets before packaging.
$moduleTree = Join-Path $here 'out/module'
# The pinned packager also gathers only its known module and metadata trees. Keep app resources in
# their own subdirectory of sce_sys so both folder deployments and packages contain exactly the same files.
$assetOutput = Join-Path $moduleTree 'sce_sys/prospero-themes'
New-Item -ItemType Directory -Force -Path $assetOutput | Out-Null
foreach ($asset in Get-ChildItem -LiteralPath (Join-Path $here 'assets') -File |
    Where-Object { $_.Extension -in @('.p5font', '.p5theme') }) {
    Copy-Item -LiteralPath $asset.FullName -Destination (Join-Path $assetOutput $asset.Name) -Force
}
# ThemeHelper is the payload the app hands to the ELF loader when it needs to write theme assets
# (ThemeHelperClient.ElfPath). Built fresh every time so the app and the helper's build tag never drift.
$helperProject = Join-Path $here '../ThemeHelper/ThemeHelper.csproj'
& (Join-Path $SdkRoot "build/build-app.ps1") -ProjectPath $helperProject -Payload -Configuration $Configuration -SdkRoot $SdkRoot
$helperElf = Join-Path $here '../ThemeHelper/out/ThemeHelper.elf'
if (-not (Test-Path $helperElf)) { throw 'ThemeHelper.elf was not produced.' }
Copy-Item -LiteralPath $helperElf -Destination (Join-Path $assetOutput 'ThemeHelper.elf') -Force

# The measured asset catalogs: the firmware report (System tab) looks at the files 13.00's lists, and the applier
# tells the console's originals from a theme's files by the stock hashes of the console's firmware (StockCatalog).
$catalogOutput = Join-Path $assetOutput 'catalog'
New-Item -ItemType Directory -Force -Path $catalogOutput | Out-Null
Copy-Item -Path (Join-Path $here '../catalog/*.json') -Destination $catalogOutput -Force

$fontLicenseOutput = Join-Path $assetOutput 'fonts'
New-Item -ItemType Directory -Force -Path $fontLicenseOutput | Out-Null
Copy-Item -LiteralPath (Join-Path $here 'assets/fonts/OFL.txt') -Destination $fontLicenseOutput -Force
if ($Output -eq 'Package') {
    & dotnet run --project (Join-Path $SdkRoot 'tools/SharpProspero.Packager/SharpProspero.Packager.csproj') `
        -c $Configuration -- --in $moduleTree --out (Join-Path $here 'out')
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
}
