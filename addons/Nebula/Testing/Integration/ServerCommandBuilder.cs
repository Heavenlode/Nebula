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

    /// <summary>
    /// Creates a user input command for the given input command and value.
    /// </summary>
    /// <param name="inputCommand">The input command to send</param>
    /// <param name="inputValue">The value to send for the input command</param>
    public InputCommand Input(byte inputCommand, string inputValue) => new InputCommand(_server, _client, inputCommand, inputValue);

    public CustomCommand Custom(string title) => new CustomCommand(_server, _client, title);
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

public class CustomCommand
{
    private readonly GodotProcess _server;
    private readonly GodotProcess _client;
    public readonly string _title;

    public CustomCommand(GodotProcess server, GodotProcess client, string title)
    {
        _server = server;
        _client = client;
        _title = title;
    }

    public async Task SendServer() {
        _server.SendCommand(_title);
    }

    public async Task SendClient() {
        _client.SendCommand(_title);
    }
}

/// <summary>
/// Represents a spawn command with fluent verification options.
/// </summary>
public class InputCommand
{
    private readonly GodotProcess _server;
    private readonly GodotProcess _client;
    private readonly byte _inputCommand;
    private readonly string _inputValue;

    public InputCommand(GodotProcess server, GodotProcess client, byte inputCommand, string inputValue)
    {
        _server = server;
        _client = client;
        _inputCommand = inputCommand;
        _inputValue = inputValue;
    }

    /// <summary>
    /// Sends the spawn command and verifies it was exported by the server.
    /// </summary>
    public async Task VerifyServer()
    {
        _client.SendCommand($"Input:{_inputCommand}:{_inputValue}");
        await _server.WaitForDebugEvent("Input", $"{_inputCommand}:{_inputValue}");
    }

    /// <summary>
    /// Sends the spawn command and verifies it was imported by the client.
    /// </summary>
    public async Task VerifyClient()
    {
        _client.SendCommand($"Input:{_inputCommand}:{_inputValue}");
        await _client.WaitForDebugEvent("Input", $"{_inputCommand}:{_inputValue}");
    }

    /// <summary>
    /// Sends the spawn command and verifies it was both exported by server and imported by client.
    /// </summary>
    public async Task VerifyBoth()
    {
        _client.SendCommand($"Input:{_inputCommand}:{_inputValue}");
        await Task.WhenAll(
            _server.WaitForDebugEvent("Input", $"{_inputCommand}:{_inputValue}"),
            _client.WaitForDebugEvent("Input", $"{_inputCommand}:{_inputValue}")
        );
    }
}
