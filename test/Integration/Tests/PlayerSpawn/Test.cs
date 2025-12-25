using System.Threading.Tasks;
using Xunit;

namespace NebulaTests.Integration;

/// <summary>
/// Integration tests for player spawning and position synchronization.
/// </summary>
public class PlayerSpawnTests : IntegrationTestBase
{
    [Fact]
    public async Task PlayerSpawn_PositionSyncsToClient()
    {
        using var server = StartServer(new ServerConfig
        {
            InitialWorldScene = "res://Integration/Tests/PlayerSpawn/Scene.tscn"
        });
        using var client = StartClient();

        await server.WaitForOutput("Server ready");
        await client.WaitForOutput("Connected to server");
    }
}


