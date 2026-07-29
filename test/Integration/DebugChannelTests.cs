#nullable enable
namespace NebulaTests.Integration;

using System;
using System.Threading.Tasks;
using Nebula.Testing.Integration;
using Xunit;

/// <summary>
/// Smoke coverage for the debug channel itself.
///
/// <para>The integration harness had no callers at all, so nothing verified the
/// wire format it depends on — and the debugger, the editor's Play flow and
/// these tests now all ride the same socket. These tests exist so a framing
/// change (the frame header, the DEBUG_EVENT type byte, the pre-connection
/// replay buffer) fails loudly instead of silently breaking the debugger.</para>
///
/// <para>Lives in the test project rather than alongside the harness in
/// addons/Nebula/Testing/Integration: it needs a real NetScene to drive, and the
/// addon ships none. The harness stays in the addon; the tests that use it live
/// here, next to the scenes they need.</para>
/// </summary>
public class DebugChannelTests : IntegrationTestBase
{
    /// <summary>
    /// A server announces its world on the debug channel, and events emitted
    /// before the client attached are replayed to it.
    /// </summary>
    [Fact]
    public async Task Server_EmitsWorldCreated_OnDebugChannel()
    {
        var server = StartServer(new ServerConfig
        {
            InitialWorldScene = DefaultWorldScene,
            DebugPort = 59930,
        });

        await server.ConnectDebug();

        // WorldCreated is emitted from NetRunner.SetupWorldInstance, which runs
        // during startup — before ConnectDebug can possibly have completed. So
        // receiving it also proves the hub's replay-once buffering works.
        var evt = await server.WaitForDebugEvent("WorldCreated", timeout: TimeSpan.FromSeconds(20));
        Assert.Equal("WorldCreated", evt.Category);
        Assert.False(string.IsNullOrWhiteSpace(evt.Message));
    }

    /// <summary>
    /// The debug channel works on clients too, not just servers. It used to be
    /// created only in StartServer, so a client's --debugPort was silently
    /// ignored.
    /// </summary>
    [Fact]
    public async Task Client_ExposesDebugChannel()
    {
        StartServer(new ServerConfig
        {
            InitialWorldScene = DefaultWorldScene,
            DebugPort = 59931,
        });

        var client = StartClient(new ClientConfig { DebugPort = 59932 });

        // Connecting at all is the assertion: a client with no hub would refuse.
        await client.ConnectDebug();
    }

    /// <summary>
    /// A real NetScene, so SetupWorldInstance actually creates a world. The
    /// previous ProtocolBuilder.tscn is a plain Node build helper — no world was
    /// ever created for it, so WorldCreated could never fire and the first test
    /// could only ever time out.
    /// </summary>
    private const string DefaultWorldScene = "res://Integration/Basic/Scene.tscn";
}
