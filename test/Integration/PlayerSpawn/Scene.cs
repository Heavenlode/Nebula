namespace NebulaTests.Integration.PlayerSpawn;

using Godot;
using Nebula;
using Nebula.Utility.Tools;
using Nebula.Testing.Integration;
using System.Linq;
using System;

public partial class Scene : NetNode3D
{
    private StdinCommandHandler _commandHandler;

    bool verifyingDespawn = false;

    public override void _WorldReady()
    {
        base._WorldReady();
        _commandHandler = new StdinCommandHandler();
        AddChild(_commandHandler);
        _commandHandler.CommandReceived += OnCommandReceived;
        Debugger.Instance.Log("Scene _WorldReady");
    }

    private void OnCommandReceived(string command)
    {
        if (command.StartsWith("spawn:"))
        {
            var scenePath = command.Substring("spawn:".Length).Trim();
            SpawnScene(scenePath);
        }

        if (command.StartsWith("Input:"))
        {
            var inputParts = command.Substring("Input:".Length).Trim().Split(':');
            if (inputParts.Length != 2)
            {
                Debugger.Instance.Log($"Invalid Input command: {command}", Debugger.DebugLevel.ERROR);
                return;
            }
            var inputCommand = byte.Parse(inputParts[0]);
            var inputValue = inputParts[1];
            PlayerNode.Network.SetNetworkInput(inputCommand, inputValue);
        }

        if (command == "GetScore")
        {
            Network.CurrentWorld.Debug?.Send("GetScore", PlayerNode.Score.ToString());
        }

        if (command == "VerifyNodeStructure")
        {
            var node = PlayerNode.GetNode("Level1/Level2/Level3/Item/Level4");
            if (node != null)
            {
                Network.CurrentWorld.Debug?.Send("VerifyNodeStructure", "true");
            }
        }

        if (command == "CanDespawnNodes")
        {
            verifyingDespawn = true;
            if (NetRunner.Instance.IsClient) {
                return;
            }
            var node = PlayerNode.GetNode<NetNode3D>("Level1/Level2/Level3/Item");
            node.Network.Despawn();
        }
    }

    public Player PlayerNode;

    private void SpawnScene(string scenePath)
    {
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
            PlayerNode = Network.CurrentWorld.Spawn(
                netNode3D,
                parentWrapper,
                inputAuthority: NetRunner.Instance.Peers.Values.First(),
                interestLayers: new Godot.Collections.Dictionary<UUID, long> { { NetRunner.Instance.GetPeerId(NetRunner.Instance.Peers.Values.First()), long.MaxValue } }) as Player;
            Debugger.Instance.Log($"Spawned: {scenePath}");
        }
        else
        {
            Debugger.Instance.Log($"Scene root is not a NetNode: {scenePath}", Debugger.DebugLevel.ERROR);
            instance.QueueFree();
        }
    }

    public override void _NetworkProcess(int _tick)
    {
        base._NetworkProcess(_tick);
        if (verifyingDespawn)
        {
            var node = PlayerNode.GetNodeOrNull("Level1/Level2/Level3/Item");
            if (node == null)
            {
                Network.CurrentWorld.Debug?.Send("CanDespawnNodes", "true");
                Debugger.Instance.Log("CanDespawnNodes: true", Debugger.DebugLevel.INFO);
                verifyingDespawn = false;
            }
        }
    }
}
