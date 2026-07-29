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

        /// <summary>
        /// The most recent tick at which spawn data was written for each peer. Spawn data
        /// re-ships every tick while Spawning (see Export), so together with the soft-interest
        /// revert this keeps the invariant "spawn rode every exported tick in
        /// [setupTick, lastSpawnSendTick]" - which is what makes the cumulative ack commit in
        /// Acknowledge sound: an acked tick inside that range is a packet that provably
        /// contained the spawn data.
        /// </summary>
        private Dictionary<UUID, Tick> lastSpawnSendTicks = new();

        private bool hasImported = false; // Track if this serializer has already imported

        /// <summary>One-shot guard so an unpackable spawn path logs once, not every tick.</summary>
        private bool _loggedUnpackableSpawnPath = false;

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
            lastSpawnSendTicks.Remove(peerId);
        }

        /// <summary>
        /// Tells every sibling serializer to forget its per-peer delta/ack baseline. Called at
        /// each NotSpawned -&gt; Spawning transition: the client is about to build this node from
        /// scratch (first spawn, or a respawn after interest loss destroyed its copy), so its
        /// applied-state history is empty. Any baseline retained server-side from a previous
        /// incarnation would make the next export delta against a tick the fresh client node
        /// can never resolve - the payload gets discarded, and (before Import reported
        /// discards) the ack latched the mismatch in place permanently.
        /// </summary>
        private static void ResetPeerBaselines(NetworkController controller, UUID peerId)
        {
            var serializers = controller.NetNode?.Serializers;
            if (serializers == null) return;
            for (int i = 0; i < serializers.Length; i++)
            {
                serializers[i].ResetPeerBaseline(peerId);
            }
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

            // Per-peer HARD despawn: peer-filter enabled and this peer isn't a member.
            // Unlike interest-layer loss (soft), removing a peer from a restricted InterestPeers
            // set fully despawns the node on that client, while keeping it alive server-side.
            bool inPeerSet = !netController.RestrictToInterestPeers
                             || netController.InterestPeers.Contains(peerId);
            if (!inPeerSet)
            {
                // Only despawn if the peer actually has (or is receiving) the node.
                // NotSpawned/Despawned: nothing to do, leave state so a future re-add spawns cleanly.
                if (spawnState is WorldRunner.ClientSpawnState.Spawning
                    or WorldRunner.ClientSpawnState.Spawned
                    or WorldRunner.ClientSpawnState.Despawning)
                {
                    ExportDespawn(currentWorld, peer, peerId, spawnState, buffer);
                }
                return;
            }

            // Soft path: fails interest LAYERS / [NetInterest].
            if (!netController.IsPeerInterested(peer))
            {
                // Interest dropped while the spawn was still in flight. Resends stop here, so
                // an unacked Spawning state would break the contiguity invariant behind the
                // Acknowledge commit rule ("spawn rode every tick in [setupTick, lastSent]")
                // and a later cumulative ack could commit a spawn the client never received.
                // Revert to never-spawned: on interest regain the node runs a fresh spawn
                // cycle - same local id (registration is idempotent), and a client that did
                // receive one of the earlier sends consumes-and-skips the duplicate.
                if (spawnState == WorldRunner.ClientSpawnState.Spawning && setupTicks.ContainsKey(peerId))
                {
                    setupTicks.Remove(peerId);
                    lastSpawnSendTicks.Remove(peerId);
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.NotSpawned);
                }
                return;
            }

            // Interested and in the peer set. If this peer was previously hard-despawned due to
            // peer-set exclusion, reset so we send a fresh spawn (new local node id + full resync).
            if (spawnState == WorldRunner.ClientSpawnState.Despawned)
            {
                currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.NotSpawned);
                spawnState = WorldRunner.ClientSpawnState.NotSpawned;
            }

            // Fully delivered - the peer acked a packet that contained the spawn data.
            if (spawnState == WorldRunner.ClientSpawnState.Spawned)
            {
                return;
            }

            // Spawning falls through: spawn data re-ships every tick until the ack commits it.
            // The tick channel is unreliable, so a fire-once spawn on a lost packet left the
            // client with props for a node it never built (a blank NetNode3D whose props
            // serializer then misreads the stream), while the cumulative ack still marked it
            // Spawned server-side. Resending until acked is the same contract despawn already
            // uses (see ExportDespawn's Despawning case). First-send-only side effects below
            // are gated on this flag.
            bool firstSend = spawnState == WorldRunner.ClientSpawnState.NotSpawned;

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

            // Child spawns address their attachment point as (parent scene, packed node path).
            // Resolve it BEFORE writing anything: an unpackable path must not throw out of
            // Export (that aborts the whole export tick for every node and peer) and must not
            // leave partial bytes in the buffer. Unpackable happens when the node's Godot
            // parent is a path the protocol registry doesn't cover - e.g. a node reparented
            // at runtime under an unregistered container.
            byte nodePathId = 0;
            if (netController.NetParent != null)
            {
                var relativePath = netController.NetParent.RawNode.GetPathTo(netController.RawNode.GetParent());
                if (relativePath == "." || relativePath.IsEmpty)
                {
                    // Direct child of parent's root - 255 is the special marker
                    nodePathId = 255;
                }
                else if (!Protocol.PackNode(netController.NetParent.RawNode.SceneFilePath, relativePath, out nodePathId))
                {
                    // Nothing written; the node simply stays pending and retries next tick
                    // (delivery becomes possible if the path is registered in a future
                    // protocol build). Logged once per node, not per tick.
                    if (!_loggedUnpackableSpawnPath)
                    {
                        _loggedUnpackableSpawnPath = true;
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                            $"[SpawnSerializer] Cannot spawn {netController.RawNode.GetPath()}: node path '{relativePath}' is not in the protocol registry for scene '{netController.NetParent.RawNode.SceneFilePath}'. Spawn stays pending; further occurrences suppressed.");
                    }
                    return;
                }
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

                // Stamped every send (first and resends) - the upper bound of the ack window.
                lastSpawnSendTicks[peerId] = currentWorld.CurrentTick;

                // Mark spawn as being sent (waiting for ACK)
                if (firstSend)
                {
                    ResetPeerBaselines(netController, peerId);
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Spawning);
                }
                return;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, netController.NetParent);
            NetWriter.WriteUInt16(buffer, parentId);

            // Attachment path within the parent scene, resolved (or bailed on) above.
            NetWriter.WriteByte(buffer, nodePathId);

            // Use ID comparison instead of Equals - more reliable for ENet.Peer structs
            var hasInputAuth = netController.InputAuthority.IsSet && netController.InputAuthority.ID == peer.ID ? (byte)1 : (byte)0;
            NetWriter.WriteByte(buffer, hasInputAuth);

            // Write nested NetScenes
            ExportNestedScenes(currentWorld, peer, buffer);

            // Stamped every send (first and resends) - the upper bound of the ack window.
            lastSpawnSendTicks[peerId] = currentWorld.CurrentTick;

            // Mark spawn as being sent (waiting for ACK)
            if (firstSend)
            {
                ResetPeerBaselines(netController, peerId);
                currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Spawning);
            }

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

                var peerUUID = NetRunner.Instance.GetPeerId(peer);

                // Nested scenes ride along in the parent's spawn data, so the client is about
                // to build them from scratch - their per-peer baselines must reset like the
                // parent's own NotSpawned -> Spawning transition. This must cover EVERY state
                // except Spawning (which means "already in this parent's resend stream" -
                // resetting per resend would wipe props delta state every tick). Notably it
                // must cover stale Spawned: a parent that was per-peer despawned takes its
                // nested subtree down with it CLIENT-side (QueueFree frees children), but
                // despawn does not cascade server-side, so the nested node still reads
                // Spawned here while the client is about to rebuild it with an empty applied
                // ring. Skipping the reset then leaves a delta baseline the rebuilt node can
                // never resolve ("missing applied-state baseline" bursts on respawn).
                if (currentWorld.GetClientSpawnState(nested.NetId, peer) != WorldRunner.ClientSpawnState.Spawning)
                {
                    ResetPeerBaselines(nested, peerUUID);
                }

                // IMPORTANT: Set the nested scene's state to Spawning since we're including it in the parent's spawn data.
                // Without this, despawn logic would see NotSpawned and skip sending despawn data.
                currentWorld.SetClientSpawnState(nested.NetId, peer, WorldRunner.ClientSpawnState.Spawning);

                // Also set up the nested scene's SpawnSerializer setupTick for ACK tracking
                if (nested.NetNode?.Serializers != null && nested.NetNode.Serializers.Length > 0
                    && nested.NetNode.Serializers[0] is SpawnSerializer nestedSpawnSerializer)
                {
                    if (!nestedSpawnSerializer.setupTicks.ContainsKey(peerUUID))
                    {
                        nestedSpawnSerializer.setupTicks[peerUUID] = currentWorld.CurrentTick;
                    }
                    // Every inclusion (the nested payload rides every parent resend), so the
                    // nested node's own ack window tracks the parent's resend stream.
                    nestedSpawnSerializer.lastSpawnSendTicks[peerUUID] = currentWorld.CurrentTick;
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

        public bool Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick)
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
                    lastSpawnSendTicks.Remove(peerId);

                    // Free the local NetId for this peer so it can be reused
                    currentWorld.DeregisterPeerNode(netController, peer);

                    // Check if all peers have acknowledged despawn.
                    // Only delete the node globally for a genuine global despawn (IsQueuedForDespawn).
                    // Interest-driven per-peer despawns must keep the node alive server-side so it
                    // can re-spawn when the peer regains interest.
                    if (netController.IsQueuedForDespawn && currentWorld.AreAllPeersDespawned(netController.NetId))
                    {
                        // All peers have despawned, add to pending deletion
                        currentWorld._pendingDeletion.Add(netController);
                    }
                }
                // If despawn is pending (tick < despawnTick), don't process spawn ACK
                // The node is being despawned, so transitioning to Spawned would be wrong
                return despawnTicks.ContainsKey(peerId) || setupTicks.ContainsKey(peerId);
            }

            // Handle spawn acknowledgment (only if no despawn is pending).
            //
            // Commit only when the acked tick falls inside [setupTick, lastSpawnSendTick]:
            // spawn data rides every exported tick in that window (Export resends while
            // Spawning; the soft-interest revert keeps the window contiguous), so an ack
            // inside it is a packet that provably contained the spawn data. A bare
            // `tick >= setupTick` would also commit on acks of ticks that carried only this
            // node's props - which is exactly how a lost spawn packet used to get marked
            // Spawned while the client sat on a blank node.
            if (setupTicks.TryGetValue(peerId, out var setupTick) && setupTick != 0)
            {
                if (tick >= setupTick
                    && lastSpawnSendTicks.TryGetValue(peerId, out var lastSent)
                    && tick <= lastSent)
                {
                    currentWorld.SetSpawnedForClient(netController.NetId, peer);
                    setupTicks.Remove(peerId); // Clean up after successful ack
                    lastSpawnSendTicks.Remove(peerId);
                }
            }

            // Still pending while an unacked spawn or despawn send exists for this peer
            return setupTicks.ContainsKey(peerId) || despawnTicks.ContainsKey(peerId);
        }

        // Import is client-only and infrequent, less critical to optimize
        public bool Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController controllerOut)
        {
            controllerOut = netController;

            // Check if this is a despawn message (first byte is DESPAWN_MARKER)
            var firstByte = NetReader.ReadByte(buffer);
            if (firstByte == DESPAWN_MARKER)
            {
                ImportDespawn(currentWorld, buffer);
                return true;
            }

            // Not a despawn - continue with normal spawn import
            // We already read the classId, so reconstruct the data
            var data = DeserializeAfterClassId(buffer, firstByte);

            // Skip if this node was already properly imported
            if (hasImported)
            {
                return true;
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
                // The spawn bytes were consumed but the node was never built. Withholding the
                // ack keeps this tick out of the delta-baseline bookkeeping, and since the
                // server resends spawn data every tick until an ack inside its send window
                // commits it, the spawn simply re-arrives next tick (by which point the
                // parent may exist).
                return false;
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
                return true;
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
            return true;
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
