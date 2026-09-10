using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Nebula.Serialization.Serializers
{
    /// <summary>
    /// Tells each peer whether it currently has interest in this node, as one bit,
    /// sent ON CHANGE and resent every tick until the peer acks a packet that carried it.
    ///
    /// <para>This used to be a periodic repeat (every node, every peer, every 3 ticks,
    /// unconditionally) because it kept no per-peer state and healed packet loss by simply
    /// sending again. The byte was never the cost: a section pays framing (a node header,
    /// and a group header when it opens one), so an idle node paid 2 to 10 bytes per peer
    /// every 100 ms as a permanent floor that scaled with scene count, not activity. The ack
    /// ring (<see cref="SentNodeRing"/>) now routes acks to exactly the nodes in a packet, so
    /// the spawn serializer's resend-until-acked contract (<see cref="SendWindow"/>) is cheap
    /// enough to reuse here, and an idle node costs nothing.</para>
    ///
    /// <para><b>Baseline.</b> A node is only ever spawned for a peer that has interest in it
    /// (SpawnSerializer refuses otherwise), so both sides start from "interested" at spawn:
    /// the server's per-peer state initialises <c>Acked = true</c> and the client's
    /// <c>clientHasInterest</c> starts true. A respawn resets the server side through
    /// <see cref="ResetPeerBaseline"/>.</para>
    ///
    /// <para><b>Detection.</b> Interest is evaluated on the node's stagger slot (once per
    /// <see cref="SYNC_INTERVAL"/> ticks, as before) by comparing the computed
    /// <c>IsPeerInterested</c> against the value the peer provably holds - not by listening
    /// to <c>InterestChanged</c>, which peer-set writes (AddInterestPeer / RemoveInterestPeer)
    /// never fire. While anything is in flight the node is evaluated every tick.</para>
    ///
    /// <para><b>The flip-back trap.</b> If interest is lost, the 0 ships, the peer applies it
    /// and only the ack is lost, and interest is then regained, a naive "desired equals acked,
    /// nothing to do" would strand the peer with a hidden node forever. So a value stays in
    /// flight until an ack proves which byte the peer holds; a change while in flight restarts
    /// the window with the new value and keeps sending.</para>
    /// </summary>
    public partial class InterestResyncSerializer : RefCounted, IStateSerializer
    {
        /// <summary>Stagger period, in ticks, of the idle interest check (~10 Hz at 30 TPS).</summary>
        private const int SYNC_INTERVAL = 3;

        /// <summary>The section is exactly one bit.</summary>
        private const int SectionBits = 1;

        private NetworkController network;

        // Client-side: the value last applied. Starts true (see class doc).
        private bool clientHasInterest = true;

        /// <summary>Server-side, per peer: what the peer holds, and what is being resent.</summary>
        private struct PeerInterestState
        {
            /// <summary>The value the peer provably holds (spawn implies true).</summary>
            public bool Acked;
            /// <summary>The value in the open resend window; meaningful only while InFlight.</summary>
            public bool Sent;
            /// <summary>True while a value is being resent and no ack has covered it yet.</summary>
            public bool InFlight;
            /// <summary>Ticks the Sent value rode a packet; Covers(ack) commits it.</summary>
            public SendWindow Window;
        }

        private Dictionary<UUID, PeerInterestState> _peerStates;

        /// <summary>
        /// Peers with an in-flight value. Zero is the idle fast path: off the stagger slot the
        /// serializer returns without touching the dictionary at all.
        /// </summary>
        private int _pendingPeers;

        /// <summary>Export wrote a byte for this peer; CommitExport stamps the window.</summary>
        private bool _pendingCommit;
        private UUID _pendingCommitPeer;

        public InterestResyncSerializer(NetworkController controller)
        {
            network = controller;
        }

        public void Begin() { }
        public void Cleanup() { }

        public void CleanupPeer(UUID peerId) => ForgetPeer(peerId);

        /// <summary>
        /// The peer is about to receive a fresh copy of this node, whose client-side instance
        /// starts from "interested" like every spawn; whatever was in flight for the old
        /// instance is meaningless to the new one.
        /// </summary>
        public void ResetPeerBaseline(UUID peerId) => ForgetPeer(peerId);

        private void ForgetPeer(UUID peerId)
        {
            if (_peerStates == null) return;
            if (_peerStates.Remove(peerId, out var state) && state.InFlight)
            {
                _pendingPeers--;
            }
        }

        public ExportResult Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits)
        {
            _pendingCommit = false;

            // Only sync after the node has been spawned for this peer
            if (!currentWorld.HasSpawnedForClient(network.NetId, peer))
            {
                return ExportResult.None;
            }

            // Idle fast path: nothing in flight and not this node's stagger slot. This is
            // the per-tick cost of the old design (one interest evaluation per node per
            // SYNC_INTERVAL ticks), minus the byte it used to write.
            int tickOffset = (int)(network.NetId.Value % SYNC_INTERVAL);
            bool staggerSlot = (currentWorld.CurrentTick + tickOffset) % SYNC_INTERVAL == 0;
            if (_pendingPeers == 0 && !staggerSlot)
            {
                return ExportResult.None;
            }

            var peerId = NetRunner.Instance.GetPeerId(peer);
            _peerStates ??= new Dictionary<UUID, PeerInterestState>();
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_peerStates, peerId, out bool exists);
            if (!exists)
            {
                state.Acked = true;
            }

            if (!state.InFlight && !staggerSlot)
            {
                // Another peer's in-flight value dragged this node onto the every-tick path;
                // this peer only re-evaluates on the stagger slot.
                return ExportResult.None;
            }

            bool desired = network.IsPeerInterested(peer);
            bool held = state.InFlight ? state.Sent : state.Acked;
            if (desired != held)
            {
                // A new value: restart the window so an ack of a tick that carried the
                // previous value cannot commit this one.
                if (!state.InFlight)
                {
                    state.InFlight = true;
                    _pendingPeers++;
                }
                state.Sent = desired;
                state.Window = default;
            }
            else if (!state.InFlight)
            {
                return ExportResult.None;
            }

            // Self-limiting: the section is one bit. A deferred tick just leaves a gap in
            // the window (SendWindow restarts across gaps), which is always safe.
            if (maxBits < SectionBits)
            {
                return ExportResult.None;
            }

            buffer.WriteBool(state.Sent);
            _pendingCommit = true;
            _pendingCommitPeer = peerId;
            return ExportResult.Written;
        }

        public void CommitExport(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            if (!_pendingCommit) return;
            _pendingCommit = false;
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, _pendingCommitPeer);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref state)) return;
            state.Window.RecordSend(tick);
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck)
        {
            if (_peerStates == null || _pendingPeers == 0) return;
            var peerId = NetRunner.Instance.GetPeerId(peer);
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref state) || !state.InFlight) return;

            // Commit only when the acked tick's packet provably carried the current value;
            // an ack for a pre-restart or pre-gap send costs one more resend round.
            if (!state.Window.Covers(latestAck)) return;
            state.Acked = state.Sent;
            state.InFlight = false;
            state.Window = default;
            _pendingPeers--;
        }

        public bool Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;
            if (network == null) return true;

            bool hasInterest = buffer.ReadBool();

            // Only fire event if state actually changed
            if (hasInterest != clientHasInterest)
            {
                clientHasInterest = hasInterest;
                network.FireInterestChanged(hasInterest);
            }
            return true;
        }

        /// <summary>Test seam: peers with a value in flight.</summary>
        internal int PendingPeersForTests => _pendingPeers;

        /// <summary>Test seam: whether any per-peer state exists for this peer.</summary>
        internal bool HasPeerStateForTests(UUID peerId) => _peerStates != null && _peerStates.ContainsKey(peerId);
    }
}
