#Requires -Version 5.1
<#
======================================================================
 Build script for Windows DaSi Tool (Avalonia / .NET 10)
----------------------------------------------------------------------
 Creates a native Windows EXE from the C#/Avalonia project.

 USAGE (normal PowerShell, NO admin rights needed):

   .\build.ps1                          # default: self-contained single-file EXE
   .\build.ps1 -Mode framework          # small EXE, needs the .NET 10 runtime
   .\build.ps1 -Mode selfcontained      # explicitly self-contained (= default)
   .\build.ps1 -Clean                   # delete bin/ and obj/ first
   .\build.ps1 -Sign -PfxPath cert.pfx  # sign after the build (PFX)
   .\build.ps1 -Sign                    # sign with a certificate from the store

 The result is placed at:  .\publish\WindowsDaSiTool.exe
======================================================================
#>
[CmdletBinding()]
param(
    # Publish variant:
    #   selfcontained = standalone EXE including .NET (larger, runs everywhere)
    #   framework     = small EXE, needs the .NET 10 Desktop Runtime installed
    [ValidateSet('selfcontained', 'framework')]
    [string]$Mode = 'selfcontained',

    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish'),

    [switch]$Clean,

    # Signing (optional)
    [switch]$Sign,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[OK] $msg"  -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[!] $msg"   -ForegroundColor Yellow }
function Fail($msg)       { Write-Host "[FEHLER] $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------
# 1. Find the project file
# ---------------------------------------------------------------------
$projectFile = Get-ChildItem -Path $PSScriptRoot -Filter '*.csproj' -File | Select-Object -First 1
if (-not $projectFile) {
    Fail "Keine .csproj-Datei neben build.ps1 gefunden. Skript bitte in den Projektordner legen."
}
Write-Step "Projekt: $($projectFile.Name)"

# ---------------------------------------------------------------------
# 2. Check the .NET SDK (10.0)
#    The project is pinned to .NET 10 through global.json: built with
#    .NET 11, the EXE did not start on Windows 10 computers. A .NET 11 SDK
#    installed alongside does no harm - dotnet still picks 10 here.
# ---------------------------------------------------------------------
Write-Step "Pruefe .NET SDK..."
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail @"
.NET SDK wurde nicht gefunden.
Bitte das .NET 10 SDK installieren: https://dotnet.microsoft.com/download/dotnet/10.0
(Nach der Installation eine neue PowerShell oeffnen, damit 'dotnet' im PATH ist.)
"@
}

$sdks = (& dotnet --list-sdks) 2>$null
$hasSdk = $sdks | Where-Object { $_ -match '^\s*10\.' }
if (-not $hasSdk) {
    Write-Warn "Installierte SDKs:"
    $sdks | ForEach-Object { Write-Host "    $_" }
    Write-Warn "Das Projekt ist per global.json auf .NET 10 festgelegt - ein neueres SDK (z. B. .NET 11) wird dafuer nicht verwendet."
    Fail ".NET 10 SDK nicht gefunden. Bitte .NET 10 SDK installieren: https://dotnet.microsoft.com/download/dotnet/10.0"
}
Write-Ok "SDK vorhanden: $(($hasSdk | Select-Object -First 1).Trim())"

# ---------------------------------------------------------------------
# 3b. Check the package sources (cause of NU1100 = no nuget.org source)
# ---------------------------------------------------------------------
Write-Step "Pruefe NuGet-Paketquellen..."
$localConfig = Join-Path $PSScriptRoot 'NuGet.config'
if (Test-Path $localConfig) {
    Write-Ok "Lokale NuGet.config gefunden - nuget.org wird erzwungen."
} else {
    $sources = (& dotnet nuget list source) 2>$null
    $hasNuget = $sources | Where-Object { $_ -match 'nuget\.org' -and $_ -notmatch '\[Disabled\]|\[Deaktiviert\]' }
    if (-not $hasNuget) {
        Write-Warn "Es ist keine aktive nuget.org-Quelle konfiguriert."
        Write-Warn "Fuege sie einmalig hinzu mit:"
        Write-Host  "    dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org" -ForegroundColor Yellow
        Write-Warn "Oder lege die mitgelieferte NuGet.config neben die .csproj (empfohlen)."
    } else {
        Write-Ok "nuget.org ist als Quelle vorhanden."
    }
}

# ---------------------------------------------------------------------
# 3. Optional clean-up
# ---------------------------------------------------------------------
if ($Clean) {
    Write-Step "Raeume bin/ und obj/ auf..."
    Get-ChildItem -Path $PSScriptRoot -Include 'bin', 'obj' -Directory -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Ok "Aufgeraeumt."
}

