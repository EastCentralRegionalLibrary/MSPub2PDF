# Requires PowerShell 5+ (works fine in Windows PowerShell or PS Core)
# May need to run Set-ExecutionPolicy -Scope Process Bypass first if prevented by system policy

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Move to script directory
Set-Location -Path $PSScriptRoot

Write-Host "=== Cleaning previous builds ==="
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "bin", "obj"

# ==========================================
# 1. Google Fonts API Key
# ==========================================
if (-not $env:GOOGLE_FONTS_API_KEY) {
    Write-Host "[NOTICE] GOOGLE_FONTS_API_KEY is not defined."
    Write-Host "--------------------------------------------------"

    $inputKey = Read-Host "Enter Google Fonts API Key (or press Enter to skip)"

    if ($inputKey) {
        $env:GOOGLE_FONTS_API_KEY = $inputKey.Trim()
        Write-Host "API Key applied for this session."
    }
    else {
        Write-Warning "No key provided. Font tests will be skipped."
    }

    Write-Host "--------------------------------------------------"
}

# ==========================================
# 2. Build
# ==========================================
Write-Host "=== Running dotnet publish ==="
dotnet publish -r win-x64 -c Release `
    --self-contained true `
    -p:PublishSingleFile=true

# ==========================================
# 3. Find signtool
# ==========================================
function Find-SignTool {
    Write-Host "Locating signtool..."

    # 1. PATH
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"

    if (-not (Test-Path $kitRoot)) {
        return $null
    }

    # 2. New SDK layout (prefer x64, newest first)
    $paths = Get-ChildItem "$kitRoot\bin\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending

    if ($paths) {
        return $paths[0].FullName
    }

    # 3. x86 fallback
    $paths = Get-ChildItem "$kitRoot\bin\*\x86\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending

    if ($paths) {
        return $paths[0].FullName
    }

    # 4. Legacy layout (your case)
    $legacy = @(
        "$kitRoot\tools\bin\x64\signtool.exe",
        "$kitRoot\tools\bin\i386\signtool.exe"
    )

    foreach ($path in $legacy) {
        if (Test-Path $path) {
            return $path
        }
    }

    return $null
}

$signtool = Find-SignTool

if (-not $signtool) {
    Write-Error "signtool.exe not found. Install Windows SDK."
}

Write-Host "Using signtool:"
Write-Host $signtool

# ==========================================
# 4. SHA-1 Prompt
# ==========================================
if (-not $env:SIGNING_SHA1) {
    Write-Host "[NOTICE] SIGNING_SHA1 not defined."
    Write-Host "--------------------------------------------------"

    $inputSha = Read-Host "Enter certificate SHA-1 thumbprint (or press Enter to skip)"

    if ($inputSha) {
        # Remove spaces
        $env:SIGNING_SHA1 = ($inputSha -replace " ", "")
        Write-Host "Thumbprint applied for this session."
    }
    else {
        Write-Warning "No SHA-1 provided. Signing will be skipped."
    }

    Write-Host "--------------------------------------------------"
}

# ==========================================
# 5. Signing
# ==========================================
if ($env:SIGNING_SHA1) {
    Write-Host "=== Signing binary ==="

    $exePath = "bin\Release\net8.0-windows\win-x64\publish\Pub2PDF.exe"

    if (-not (Test-Path $exePath)) {
        Write-Error "Build output not found: $exePath"
    }

    & $signtool sign `
        /sha1 $env:SIGNING_SHA1 `
        /tr http://timestamp.digicert.com `
        /td sha256 `
        /fd sha256 `
        $exePath

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Binary successfully signed."
    }
    else {
        Write-Error "Code signing failed."
    }
}
else {
    Write-Host "Skipping code signing step."
}
