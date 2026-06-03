REM CD to bat directory to avoid inadvertent deletion
cd /d "%~dp0"
REM clean previous builds
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
REM run publish build
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true
REM sign binary
REM "C:\Program Files (x86)\Windows Kits\10\tools\bin\i386\signtool.exe" sign /sha1 {yourcertsha1} /tr http://timestamp.digicert.com /td sha256 /fd sha256 bin\Release\net8.0-windows\win-x64\publish\Pub2PDF.exe