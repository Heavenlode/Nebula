#nullable enable
using System;
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

    /// <summary>Generous: a cold build imports the whole test project first.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

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
        var testProjectPath = HeadlessGodot.FindTestProjectPath();
        if (testProjectPath == null)
        {
            throw new InvalidOperationException(
                "Could not find test project path (project.godot). " +
                "Make sure you're running tests from the correct directory.");
        }

        var result = HeadlessGodot.Run(
            $"--path \"{testProjectPath}\" --headless res://addons/Nebula/Testing/ProtocolBuilder/ProtocolBuilder.tscn",
            BuildTimeout,
            testProjectPath);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Protocol build failed with exit code {result.ExitCode}.\n" +
                $"Output:\n{result.StandardOutput}\n" +
                $"Stderr:\n{result.StandardError}");
        }

        if (!result.StandardOutput.Contains("[PROTOCOL_BUILD_SUCCESS]"))
        {
            throw new InvalidOperationException(
                $"Protocol build did not report success.\n" +
                $"Output:\n{result.StandardOutput}\n" +
                $"Stderr:\n{result.StandardError}");
        }
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
