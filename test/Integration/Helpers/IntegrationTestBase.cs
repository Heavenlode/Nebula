#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace NebulaTests.Integration;

/// <summary>
/// Configuration for starting a Godot server instance.
/// </summary>
public class ServerConfig
{
    public string InitialWorldScene { get; set; } = "res://Integration/Helpers/empty_scene.tscn";
    public string? WorldId { get; set; }
    public Dictionary<string, string> ExtraArgs { get; set; } = new();
}

/// <summary>
/// Configuration for starting a Godot client instance.
/// </summary>
public class ClientConfig
{
    public Dictionary<string, string> ExtraArgs { get; set; } = new();
}

/// <summary>
/// Base class for integration tests that spawn multiple Godot instances.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    /// <summary>
    /// Default timeout for waiting on process output.
    /// </summary>
    protected virtual TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Path to the test project directory (where project.godot lives).
    /// </summary>
    protected virtual string TestProjectPath => GetTestProjectPath();

    /// <summary>
    /// Starts a headless Godot server instance using ServerClientConnector.
    /// </summary>
    /// <param name="config">Server configuration (optional)</param>
    /// <returns>A GodotProcess instance</returns>
    protected GodotProcess StartServer(ServerConfig? config = null)
    {
        config ??= new ServerConfig();
        
        var args = new List<string>
        {
            "--headless",
            "--server",
            $"--initialWorldScene={config.InitialWorldScene}"
        };

        if (!string.IsNullOrEmpty(config.WorldId))
        {
            args.Add($"--worldId={config.WorldId}");
        }

        foreach (var kvp in config.ExtraArgs)
        {
            args.Add($"--{kvp.Key}={kvp.Value}");
        }

        return StartGodot(args.ToArray());
    }

    /// <summary>
    /// Starts a headless Godot client instance using ServerClientConnector.
    /// </summary>
    /// <param name="config">Client configuration (optional)</param>
    /// <returns>A GodotProcess instance</returns>
    protected GodotProcess StartClient(ClientConfig? config = null)
    {
        config ??= new ClientConfig();

        var args = new List<string>
        {
            "--headless"
        };

        foreach (var kvp in config.ExtraArgs)
        {
            args.Add($"--{kvp.Key}={kvp.Value}");
        }

        return StartGodot(args.ToArray());
    }

    /// <summary>
    /// Starts a Godot process with the given arguments.
    /// Automatically adds --path to point to the test project.
    /// </summary>
    /// <param name="args">Additional arguments for Godot</param>
    /// <returns>A GodotProcess instance</returns>
    protected GodotProcess StartGodot(params string[] args)
    {
        var fullArgs = new string[args.Length + 2];
        fullArgs[0] = "--path";
        fullArgs[1] = TestProjectPath;
        Array.Copy(args, 0, fullArgs, 2, args.Length);

        return GodotProcess.Start(fullArgs, TestProjectPath);
    }

    /// <summary>
    /// Starts a headless Godot process with the given scene.
    /// For custom scenes outside of the standard ServerClientConnector flow.
    /// </summary>
    /// <param name="scenePath">Resource path to the scene</param>
    /// <returns>A GodotProcess instance</returns>
    protected GodotProcess StartHeadless(string scenePath)
    {
        return StartGodot("--headless", scenePath);
    }

    private static string GetTestProjectPath()
    {
        // Walk up from the current directory to find project.godot
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

        // Fallback: assume we're in test/ directory relative to workspace
        var workspaceRoot = Environment.CurrentDirectory;
        var testPath = Path.Combine(workspaceRoot, "test");
        if (Directory.Exists(testPath) && File.Exists(Path.Combine(testPath, "project.godot")))
        {
            return testPath;
        }

        throw new InvalidOperationException(
            "Could not find project.godot. Make sure you're running tests from the correct directory.");
    }

    public virtual void Dispose()
    {
        // Override in derived classes if cleanup is needed
    }
}