# ---------------------------------------------------------------------
# 4. Restore
#    Some .NET installations cannot resolve their workloads and fail right
#    here:
#      MSB4236: The SDK
#      "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator" specified could not be found.
#    This project needs no workloads; without workload resolution the EXE
#    is byte-for-byte the same. So if exactly this error occurs, it is
#    switched off for restore and publish - only for this run; the
#    variable is reset afterwards in any case (even on abort).
# ---------------------------------------------------------------------
$workloadVorher = $env:MSBuildEnableWorkloadResolver
try {
    Write-Step "Stelle NuGet-Pakete wieder her (restore)..."
    & dotnet restore $projectFile.FullName | Tee-Object -Variable restoreAusgabe
    if ($LASTEXITCODE -ne 0 -and ($restoreAusgabe -match 'WorkloadAutoImportPropsLocator')) {
        Write-Warn "Die .NET-Installation kann ihre Workloads nicht aufloesen (MSB4236)."
        Write-Warn "Dieses Projekt braucht keine - zweiter Versuch ohne Workload-Aufloesung..."
        $env:MSBuildEnableWorkloadResolver = 'false'
        & dotnet restore $projectFile.FullName
        if ($LASTEXITCODE -eq 0) {
            Write-Warn "Ohne Workload-Aufloesung geklappt; die EXE ist dieselbe. Dauerhaft beheben: siehe README, 'Selbst bauen'."
        }
    }
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore fehlgeschlagen." }
    Write-Ok "Restore abgeschlossen."

    # -----------------------------------------------------------------
    # 5. Publish
    # -----------------------------------------------------------------
    $selfContained = ($Mode -eq 'selfcontained')

    $publishArgs = @(
        'publish', $projectFile.FullName,
        '-c', $Configuration,
        '-r', $Runtime,
        '-o', $OutputDir,
        "--self-contained", $selfContained.ToString().ToLower(),
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false"
    )
    # Compressing the embedded runtime only pays off in self-contained mode
    # (makes the EXE much smaller; the first start takes slightly longer).
    if ($selfContained) {
        $publishArgs += "-p:EnableCompressionInSingleFile=true"
    }

    Write-Step "Kompiliere ($Mode, $Runtime, $Configuration)..."
    Write-Host "    dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish fehlgeschlagen." }
} finally {
    # Assigning $null removes the variable completely again.
    $env:MSBuildEnableWorkloadResolver = $workloadVorher
}

# ---------------------------------------------------------------------
# 5b. Clean-up: remove native debug symbols (.pdb)
#     SkiaSharp/HarfBuzzSharp ship libSkiaSharp.pdb and
#     libHarfBuzzSharp.pdb. They are not needed at runtime and cannot be
#     suppressed reliably through the .csproj, so they are deleted here
#     after publishing.
# ---------------------------------------------------------------------
$pdbFiles = Get-ChildItem -Path $OutputDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue
if ($pdbFiles) {
    foreach ($pdb in $pdbFiles) {
        try { Remove-Item $pdb.FullName -Force -ErrorAction Stop }
        catch { Write-Warn "Konnte $($pdb.Name) nicht loeschen: $($_.Exception.Message)" }
    }
    Write-Ok "Debug-Symbole entfernt: $($pdbFiles.Count) .pdb-Datei(en)."
}

# ---------------------------------------------------------------------
# 6. Check the result
# ---------------------------------------------------------------------
$exePath = Join-Path $OutputDir 'WindowsDaSiTool.exe'
if (-not (Test-Path $exePath)) {
    Fail "Kompilierung meldete Erfolg, aber $exePath wurde nicht gefunden."
}
$sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Ok "EXE erstellt: $exePath ($sizeMb MB)"

# ---------------------------------------------------------------------
# 7. Optional signing
# ---------------------------------------------------------------------
if ($Sign) {
    Write-Step "Signiere die EXE..."

    # Find signtool (Windows SDK)
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $candidates = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'x64' } | Sort-Object FullName -Descending
        if ($candidates) { $signtool = $candidates[0] }
    }
    if (-not $signtool) {
        Write-Warn "signtool.exe nicht gefunden (Teil des Windows 10/11 SDK). Signierung uebersprungen."
    } else {
        $stArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
        if ($PfxPath) {
            if (-not (Test-Path $PfxPath)) { Fail "PFX-Datei nicht gefunden: $PfxPath" }
            $stArgs += @('/f', $PfxPath)
            if ($PfxPassword) { $stArgs += @('/p', $PfxPassword) }
        } else {
            # Pick the certificate automatically from the personal certificate store
            $stArgs += '/a'
        }
        $stArgs += $exePath

        & $signtool.Source @stArgs
        if ($LASTEXITCODE -ne 0) { Fail "Signierung fehlgeschlagen." }
        Write-Ok "EXE signiert und zeitgestempelt."
    }
}

# ---------------------------------------------------------------------
# 8. Done
# ---------------------------------------------------------------------
Write-Host ""
Write-Ok "Fertig."
Write-Host "Ausgabe: $exePath" -ForegroundColor Green
if ($Mode -eq 'framework') {
    Write-Warn "Framework-abhaengig: Zielrechner benoetigt das .NET 10 Desktop Runtime."
} else {
    Write-Host "Self-contained: laeuft auch ohne installiertes .NET." -ForegroundColor DarkGray
}
if (-not $Sign) {
    Write-Host "Tipp: Mit -Sign (und ggf. -PfxPath) signieren, um SmartScreen-/Scanner-Warnungen zu reduzieren." -ForegroundColor DarkGray
}
