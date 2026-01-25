#!/bin/bash

# Build script for Nebula Documentation using DocFX

set -e  # Exit on any error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

echo "=========================================="
echo "  Nebula Documentation Builder"
echo "=========================================="

# Check if DocFX is installed
if ! command -v docfx &> /dev/null; then
    echo "DocFX is not installed. Installing via dotnet tool..."
    dotnet tool install -g docfx
fi

# Parse arguments
SKIP_BUILD=false
SERVE=false
for arg in "$@"; do
    case $arg in
        --skip-build) SKIP_BUILD=true ;;
        --serve) SERVE=true ;;
        -h|--help)
            echo "Usage: ./build.sh [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --skip-build   Skip rebuilding Nebula.dll (use existing)"
            echo "  --serve        Start local server after build"
            echo "  -h, --help     Show this help message"
            exit 0
            ;;
    esac
done

# Step 1: Build the Nebula project (to get fresh DLL + XML docs)
if [ "$SKIP_BUILD" = false ]; then
    echo ""
    echo "[1/3] Building Nebula project..."
    cd "$PROJECT_ROOT"
    dotnet build Nebula.csproj -c Debug
    cd "$SCRIPT_DIR"
else
    echo ""
    echo "[1/3] Skipping Nebula build (--skip-build)"
fi

# Step 2: Generate API metadata from DLL
echo ""
echo "[2/3] Generating API documentation from Nebula.dll..."
docfx metadata docfx.json

# Step 3: Build the documentation site
echo ""
echo "[3/3] Building documentation site..."
rm -rf _site
docfx build docfx.json

echo ""
echo "=========================================="
echo "  Build complete!"
echo "=========================================="
echo "Output: $SCRIPT_DIR/_site"

# Optionally serve
if [ "$SERVE" = true ]; then
    echo ""
    echo "Starting local server at http://localhost:8080 ..."
    docfx serve _site
else
    echo ""
    echo "To preview locally: ./build.sh --serve"
    echo "Or run: docfx serve _site"
fi
