#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Nebula.Testing;

/// <summary>
/// xUnit fixture that ensures the Nebula protocol registry is built before any tests run.
/// This fixture spawns a headless Godot instance with the ProtocolBuilder scene,
/// which builds and saves the protocol resource file.
/// </summary>
public class NebulaTestFixture : IDisposable
{
    private static readonly object _buildLock = new();
    private static bool _protocolBuilt = false;

    public NebulaTestFixture()
    {
        EnsureProtocolBuilt();
    }

    private void EnsureProtocolBuilt()
    {
        lock (_buildLock)
        {
            if (_protocolBuilt)
            {
                return;
            }

            BuildProtocol();
            _protocolBuilt = true;
        }
    }

    private void BuildProtocol()
    {
        var godotBin = Environment.GetEnvironmentVariable("GODOT");
        if (string.IsNullOrEmpty(godotBin))
        {
            throw new InvalidOperationException(
                "GODOT environment variable is not set. " +
                "Set it to the path of your Godot executable.");
        }

        var testProjectPath = FindTestProjectPath();
        if (testProjectPath == null)
        {
            throw new InvalidOperationException(
                "Could not find test project path (project.godot). " +
                "Make sure you're running tests from the correct directory.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = godotBin,
            Arguments = $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/ProtocolBuilder/ProtocolBuilder.tscn",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = testProjectPath
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Protocol build failed with exit code {process.ExitCode}.\n" +
                $"Output:\n{output}\n" +
                $"Stderr:\n{stderr}");
        }

        if (!output.Contains("[PROTOCOL_BUILD_SUCCESS]"))
        {
            throw new InvalidOperationException(
                $"Protocol build did not report success.\n" +
                $"Output:\n{output}\n" +
                $"Stderr:\n{stderr}");
        }
    }

    private static string? FindTestProjectPath()
    {
        // Try to find from base directory
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var projectFile = Path.Combine(dir, "project.godot");
            if (File.Exists(projectFile))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback - try current directory
        var workspaceRoot = Environment.CurrentDirectory;
        var testPath = Path.Combine(workspaceRoot, "test");
        if (Directory.Exists(testPath) && File.Exists(Path.Combine(testPath, "project.godot")))
        {
            return testPath;
        }

        return null;
    }

    public void Dispose()
    {
        // Nothing to clean up - the protocol file persists for other test runs
    }
}

/// <summary>
/// Collection definition for Nebula tests.
/// All test classes using [Collection("Nebula")] will share the same fixture instance,
/// ensuring the protocol is only built once per test run.
/// </summary>
[CollectionDefinition("Nebula")]
public class NebulaTestCollection : ICollectionFixture<NebulaTestFixture>
{
}

