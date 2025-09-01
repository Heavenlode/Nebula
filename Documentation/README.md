# Nebula Documentation

This directory contains the documentation for the Nebula networking framework, built with DocFX.

## Building the Documentation

### Prerequisites

- .NET SDK (for DocFX)
- DocFX tool (will be installed automatically if missing)

### Build Commands

**Linux/macOS:**
```bash
./build.sh
```

**Windows:**
```cmd
build.bat
```

**Manual build:**
```bash
# Install DocFX if not already installed
dotnet tool install -g docfx

# Build the documentation
docfx build docfx.json
```

### Serving Locally

To preview the documentation locally:

```bash
docfx serve _site
```

Then open http://localhost:8080 in your browser.

## Documentation Structure

- `index.md` - Main landing page
- `getting-started/` - Getting started guides
- `tutorials/` - Step-by-step tutorials
- `api/` - API reference (auto-generated from source code)
- `images/` - Documentation images and assets
- `docfx.json` - DocFX configuration
- `toc.yml` - Table of contents structure

## Adding New Content

1. Create markdown files in the appropriate directory
2. Update `toc.yml` to include new pages in the navigation
3. Rebuild the documentation

## Configuration

The `docfx.json` file contains all the build configuration. Key settings:

- **Content sources**: Markdown files and C# project files
- **Output directory**: `_site`
- **Template**: Modern template with responsive design
- **API generation**: Automatically generates API docs from `../Nebula.csproj`

## Migration from SHFB

This documentation was migrated from Sandcastle Help File Builder (SHFB) to DocFX for:

- Better markdown support
- Modern responsive design
- Easier maintenance
- Better integration with .NET tooling
- Improved performance
