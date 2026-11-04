@echo off
setlocal
echo ============================================
echo  KnightLock - release build
echo ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] The .NET 8 SDK was not found on PATH.
    echo         Download it from https://dotnet.microsoft.com/download/dotnet/8.0
    goto :end
)

echo [1/5] Restoring and building...
dotnet build "%~dp0src\KnightLock\KnightLock.csproj" -c Release
if errorlevel 1 goto :failed

echo [2/5] Running the vault engine test suite...
dotnet run -c Release --project "%~dp0tests\KnightLock.CoreTests"
if errorlevel 1 goto :failed

echo [3/5] Publishing the self-contained single-file exe...
dotnet publish "%~dp0src\KnightLock\KnightLock.csproj" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:DebugType=none ^
    -o "%~dp0release"
if errorlevel 1 goto :failed

echo [4/5] Fixing the version resource (OriginalFilename -> KnightLock.exe)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\fix-original-filename.ps1" -Exe "%~dp0release\KnightLock.exe"

echo [5/5] Signing...
if exist "%~dp0cert\KnightWin.pfx" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\sign.ps1" -Pfx "%~dp0cert\KnightWin.pfx"
) else (
    echo       skipped - put the private key at cert\KnightWin.pfx to sign, or sign later
    echo       with: tools\sign.ps1 -Pfx ^<path^>   ^(signing must be the last step^)
)

echo.
echo Done. Run: %~dp0release\KnightLock.exe
goto :end

:failed
echo.
echo [ERROR] Build failed - see the messages above.

:end
endlocal
pause
