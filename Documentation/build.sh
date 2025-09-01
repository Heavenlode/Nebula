#!/bin/bash

# Build script for Nebula Documentation using DocFX

echo "Building Nebula Documentation with DocFX..."

# Check if DocFX is installed
if ! command -v docfx &> /dev/null; then
    echo "DocFX is not installed. Installing via dotnet tool..."
    dotnet tool install -g docfx
fi

# Clean previous build
echo "Cleaning previous build..."
rm -rf _site

# Build documentation
echo "Building documentation..."
docfx build docfx.json

# Check if build was successful
if [ $? -eq 0 ]; then
    echo "Documentation built successfully!"
    echo "Output available in _site directory"
    echo "To serve locally, run: docfx serve _site"
else
    echo "Build failed!"
    exit 1
fi
