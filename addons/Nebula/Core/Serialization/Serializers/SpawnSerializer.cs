using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization.Serializers
{
    public partial class SpawnSerializer : RefCounted, IStateSerializer
    {
        private struct Data
        {
            public byte classId;
            public byte parentId;
            public byte nodePathId;
            public Vector3 position;
            public Vector3 rotation;
            public byte hasInputAuthority;
        }

        private NetworkController netController;
        private Dictionary<NetPeer, Tick> setupTicks = new();

        public SpawnSerializer(NetworkController controller)
        {
            netController = controller;
        }

        public void Begin() { }

        public void Cleanup() { }

        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            if (netController.IsQueuedForDespawn)
            {
                return;
            }

            if (!netController.IsPeerInterested(peer))
            {
                return;
            }

            if (currentWorld.HasSpawnedForClient(netController.NetId, peer))
            {
                return;
            }

            if (netController.NetParent != null && !currentWorld.HasSpawnedForClient(netController.NetParent.NetId, peer))
            {
                return;
            }

            if (netController.RawNode is INetNodeBase netNode)
            {
                if (!netNode.Network.spawnReady.GetValueOrDefault(peer, false))
                {
                    netNode.Network.PrepareSpawn(peer);
                    return;
                }
            }

            var id = currentWorld.TryRegisterPeerNode(netController, peer);
            if (id == 0)
            {
                return;
            }

            setupTicks[peer] = currentWorld.CurrentTick;
            NetWriter.WriteByte(buffer, Protocol.PackScene(netController.NetSceneFilePath));

            if (netController.NetParent == null)
            {
                NetWriter.WriteByte(buffer, 0);
                return;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, netController.NetParent);
            NetWriter.WriteByte(buffer, parentId);

            if (Protocol.PackNode(netController.NetParent.RawNode.SceneFilePath, netController.NetParent.RawNode.GetPathTo(netController.RawNode.GetParent()), out var nodePathId))
            {
                NetWriter.WriteByte(buffer, nodePathId);
            }
            else
            {
                throw new System.Exception($"FAILED TO PACK FOR SPAWN: Node path not found for {netController.RawNode.GetPath()}");
            }

            if (netController.RawNode is Node3D node)
            {
                NetWriter.WriteVector3(buffer, node.Position);
                NetWriter.WriteVector3(buffer, node.Rotation);
            }

            NetWriter.WriteByte(buffer, netController.InputAuthority == peer ? (byte)1 : (byte)0);

            currentWorld.Debug?.Send("Spawn", $"Exported:{netController.RawNode.SceneFilePath}");
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            if (!setupTicks.TryGetValue(peer, out var setupTick) || setupTick == 0)
            {
                return;
            }

            if (tick >= setupTick)
            {
                currentWorld.SetSpawnedForClient(netController.NetId, peer);
            }
        }

        // Import is client-only and infrequent, less critical to optimize
        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController controllerOut)
        {
            controllerOut = netController;
            var data = Deserialize(buffer);

            var result = currentWorld.TryRegisterPeerNode(controllerOut);
            if (result == 0)
            {
                return;
            }

            var networkId = netController.NetId;

            currentWorld.DeregisterPeerNode(controllerOut);
            netController.RawNode.QueueFree();

            var networkParent = currentWorld.GetNodeFromNetId(data.parentId);
            if (data.parentId != 0 && networkParent == null)
            {
                Debugger.Instance.Log($"Parent node not found for: {Protocol.UnpackScene(data.classId).ResourcePath} - Parent ID: {data.parentId}", Debugger.DebugLevel.ERROR);
                return;
            }

            NetRunner.Instance.RemoveChild(controllerOut.RawNode);
            var newNode = Protocol.UnpackScene(data.classId).Instantiate<INetNodeBase>();
            newNode.Network.IsClientSpawn = true;
            newNode.Network.NetId = networkId;
            newNode.Network.CurrentWorld = currentWorld;
            newNode.SetupSerializers();
            controllerOut = newNode.Network;
            NetRunner.Instance.AddChild(controllerOut.RawNode);

            if (networkParent != null)
            {
                controllerOut.NetParentId = networkParent.NetId;
            }
            currentWorld.TryRegisterPeerNode(controllerOut);

            ProcessChildNodes(controllerOut);

            if (data.parentId == 0)
            {
                currentWorld.ChangeScene(controllerOut);
                currentWorld.Debug?.Send("Spawn", $"Imported:{controllerOut.NetSceneFilePath}");
                return;
            }

            if (data.hasInputAuthority == 1)
            {
                controllerOut.SetInputAuthority(NetRunner.Instance.ServerPeer);
            }

            networkParent.RawNode.GetNode(Protocol.UnpackNode(networkParent.RawNode.SceneFilePath, data.nodePathId)).AddChild(controllerOut.RawNode);

            controllerOut._NetworkPrepare(currentWorld);
            controllerOut._WorldReady();

            currentWorld.Debug?.Send("Spawn", $"Imported:{controllerOut.RawNode.SceneFilePath}");
        }

        private void ProcessChildNodes(NetworkController nodeOut)
        {
            var children = nodeOut.RawNode.GetChildren().ToList();
            var networkChildren = new List<NetworkController>();

            while (children.Count > 0)
            {
                var child = children[0];
                children.RemoveAt(0);

                var networkChild = new NetworkController(child);
                if (networkChild != null && networkChild.IsNetScene())
                {
                    networkChild.RawNode.GetParent().RemoveChild(networkChild.RawNode);
                    networkChild.RawNode.QueueFree();
                    continue;
                }

                children.AddRange(child.GetChildren());

                if (networkChild == null)
                {
                    continue;
                }

                networkChild.IsClientSpawn = true;
                networkChild.SetInputAuthority(nodeOut.InputAuthority);
                networkChildren.Add(networkChild);
            }

            networkChildren.Reverse();
            NetRunner.Instance.RemoveChild(nodeOut.RawNode);
        }

        private Data Deserialize(NetBuffer data)
        {
            var spawnData = new Data
            {
                classId = NetReader.ReadByte(data),
                parentId = NetReader.ReadByte(data),
            };

            if (spawnData.parentId == 0)
            {
                return spawnData;
            }

            spawnData.nodePathId = NetReader.ReadByte(data);
            spawnData.position = NetReader.ReadVector3(data);
            spawnData.rotation = NetReader.ReadVector3(data);
            spawnData.hasInputAuthority = NetReader.ReadByte(data);
            return spawnData;
        }
    }
}
