using Godot;
using Nebula;
using Nebula.Utility.Tools;

namespace Nebula.Testing.NetSync
{
    /// <summary>
    /// Test bootstrap for the NetArray sync repro. Like Nebula's ServerClientConnector but with
    /// diagnostics: on the server it logs peer connect + per-peer world sync-status transitions so we
    /// can see whether a player-less peer actually reaches IN_WORLD (and thus gets world state).
    /// </summary>
    public partial class NetArraySyncTestConnector : Node
    {
        public override void _Ready()
        {
            if (Env.Instance.HasServerFeatures)
                PrepareServer();
            else
                PrepareClient();
        }

        private void PrepareServer()
        {
            NetRunner.Instance.OnPeerConnected += (peerId) =>
                GD.Print($"[NETSYNC-DIAG] server: OnPeerConnected native={peerId}");

            NetRunner.Instance.StartServer();

            var scenePath = Env.Instance.InitialWorldScene;
            GD.Print($"[NETSYNC-DIAG] server: creating world from {scenePath}");
            var scene = GD.Load<PackedScene>(scenePath);
            var world = NetRunner.Instance.CreateWorld(Env.Instance.InitialWorldId, scene);

            world.OnPeerSyncStatusChange += (peerId, status) =>
                GD.Print($"[NETSYNC-DIAG] server: peer {peerId} sync status -> {status}");
            world.OnPlayerJoined += (worldId, peerId) =>
                GD.Print($"[NETSYNC-DIAG] server: OnPlayerJoined peer {peerId} (IN_WORLD)");

            GD.Print("[NETSYNC-DIAG] server ready");
        }

        private async void PrepareClient()
        {
            GD.Print("[NETSYNC-DIAG] client: prepareClient");
            await System.Threading.Tasks.Task.Delay(300);
            NetRunner.Instance.StartClient();
        }
    }
}
