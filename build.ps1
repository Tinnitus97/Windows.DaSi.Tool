#Requires -Version 5.1
<#
======================================================================
 Build-Skript fuer das Windows DaSi Tool (Avalonia / .NET 10)
----------------------------------------------------------------------
 Erzeugt eine native Windows-EXE aus dem C#/Avalonia-Projekt.

 AUFRUF (normale PowerShell, KEINE Admin-Rechte noetig):

   .\build.ps1                          # Standard: self-contained SingleFile-EXE
   .\build.ps1 -Mode framework          # kleine EXE, benoetigt .NET 10 Runtime
   .\build.ps1 -Mode selfcontained      # explizit self-contained (= Standard)
   .\build.ps1 -Clean                   # vorher bin/ und obj/ loeschen
   .\build.ps1 -Sign -PfxPath cert.pfx  # nach dem Build signieren (PFX)
   .\build.ps1 -Sign                     # signieren mit Zertifikat aus dem Store

 Ergebnis liegt danach unter:  .\publish\WindowsDaSiTool.exe
======================================================================
#>
[CmdletBinding()]
param(
    # Publish-Variante:
    #   selfcontained = eigenstaendige EXE inkl. .NET (groesser, laeuft ueberall)
    #   framework     = kleine EXE, benoetigt installiertes .NET 10 Desktop Runtime
    [ValidateSet('selfcontained', 'framework')]
    [string]$Mode = 'selfcontained',

    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish'),

    [switch]$Clean,

    # Signieren (optional)
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
# 1. Projektdatei finden
# ---------------------------------------------------------------------
$projectFile = Get-ChildItem -Path $PSScriptRoot -Filter '*.csproj' -File | Select-Object -First 1
if (-not $projectFile) {
    Fail "Keine .csproj-Datei neben build.ps1 gefunden. Skript bitte in den Projektordner legen."
}
Write-Step "Projekt: $($projectFile.Name)"

# ---------------------------------------------------------------------
# 2. .NET SDK pruefen (mind. 10.0)
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
$has10 = $sdks | Where-Object { $_ -match '^\s*10\.' }
if (-not $has10) {
    Write-Warn "Installierte SDKs:"
    $sdks | ForEach-Object { Write-Host "    $_" }
    Fail ".NET 10 SDK nicht gefunden. Bitte .NET 10 SDK installieren: https://dotnet.microsoft.com/download/dotnet/10.0"
}
Write-Ok "SDK vorhanden: $(($has10 | Select-Object -First 1).Trim())"

# ---------------------------------------------------------------------
# 3b. Paketquellen pruefen (Ursache von NU1100 = keine nuget.org-Quelle)
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
# 3. Optional aufraeumen
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
# ---------------------------------------------------------------------
Write-Step "Stelle NuGet-Pakete wieder her (restore)..."
& dotnet restore $projectFile.FullName
if ($LASTEXITCODE -ne 0) { Fail "dotnet restore fehlgeschlagen." }
Write-Ok "Restore abgeschlossen."

# ---------------------------------------------------------------------
# 5. Publish
# ---------------------------------------------------------------------
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
# Kompression der eingebetteten Laufzeit lohnt nur im self-contained-Modus
# (verkleinert die EXE deutlich; erster Start dauert minimal laenger).
if ($selfContained) {
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
}

Write-Step "Kompiliere ($Mode, $Runtime, $Configuration)..."
Write-Host "    dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish fehlgeschlagen." }

# ---------------------------------------------------------------------
# 5b. Aufraeumen: native Debug-Symbole (.pdb) entfernen
#     SkiaSharp/HarfBuzzSharp liefern libSkiaSharp.pdb und
#     libHarfBuzzSharp.pdb mit. Diese werden zur Laufzeit nicht benoetigt
#     und lassen sich ueber die .csproj nicht zuverlaessig unterdruecken,
#     daher werden sie hier nach dem Publish geloescht.
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
# 6. Ergebnis pruefen
# ---------------------------------------------------------------------
$exePath = Join-Path $OutputDir 'WindowsDaSiTool.exe'
if (-not (Test-Path $exePath)) {
    Fail "Kompilierung meldete Erfolg, aber $exePath wurde nicht gefunden."
}
$sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Ok "EXE erstellt: $exePath ($sizeMb MB)"

# ---------------------------------------------------------------------
# 7. Optional signieren
# ---------------------------------------------------------------------
if ($Sign) {
    Write-Step "Signiere die EXE..."

    # signtool suchen (Windows SDK)
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
            # Zertifikat automatisch aus dem persoenlichen Zertifikatspeicher waehlen
            $stArgs += '/a'
        }
        $stArgs += $exePath

        & $signtool.Source @stArgs
        if ($LASTEXITCODE -ne 0) { Fail "Signierung fehlgeschlagen." }
        Write-Ok "EXE signiert und zeitgestempelt."
    }
}

# ---------------------------------------------------------------------
# 8. Abschluss
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
