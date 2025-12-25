using Godot;
using Nebula;
using Nebula.Utility.Tools;
using NebulaTests.Integration.Helpers;

namespace NebulaTests.Integration.Tests.PlayerSpawn
{
    public partial class Scene : NetNode3D
    {
        private StdinCommandHandler _commandHandler;

        public override void _Ready()
        {
            base._Ready();
            
            // Only set up command handling on the server
            if (NetRunner.Instance.IsServer)
            {
                _commandHandler = new StdinCommandHandler();
                AddChild(_commandHandler);
                _commandHandler.CommandReceived += OnCommandReceived;
            }
        }

        public override void _WorldReady()
        {
            base._WorldReady();
            Debugger.Instance.Log("Scene _WorldReady");
        }

        private void OnCommandReceived(string command)
        {
            Debugger.Instance.Log($"Scene received command: {command}");

            if (command.StartsWith("spawn:"))
            {
                var scenePath = command.Substring("spawn:".Length).Trim();
                SpawnScene(scenePath);
            }
        }

        private void SpawnScene(string scenePath)
        {
            Debugger.Instance.Log($"Spawning scene: {scenePath}");

            var packedScene = GD.Load<PackedScene>(scenePath);
            if (packedScene == null)
            {
                Debugger.Instance.Log($"Failed to load scene: {scenePath}", Debugger.DebugLevel.ERROR);
                return;
            }

            var instance = packedScene.Instantiate();
            if (instance is NetNode3D netNode3D)
            {
                var parentWrapper = new NetNodeWrapper(this);
                Network.CurrentWorld.Spawn(netNode3D, parentWrapper);
                Debugger.Instance.Log($"Spawned: {scenePath}");
            }
            else if (instance is NetNode2D netNode2D)
            {
                var parentWrapper = new NetNodeWrapper(this);
                Network.CurrentWorld.Spawn(netNode2D, parentWrapper);
                Debugger.Instance.Log($"Spawned: {scenePath}");
            }
            else if (instance is NetNode netNode)
            {
                var parentWrapper = new NetNodeWrapper(this);
                Network.CurrentWorld.Spawn(netNode, parentWrapper);
                Debugger.Instance.Log($"Spawned: {scenePath}");
            }
            else
            {
                Debugger.Instance.Log($"Scene root is not a NetNode: {scenePath}", Debugger.DebugLevel.ERROR);
                instance.QueueFree();
            }
        }
    }
}
