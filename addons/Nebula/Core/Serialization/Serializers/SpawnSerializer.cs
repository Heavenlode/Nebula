using System;
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

        // Pre-allocated scratch for nested scene handling, shared across every SpawnSerializer
        // instance to avoid a per-instance allocation. Each buffer is filled and consumed within a
        // single Export/Import call chain, so sharing is safe as long as only one such chain runs
        // at a time.
        //
        // [ThreadStatic] is what keeps that true once worlds tick on their own threads (see
        // NetRunner's per_world_thread_group setting): two worlds exporting concurrently would
        // otherwise stomp each other's spawn tables, which surfaces as a desync rather than a
        // crash. Note that [ThreadStatic] and field initializers do NOT mix -- an initializer runs
        // only on the first thread to touch the type and every other thread silently sees null --
        // so these are lazily initialized through the properties below and must only ever be
        // reached that way.
        [ThreadStatic] private static List<NetworkController> _nestedSceneBuffer;
        [ThreadStatic] private static NestedSceneData[] _nestedDataBuffer;
        [ThreadStatic] private static int _nestedDataCount;
        [ThreadStatic] private static List<NetworkController> _allLocalNestedScenes;

        private static List<NetworkController> NestedSceneBuffer => _nestedSceneBuffer ??= new(16);
        private static NestedSceneData[] NestedDataBuffer => _nestedDataBuffer ??= new NestedSceneData[64];
        private static List<NetworkController> AllLocalNestedScenes => _allLocalNestedScenes ??= new(64);

        private NetworkController netController;

        /// <summary>
        /// Per-peer contiguous run of ticks whose packets carried this node's spawn record
        /// (see SendWindow). Presence in the dictionary means "a spawn send is in flight
        /// (unacked)"; Acknowledge commits Spawning -> Spawned only for an acked tick the
        /// window Covers. Budget splitting can defer a resend on any tick - RecordSend's
        /// gap-restart is what keeps the commit rule sound when that happens.
        /// </summary>
        private Dictionary<UUID, SendWindow> spawnWindows = new();

        /// <summary>Same contract as <see cref="spawnWindows"/>, for despawn markers.</summary>
        private Dictionary<UUID, SendWindow> despawnWindows = new();

        /// <summary>
        /// What the bytes of the immediately preceding Export were, so CommitExport knows
        /// which packet-coupled stamps to apply. Valid only between an Export and its
        /// commit (host contract: CommitExport runs before any other Export on this
        /// instance, on the same world tick thread).
        /// </summary>
        private enum PendingCommit : byte
        {
            None,
            Spawn,
            Despawn,
        }

        private PendingCommit _pendingCommit;
        private bool _pendingFirstSend;

        /// <summary>
        /// Nested children whose table entries were written by the preceding Export
        /// (registration succeeded). Their Spawning flips and window stamps apply in
        /// CommitExport - stamping at write time would corrupt their ack windows when the
        /// host drops this record for budget.
        /// </summary>
        private List<NetworkController> _pendingNestedCommit = new(64);

        private bool hasImported = false; // Track if this serializer has already imported

        /// <summary>One-shot guard so an unpackable spawn path logs once, not every tick.</summary>
        private bool _loggedUnpackableSpawnPath = false;

        // Wire record, bit-packed and padded to a byte at the end of the section:
        //   [isDespawn:1]
        //   despawn: [localNodeId:9]
        //   spawn:   [sceneId:8][parentId:9] then, for a child, [nodePathId:8][hasInputAuth:1];
        //            then [nestedCount:8] and per entry [sceneId:8][nodePathId:8][netId:9][hasInputAuth:1]
        // Peer-local node ids are 8 groups x 64 (NodeIdUtils), so 9 bits.
        private const int SCENE_ID_BITS = 8;
        private const int NODE_ID_BITS = 9;
        private const int NODE_PATH_BITS = 8;
        private const int INPUT_AUTH_BITS = 1;

        /// <summary>The nested-table count preceding the entries.</summary>
        private const int NESTED_COUNT_BITS = 8;

        /// <summary>
        /// One nested-table entry: sceneId + nodePathId + netId + hasInputAuthority. Must
        /// match ExportNestedScenes' writes.
        /// </summary>
        private const int NESTED_ENTRY_BITS = SCENE_ID_BITS + NODE_PATH_BITS + NODE_ID_BITS + INPUT_AUTH_BITS;

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
            spawnWindows.Remove(peerId);
            despawnWindows.Remove(peerId);
        }

        /// <summary>
        /// Server-side despawn cascade over every nested NetScene under a despawning parent.
        /// The client applies a despawn by freeing the parent's whole Godot subtree - nested
        /// NetScenes included - so their per-peer server state must follow. Runs twice per
        /// despawn, split by what is safe when:
        ///
        /// Send-time (freeIds: false), from ExportDespawn at each transition into despawn:
        /// marks the children Despawned, which silences their props/resync exporters in the
        /// same tick the parent's despawn marker first ships. ClientProcessTick's monotonic
        /// tick guard means the client never applies a packet older than an applied despawn,
        /// so no packet the client processes after freeing the subtree can carry child data -
        /// the "props for an unknown node" window is unreachable through this path. Children
        /// already running their own despawn (IsQueuedForDespawn, or Despawning for this
        /// peer) are left alone: their exporters are already silenced by their own state, and
        /// flipping them Despawned here would clear their pending despawn ack and let the
        /// AreAllPeersDespawned sweep delete them before anything freed their per-peer local
        /// ids - a permanent id leak against the per-peer node cap.
        ///
        /// Ack-time (freeIds: true), from Acknowledge's despawn branch: the ack proves the
        /// client's subtree is gone, so now - and only now - the children's local ids are
        /// freed. Freeing at send-time would let TryRegisterPeerNode hand an id to a new node
        /// while the client's old node still occupies it; the old node's hasImported guard
        /// would consume-and-skip the new node's spawn, silently binding the stream to the
        /// wrong node. DeregisterPeerNode is idempotent, so the id sweep runs unconditionally
        /// - most children were already marked Despawned by the send-time pass and the state
        /// guard must not skip their deregistration.
        ///
        /// Recurses unconditionally: a grandchild can be spawned for the peer even when the
        /// intermediate child is not (spawn tables collect the whole subtree,
        /// interest-filtered per node).
        /// </summary>
        private static void CascadeDespawnToNestedChildren(WorldRunner currentWorld, NetPeer peer, UUID peerId, NetworkController parent, bool freeIds)
        {
            foreach (var child in parent.DynamicNetworkChildren)
            {
                var state = currentWorld.GetClientSpawnState(child.NetId, peer);
                bool active = state != WorldRunner.ClientSpawnState.NotSpawned
                    && state != WorldRunner.ClientSpawnState.Despawned;
                bool ownDespawnInFlight = child.IsQueuedForDespawn
                    || state == WorldRunner.ClientSpawnState.Despawning;

                if (active && (freeIds || !ownDespawnInFlight))
                {
                    currentWorld.SetClientSpawnState(child.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                    ResetPeerBaselines(child, peerId);

                    if (child.NetNode?.Serializers != null && child.NetNode.Serializers.Length > 0
                        && child.NetNode.Serializers[0] is SpawnSerializer childSpawn)
                    {
                        childSpawn.spawnWindows.Remove(peerId);
                        childSpawn.despawnWindows.Remove(peerId);
                    }
                }

                if (freeIds)
                {
                    currentWorld.DeregisterPeerNode(child, peer);
                }

                CascadeDespawnToNestedChildren(currentWorld, peer, peerId, child, freeIds);
            }
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

        public ExportResult Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits)
        {
            // Atomic serializer: a spawn/despawn record cannot be split, so this may
            // write more than maxBits; the host then drops the bits and skips
            // CommitExport. Packet-coupled stamps live in CommitExport, so a dropped
            // record retries cleanly next tick.
            _pendingCommit = PendingCommit.None;
            var start = buffer.WriteBitPosition;
            ExportCore(currentWorld, peer, buffer, maxBits);
            return buffer.WriteBitPosition == start ? ExportResult.None : ExportResult.Written;
        }

        public void CommitExport(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            switch (_pendingCommit)
            {
                case PendingCommit.Spawn:
                {
                    spawnWindows.TryGetValue(peerId, out var window);
                    window.RecordSend(tick);
                    spawnWindows[peerId] = window;

                    if (_pendingFirstSend)
                    {
                        ResetPeerBaselines(netController, peerId);
                        currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Spawning);
                    }

                    // Nested children rode this record's spawn table: flip them Spawning
                    // and stamp their own windows so their acks track the parent's
                    // resend stream. States cannot have changed since Export (same
                    // thread, adjacent calls), so this evaluates as it would have at
                    // write time. The baseline reset must cover EVERY state except
                    // Spawning (= "already in this parent's resend stream"; resetting per
                    // resend would wipe props delta state every tick) - notably stale
                    // Spawned: a per-peer despawned parent takes its nested subtree down
                    // client-side (QueueFree frees children) without a server-side
                    // cascade, so the nested node still reads Spawned while the client is
                    // about to rebuild it with an empty applied ring. Skipping the reset
                    // then leaves a delta baseline the rebuilt node can never resolve
                    // ("missing applied-state baseline" bursts on respawn).
                    for (int i = 0; i < _pendingNestedCommit.Count; i++)
                    {
                        var nested = _pendingNestedCommit[i];
                        if (currentWorld.GetClientSpawnState(nested.NetId, peer) != WorldRunner.ClientSpawnState.Spawning)
                        {
                            ResetPeerBaselines(nested, peerId);
                        }
                        currentWorld.SetClientSpawnState(nested.NetId, peer, WorldRunner.ClientSpawnState.Spawning);

                        if (nested.NetNode?.Serializers != null && nested.NetNode.Serializers.Length > 0
                            && nested.NetNode.Serializers[0] is SpawnSerializer nestedSpawnSerializer)
                        {
                            nestedSpawnSerializer.spawnWindows.TryGetValue(peerId, out var nestedWindow);
                            nestedWindow.RecordSend(tick);
                            nestedSpawnSerializer.spawnWindows[peerId] = nestedWindow;

                            // The child committed no section of its own, so the host would
                            // never route this tick's ack to it - and the window just
                            // stamped would be unreachable. Register it as a rider of this
                            // packet. (Before the per-tick ack ring, a child with nothing
                            // else to send could sit in Spawning until it happened to export.)
                            currentWorld.NoteNestedSpawnRider(nested);
                        }
                    }
                    break;
                }

                case PendingCommit.Despawn:
                {
                    despawnWindows.TryGetValue(peerId, out var window);
                    window.RecordSend(tick);
                    despawnWindows[peerId] = window;

                    if (_pendingFirstSend)
                    {
                        // The marker genuinely shipped: silence the nested subtree in the
                        // same tick (see CascadeDespawnToNestedChildren). Must not run
                        // when the record was dropped for budget - the client still has
                        // a live copy and keeps receiving props until the marker ships.
                        currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawning);
                        CascadeDespawnToNestedChildren(currentWorld, peer, peerId, netController, freeIds: false);
                    }
                    break;
                }
            }
            // _pendingCommit intentionally survives until the next Export (which resets
            // it on entry): WorldRunner's props phase reads it through
            // NestedSceneRodeLastSpawnExport after this commit.
        }

        private void ExportCore(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits)
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
                if (spawnState == WorldRunner.ClientSpawnState.Spawning && spawnWindows.ContainsKey(peerId))
                {
                    spawnWindows.Remove(peerId);
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

            buffer.WriteBool(false); // not a despawn
            buffer.WriteBits(sceneId, SCENE_ID_BITS);

            if (netController.NetParent == null)
            {
                buffer.WriteBits(0, NODE_ID_BITS); // parentId 0 = root

                // Write nested NetScenes for root scene
                ExportNestedScenes(currentWorld, peer, buffer, firstSend, maxBits);

                // Window stamp, Spawning flip, and nested-child stamps apply in
                // CommitExport - only if these bytes actually ride the packet.
                _pendingCommit = PendingCommit.Spawn;
                _pendingFirstSend = firstSend;
                return;
            }

            var parentId = currentWorld.GetPeerNodeId(peer, netController.NetParent);
            buffer.WriteBits(parentId, NODE_ID_BITS);

            // Attachment path within the parent scene, resolved (or bailed on) above.
            buffer.WriteBits(nodePathId, NODE_PATH_BITS);

            // Use ID comparison instead of Equals - more reliable for ENet.Peer structs
            bool hasInputAuth = netController.InputAuthority.IsSet && netController.InputAuthority.ID == peer.ID;
            buffer.WriteBool(hasInputAuth);

            // Write nested NetScenes
            ExportNestedScenes(currentWorld, peer, buffer, firstSend, maxBits);

            // Window stamp, Spawning flip, and nested-child stamps apply in
            // CommitExport - only if these bytes actually ride the packet.
            _pendingCommit = PendingCommit.Spawn;
            _pendingFirstSend = firstSend;

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
                    // Peer never received spawn, mark as despawned immediately (no data to send).
                    // Children can still be Spawning/Spawned via an ancestor's spawn table even
                    // though this parent never spawned for the peer - silence them too.
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                    CascadeDespawnToNestedChildren(currentWorld, peer, peerId, netController, freeIds: false);
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
                        CascadeDespawnToNestedChildren(currentWorld, peer, peerId, netController, freeIds: false);
                        break;
                    }
                    // Peer received (or is receiving) spawn, send despawn data. The
                    // Despawning flip and the nested-subtree silencing cascade apply in
                    // CommitExport, in the same tick the marker actually first ships -
                    // that timing is what closes the orphan-props window (see
                    // CascadeDespawnToNestedChildren).
                    WriteDespawnData(currentWorld, peer, peerId, localNodeId, buffer);
                    _pendingCommit = PendingCommit.Despawn;
                    _pendingFirstSend = true;
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
                    _pendingCommit = PendingCommit.Despawn;
                    _pendingFirstSend = false;
                    break;

                case WorldRunner.ClientSpawnState.Despawned:
                    // Already despawned for this peer, nothing to do
                    break;
            }
        }

        /// <summary>
        /// Writes the despawn data to the buffer.
        /// Format: [isDespawn = 1][localNodeId:9]
        /// </summary>
        private void WriteDespawnData(WorldRunner currentWorld, NetPeer peer, UUID peerId, ushort localNodeId, NetBuffer buffer)
        {
            buffer.WriteBool(true);

            // Write the local node ID for this peer so client knows which node to despawn
            buffer.WriteBits(localNodeId, NODE_ID_BITS);

            currentWorld.Debug?.Send("Despawn", $"Exported despawn for {netController.RawNode?.Name}, localNodeId={localNodeId}");
        }

        /// <summary>
        /// Exports all nested NetScenes in the subtree that the peer has interest in.
        /// On a FIRST send the table is capped to what fits <paramref name="maxBits"/>:
        /// excluded children simply stay NotSpawned - they are never part of the frozen
        /// membership, so resends stay consistent, and once this parent commits Spawned
        /// they spawn through their own child-spawn Export. On a resend the frozen table
        /// rides whole (the host drops the whole record if it no longer fits).
        /// </summary>
        private void ExportNestedScenes(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, bool firstSend, int maxBits)
        {
            var peerUUID = NetRunner.Instance.GetPeerId(peer);

            // Collect nested NetScenes recursively (entire subtree)
            NestedSceneBuffer.Clear();
            CollectNestedNetScenesRecursive(currentWorld, peer, netController, NestedSceneBuffer);

            // Filter to only include scenes the peer has interest in
            _interestedNestedBuffer.Clear();
            for (int i = 0; i < NestedSceneBuffer.Count; i++)
            {
                var nested = NestedSceneBuffer[i];
                if (!nested.IsPeerInterested(peer))
                {
                    continue;
                }

                // Table membership is FROZEN per peer while this spawn is in flight: on a
                // resend, only children whose spawn window proves they rode an earlier
                // committed send of this table may appear. A client that already imported
                // this spawn consume-and-skips every resend (hasImported), so a child ADDED
                // to the table mid-resend rides only payloads that client is guaranteed to
                // discard - it can never learn the id, and the child's props exporter
                // (switched on by the Spawning flip in CommitExport) then feeds it props
                // for an unknown node: tick-import abort, no acks, and the abort starves
                // the very ack that would commit this parent and let the child spawn
                // standalone. Deadlock until something else acks (or the peer times out).
                //
                // The invariant is simply "this table is byte-identical on every resend".
                // Runtime spawns used to be the way it got violated; they no longer enter the
                // table at all (see CollectNestedNetScenesRecursive). The live trigger is now
                // the FIRST-send budget cap below: children truncated off that send get no
                // spawn window, and this is what keeps them out of a later, roomier resend.
                // They stay NotSpawned (props gated) and spawn via their own Export once this
                // parent commits.
                if (!firstSend
                    && (nested.NetNode?.Serializers == null
                        || nested.NetNode.Serializers.Length == 0
                        || nested.NetNode.Serializers[0] is not SpawnSerializer memberCheck
                        || !memberCheck.spawnWindows.ContainsKey(peerUUID)))
                {
                    continue;
                }

                _interestedNestedBuffer.Add(nested);
            }

            var includeCount = _interestedNestedBuffer.Count;
            if (firstSend)
            {
                // Budget cap: entries that don't fit are left out of this first send
                // entirely (never flipped Spawning, never in the frozen set).
                // The host resets the section buffer before every Export, so the write
                // cursor IS the section size so far. Bits, with a long so an unbounded
                // test budget cannot overflow.
                long entryBudget = (long)maxBits - buffer.WriteBitPosition - NESTED_COUNT_BITS;
                var maxEntries = entryBudget > 0 ? (int)Math.Min(int.MaxValue, entryBudget / NESTED_ENTRY_BITS) : 0;
                if (includeCount > maxEntries)
                {
                    includeCount = maxEntries;
                }
            }

            buffer.WriteBits((ulong)includeCount, NESTED_COUNT_BITS);

            _pendingNestedCommit.Clear();
            for (int i = 0; i < includeCount; i++)
            {
                var nested = _interestedNestedBuffer[i];

                // Allocate peer-specific ID for this nested scene
                var nestedPeerId = currentWorld.TryRegisterPeerNode(nested, peer);
                if (nestedPeerId == 0)
                {
                    // Failed to allocate ID - write zeros so client can skip
                    buffer.WriteBits(0, NESTED_ENTRY_BITS);
                    continue;
                }

                // Nested scenes ride along in the parent's spawn data, so the client is
                // about to build them from scratch. Their baseline resets, Spawning flips,
                // and window stamps happen in the parent's CommitExport - only when the
                // table provably rides the packet (see CommitExport for the reset rule).
                _pendingNestedCommit.Add(nested);

                var nestedSceneId = Protocol.PackScene(nested.NetSceneFilePath);
                if (nestedSceneId > 245)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"SceneId {nestedSceneId} exceeds safe limit (245). Too many registered scenes.");
                }

                // Check if this peer owns the nested scene
                bool nestedHasInputAuth = nested.InputAuthority.IsSet && nested.InputAuthority.ID == peer.ID;

                buffer.WriteBits(nestedSceneId, SCENE_ID_BITS);
                buffer.WriteBits(nested.CachedNodePathIdInParent, NODE_PATH_BITS);
                buffer.WriteBits(nestedPeerId, NODE_ID_BITS);
                buffer.WriteBool(nestedHasInputAuth);
            }
        }

        // Reusable buffer for interested nested scenes to avoid allocation
        private List<NetworkController> _interestedNestedBuffer = new(64);

        /// <summary>
        /// Whether the given nested scene rode the spawn table written by this
        /// serializer's most recent Export. Only meaningful between that Export and the
        /// next one on this instance - WorldRunner's props phase queries it right after
        /// its spawn phase for the same peer, inside that window.
        /// </summary>
        internal bool NestedSceneRodeLastSpawnExport(NetworkController nested)
            => _pendingCommit == PendingCommit.Spawn && _pendingNestedCommit.Contains(nested);

        /// <summary>
        /// Recursively collects the AUTHORED nested NetScenes in the subtree - the ones the client
        /// rebuilds for itself from the parent's .tscn (see NetworkController.ExistsInParentScene).
        /// Runtime spawns are skipped along with everything beneath them; they ship their own
        /// records.
        ///
        /// Also prunes any scene (and everything under it) whose despawn is in flight for this
        /// peer. Re-including one would flip its per-peer state back to Spawning mid-despawn
        /// (ExportNestedScenes sets every included scene Spawning), reopening the props exporters
        /// the despawn cascade just silenced. Stale Despawned with no despawn pending stays
        /// included - that is the legitimate re-add path when a parent respawns.
        /// </summary>
        private static void CollectNestedNetScenesRecursive(WorldRunner currentWorld, NetPeer peer, NetworkController parent, List<NetworkController> results)
        {
            foreach (var child in parent.DynamicNetworkChildren)
            {
                // Authored nesting only. A table entry says "the node you already built from the
                // .tscn is this NetId" - it is matched against a local instance, and carries no
                // parent field because its parent is implicitly the record being imported. That is
                // sound for authored scenes at any depth, since the client rebuilds the whole
                // subtree from the parent's .tscn and every entry is only reconciling ids.
                //
                // A runtime spawn has no local instance to match, so the client would have to
                // CONSTRUCT it - and with no parent field the only place it can go is the record's
                // own root. For a direct child of the record that happens to be right; for a
                // grandchild it silently reparents the node up the tree. Runtime spawns therefore
                // ship their own record, which does carry an explicit parent id.
                //
                // Skipping the recursion too, not just the entry: everything under a runtime spawn
                // belongs to that spawn's record, not to this one.
                if (!child.ExistsInParentScene)
                {
                    continue;
                }

                if (child.IsQueuedForDespawn
                    || currentWorld.GetClientSpawnState(child.NetId, peer) == WorldRunner.ClientSpawnState.Despawning)
                {
                    continue;
                }
                results.Add(child);
                // Recurse into child's nested scenes
                CollectNestedNetScenesRecursive(currentWorld, peer, child, results);
            }
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);

            // Handle despawn acknowledgment FIRST (takes priority over spawn)
            // If despawn is in progress, we don't want spawn ACK to overwrite the state
            if (despawnWindows.TryGetValue(peerId, out var despawnWindow))
            {
                // Commit only when the acked tick's packet provably carried the marker.
                // (The old rule was an unbounded `tick >= despawnTick`, which was only
                // sound while resends could never skip a tick; budget deferral broke
                // that assumption, and SendWindow's gap-restart restores it.)
                if (despawnWindow.Covers(tick))
                {
                    // Despawn acknowledged
                    currentWorld.SetClientSpawnState(netController.NetId, peer, WorldRunner.ClientSpawnState.Despawned);
                    despawnWindows.Remove(peerId); // Clean up after successful ack
                    spawnWindows.Remove(peerId); // Also clean up spawn tracking since despawn supersedes it

                    // Free the local NetId for this peer so it can be reused
                    currentWorld.DeregisterPeerNode(netController, peer);

                    // The acked despawn proves the client freed the whole subtree, so the
                    // nested children's local ids are now safe to free for reuse. State and
                    // baselines were already handled by the send-time pass in ExportDespawn;
                    // this pass is the id sweep plus an idempotent backstop (see
                    // CascadeDespawnToNestedChildren).
                    CascadeDespawnToNestedChildren(currentWorld, peer, peerId, netController, freeIds: true);

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
                // If the despawn is still unacked, don't process spawn ACK
                // The node is being despawned, so transitioning to Spawned would be wrong
                return;
            }

            // Handle spawn acknowledgment (only if no despawn is pending).
            //
            // Commit only when the acked tick is inside the spawn's send window: every
            // tick in that window carried the spawn data (CommitExport records only
            // committed sends, and any budget-deferred tick restarts the window), so an
            // ack inside it is a packet that provably contained the spawn data. A bare
            // `tick >= firstSend` would also commit on acks of ticks that carried only
            // this node's props - which is exactly how a lost spawn packet used to get
            // marked Spawned while the client sat on a blank node.
            if (spawnWindows.TryGetValue(peerId, out var spawnWindow))
            {
                if (spawnWindow.Covers(tick))
                {
                    currentWorld.SetSpawnedForClient(netController.NetId, peer);
                    spawnWindows.Remove(peerId); // Clean up after successful ack
                }
            }
        }

        // Import is client-only and infrequent, less critical to optimize
        public bool Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController controllerOut)
        {
            controllerOut = netController;

            // Despawn or spawn record.
            if (buffer.ReadBool())
            {
                ImportDespawn(currentWorld, (ushort)buffer.ReadBits(NODE_ID_BITS));
                return true;
            }

            var data = Deserialize(buffer);

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

            // Timed because this is the client's single biggest per-tick stall risk: both the
            // resolve and the instantiate run synchronously on the main thread, and every spawn
            // record riding this tick's packet is built in this one frame. See SpawnImportProfiler.
            bool sceneWasCached = Protocol.IsSceneCached(data.classId);
            var spawnLoadTs = System.Diagnostics.Stopwatch.GetTimestamp();
            var packedScene = Protocol.UnpackScene(data.classId);
            var spawnLoadMs = Diagnostics.SpawnImportProfiler.Elapsed(spawnLoadTs);

            var spawnInstTs = System.Diagnostics.Stopwatch.GetTimestamp();
            var newNode = packedScene.Instantiate<INetNodeBase>();
            Diagnostics.SpawnImportProfiler.Record(
                packedScene.ResourcePath, spawnLoadMs,
                Diagnostics.SpawnImportProfiler.Elapsed(spawnInstTs), !sceneWasCached);

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
        private void ImportDespawn(WorldRunner currentWorld, ushort localNodeId)
        {

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
        /// Reconciles local nested NetScenes against spawn data.
        /// Keeps matched scenes (syncs NetId), deletes unmatched local scenes,
        /// and creates new scenes from unmatched spawn data.
        /// </summary>
        private void ProcessChildNodes(NetworkController nodeOut, WorldRunner currentWorld)
        {
            // Collect all local nested scenes (flat list)
            CollectAllNestedScenes(nodeOut);

            // Match local instances against spawn data
            for (int i = 0; i < AllLocalNestedScenes.Count; i++)
            {
                var local = AllLocalNestedScenes[i];
                var localPathId = local.CachedNodePathIdInParent;
                var localSceneId = Protocol.PackScene(local.NetSceneFilePath);

                // Linear search spawn data for match
                int matchIndex = -1;
                for (int j = 0; j < _nestedDataCount; j++)
                {
                    if (NestedDataBuffer[j].NodePathId == localPathId &&
                        NestedDataBuffer[j].SceneId == localSceneId)
                    {
                        matchIndex = j;
                        break;
                    }
                }

                if (matchIndex >= 0)
                {
                    // Keep local, sync NetId
                    local.NetId = new NetId(NestedDataBuffer[matchIndex].NetId);
                    local.IsClientSpawn = true;
                    local.CurrentWorld = currentWorld;
                    // Set InputAuthority if this client owns the nested scene
                    if (NestedDataBuffer[matchIndex].HasInputAuthority == 1)
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
                    NestedDataBuffer[matchIndex].SceneId = 246;
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
                if (NestedDataBuffer[i].SceneId >= 246 || NestedDataBuffer[i].SceneId == 0)
                    continue;

                var data = NestedDataBuffer[i];
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
            AllLocalNestedScenes.Clear();
            CollectNestedRecursive(root.RawNode, root.RawNode, root.NetSceneFilePath);
        }

        private void CollectNestedRecursive(Node treeRoot, Node node, string rootScenePath)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                var child = node.GetChild(i);

                if (child is INetNodeBase netNode && netNode.Network != null && netNode.Network.IsNetScene())
                {
                    AllLocalNestedScenes.Add(netNode.Network);

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

        /// <summary>Reads a spawn record after its isDespawn bit (0) has been consumed.</summary>
        private Data Deserialize(NetBuffer buffer)
        {
            var spawnData = new Data
            {
                classId = (byte)buffer.ReadBits(SCENE_ID_BITS),
                parentId = (ushort)buffer.ReadBits(NODE_ID_BITS),
            };

            if (spawnData.parentId == 0)
            {
                // Root scene - read nested count
                spawnData.nestedCount = (byte)buffer.ReadBits(NESTED_COUNT_BITS);
                DeserializeNestedScenes(buffer, spawnData.nestedCount);
                return spawnData;
            }

            spawnData.nodePathId = (byte)buffer.ReadBits(NODE_PATH_BITS);
            spawnData.hasInputAuthority = buffer.ReadBool() ? (byte)1 : (byte)0;

            // Read nested scenes
            spawnData.nestedCount = (byte)buffer.ReadBits(NESTED_COUNT_BITS);
            DeserializeNestedScenes(buffer, spawnData.nestedCount);

            return spawnData;
        }

        private static void DeserializeNestedScenes(NetBuffer buffer, int count)
        {
            _nestedDataCount = 0;

            for (int i = 0; i < count && i < NestedDataBuffer.Length; i++)
            {
                var sceneId = (byte)buffer.ReadBits(SCENE_ID_BITS);
                var nodePathId = (byte)buffer.ReadBits(NODE_PATH_BITS);
                var netId = (ushort)buffer.ReadBits(NODE_ID_BITS);
                var hasInputAuth = buffer.ReadBool() ? (byte)1 : (byte)0;

                // Skip entries where allocation failed on server (netId == 0)
                // Note: sceneId=0 is valid (first registered scene), but netId=0 means no allocation
                if (netId == 0) continue;

                NestedDataBuffer[_nestedDataCount++] = new NestedSceneData
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
