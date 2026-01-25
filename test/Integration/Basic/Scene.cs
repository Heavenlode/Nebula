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
            // Only the client should handle input commands (clients set input, server receives it)
            if (NetRunner.Instance.IsServer) return;
            
            var inputParts = command.Substring("Input:".Length).Trim().Split(':');
            if (inputParts.Length != 2)
            {
                Debugger.Instance.Log($"Invalid Input command: {command}", Debugger.DebugLevel.ERROR);
                return;
            }
            var inputChannel = byte.Parse(inputParts[0]);
            var inputValue = inputParts[1];
            
            // Parse the command string to the enum
            var testCommand = inputValue switch
            {
                "add_score" => TestCommand.AddScore,
                "subtract_score" => TestCommand.SubtractScore,
                "clear_input" => TestCommand.ClearInput,
                "foo" => TestCommand.Foo,
                "bar" => TestCommand.Bar,
                _ => TestCommand.None
            };
            
            PlayerNode.Network.SetInput(new TestInput { Command = testCommand, Channel = inputChannel });
        }

        if (command == "GetScore")
        {
            Network.CurrentWorld.Debug?.Send("GetScore", PlayerNode.Score.ToString());
        }

        if (command == "VerifyNodeStructure")
        {
            // Use GetNodeOrNull to avoid crash on client if node doesn't exist
            var node = PlayerNode.GetNodeOrNull("Level1/Level2/Level3/Item/Level4");
            if (node != null)
            {
                Network.CurrentWorld.Debug?.Send("VerifyNodeStructure", "true");
            }
            else
            {
                // On client, static children of NetNodes may not be replicated yet
                // Send result instead of throwing so test can see the actual state
                Network.CurrentWorld.Debug?.Send("VerifyNodeStructure", "false");
            }
        }

        if (command == "CanDespawnNodes")
        {
            verifyingDespawn = true;
            if (NetRunner.Instance.IsClient)
            {
                return;
            }
            var node = PlayerNode.GetNode<NetNode3D>("Level1/Level2/Level3/Item");
            node.Network.Despawn();
        }

        if (command == "CheckDynamicChildren")
        {
            var count = PlayerNode.Network.DynamicNetworkChildren.Count;
            var names = string.Join(",", PlayerNode.Network.DynamicNetworkChildren.Select(c => c.RawNode.Name));
            Network.CurrentWorld.Debug?.Send("CheckDynamicChildren", $"{count}:{names}");
        }

        if (command == "CheckItemExists")
        {
            var itemNode = PlayerNode.GetNodeOrNull("Level1/Level2/Level3/Item");
            var exists = itemNode != null;
            var isNetScene = exists && itemNode is INetNodeBase netNode && netNode.Network != null && netNode.Network.IsNetScene();
            Network.CurrentWorld.Debug?.Send("CheckItemExists", $"{exists}:{isNetScene}");
        }

        if (command == "GetItemNetId")
        {
            var itemNode = PlayerNode.GetNodeOrNull<NetNode3D>("Level1/Level2/Level3/Item");
            if (itemNode != null)
            {
                Network.CurrentWorld.Debug?.Send("GetItemNetId", itemNode.Network.NetId.ToString());
            }
            else
            {
                Network.CurrentWorld.Debug?.Send("GetItemNetId", "not_found");
            }
        }

        if (command == "CheckItemHasWorld")
        {
            var itemNode = PlayerNode.GetNodeOrNull<NetNode3D>("Level1/Level2/Level3/Item");
            if (itemNode != null)
            {
                var hasWorld = itemNode.Network.CurrentWorld != null;
                Network.CurrentWorld.Debug?.Send("CheckItemHasWorld", hasWorld.ToString());
            }
            else
            {
                Network.CurrentWorld.Debug?.Send("CheckItemHasWorld", "not_found");
            }
        }

        if (command == "CheckStaticChildren")
        {
            var count = PlayerNode.Network.StaticNetworkChildren.Length;
            var names = string.Join(",", PlayerNode.Network.StaticNetworkChildren.Where(c => c != null).Select(c => c.RawNode.Name));
            Network.CurrentWorld.Debug?.Send("CheckStaticChildren", $"{count}:{names}");
        }

        if (command == "CheckLevel4Exists")
        {
            // Level4 is a child of Item (nested NetScene) - verifies children are preserved
            var level4Node = PlayerNode.GetNodeOrNull("Level1/Level2/Level3/Item/Level4");
            var exists = level4Node != null;
            Network.CurrentWorld.Debug?.Send("CheckLevel4Exists", exists.ToString().ToLower());
        }

        if (command == "LookupItemByNetId")
        {
            // Verify Item is registered with WorldRunner by looking it up via NetId
            var itemNode = PlayerNode.GetNodeOrNull<NetNode3D>("Level1/Level2/Level3/Item");
            if (itemNode == null)
            {
                Network.CurrentWorld.Debug?.Send("LookupItemByNetId", "false");
                return;
            }
            
            var netId = itemNode.Network.NetId;
            var lookedUp = Network.CurrentWorld.GetNodeFromNetId(netId);
            var found = lookedUp != null && lookedUp.RawNode == itemNode;
            Network.CurrentWorld.Debug?.Send("LookupItemByNetId", found.ToString().ToLower());
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
            PlayerNode = Network.CurrentWorld.Spawn(
                netNode3D,
                parent: Network,
                inputAuthority: NetRunner.Instance.Peers.Values.First()
            ) as Player;
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
