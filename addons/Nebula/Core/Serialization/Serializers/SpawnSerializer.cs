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
            public byte HasInputAuthority;
        }

        // Pre-allocated buffers for nested scene handling (static to avoid per-instance allocation)
        private static readonly List<NetworkController> _nestedSceneBuffer = new(16);
        private static readonly NestedSceneData[] _nestedDataBuffer = new NestedSceneData[64];
        private static int _nestedDataCount;
        private static readonly List<NetworkController> _allLocalNestedScenes = new(64);

        private NetworkController netController;
        private Dictionary<UUID, Tick> setupTicks = new();
        private Dictionary<UUID, Tick> despawnTicks = new(); // Track when despawn was sent per peer
        private bool hasImported = false; // Track if this serializer has already imported

        /// <summary>
        /// Despawn marker byte - when first byte is 255, it's a despawn message.
        /// </summary>
        private const byte DESPAWN_MARKER = 255;

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
            despawnTicks.Remove(peerId);
        }

        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            var spawnState = currentWorld.GetClientSpawnState(netController.NetId, peer);

            // Handle despawn case
            if (netController.IsQueuedForDespawn)
            {
                ExportDespawn(currentWorld, peer, peerId, spawnState, buffer);
                return;
            }

            if (!netController.IsPeerInterested(peer))
            {
                return;
            }

            // Check if spawn data has already been sent (Spawning or Spawned state)
            if (spawnState != WorldRunner.ClientSpawnState.NotSpawned)
            {
                // Already sent spawn data (or ACKed), skip
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

                // Mark spawn as being sent (waiting for ACK)
                currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Spawning);
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

            // Use ID comparison instead of Equals - more reliable for ENet.Peer structs
            var hasInputAuth = netController.InputAuthority.IsSet && netController.InputAuthority.ID == peer.ID ? (byte)1 : (byte)0;
            NetWriter.WriteByte(buffer, hasInputAuth);

            // Write nested NetScenes
            ExportNestedScenes(currentWorld, peer, buffer);

            // Mark spawn as being sent (waiting for ACK)
            currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Spawning);

            currentWorld.Debug?.Send("Spawn", $"Exported:{netController.RawNode.SceneFilePath}");
        }

        /// <summary>
        /// Exports despawn data for a node that is queued for despawn.
        /// </summary>
        private void ExportDespawn(WorldRunner currentWorld, NetPeer peer, UUID peerId, WorldRunner.ClientSpawnState spawnState, NetBuffer buffer)
        {
            // First check if the node is actually registered for this peer
            // If not registered, we can't send despawn data (no local node ID to reference)
            var localNodeId = currentWorld.GetPeerNodeId(peer, netController);
            bool isRegistered = localNodeId != 0;

            switch (spawnState)
            {
                case WorldRunner.ClientSpawnState.NotSpawned:
                    // Peer never received spawn, mark as despawned immediately (no data to send)
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                    break;

                case WorldRunner.ClientSpawnState.Spawning:
                case WorldRunner.ClientSpawnState.Spawned:
                    if (!isRegistered)
                    {
                        // This should never happen - if state is Spawning/Spawned, the node must be registered.
                        // If we hit this, there's a bug in state management that needs investigation.
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                            $"[SpawnSerializer] BUG: Node {netController.RawNode?.Name} (NetId={netController.NetId}) has state {spawnState} but isn't registered for peer. This indicates a state machine violation.");
                        currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                        break;
                    }
                    // Peer received (or is receiving) spawn, send despawn data
                    WriteDespawnData(currentWorld, peer, peerId, localNodeId, buffer);
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawning);
                    break;

                case WorldRunner.ClientSpawnState.Despawning:
                    if (!isRegistered)
                    {
                        // Already deregistered, mark as despawned
                        currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                        break;
                    }
                    // Already sent despawn, resend until ACKed
                    WriteDespawnData(currentWorld, peer, peerId, localNodeId, buffer);
                    break;

                case WorldRunner.ClientSpawnState.Despawned:
                    // Already despawned for this peer, nothing to do
                    break;
            }
        }

        /// <summary>
        /// Writes the despawn data to the buffer.
        /// Format: [DESPAWN_MARKER (1 byte)] [LocalNodeId (2 bytes)]
        /// </summary>
        private void WriteDespawnData(WorldRunner currentWorld, NetPeer peer, UUID peerId, ushort localNodeId, NetBuffer buffer)
        {
            // Only set despawnTick on FIRST export - don't overwrite on re-exports
            if (!despawnTicks.ContainsKey(peerId))
            {
                despawnTicks[peerId] = currentWorld.CurrentTick;
            }

            // Write despawn marker
            NetWriter.WriteByte(buffer, DESPAWN_MARKER);

            // Write the local node ID for this peer so client knows which node to despawn
            NetWriter.WriteUInt16(buffer, localNodeId);

            currentWorld.Debug?.Send("Despawn", $"Exported despawn for {netController.RawNode?.Name}, localNodeId={localNodeId}");
        }

        /// <summary>
        /// Exports all nested NetScenes in the subtree that the peer has interest in.
        /// </summary>
        private void ExportNestedScenes(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            // Collect nested NetScenes recursively (entire subtree)
            _nestedSceneBuffer.Clear();
            CollectNestedNetScenesRecursive(netController, _nestedSceneBuffer);

            // Filter to only include scenes the peer has interest in
            _interestedNestedBuffer.Clear();
            for (int i = 0; i < _nestedSceneBuffer.Count; i++)
            {
                var nested = _nestedSceneBuffer[i];
                if (nested.IsPeerInterested(peer))
                {
                    _interestedNestedBuffer.Add(nested);
                }
            }

            NetWriter.WriteByte(buffer, (byte)_interestedNestedBuffer.Count);

            for (int i = 0; i < _interestedNestedBuffer.Count; i++)
            {
                var nested = _interestedNestedBuffer[i];

                // Allocate peer-specific ID for this nested scene
                var nestedPeerId = currentWorld.TryRegisterPeerNode(nested, peer);
                if (nestedPeerId == 0)
                {
                    // Failed to allocate ID - write zeros so client can skip
                    NetWriter.WriteByte(buffer, 0);
                    NetWriter.WriteByte(buffer, 0);
                    NetWriter.WriteUInt16(buffer, 0);
                    NetWriter.WriteByte(buffer, 0);
                    continue;
                }

                // IMPORTANT: Set the nested scene's state to Spawning since we're including it in the parent's spawn data.
                // Without this, despawn logic would see NotSpawned and skip sending despawn data.
                currentWorld.SetClientSpawnState(nested.NetId, peer, WorldRunner.ClientSpawnState.Spawning);

                // Also set up the nested scene's SpawnSerializer setupTick for ACK tracking
                if (nested.NetNode?.Serializers != null && nested.NetNode.Serializers.Length > 0
                    && nested.NetNode.Serializers[0] is SpawnSerializer nestedSpawnSerializer)
                {
                    var peerUUID = NetRunner.Instance.GetPeerId(peer);
                    if (!nestedSpawnSerializer.setupTicks.ContainsKey(peerUUID))
                    {
                        nestedSpawnSerializer.setupTicks[peerUUID] = currentWorld.CurrentTick;
                    }
                }

                var nestedSceneId = Protocol.PackScene(nested.NetSceneFilePath);
                if (nestedSceneId > 245)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"SceneId {nestedSceneId} exceeds safe limit (245). Too many registered scenes.");
                }

                // Check if this peer owns the nested scene
                var nestedHasInputAuth = nested.InputAuthority.IsSet && nested.InputAuthority.ID == peer.ID ? (byte)1 : (byte)0;

                NetWriter.WriteByte(buffer, nestedSceneId);
                NetWriter.WriteByte(buffer, nested.CachedNodePathIdInParent);
                NetWriter.WriteUInt16(buffer, nestedPeerId);
                NetWriter.WriteByte(buffer, nestedHasInputAuth);
            }
        }

        // Reusable buffer for interested nested scenes to avoid allocation
        private List<NetworkController> _interestedNestedBuffer = new(64);

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

            // Handle despawn acknowledgment FIRST (takes priority over spawn)
            // If despawn is in progress, we don't want spawn ACK to overwrite the state
            if (despawnTicks.TryGetValue(peerId, out var despawnTick) && despawnTick != 0)
            {
                if (tick >= despawnTick)
                {
                    // Despawn acknowledged
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                    despawnTicks.Remove(peerId); // Clean up after successful ack
                    setupTicks.Remove(peerId); // Also clean up spawn tracking since despawn supersedes it

                    // Free the local NetId for this peer so it can be reused
                    currentWorld.DeregisterPeerNode(netController, peer);

                    // Check if all peers have acknowledged despawn
                    if (currentWorld.AreAllPeersDespawned(netController.NetId))
                    {
                        // All peers have despawned, add to pending deletion
                        currentWorld._pendingDeletion.Add(netController);
                    }
                }
                // If despawn is pending (tick < despawnTick), don't process spawn ACK
                // The node is being despawned, so transitioning to Spawned would be wrong
                return;
            }

            // Handle spawn acknowledgment (only if no despawn is pending)
            if (setupTicks.TryGetValue(peerId, out var setupTick) && setupTick != 0)
            {
                if (tick >= setupTick)
                {
                    currentWorld.SetSpawnedForClient(netController.NetId, peer);
                    setupTicks.Remove(peerId); // Clean up after successful ack
                }
            }
        }

        // Import is client-only and infrequent, less critical to optimize
        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController controllerOut)
        {
            controllerOut = netController;

            // Check if this is a despawn message (first byte is DESPAWN_MARKER)
            var firstByte = NetReader.ReadByte(buffer);
            if (firstByte == DESPAWN_MARKER)
            {
                ImportDespawn(currentWorld, buffer);
                return;
            }

            // Not a despawn - continue with normal spawn import
            // We already read the classId, so reconstruct the data
            var data = DeserializeAfterClassId(buffer, firstByte);

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
                // Debugger.Instance.Log($"[SpawnSerializer.Import] ROOT SCENE - calling ChangeScene, controllerOut.NetId={controllerOut.NetId}, scenePath='{controllerOut.NetSceneFilePath}'");
                currentWorld.ChangeScene(controllerOut);
                currentWorld.Debug?.Send("Spawn", $"Imported:{controllerOut.NetSceneFilePath}");

                // Check for pending despawn after spawn completes
                CheckPendingDespawn(currentWorld, controllerOut);
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

            // Check for pending despawn after spawn completes
            CheckPendingDespawn(currentWorld, controllerOut);
        }

        /// <summary>
        /// Handles importing a despawn message on the client.
        /// </summary>
        private void ImportDespawn(WorldRunner currentWorld, NetBuffer buffer)
        {
            // Read the local node ID
            var localNodeId = NetReader.ReadUInt16(buffer);

            // Look up the node
            var node = currentWorld.GetNodeFromNetId(localNodeId);

            if (node != null)
            {
                // Node exists, despawn it
                node.handleDespawn();
            }
            else
            {
                // Node doesn't exist yet - despawn arrived before spawn (packet loss)
                // Add to pending despawns so it gets despawned when spawn arrives
                var netId = new NetId(localNodeId);
                currentWorld.AddPendingClientDespawn(netId);
            }
        }

        /// <summary>
        /// Checks if the newly spawned node has a pending despawn and handles it.
        /// </summary>
        private void CheckPendingDespawn(WorldRunner currentWorld, NetworkController controller)
        {
            if (currentWorld.CheckAndRemovePendingClientDespawn(controller.NetId))
            {
                // There was a pending despawn for this node
                controller.handleDespawn();
            }
        }

        /// <summary>
        /// Deserializes spawn data after the classId has already been read.
        /// </summary>
        private Data DeserializeAfterClassId(NetBuffer buffer, byte classId)
        {
            var spawnData = new Data
            {
                classId = classId,
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
                    // Set InputAuthority if this client owns the nested scene
                    if (_nestedDataBuffer[matchIndex].HasInputAuthority == 1)
                    {
                        local.InputAuthority = NetRunner.Instance.ServerPeer;
                        currentWorld.MarkOwnedEntitiesDirty();
                    }
                    // Set NetParentId so it gets added to DynamicNetworkChildren
                    local.NetParentId = nodeOut.NetId;
                    // Register with WorldRunner so it can receive despawn commands
                    currentWorld.TryRegisterPeerNode(local);
                    // Mark the nested scene's SpawnSerializer as imported to prevent duplicate import
                    if (local.NetNode.Serializers.Length > 0 && local.NetNode.Serializers[0] is SpawnSerializer nestedSpawnSerializer)
                    {
                        nestedSpawnSerializer.hasImported = true;
                    }
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
                // Set InputAuthority if this client owns the nested scene
                if (data.HasInputAuthority == 1)
                {
                    instance.Network.InputAuthority = NetRunner.Instance.ServerPeer;
                    currentWorld.MarkOwnedEntitiesDirty();
                }

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
                // Mark the nested scene's SpawnSerializer as imported to prevent duplicate import
                // (serializers are already created during NotificationSceneInstantiated)
                if (instance.Serializers.Length > 0 && instance.Serializers[0] is SpawnSerializer nestedSpawnSerializer)
                {
                    nestedSpawnSerializer.hasImported = true;
                }
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
                var hasInputAuth = NetReader.ReadByte(buffer);

                // Skip entries where allocation failed on server (netId == 0)
                // Note: sceneId=0 is valid (first registered scene), but netId=0 means no allocation
                if (netId == 0) continue;

                _nestedDataBuffer[_nestedDataCount++] = new NestedSceneData
                {
                    SceneId = sceneId,
                    NodePathId = nodePathId,
                    NetId = netId,
                    HasInputAuthority = hasInputAuth
                };
            }
        }

        public void _Process(double delta) { }
    }
}
