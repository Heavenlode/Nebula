using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization.Serializers
{
    public partial class SpawnSerializer : Node, IStateSerializer
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

        private NetNodeWrapper wrapper;
        private HLBuffer _exportBuffer = new();
        private Dictionary<NetPeer, Tick> setupTicks = new();

        public void Setup()
        {
            Name = "SpawnSerializer";
            wrapper = new NetNodeWrapper(GetParent());
        }

        public void Begin() { }

        public void Cleanup() { }

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peer)
        {
            _exportBuffer.Clear();

            if (wrapper.Network.IsQueuedForDespawn)
            {
                return _exportBuffer;
            }

            if (!wrapper.Network.IsPeerInterested(peer))
            {
                return _exportBuffer;
            }

            if (currentWorld.HasSpawnedForClient(wrapper.NetId, peer))
            {
                return _exportBuffer;
            }

            if (wrapper.NetParent != null && !currentWorld.HasSpawnedForClient(wrapper.NetParent.NetId, peer))
            {
                return _exportBuffer;
            }

            if (wrapper.Node is INetNodeBase netNode)
            {
                if (!netNode.Network.spawnReady.GetValueOrDefault(peer, false))
                {
                    netNode.Network.PrepareSpawn(peer);
                    return _exportBuffer;
                }
            }

            var id = currentWorld.TryRegisterPeerNode(wrapper, peer);
            if (id == 0)
            {
                return _exportBuffer;
            }

            setupTicks[peer] = currentWorld.CurrentTick;
            HLBytes.Pack(_exportBuffer, wrapper.NetSceneId);

            if (wrapper.NetParent == null)
            {
                HLBytes.Pack(_exportBuffer, (byte)0);
                return _exportBuffer;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, wrapper.NetParent);
            HLBytes.Pack(_exportBuffer, parentId);

            if (ProtocolRegistry.Instance.PackNode(wrapper.NetParent.Node.SceneFilePath, wrapper.NetParent.Node.GetPathTo(wrapper.Node.GetParent()), out var nodePathId))
            {
                HLBytes.Pack(_exportBuffer, nodePathId);
            }
            else
            {
                throw new System.Exception($"FAILED TO PACK FOR SPAWN: Node path not found for {wrapper.Node.GetPath()}");
            }

            if (wrapper.Node is Node3D node)
            {
                HLBytes.Pack(_exportBuffer, node.Position);
                HLBytes.Pack(_exportBuffer, node.Rotation);
            }

            HLBytes.Pack(_exportBuffer, wrapper.InputAuthority == peer ? (byte)1 : (byte)0);

            currentWorld.Debug?.Send("Spawn", $"Exported:{wrapper.Node.SceneFilePath}");

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
                currentWorld.SetSpawnedForClient(wrapper.NetId, peer);
            }
        }

        // Import is client-only and infrequent, less critical to optimize
        public void Import(WorldRunner currentWorld, HLBuffer buffer, out NetNodeWrapper nodeOut)
        {
            nodeOut = wrapper;
            var data = Deserialize(buffer);

            var result = currentWorld.TryRegisterPeerNode(nodeOut);
            if (result == 0)
            {
                return;
            }

            var networkId = wrapper.NetId;

            currentWorld.DeregisterPeerNode(nodeOut);
            wrapper.Node.QueueFree();

            var networkParent = currentWorld.GetNodeFromNetId(data.parentId);
            if (data.parentId != 0 && networkParent == null)
            {
                Debugger.Instance.Log($"Parent node not found for: {ProtocolRegistry.Instance.UnpackScene(data.classId).ResourcePath} - Parent ID: {data.parentId}", Debugger.DebugLevel.ERROR);
                return;
            }

            NetRunner.Instance.RemoveChild(nodeOut.Node);
            var newNode = ProtocolRegistry.Instance.UnpackScene(data.classId).Instantiate<INetNodeBase>();
            newNode.Network.IsClientSpawn = true;
            newNode.Network.NetId = networkId;
            newNode.Network.CurrentWorld = currentWorld;
            newNode.SetupSerializers();
            nodeOut = newNode.Network.AttachedNetNode;
            NetRunner.Instance.AddChild(nodeOut.Node);

            if (networkParent != null)
            {
                nodeOut.NetParentId = networkParent.NetId;
            }
            currentWorld.TryRegisterPeerNode(nodeOut);

            ProcessChildNodes(nodeOut);

            if (data.parentId == 0)
            {
                currentWorld.ChangeScene(nodeOut);
                currentWorld.Debug?.Send("Spawn", $"Imported:{nodeOut.Node.SceneFilePath}");
                return;
            }

            if (data.hasInputAuthority == 1)
            {
                nodeOut.InputAuthority = NetRunner.Instance.ENetHost;
            }

            networkParent.Node.GetNode(ProtocolRegistry.Instance.UnpackNode(networkParent.Node.SceneFilePath, data.nodePathId)).AddChild(nodeOut.Node);

            nodeOut._NetworkPrepare(currentWorld);
            nodeOut._WorldReady();

            currentWorld.Debug?.Send("Spawn", $"Imported:{nodeOut.Node.SceneFilePath}");
        }

        private void ProcessChildNodes(NetNodeWrapper nodeOut)
        {
            var children = nodeOut.Node.GetChildren().ToList();
            var networkChildren = new List<NetNodeWrapper>();

            while (children.Count > 0)
            {
                var child = children[0];
                children.RemoveAt(0);

                var networkChild = new NetNodeWrapper(child);
                if (networkChild != null && networkChild.IsNetScene())
                {
                    networkChild.Node.GetParent().RemoveChild(networkChild.Node);
                    networkChild.Node.QueueFree();
                    continue;
                }

                children.AddRange(child.GetChildren());

                if (networkChild == null)
                {
                    continue;
                }

                networkChild.IsClientSpawn = true;
                networkChild.InputAuthority = nodeOut.InputAuthority;
                networkChildren.Add(networkChild);
            }

            networkChildren.Reverse();
            NetRunner.Instance.RemoveChild(nodeOut.Node);
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