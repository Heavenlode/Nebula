namespace NebulaTests.Integration.PlayerSpawn;

using System;
using System.Threading.Tasks;
using Nebula.Testing.Integration;
using Xunit;
using Xunit.Abstractions;

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
    public string WorldId { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        WorldId = Guid.NewGuid().ToString();
        Server = StartServer(new ServerConfig
        {
            WorldId = WorldId,
            InitialWorldScene = "res://Integration/PlayerSpawn/Scene.tscn",
            DebugPort = ServerDebugPort
        });
        Client = StartClient(new ClientConfig
        {
            DebugPort = ClientDebugPort
        });

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
/// 
[TestCaseOrderer("Nebula.Testing.Integration.PriorityOrderer", "NebulaTests")]
public class BasicIntegrationTests : IClassFixture<BasicIntegrationFixture>
{
    private readonly BasicIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public BasicIntegrationTests(BasicIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact, Order(1)]
    public async Task ClientConnect()
    {
        await _fixture.NebulaTest(async () =>
        {
            await _fixture.Server.WaitForDebugEvent("WorldCreated", _fixture.WorldId);
            await _fixture.Client.WaitForDebugEvent("WorldJoined", "res://Integration/PlayerSpawn/Scene.tscn");
        });
    }

    [Fact, Order(2)]
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

    [Fact, Order(3)]
    public async Task HandlesInput()
    {
        await _fixture.NebulaTest(async () =>
        {
            await _fixture.Commands
                .Input(0, "foo")
                .VerifyServer();

            await _fixture.Commands
                .Input(1, "bar")
                .VerifyServer();
        });
    }


    [Fact, Order(4)]
    public async Task MutatesStateWithInput()
    {
        await _fixture.NebulaTest(async () =>
        {
            await _fixture.Commands
                .Input(0, "add_score")
                .VerifyServer();

            // Input values remain buffered until explicitly released
            await _fixture.Server.WaitForDebugEvent("ScoreInput", "2");
            await _fixture.Server.WaitForDebugEvent("ScoreInput", "3");
            await _fixture.Server.WaitForDebugEvent("ScoreInput", "10");

            await _fixture.Commands
                .Input(0, "subtract_score")
                .VerifyServer();

            await _fixture.Server.WaitForDebugEvent("ScoreInput", "7");

            await _fixture.Commands
                .Input(0, "clear_input")
                .VerifyServer();

            await Task.Delay(50);

            await _fixture.Commands
                .Custom("GetScore")
                .SendServer();

            await _fixture.Commands
                .Custom("GetScore")
                .SendClient();

            var serverScoreValue = await _fixture.Server.WaitForDebugEvent("GetScore");
            var clientScoreValue = await _fixture.Client.WaitForDebugEvent("GetScore");

            Assert.Equal(serverScoreValue.Message, clientScoreValue.Message);

            await _fixture.Commands
                .Custom("GetScore")
                .SendServer();

            await _fixture.Commands
                .Custom("GetScore")
                .SendClient();

            var serverScoreValue2 = await _fixture.Server.WaitForDebugEvent("GetScore");
            var clientScoreValue2 = await _fixture.Client.WaitForDebugEvent("GetScore");

            Assert.Equal(serverScoreValue2.Message, clientScoreValue2.Message);
            Assert.Equal(serverScoreValue.Message, serverScoreValue2.Message);
            Assert.Equal(clientScoreValue.Message, clientScoreValue2.Message);
            Assert.NotEqual("0", serverScoreValue.Message);
        });
    }

    [Fact, Order(5)]
    public async Task VerifyNodeStructure()
    {
        await _fixture.NebulaTest(async () =>
        {
            await _fixture.Commands
                .Custom("VerifyNodeStructure")
                .SendBoth();

            var serverResult = await _fixture.Server.WaitForDebugEvent("VerifyNodeStructure");
            var clientResult = await _fixture.Client.WaitForDebugEvent("VerifyNodeStructure");

            Assert.Equal(serverResult.Message, clientResult.Message);
            Assert.Equal("true", serverResult.Message);
        });
    }


    [Fact, Order(6)]
    public async Task CanDespawnNodes()
    {
        await _fixture.NebulaTest(async () =>
        {
            await _fixture.Commands
                .Custom("CanDespawnNodes")
                .SendBoth();

            var serverResult = await _fixture.Server.WaitForDebugEvent("CanDespawnNodes");
            var clientResult = await _fixture.Client.WaitForDebugEvent("CanDespawnNodes");

            Assert.Equal(serverResult.Message, clientResult.Message);
            Assert.Equal("true", serverResult.Message);
        });
    }
}
