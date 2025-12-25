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

        public override void _EnterTree()
        {
            base._EnterTree();
            Name = "SpawnSerializer";
            wrapper = new NetNodeWrapper(GetParent());
        }
        private Dictionary<NetPeer, Tick> setupTicks = [];

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

        public void Begin() {}

        public void Import(WorldRunner currentWorld, HLBuffer buffer, out NetNodeWrapper nodeOut)
        {
            nodeOut = wrapper;
            var data = Deserialize(buffer);

            // If the node is already registered, then we don't need to spawn it again
            var result = currentWorld.TryRegisterPeerNode(nodeOut);
            if (result == 0)
            {
                return;
            }

            var networkId = wrapper.NetId;

            // Deregister and delete the node, because it is simply a "Placeholder" that doesn't really exist
            currentWorld.DeregisterPeerNode(nodeOut);
            wrapper.Node.QueueFree();

            var networkParent = currentWorld.GetNodeFromNetId(data.parentId);
            if (data.parentId != 0 && networkParent == null)
            {
                // The parent node is not registered, so we can't spawn this node
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

            // Iterate through all child nodes of nodeOut
            // If the child node is a NetNodeWrapper, then we set it to dynamic spawn
            // We don't register it as a node in the NetRunner because only the parent needs registration
            var children = nodeOut.Node.GetChildren().ToList();
            List<NetNodeWrapper> networkChildren = new List<NetNodeWrapper>();
            while (children.Count > 0)
            {
                var child = children[0];
                var networkChild = new NetNodeWrapper(child);
                children.RemoveAt(0);
                if (networkChild != null && networkChild.IsNetScene())
                {
                    // Nested network scenes are spawned separately
                    networkChild.Node.GetParent().RemoveChild(networkChild.Node);
                    networkChild.Node.QueueFree();
                    continue;
                }
                children.AddRange(child.GetChildren());
                if (networkChild == null) {
                    continue;
                }
                networkChild.IsClientSpawn = true;
                networkChild.InputAuthority = nodeOut.InputAuthority;
                networkChildren.Add(networkChild);
            }
            networkChildren.Reverse();
            NetRunner.Instance.RemoveChild(nodeOut.Node);

            if (data.parentId == 0)
            {
                currentWorld.ChangeScene(nodeOut);
                // Notify debug listeners that scene change completed
                currentWorld.Debug?.Send("Spawn", $"Imported:{nodeOut.Node.SceneFilePath}");
                return;
            }

            if (data.hasInputAuthority == 1)
            {
                nodeOut.InputAuthority = NetRunner.Instance.ENetHost;
            }

            networkParent.Node.GetNode(ProtocolRegistry.Instance.UnpackNode(networkParent.Node.SceneFilePath, data.nodePathId)).AddChild(nodeOut.Node);
            if (nodeOut.Node is Node3D node) {
                // node.Position = data.position;
                // node.Rotation = data.rotation;
            }

            nodeOut._NetworkPrepare(currentWorld);
            nodeOut._WorldReady();

            // Notify debug listeners that spawn import completed
            currentWorld.Debug?.Send("Spawn", $"Imported:{nodeOut.Node.SceneFilePath}");

            return;
        }

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peer)
        {
            var buffer = new HLBuffer();
            if (currentWorld.HasSpawnedForClient(wrapper.NetId, peer))
            {
                // The target client is already aware of this node.
                return buffer;
            }

            if (wrapper.NetParent != null && !currentWorld.HasSpawnedForClient(wrapper.NetParent.NetId, peer))
            {
                // The parent node is not registered with the client yet, so we can't spawn this node
                return buffer;
            }

            if (wrapper.Node is INetNodeBase netNode) {
                // TODO: Maybe this should exist in the node wrapper?
                if (!netNode.Network.spawnReady.GetValueOrDefault(peer, false))
                {
                    netNode.Network.PrepareSpawn(peer);
                    // The node is not ready to be spawned yet
                    return buffer;
                }
            }

            var id = currentWorld.TryRegisterPeerNode(wrapper, peer);
            if (id == 0)
            {
                // Unable to spawn this node. The client is already tracking the max amount.
                return buffer;
            }

            setupTicks[peer] = currentWorld.CurrentTick;
            HLBytes.Pack(buffer, wrapper.NetSceneId);

            // Pack the node path
            if (wrapper.NetParent == null)
            {
                // If this scene has no parent, then it is the root scene
                // In other words, this indicates a scene change
                HLBytes.Pack(buffer, (byte)0);

                // We exit early because no other property is valid on a root scene
                return buffer;
            }


            // Pack the parent network ID and the node path
            var parentId = currentWorld.GetPeerNodeId(peer, wrapper.NetParent);
            HLBytes.Pack(buffer, parentId);
            if (ProtocolRegistry.Instance.PackNode(wrapper.NetParent.Node.SceneFilePath, wrapper.NetParent.Node.GetPathTo(wrapper.Node.GetParent()), out var nodePathId))
            {
                HLBytes.Pack(buffer, nodePathId);
            } else {
                throw new System.Exception($"FAILED TO PACK FOR SPAWN: Node path not found for {wrapper.Node.GetPath()} - Parent Path: { wrapper.NetParent.Node.GetPath()} - Parent Scene: {wrapper.NetParent.Node.SceneFilePath} - Parent Path To Parent: {wrapper.NetParent.Node.GetPathTo(wrapper.Node.GetParent())}");
            }

            if (wrapper.Node is Node3D node)
            {
                // TODO: Support Node2D
                HLBytes.Pack(buffer, node.Position);
                HLBytes.Pack(buffer, node.Rotation);
            }

            if (wrapper.InputAuthority == peer)
            {
                HLBytes.Pack(buffer, (byte)1);
            }
            else
            {
                HLBytes.Pack(buffer, (byte)0);
            }

            // Notify debug listeners that spawn export completed
            currentWorld.Debug?.Send("Spawn", $"Exported:{wrapper.Node.SceneFilePath}");

            return buffer;
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            var peerTick = setupTicks.TryGetValue(peer, out var setupTick) ? setupTick : 0;
            if (setupTick == 0)
            {
                return;
            }

            if (tick >= peerTick)
            {
                currentWorld.SetSpawnedForClient(wrapper.NetId, peer);
            }
        }

        public void PhysicsProcess(double delta) { }

        public void Cleanup() { }
    }
}