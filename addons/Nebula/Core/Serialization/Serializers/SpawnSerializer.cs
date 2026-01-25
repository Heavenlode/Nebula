using System.Collections.Generic;
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
            public byte hasInputAuthority;
            public int nestedCount;
        }

        /// <summary>
        /// Data for a nested NetScene included in spawn message.
        /// Struct to avoid heap allocation.
        /// </summary>
        private struct NestedSceneData
        {
            public byte SceneId;
            public byte NodePathId;
            public ushort NetId;
        }

        // Pre-allocated buffers for nested scene handling (static to avoid per-instance allocation)
        private static readonly List<NetworkController> _nestedSceneBuffer = new(16);
        private static readonly NestedSceneData[] _nestedDataBuffer = new NestedSceneData[64];
        private static int _nestedDataCount;
        private static readonly List<NetworkController> _allLocalNestedScenes = new(64);

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
            if (sceneId > 245)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, 
                    $"SceneId {sceneId} exceeds safe limit (245). Too many registered scenes.");
            }
            
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
                
                // Write nested NetScenes for root scene
                ExportNestedScenes(currentWorld, peer, buffer);
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

            var hasInputAuth = netController.InputAuthority.Equals(peer) ? (byte)1 : (byte)0;
            NetWriter.WriteByte(buffer, hasInputAuth);
            
            // Write nested NetScenes
            ExportNestedScenes(currentWorld, peer, buffer);

            currentWorld.Debug?.Send("Spawn", $"Exported:{netController.RawNode.SceneFilePath}");

            // Mark spawned immediately so NetPropertiesSerializer can export in the same tick
            currentWorld.SetSpawnedForClient(netController.NetId, peer);
        }
        
        /// <summary>
        /// Exports all nested NetScenes in the subtree.
        /// </summary>
        private void ExportNestedScenes(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            // Collect nested NetScenes recursively (entire subtree)
            _nestedSceneBuffer.Clear();
            CollectNestedNetScenesRecursive(netController, _nestedSceneBuffer);
            
            NetWriter.WriteByte(buffer, (byte)_nestedSceneBuffer.Count);
            
            for (int i = 0; i < _nestedSceneBuffer.Count; i++)
            {
                var nested = _nestedSceneBuffer[i];
                
                // Allocate peer-specific ID for this nested scene
                var nestedPeerId = currentWorld.TryRegisterPeerNode(nested, peer);
                if (nestedPeerId == 0)
                {
                    // Failed to allocate ID - write zeros so client can skip
                    NetWriter.WriteByte(buffer, 0);
                    NetWriter.WriteByte(buffer, 0);
                    NetWriter.WriteUInt16(buffer, 0);
                    continue;
                }
                
                var nestedSceneId = Protocol.PackScene(nested.NetSceneFilePath);
                if (nestedSceneId > 245)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, 
                        $"SceneId {nestedSceneId} exceeds safe limit (245). Too many registered scenes.");
                }
                
                NetWriter.WriteByte(buffer, nestedSceneId);
                NetWriter.WriteByte(buffer, nested.CachedNodePathIdInParent);
                NetWriter.WriteUInt16(buffer, nestedPeerId);
                
                // Mark nested as spawned so its NetPropertiesSerializer exports in same tick
                currentWorld.SetSpawnedForClient(nested.NetId, peer);
            }
        }
        
        /// <summary>
        /// Recursively collects all nested NetScenes in the subtree.
        /// </summary>
        private static void CollectNestedNetScenesRecursive(NetworkController parent, List<NetworkController> results)
        {
            foreach (var child in parent.DynamicNetworkChildren)
            {
                results.Add(child);
                // Recurse into child's nested scenes
                CollectNestedNetScenesRecursive(child, results);
            }
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

            // Reconcile local nested scenes against spawn data
            ProcessChildNodes(controllerOut, currentWorld);

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

        /// <summary>
        /// Reconciles local nested NetScenes against spawn data.
        /// Keeps matched scenes (syncs NetId), deletes unmatched local scenes,
        /// and creates new scenes from unmatched spawn data.
        /// </summary>
        private void ProcessChildNodes(NetworkController nodeOut, WorldRunner currentWorld)
        {
            // Collect all local nested scenes (flat list)
            CollectAllNestedScenes(nodeOut);
            
            // Match local instances against spawn data
            for (int i = 0; i < _allLocalNestedScenes.Count; i++)
            {
                var local = _allLocalNestedScenes[i];
                var localPathId = local.CachedNodePathIdInParent;
                var localSceneId = Protocol.PackScene(local.NetSceneFilePath);
                
                // Linear search spawn data for match
                int matchIndex = -1;
                for (int j = 0; j < _nestedDataCount; j++)
                {
                    if (_nestedDataBuffer[j].NodePathId == localPathId &&
                        _nestedDataBuffer[j].SceneId == localSceneId)
                    {
                        matchIndex = j;
                        break;
                    }
                }
                
                if (matchIndex >= 0)
                {
                    // Keep local, sync NetId
                    local.NetId = new NetId(_nestedDataBuffer[matchIndex].NetId);
                    local.IsClientSpawn = true;
                    local.CurrentWorld = currentWorld;
                    // Set NetParentId so it gets added to DynamicNetworkChildren
                    local.NetParentId = nodeOut.NetId;
                    // Register with WorldRunner so it can receive despawn commands
                    currentWorld.TryRegisterPeerNode(local);
                    // Mark as processed (use 246 as sentinel, > 245 reserved)
                    _nestedDataBuffer[matchIndex].SceneId = 246;
                }
                else
                {
                    // Server removed this - delete local
                    var parent = local.RawNode.GetParent();
                    parent?.RemoveChild(local.RawNode);
                    local.QueueNodeForDeletion();
                }
            }
            
            // Create any new NetScenes from unmatched spawn data
            for (int i = 0; i < _nestedDataCount; i++)
            {
                if (_nestedDataBuffer[i].SceneId >= 246 || _nestedDataBuffer[i].SceneId == 0) 
                    continue;
                
                var data = _nestedDataBuffer[i];
                var instance = Protocol.UnpackScene(data.SceneId).Instantiate<INetNodeBase>();
                instance.Network.NetId = new NetId(data.NetId);
                instance.Network.IsClientSpawn = true;
                instance.Network.CurrentWorld = currentWorld;
                
                // Add to correct parent node using the path
                Node targetParent;
                if (data.NodePathId == 255)
                {
                    // Direct child of root
                    targetParent = nodeOut.RawNode;
                }
                else
                {
                    targetParent = nodeOut.RawNode.GetNode(
                        Protocol.UnpackNode(nodeOut.NetSceneFilePath, data.NodePathId));
                }
                targetParent.AddChild(instance.Network.RawNode);
                
                // Set NetParentId so it gets added to DynamicNetworkChildren
                instance.Network.NetParentId = nodeOut.NetId;
                // Register with WorldRunner so it can receive despawn commands
                currentWorld.TryRegisterPeerNode(instance.Network);
            }
            
            // Also process static children (non-NetScene NetNodes)
            ProcessStaticChildNodes(nodeOut);
        }
        
        /// <summary>
        /// Processes static children (non-NetScene NetNodes) - sets up their network state.
        /// </summary>
        private void ProcessStaticChildNodes(NetworkController nodeOut)
        {
            // Use index-based iteration to avoid GetChildren() allocation
            ProcessStaticChildNodesRecursive(nodeOut.RawNode, nodeOut);
        }
        
        private void ProcessStaticChildNodesRecursive(Node node, NetworkController root)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                var child = node.GetChild(i);
                
                if (child is INetNodeBase netNodeBase)
                {
                    var networkChild = netNodeBase.Network;
                    if (networkChild != null)
                    {
                        if (networkChild.IsNetScene())
                        {
                            // Skip NetScenes - they're handled by ProcessChildNodes
                            continue;
                        }
                        
                        // Static child - set up network state
                        networkChild.IsClientSpawn = true;
                        networkChild.InputAuthority = root.InputAuthority;
                    }
                }
                
                // Recurse into children
                ProcessStaticChildNodesRecursive(child, root);
            }
        }
        
        /// <summary>
        /// Collects all nested NetScenes in the subtree into a flat list.
        /// Also computes CachedNodePathIdInParent for each.
        /// </summary>
        private void CollectAllNestedScenes(NetworkController root)
        {
            _allLocalNestedScenes.Clear();
            CollectNestedRecursive(root.RawNode, root.RawNode, root.NetSceneFilePath);
        }
        
        private void CollectNestedRecursive(Node treeRoot, Node node, string rootScenePath)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                var child = node.GetChild(i);
                
                if (child is INetNodeBase netNode && netNode.Network != null && netNode.Network.IsNetScene())
                {
                    _allLocalNestedScenes.Add(netNode.Network);
                    
                    // Compute and cache the node path ID for matching
                    var relativePath = treeRoot.GetPathTo(child);
                    if (relativePath == "." || relativePath.IsEmpty)
                    {
                        netNode.Network.CachedNodePathIdInParent = 255;
                    }
                    else if (Protocol.PackNode(rootScenePath, relativePath, out var pathId))
                    {
                        netNode.Network.CachedNodePathIdInParent = pathId;
                    }
                    else
                    {
                        netNode.Network.CachedNodePathIdInParent = 255;
                    }
                    
                    // Recurse INTO this nested scene to find deeper nested scenes
                    CollectNestedRecursive(treeRoot, child, rootScenePath);
                    continue;
                }
                
                CollectNestedRecursive(treeRoot, child, rootScenePath);
            }
        }

        private Data Deserialize(NetBuffer buffer)
        {
            var spawnData = new Data
            {
                classId = NetReader.ReadByte(buffer),
                parentId = NetReader.ReadUInt16(buffer),
            };

            if (spawnData.parentId == 0)
            {
                // Root scene - read nested count
                spawnData.nestedCount = NetReader.ReadByte(buffer);
                DeserializeNestedScenes(buffer, spawnData.nestedCount);
                return spawnData;
            }

            spawnData.nodePathId = NetReader.ReadByte(buffer);
            spawnData.hasInputAuthority = NetReader.ReadByte(buffer);
            
            // Read nested scenes
            spawnData.nestedCount = NetReader.ReadByte(buffer);
            DeserializeNestedScenes(buffer, spawnData.nestedCount);
            
            return spawnData;
        }
        
        private static void DeserializeNestedScenes(NetBuffer buffer, int count)
        {
            _nestedDataCount = 0;
            
            for (int i = 0; i < count && i < _nestedDataBuffer.Length; i++)
            {
                var sceneId = NetReader.ReadByte(buffer);
                var nodePathId = NetReader.ReadByte(buffer);
                var netId = NetReader.ReadUInt16(buffer);
                
                // Skip entries where allocation failed on server (netId == 0)
                // Note: sceneId=0 is valid (first registered scene), but netId=0 means no allocation
                if (netId == 0) continue;
                
                _nestedDataBuffer[_nestedDataCount++] = new NestedSceneData
                {
                    SceneId = sceneId,
                    NodePathId = nodePathId,
                    NetId = netId
                };
            }
        }

        public void _Process(double delta) {}
    }
}
