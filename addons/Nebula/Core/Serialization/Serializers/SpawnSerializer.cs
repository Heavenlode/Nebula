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
            public ushort parentId;
            public byte nodePathId;
            public Vector3 position;
            public Vector3 rotation;
            public byte hasInputAuthority;
        }

        private NetworkController netController;
        private Dictionary<UUID, Tick> setupTicks = new();
        private bool hasImported = false; // Track if this serializer has already imported

        public SpawnSerializer(NetworkController controller)
        {
            netController = controller;
        }

        public void Begin() { }

        public void Cleanup() 
        {
            // NOTE: This is called every tick after ExportState(), NOT when the object is destroyed.
            // Do not clear per-peer caches here - that would break spawn synchronization!
            // Use CleanupPeer() for per-peer cleanup on disconnect instead.
        }
        
        public void CleanupPeer(UUID peerId)
        {
            setupTicks.Remove(peerId);
        }

        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            
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
                // This is expected for already-spawned nodes, don't log
                return;
            }

            if (netController.NetParent != null && !currentWorld.HasSpawnedForClient(netController.NetParent.NetId, peer))
            {
                return;
            }

            if (netController.RawNode is INetNodeBase netNode)
            {
                if (!netNode.Network.spawnReady.GetValueOrDefault(peerId, false))
                {
                    netNode.Network.PrepareSpawn(peer);
                    return;
                }
            }

            var id = currentWorld.TryRegisterPeerNode(netController, peer);
            if (id == 0)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"[SpawnSerializer WARN] TryRegisterPeerNode returned 0 for peer {peer.ID}, node {netController.RawNode.Name}");
                return;
            }

            var sceneId = Protocol.PackScene(netController.NetSceneFilePath);
            // Only set setupTick on FIRST export - don't overwrite on re-exports
            // Otherwise the ACK can never catch up (setupTick keeps moving forward)
            if (!setupTicks.ContainsKey(peerId))
            {
                setupTicks[peerId] = currentWorld.CurrentTick;
            }

            NetWriter.WriteByte(buffer, sceneId);

            if (netController.NetParent == null)
            {
                NetWriter.WriteUInt16(buffer, 0);
                return;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, netController.NetParent);
            NetWriter.WriteUInt16(buffer, parentId);

            // Get the path from parent's root to the spawned node's parent
            byte nodePathId = 0;
            var relativePath = netController.NetParent.RawNode.GetPathTo(netController.RawNode.GetParent());
            if (relativePath == "." || relativePath.IsEmpty)
            {
                // Direct child of parent's root - use 255 as special marker
                nodePathId = 255;
                NetWriter.WriteByte(buffer, 255);
            }
            else if (Protocol.PackNode(netController.NetParent.RawNode.SceneFilePath, relativePath, out nodePathId))
            {
                NetWriter.WriteByte(buffer, nodePathId);
            }
            else
            {
                throw new System.Exception($"FAILED TO PACK FOR SPAWN: Node path not found for {netController.RawNode.GetPath()}, relativePath={relativePath}");
            }

            if (netController.RawNode is Node3D node)
            {
                NetWriter.WriteVector3(buffer, node.Position);
                NetWriter.WriteVector3(buffer, node.Rotation);
            }

            var hasInputAuth = netController.InputAuthority.Equals(peer) ? (byte)1 : (byte)0;
            NetWriter.WriteByte(buffer, hasInputAuth);

            currentWorld.Debug?.Send("Spawn", $"Exported:{netController.RawNode.SceneFilePath}");

            // Mark spawned immediately so NetPropertiesSerializer can export in the same tick
            currentWorld.SetSpawnedForClient(netController.NetId, peer);
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            
            if (!setupTicks.TryGetValue(peerId, out var setupTick) || setupTick == 0)
            {
                return;
            }

            if (tick >= setupTick)
            {
                currentWorld.SetSpawnedForClient(netController.NetId, peer);
                setupTicks.Remove(peerId); // Clean up after successful ack
            }
        }

        // Import is client-only and infrequent, less critical to optimize
        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController controllerOut)
        {
            controllerOut = netController;
            var data = Deserialize(buffer);

            // Skip if this node was already properly imported
            if (hasImported)
            {
                return;
            }

            // Note: The node is already registered by WorldRunner before Import is called.
            // We just need to replace the blank node with the actual scene.
            var networkId = netController.NetId;

            currentWorld.DeregisterPeerNode(controllerOut);
            
            // Store reference to old node before reassigning controllerOut
            var oldNode = netController.RawNode;

            var networkParent = currentWorld.GetNodeFromNetId(data.parentId);
            if (data.parentId != 0 && networkParent == null)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Parent node not found for: {Protocol.UnpackScene(data.classId).ResourcePath} - Parent ID: {data.parentId}");
                return;
            }

            var newNode = Protocol.UnpackScene(data.classId).Instantiate<INetNodeBase>();
            newNode.Network.IsClientSpawn = true;
            newNode.Network.NetId = networkId;
            newNode.Network.CurrentWorld = currentWorld;
            newNode.SetupSerializers();
            controllerOut = newNode.Network;

            // Mark the new node's SpawnSerializer as already imported
            if (controllerOut.NetNode.Serializers.Length > 0 && controllerOut.NetNode.Serializers[0] is SpawnSerializer spawnSerializer)
            {
                spawnSerializer.hasImported = true;
            }

            if (networkParent != null)
            {
                controllerOut.NetParentId = networkParent.NetId;
            }
            currentWorld.TryRegisterPeerNode(controllerOut);

            ProcessChildNodes(controllerOut);

            // Clean up the old blank node - just queue free, don't try to remove from parent
            // since it might have already been freed or reparented
            oldNode.QueueFree();

            if (data.parentId == 0)
            {
                currentWorld.ChangeScene(controllerOut);
                currentWorld.Debug?.Send("Spawn", $"Imported:{controllerOut.NetSceneFilePath}");
                return;
            }

            if (data.hasInputAuthority == 1)
            {
                controllerOut.InputAuthority = NetRunner.Instance.ServerPeer;
                // Mark owned entities cache dirty so prediction loop picks up this entity
                currentWorld.MarkOwnedEntitiesDirty();
            }

            // 255 means direct child of parent's root node
            if (data.nodePathId == 255)
            {
                networkParent.RawNode.AddChild(controllerOut.RawNode);
            }
            else
            {
                networkParent.RawNode.GetNode(Protocol.UnpackNode(networkParent.RawNode.SceneFilePath, data.nodePathId)).AddChild(controllerOut.RawNode);
            }

            controllerOut._NetworkPrepare(currentWorld);

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

                // Only process nodes that implement INetNodeBase
                if (child is not INetNodeBase netNodeBase)
                {
                    // Still need to check grandchildren
                    children.AddRange(child.GetChildren());
                    continue;
                }

                var networkChild = netNodeBase.Network;
                if (networkChild != null && networkChild.IsNetScene())
                {
                    // Remove from parent if it has one, then queue for deletion
                    var parent = networkChild.RawNode.GetParent();
                    if (parent != null)
                    {
                        parent.RemoveChild(networkChild.RawNode);
                    }
                    networkChild.QueueNodeForDeletion();
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
        }

        private Data Deserialize(NetBuffer data)
        {
            var spawnData = new Data
            {
                classId = NetReader.ReadByte(data),
                parentId = NetReader.ReadUInt16(data),
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

        public void _Process(double delta) {}
    }
}
