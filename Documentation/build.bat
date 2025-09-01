@echo off
REM Build script for Nebula Documentation using DocFX

echo Building Nebula Documentation with DocFX...

REM Check if DocFX is installed
where docfx >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo DocFX is not installed. Installing via dotnet tool...
    dotnet tool install -g docfx
)

REM Clean previous build
echo Cleaning previous build...
if exist _site rmdir /s /q _site

REM Build documentation
echo Building documentation...
docfx build docfx.json

REM Check if build was successful
if %ERRORLEVEL% EQU 0 (
    echo Documentation built successfully!
    echo Output available in _site directory
    echo To serve locally, run: docfx serve _site
) else (
    echo Build failed!
    exit /b 1
)
