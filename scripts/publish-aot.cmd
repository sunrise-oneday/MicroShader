@echo off
rem ============================================================================
rem  Module 12: Native AOT publish + pre-delivery smoke gates (fail-fast).
rem  Same three gates as scripts/publish-aot.ps1 (pwsh) and scripts/publish-aot.sh (bash).
rem
rem  Gates:
rem    1. PDBs are archived into symbols\ (never deleted). They carry the line
rem       mapping for Native AOT crash stacks (IlcGenerateStackTraceData is on).
rem    2. dxcompiler.dll / dxil.dll must sit next to the exe (paired delivery
rem       layout, never a system-wide version search).
rem    3. "exe --version" must really load DXC and print its version.
rem
rem  Usage:
rem    scripts\publish-aot.cmd                  full publish + gates
rem    set SKIP_PUBLISH=1 && scripts\publish-aot.cmd   re-run gates only
rem    scripts\publish-aot.cmd <dxcSourceDir>   custom dxc source directory
rem ============================================================================
setlocal

set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\src\MicroShader.Server\MicroShader.Server.csproj"
set "STAGE=%ROOT%\dist\stg\bin"
set "DXCSRC=%~1"
if "%DXCSRC%"=="" set "DXCSRC=%ROOT%\probes\dxc-aot\native"

if /I "%SKIP_PUBLISH%"=="1" goto gates

echo == 1/5 clean staging (avoid stale PDB / DLL left by incremental publish)
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%" 2>nul

echo == 2/5 native AOT publish (-r win-x64 -p:PublishAot=true)
dotnet publish "%PROJECT%" -c Release -r win-x64 -p:PublishAot=true -o "%STAGE%" --nologo
if errorlevel 1 (
  echo AOT publish FAILED
  exit /b 1
)

:gates
if not exist "%STAGE%" mkdir "%STAGE%" 2>nul

echo == 3/5 gate 1: archive PDBs into symbols\ (keep crash line mapping)
if exist "%STAGE%\*.pdb" (
  if not exist "%STAGE%\symbols" mkdir "%STAGE%\symbols"
  move /y "%STAGE%\*.pdb" "%STAGE%\symbols\" >nul
  if errorlevel 1 (
    echo PDB archive FAILED
    exit /b 1
  )
)

echo == 4/5 gate 2: stage paired native dependencies next to the exe
for %%F in (dxcompiler.dll dxil.dll) do (
  if not exist "%DXCSRC%\%%F" (
    echo MISSING native dependency: %DXCSRC%\%%F
    exit /b 1
  )
  copy /y "%DXCSRC%\%%F" "%STAGE%\%%F" >nul
)
if not exist "%STAGE%\UnityShaderLsp.exe" (
  echo MISSING artifact: UnityShaderLsp.exe
  exit /b 1
)

echo == 5/5 gate 3: AOT exe self-check (really loads DXC)
"%STAGE%\UnityShaderLsp.exe" --version
if errorlevel 1 (
  echo AOT smoke FAILED: DXC could not be loaded
  exit /b 1
)

echo.
echo Delivery staging ready: %STAGE%
dir /b "%STAGE%"
endlocal
exit /b 0
