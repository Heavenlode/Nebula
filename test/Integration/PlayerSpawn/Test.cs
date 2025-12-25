namespace NebulaTests.Integration.PlayerSpawn;

using System.Threading.Tasks;
using Nebula.Testing.Integration;
using Xunit;

/// <summary>
/// Fixture that provides a shared server and client instance for all tests.
/// </summary>
public class BasicIntegrationFixture : IntegrationTestBase, IAsyncLifetime
{
    private const int ServerDebugPort = 17878;
    private const int ClientDebugPort = 17879;

    public GodotProcess Server { get; private set; } = null!;
    public GodotProcess Client { get; private set; } = null!;
    public ServerCommandBuilder Commands { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = StartServer(new ServerConfig
        {
            InitialWorldScene = "res://Integration/PlayerSpawn/Scene.tscn",
            DebugPort = ServerDebugPort
        });
        Client = StartClient(new ClientConfig
        {
            DebugPort = ClientDebugPort
        });

        await Server.WaitForOutput("Server ready");
        await Client.WaitForOutput("Connected to server");
        await Client.WaitForOutput("Scene _WorldReady");

        // Connect to debug ports
        await Server.ConnectDebug(ServerDebugPort);
        await Client.ConnectDebug(ClientDebugPort);

        Commands = new ServerCommandBuilder(Server, Client);
    }

    public Task DisposeAsync()
    {
        // Base class Dispose handles cleanup
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a test action with automatic scene tree dumping on failure.
    /// </summary>
    public new Task NebulaTest(System.Func<Task> testAction)
    {
        return base.NebulaTest(testAction);
    }
}

/// <summary>
/// Basic integration tests for client connection and player spawning.
/// </summary>
public class BasicIntegrationTests : IClassFixture<BasicIntegrationFixture>
{
    private readonly BasicIntegrationFixture _fixture;

    public BasicIntegrationTests(BasicIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClientConnect()
    {
        await _fixture.NebulaTest(async () =>
        {
            // Verify server is running
            await _fixture.Server.WaitForOutput("Server ready");

            // Verify client connected and world is ready
            await _fixture.Client.WaitForOutput("Connected to server");
            await _fixture.Client.WaitForOutput("Scene _WorldReady");
        });
    }

    [Fact]
    public async Task SpawnsPlayer()
    {
        await _fixture.NebulaTest(async () =>
        {
            // Spawn player and verify on client using fluent API
            await _fixture.Commands
                .Spawn("res://Integration/PlayerSpawn/Player.tscn")
                .VerifyClient();
        });
    }
}
