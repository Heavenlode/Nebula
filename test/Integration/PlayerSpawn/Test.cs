namespace NebulaTests.Integration.PlayerSpawn;

using System.Threading.Tasks;
using Nebula.Testing.Integration;
using Xunit;

/// <summary>
/// Fixture that provides a shared server and client instance for all tests.
/// </summary>
public class BasicIntegrationFixture : IntegrationTestBase, IAsyncLifetime
{
    public GodotProcess Server { get; private set; } = null!;
    public GodotProcess Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = StartServer(new ServerConfig
        {
            InitialWorldScene = "res://Integration/PlayerSpawn/Scene.tscn"
        });
        Client = StartClient();

        await Server.WaitForOutput("Server ready");
        await Client.WaitForOutput("Connected to server");
        await Client.WaitForOutput("Scene _WorldReady");
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
            // Send spawn command to server
            _fixture.Server.SendCommand("spawn:res://Integration/PlayerSpawn/Player.tscn");

            // Wait for the player to be spawned and ready
            await _fixture.Server.WaitForOutput("Spawned: res://Integration/PlayerSpawn/Player.tscn");
            await _fixture.Server.WaitForOutput("Player _WorldReady");
        });
    }
}
