#nullable enable
using System.Threading.Tasks;

namespace Nebula.Testing.Integration;

/// <summary>
/// Fluent builder for server commands with type-safe verification.
/// </summary>
public class ServerCommandBuilder
{
    private readonly GodotProcess _server;
    private readonly GodotProcess _client;

    public ServerCommandBuilder(GodotProcess server, GodotProcess client)
    {
        _server = server;
        _client = client;
    }

    /// <summary>
    /// Creates a spawn command for the given scene path.
    /// </summary>
    /// <param name="scenePath">The resource path to spawn (e.g., "res://Player.tscn")</param>
    public SpawnCommand Spawn(string scenePath) => new SpawnCommand(_server, _client, scenePath);
}

/// <summary>
/// Represents a spawn command with fluent verification options.
/// </summary>
public class SpawnCommand
{
    private readonly GodotProcess _server;
    private readonly GodotProcess _client;
    private readonly string _scenePath;

    public SpawnCommand(GodotProcess server, GodotProcess client, string scenePath)
    {
        _server = server;
        _client = client;
        _scenePath = scenePath;
    }

    /// <summary>
    /// Sends the spawn command and verifies it was exported by the server.
    /// </summary>
    public async Task VerifyServer()
    {
        _server.SendCommand($"spawn:{_scenePath}");
        await _server.WaitForDebugEvent("Spawn", $"Exported:{_scenePath}");
    }

    /// <summary>
    /// Sends the spawn command and verifies it was imported by the client.
    /// </summary>
    public async Task VerifyClient()
    {
        _server.SendCommand($"spawn:{_scenePath}");
        await _client.WaitForDebugEvent("Spawn", $"Imported:{_scenePath}");
    }

    /// <summary>
    /// Sends the spawn command and verifies it was both exported by server and imported by client.
    /// </summary>
    public async Task VerifyBoth()
    {
        _server.SendCommand($"spawn:{_scenePath}");
        await Task.WhenAll(
            _server.WaitForDebugEvent("Spawn", $"Exported:{_scenePath}"),
            _client.WaitForDebugEvent("Spawn", $"Imported:{_scenePath}")
        );
    }
}


