@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM CD to bat directory to avoid inadvertent deletion
cd /d "%~dp0"

REM Clean previous builds
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"

REM ==========================================
REM 1. Check/Prompt for Google Fonts API Key
REM ==========================================
if not defined GOOGLE_FONTS_API_KEY (
    echo [NOTICE] GOOGLE_FONTS_API_KEY is not defined on this machine.
    echo -----------------------------------------------------------------
    set "INPUT_KEY="
    set /p "INPUT_KEY=Please enter your Google Fonts API Key (or press Enter to skip): "

    if defined INPUT_KEY (
        REM Optional: trim spaces
        set "GOOGLE_FONTS_API_KEY=!INPUT_KEY: =!"
        echo API Key applied for this session.
    ) else (
        echo [WARNING] No key provided. Font download integration tests will be skipped.
    )
    echo -----------------------------------------------------------------
)

REM ==========================================
REM 2. Run publish build
REM ==========================================
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true

REM ==========================================
REM 3. Locate signtool.exe (PATH + SDK scan)
REM ==========================================
set "SIGNTOOL="

REM --- 1. Try PATH first ---
for /f "delims=" %%I in ('where signtool 2^>nul') do (
    set "SIGNTOOL=%%I"
    goto :found_signtool
)

REM --- 2. Search Windows Kits (new layout, prefer x64) ---
for /f "delims=" %%I in ('dir /b /s /o:-n "%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\signtool.exe" 2^>nul') do (
    set "SIGNTOOL=%%I"
    goto :found_signtool
)

REM --- 3. Fallback to x86 (new layout) ---
for /f "delims=" %%I in ('dir /b /s /o:-n "%ProgramFiles(x86)%\Windows Kits\10\bin\*\x86\signtool.exe" 2^>nul') do (
    set "SIGNTOOL=%%I"
    goto :found_signtool
)

REM --- 4. Legacy SDK layout fallback (your case) ---
for %%I in (
    "%ProgramFiles(x86)%\Windows Kits\10\tools\bin\i386\signtool.exe"
    "%ProgramFiles(x86)%\Windows Kits\10\tools\bin\x64\signtool.exe"
) do (
    if exist "%%~I" (
        set "SIGNTOOL=%%~I"
        goto :found_signtool
    )
)

:found_signtool

if not defined SIGNTOOL (
    echo [ERROR] signtool.exe not found. Please install Windows SDK.
    echo Checked PATH and Windows Kits locations.
    goto :EOF
)

echo Using signtool:
echo !SIGNTOOL!

REM ==========================================
REM 4. Check/Prompt for Certificate SHA-1
REM ==========================================
if not defined SIGNING_SHA1 (
    echo [NOTICE] SIGNING_SHA1 is not defined on this machine.
    echo -----------------------------------------------------------------
    set "INPUT_SHA1="
    set /p "INPUT_SHA1=Please enter your Cert SHA-1 Thumbprint (or press Enter to skip signing): "

    if defined INPUT_SHA1 (
        REM Remove spaces (copy/paste fix)
        set "SIGNING_SHA1=!INPUT_SHA1: =!"
        echo SHA-1 Thumbprint applied for this session.
    ) else (
        echo [WARNING] No SHA-1 provided. Code signing step will be skipped.
    )
    echo -----------------------------------------------------------------
)

REM ==========================================
REM 5. Conditional Code Signing Step
REM ==========================================
if defined SIGNING_SHA1 (
    echo Signing binary...

    "!SIGNTOOL!" sign ^
        /sha1 "!SIGNING_SHA1!" ^
        /tr http://timestamp.digicert.com ^
        /td sha256 ^
        /fd sha256 ^
        "bin\Release\net8.0-windows\win-x64\publish\Pub2PDF.exe"

    if !ERRORLEVEL! EQU 0 (
        echo Binary successfully signed.
    ) else (
        echo [ERROR] Code signing failed. Check your thumbprint or certificate store.
    )
) else (
    echo Skipping code signing step.
)

endlocal