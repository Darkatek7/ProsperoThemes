#requires -Version 7
<#
.SYNOPSIS
    Checks the need entries of a built eboot.bin before it goes to the console.
.DESCRIPTION
    A module the loader cannot load stops the app before Main runs ("The data is corrupted",
    PrxModuleLoadError in klog, often with "Lack of a .prx file in /app0/sce_module"). The cause seen on
    FW 13.00 was a libc.prx in the PS4 container format (magic 4F 15 3D 1D): the SDK copies an already
    wrapped module unchanged, and the PS5 loader refuses it. This check fails the build for that, next to
    sce_module/libc.prx, and lists the need entries.

    Modules outside the list a hardware-tested build needed are only reported: try such a build on the
    console before relying on it, then add the module to $Tested below.
.PARAMETER Module
    The eboot.bin (signed or not) to read.
#>
param(
    [Parameter(Mandatory = $true)][string]$Module,
    [string]$SdkRoot = $env:SHARPPROSPERO_ROOT,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Imported by builds that ran on hardware (libraries named by the SDK linker). libSceHttp and libSceSsl:
# the Community tab, 2026-10-15. libSceAudioOut and libSceAudiodec: the preview's sound, 01.000.018 on 13.00, 2026-10-22.
$Tested = @("libc", "libkernel", "libSceAudioOut", "libSceAudiodec", "libSceCommonDialog", "libSceHttp", "libSceNet",
    "libScePad", "libScePngDec", "libSceSsl", "libSceSysmodule", "libSceSystemService", "libSceUserService", "libSceVideoOut")

# The modules the app carries must be in the PS5 container format (magic 54 14 F5 EE).
$moduleDir = Join-Path (Split-Path -Parent $Module) "sce_module"
foreach ($prx in @(Get-ChildItem $moduleDir -File -Filter *.prx -ErrorAction SilentlyContinue)) {
    $head = [System.IO.File]::ReadAllBytes($prx.FullName)[0..3]
    $magic = ($head | ForEach-Object { $_.ToString("X2") }) -join " "
    if ($magic -ne "54 14 F5 EE") {
        throw ("$($prx.Name) starts with $magic, not 54 14 F5 EE (PS5 container). The PS5 loader refuses it and " +
            "the app does not start. Use a libc.prx from a PS5 application package (see ThemeApp/README.md).")
    }
}

if (-not (Test-Path $Module)) { throw "No module at $Module." }
if (-not $SdkRoot) { throw "Set SHARPPROSPERO_ROOT or pass -SdkRoot." }

$report = & dotnet run --project (Join-Path $SdkRoot "tools/SharpProspero.Bindings.Generator/SharpProspero.Bindings.Generator.csproj") `
    -c $Configuration -- elf --file $Module
if ($LASTEXITCODE -ne 0) { throw "Could not read $Module." }

# "Needed modules (N):" followed by one indented name per line.
$needed = @()
$inList = $false
foreach ($line in $report) {
    if ($line -match '^Needed modules \(\d+\):') { $inList = $true; continue }
    if ($inList -and $line -match '^\s+(\S+)\s*$') { $needed += ($Matches[1] -replace '\.s?prx$', '') }
    elseif ($inList) { $inList = $false }
}
if ($needed.Count -eq 0) {
    Write-Warning "No needed modules found in the report for $Module; check it by hand (bindgen elf --file)."
    return
}

Write-Host "Needed modules: $($needed -join ', ')"
$untested = @($needed | Where-Object { $_ -notin $Tested })
if ($untested.Count -gt 0) {
    Write-Warning ("Not in a hardware-tested build yet: $($untested -join ', '). " +
        "Try this build on the console before relying on it, then add the module to `$Tested in $PSCommandPath.")
}

# The preview's sound (Library/ConsolePreviewAudio.cs) links these two. They are static imports like libSceHttp and
# libSceSsl, not resolved at run time, so a console that cannot load either one stops the app before Main. The build
# only reports them until a console has started the app with them.
$audio = @($untested | Where-Object { $_ -in @("libSceAudioOut", "libSceAudiodec") })
if ($audio.Count -gt 0) {
    Write-Warning ("The preview's sound needs $($audio -join ' and '). If the app does not start on the console, create " +
        "NullPreviewAudio instead of ConsolePreviewAudio in ThemeApp/Program.cs: the class is then trimmed and the need " +
        "entries go. Once the app has started with them, add them to `$Tested.")
}
