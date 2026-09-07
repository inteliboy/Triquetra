@echo off
REM ============================================================
REM  build.cmd - compiles src\*.cs into triquetra.exe with the
REM  .NET Framework 4.8 csc.exe (classic C# 5 compiler).
REM
REM  version.txt holds MAJOR.MINOR.BUILD.REVISION; REVISION is
REM  bumped on every successful compile and written into
REM  src\AssemblyVersion.generated.cs (a build output, not a
REM  hand-edited file, but still listed in %SOURCES% so csc.exe
REM  picks it up). MainForm reads the version back at runtime via
REM  Assembly.GetExecutingAssembly().GetName().Version - no more
REM  regex-patching a version string into the source.
REM
REM  Produces triquetra.exe / .md5 / .sha256 in the project root.
REM  Signs with signtool if available (best-effort). Does NOT
REM  deploy anywhere - that step was removed from the old
REM  build.ps1 on request.
REM ============================================================
setlocal enabledelayedexpansion

set "ASSETS=assets"
set "SRC=src"
set "EXENAME=triquetra.exe"
set "VERFILE=version.txt"
set "VERSRC=%SRC%\AssemblyVersion.generated.cs"

set "SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
set "CERTNAME=SMCE"
set "TIMESTAMPURL=http://timestamp.digicert.com"

if not exist "%ASSETS%\triquetra.ico" (
    echo(
    echo ERROR: missing required file: %ASSETS%\triquetra.ico
    echo(
    exit /b 1
)

REM --- Version bump (revision only) ---
if not exist "%VERFILE%" (
    echo 2.0.6.0>"%VERFILE%"
) else (
    set /p CURVER=<"%VERFILE%"
    for /f "tokens=1-4 delims=." %%a in ("!CURVER!") do (
        set VMAJOR=%%a
        set VMINOR=%%b
        set VBUILD=%%c
        set VREV=%%d
    )
    set /a VREV=!VREV!+1
    >"%VERFILE%" echo !VMAJOR!.!VMINOR!.!VBUILD!.!VREV!
)
set /p APPVERSION=<"%VERFILE%"

(
    echo using System.Reflection;
    echo [assembly: AssemblyVersion^("%APPVERSION%"^)]
    echo [assembly: AssemblyFileVersion^("%APPVERSION%"^)]
)>"%VERSRC%"
echo Version: %APPVERSION%

REM --- Locate csc.exe (prefer 64-bit framework) ---
set "FXDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FXDIR%\csc.exe" set "FXDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FXDIR%\csc.exe"

if not exist "%CSC%" (
    echo(
    echo ERROR: Could not find csc.exe under %%WINDIR%%\Microsoft.NET.
    echo        The .NET Framework 4.8 runtime must be installed.
    echo(
    exit /b 1
)
echo Using compiler: "%CSC%"

REM --- UAC manifest (requireAdministrator) ---
REM Embedding this at compile time makes Windows show the UAC prompt
REM before the process even starts, cleaner than the runtime
REM elevation fallback in Program.cs alone.
set "MANIFEST=%TEMP%\triquetra_build.manifest"
(
    echo ^<?xml version="1.0" encoding="UTF-8" standalone="yes"?^>
    echo ^<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"^>
    echo   ^<assemblyIdentity version="1.0.0.0" processorArchitecture="amd64"
    echo     name="Triquetra.Updater" type="win32"/^>
    echo   ^<trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"^>
    echo     ^<security^>
    echo       ^<requestedPrivileges^>
    echo         ^<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/^>
    echo       ^</requestedPrivileges^>
    echo     ^</security^>
    echo   ^</trustInfo^>
    echo   ^<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"^>
    echo     ^<application^>
    echo       ^<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/^>
    echo     ^</application^>
    echo   ^</compatibility^>
    echo ^</assembly^>
)>"%MANIFEST%"

REM --- Compile ---
echo Compiling %EXENAME% v%APPVERSION% ...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
    /win32icon:"%ASSETS%\triquetra.ico" ^
    /win32manifest:"%MANIFEST%" ^
    /out:"%EXENAME%" ^
    "%SRC%\*.cs"
set "CSCRC=%ERRORLEVEL%"
del /f /q "%MANIFEST%" >nul 2>&1

if not "%CSCRC%"=="0" (
    echo(
    echo ***** BUILD FAILED *****
    exit /b 1
)
if not exist "%EXENAME%" (
    echo ERROR: %EXENAME% not produced. Check csc output above.
    exit /b 1
)
echo Compiled: %EXENAME%

REM --- Sign (best-effort) ---
if exist "%SIGNTOOL%" (
    echo Signing %EXENAME% ...
    "%SIGNTOOL%" sign /n %CERTNAME% /fd SHA256 /tr %TIMESTAMPURL% /td SHA256 "%EXENAME%"
    if not "!ERRORLEVEL!"=="0" (
        echo(
        echo ***** SIGNING FAILED *****
        exit /b 1
    )
    echo Signed successfully.
) else (
    echo signtool.exe not found at "%SIGNTOOL%" - skipping signing.
)

REM --- Checksums (MD5 + SHA256) ---
REM certutil prints a header line ("MD5 hash of file ...:") and a footer
REM line ("CertUtil: -hashfile command completed successfully.") around
REM the hex hash - both contain the substring "hash", so filtering them
REM out with findstr /v leaves exactly the hash line (no /f skip needed,
REM since skip= would then skip the one line that's left).
for /f "tokens=1" %%h in ('certutil -hashfile "%EXENAME%" MD5 ^| findstr /v /i "hash"') do (
    if not defined MD5HASH set "MD5HASH=%%h"
)
for /f "tokens=1" %%h in ('certutil -hashfile "%EXENAME%" SHA256 ^| findstr /v /i "hash"') do (
    if not defined SHA256HASH set "SHA256HASH=%%h"
)
>"%EXENAME%.md5" echo !MD5HASH! *%EXENAME%
>"%EXENAME%.sha256" echo !SHA256HASH! *%EXENAME%
echo MD5:    !MD5HASH!
echo SHA256: !SHA256HASH!

echo(
echo ========================================
echo   Build complete  -  v%APPVERSION%
echo ========================================
endlocal
