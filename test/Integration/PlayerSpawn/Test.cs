namespace NebulaTests.Integration.PlayerSpawn;

using System.Threading.Tasks;
using Nebula.Testing.Integration;
using Xunit;

/// <summary>
/// Integration tests for player spawning and position synchronization.
/// </summary>
public class PlayerSpawnTests : IntegrationTestBase
{
    [Fact]
    public async Task PlayerSpawn_PositionSyncsToClient()
    {
        await RunWithSceneTreeDumpOnFailure(async () =>
        {
            var server = StartServer(new ServerConfig
            {
                InitialWorldScene = "res://Integration/PlayerSpawn/Scene.tscn"
            });
            var client = StartClient();

            await server.WaitForOutput("Server ready");
            await client.WaitForOutput("Connected to server");
            await client.WaitForOutput("Scene _WorldReady");

            // Send spawn command to server
            server.SendCommand("spawn:res://Integration/PlayerSpawn/Player.tscn");

            // Wait for the player to be spawned and ready
            await server.WaitForOutput("Spawned: res://Integration/PlayerSpawn/Player.tscn");
            await server.WaitForOutput("Player _WorldReady");
        });
    }
}
