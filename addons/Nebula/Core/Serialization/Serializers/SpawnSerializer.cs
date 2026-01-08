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
        private HLBuffer _exportBuffer = new();
        private Dictionary<NetPeer, Tick> setupTicks = new();

        public SpawnSerializer(NetworkController controller)
        {
            netController = controller;
        }

        public void Begin() { }

        public void Cleanup() { }

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peer)
        {
            _exportBuffer.Clear();

            if (netController.IsQueuedForDespawn)
            {
                return _exportBuffer;
            }

            if (!netController.IsPeerInterested(peer))
            {
                return _exportBuffer;
            }

            if (currentWorld.HasSpawnedForClient(netController.NetId, peer))
            {
                return _exportBuffer;
            }

            if (netController.NetParent != null && !currentWorld.HasSpawnedForClient(netController.NetParent.NetId, peer))
            {
                return _exportBuffer;
            }

            if (netController.RawNode is INetNodeBase netNode)
            {
                if (!netNode.Network.spawnReady.GetValueOrDefault(peer, false))
                {
                    netNode.Network.PrepareSpawn(peer);
                    return _exportBuffer;
                }
            }

            var id = currentWorld.TryRegisterPeerNode(netController, peer);
            if (id == 0)
            {
                return _exportBuffer;
            }

            setupTicks[peer] = currentWorld.CurrentTick;
            HLBytes.Pack(_exportBuffer, Protocol.PackScene(netController.NetSceneFilePath));

            if (netController.NetParent == null)
            {
                HLBytes.Pack(_exportBuffer, (byte)0);
                return _exportBuffer;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, netController.NetParent);
            HLBytes.Pack(_exportBuffer, parentId);

            if (Protocol.PackNode(netController.NetParent.RawNode.SceneFilePath, netController.NetParent.RawNode.GetPathTo(netController.RawNode.GetParent()), out var nodePathId))
            {
                HLBytes.Pack(_exportBuffer, nodePathId);
            }
            else
            {
                throw new System.Exception($"FAILED TO PACK FOR SPAWN: Node path not found for {netController.RawNode.GetPath()}");
            }

            if (netController.RawNode is Node3D node)
            {
                HLBytes.Pack(_exportBuffer, node.Position);
                HLBytes.Pack(_exportBuffer, node.Rotation);
            }

            HLBytes.Pack(_exportBuffer, netController.InputAuthority == peer ? (byte)1 : (byte)0);

            currentWorld.Debug?.Send("Spawn", $"Exported:{netController.RawNode.SceneFilePath}");

            return _exportBuffer;
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
        public void Import(WorldRunner currentWorld, HLBuffer buffer, out NetworkController controllerOut)
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
                controllerOut.SetInputAuthority(NetRunner.Instance.ENetHost);
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

        private Data Deserialize(HLBuffer data)
        {
            var spawnData = new Data
            {
                classId = HLBytes.UnpackByte(data),
                parentId = HLBytes.UnpackByte(data),
            };

            if (spawnData.parentId == 0)
            {
                return spawnData;
            }

            spawnData.nodePathId = HLBytes.UnpackByte(data);
            spawnData.position = HLBytes.UnpackVector3(data);
            spawnData.rotation = HLBytes.UnpackVector3(data);
            spawnData.hasInputAuthority = HLBytes.UnpackByte(data);
            return spawnData;
        }
    }
}