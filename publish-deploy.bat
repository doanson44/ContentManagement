@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "SCRIPT_NAME=%~nx0"
set "CONFIGURATION=Release"
set "RUNTIME=win-x86"
set "ARTIFACTS=artifacts"
set "SERVER_PROJECT=src\ContentManagement.Server\ContentManagement.Server.csproj"
set "FILEAPI_PROJECT=src\ContentManagement.FileApi\ContentManagement.FileApi.csproj"
set "SERVER_OUT=%ARTIFACTS%\publish"
set "FILEAPI_OUT=%ARTIFACTS%\fileapi-publish"
set "SERVER_ZIP=%ARTIFACTS%\ContentManagement-win-x86.zip"
set "FILEAPI_ZIP=%ARTIFACTS%\ContentManagement-FileApi-win-x86.zip"

set "RUN_TESTS=1"
if /i "%~1"=="-SkipTests" set "RUN_TESTS=0"
if /i "%~1"=="--skip-tests" set "RUN_TESTS=0"
if /i "%~1"=="-h" goto :usage
if /i "%~1"=="--help" goto :usage
if /i "%~1"=="/?" goto :usage

echo ==========================================================================
echo  Publish ContentManagement deployment packages
echo  Configuration : %CONFIGURATION%
echo  Runtime       : %RUNTIME% (self-contained)
echo  Output folder : %ARTIFACTS%
echo ==========================================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] The dotnet CLI was not found on PATH.
    goto :fail
)

if "%RUN_TESTS%"=="1" (
    echo [1/6] Running unit tests ^(use -SkipTests to package without running them^)...
    dotnet test tests\ContentManagement.UnitTests\ContentManagement.UnitTests.csproj --configuration %CONFIGURATION%
    if errorlevel 1 (
        echo [ERROR] Unit tests failed. Nothing was packaged.
        goto :fail
    )
) else (
    echo [1/6] Unit tests skipped by request.
)

echo.
echo [2/6] Cleaning previous publish output...
call :removeDir "%ARTIFACTS%"
if exist "%ARTIFACTS%" (
    echo [ERROR] Could not delete the "%ARTIFACTS%" folder. Close any program that
    echo         has its files open ^(ZIP viewer, Explorer preview, antivirus scan^).
    goto :fail
)
mkdir "%ARTIFACTS%"

echo.
echo [3/6] Publishing the standalone File API...
dotnet publish "%FILEAPI_PROJECT%" --configuration %CONFIGURATION% --runtime %RUNTIME% --self-contained true --output "%FILEAPI_OUT%"
if errorlevel 1 (
    echo [ERROR] FileAPI publish failed. Nothing was packaged.
    goto :fail
)

echo.
echo [4/6] Publishing the web server ^(includes the Blazor WebAssembly client^)...
dotnet publish "%SERVER_PROJECT%" --configuration %CONFIGURATION% --runtime %RUNTIME% --self-contained true --output "%SERVER_OUT%"
if errorlevel 1 (
    echo [ERROR] Server publish failed. Nothing was packaged.
    goto :fail
)

echo.
echo [5/6] Validating publish output...
call :require "%FILEAPI_OUT%\ContentManagement.FileApi.exe" || goto :fail
call :require "%SERVER_OUT%\ContentManagement.Server.exe" || goto :fail
call :require "%SERVER_OUT%\wwwroot\index.html" || goto :fail
call :require "%SERVER_OUT%\wwwroot\_framework\blazor.webassembly.js" || goto :fail
echo   All required files are present.

call :stripLocalSettings "%SERVER_OUT%"
call :stripLocalSettings "%FILEAPI_OUT%"

echo.
echo [6/6] Creating ZIP packages...
call :zip "%FILEAPI_OUT%" "%FILEAPI_ZIP%" || goto :fail
call :zip "%SERVER_OUT%" "%SERVER_ZIP%" || goto :fail

echo.
echo ==========================================================================
echo  Publish complete. Copy these files to the deployment machine:
echo ==========================================================================
for %%F in ("%FILEAPI_ZIP%" "%SERVER_ZIP%") do echo   %%~nxF  ^(%%~zF bytes^)
echo.
echo   %CD%\%FILEAPI_ZIP%
echo   %CD%\%SERVER_ZIP%
echo.
call :pauseIfDoubleClicked
exit /b 0

:usage
echo Usage: %SCRIPT_NAME% [-SkipTests]
echo.
echo   Publishes both deployment packages as %CONFIGURATION% / %RUNTIME% / self-contained:
echo     %SERVER_ZIP%
echo     %FILEAPI_ZIP%
echo.
echo   -SkipTests   Skip the unit test gate and package the current build.
echo.
call :pauseIfDoubleClicked
exit /b 0

:fail
echo.
echo ==========================================================================
echo  PUBLISH FAILED - step above did not complete, packages were not produced.
echo ==========================================================================
call :pauseIfDoubleClicked
exit /b 1

:require
if exist "%~1" exit /b 0
echo [ERROR] Missing expected file: %~1
echo [ERROR] Publish output is incomplete, so no ZIP was created.
exit /b 1

:stripLocalSettings
for /r "%~1" %%P in (appsettings.Development.json appsettings.Local.json) do (
    if exist "%%~fP" (
        del /f /q "%%~fP"
        echo   Removed %%~nxP from %~nx1
    )
)
exit /b 0

:removeDir
call :retryDelete "rmdir /s /q" "%~1"
if exist "%~1" exit /b 1
exit /b 0

:removeFile
call :retryDelete "del /f /q" "%~1"
if exist "%~1" exit /b 1
exit /b 0

:retryDelete
for /l %%A in (1,1,6) do (
    if exist "%~2" (
        %~1 "%~2" >nul 2>nul
        rem ping is used as the delay because timeout fails when stdin is redirected
        if exist "%~2" ping -n 2 127.0.0.1 >nul
    )
)
exit /b 0

:zip
set "ZIP_SOURCE=%~1"
set "ZIP_TARGET=%~2"
if exist "%ZIP_TARGET%" call :removeFile "%ZIP_TARGET%"
if exist "%ZIP_TARGET%" (
    echo [ERROR] Cannot overwrite %~nx2 because another program has it open.
    exit /b 1
)
where tar >nul 2>nul
if errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%ZIP_SOURCE%\*' -DestinationPath '%ZIP_TARGET%' -Force"
) else (
    tar -a -c -f "%ZIP_TARGET%" -C "%ZIP_SOURCE%" *
)
if not exist "%ZIP_TARGET%" (
    echo [ERROR] Failed to create %~nx2
    exit /b 1
)
echo   Created %~nx2
exit /b 0

:pauseIfDoubleClicked
echo %cmdcmdline% | find /i "%SCRIPT_NAME%" >nul
if not errorlevel 1 pause
exit /b 0
