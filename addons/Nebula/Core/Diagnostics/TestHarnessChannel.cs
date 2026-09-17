using System;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// A side channel that hands internal per-peer state to a TEST HARNESS. Disabled by default and
    /// meant to stay disabled in production: it tells a peer things a real client is supposed to
    /// learn only from the replicated stream.
    ///
    /// <para>WHY THIS EXISTS. The synthetic load client
    /// (<c>addons/Nebula/Testing/Load/</c>) speaks the client-to-server half of the protocol without
    /// running a scene, so it can drive hundreds of peers from one process. That half is small and
    /// byte-granular -- except for one thing. An input packet is addressed by a PEER-LOCAL node id
    /// (<see cref="WorldRunner.TryRegisterPeerNode"/>), and the only place a client ever learns one
    /// is the spawn section inside the bit-packed tick payload, which cannot be decoded without the
    /// full property schema. So a synthetic peer can connect and acknowledge for free but cannot
    /// send a single input packet. This channel closes exactly that gap, and nothing else.</para>
    ///
    /// <para>OFF MEANS ABSENT, not inert. When disabled, <see cref="FromProcessConfig"/> returns
    /// null, no handler is registered with <see cref="NetRunner.ReserveChannel"/>, and the pump's
    /// reserved-channel dispatch finds nothing -- a packet on this channel is dropped by code that
    /// already existed. There is no new branch anywhere on the hot path; the cost of the feature in
    /// a normal build is the <c>?.</c> on the emit call, the same price <c>_profiler?.</c> and
    /// <c>_metrics?.</c> already charge.</para>
    ///
    /// <para>KNOWN LIMITATION, and a latent inconsistency worth knowing about.
    /// <see cref="WorldRunner.PeerState.OwnedNodes"/> does not contain everything a peer holds
    /// authority over. <c>SetInputAuthorityInternal</c> only adds a node when its
    /// <c>CurrentWorld</c> is already set, and a static child's is assigned in
    /// <c>_NetworkPrepare</c>, which runs AFTER <c>InitializeStaticChildren</c> has propagated
    /// authority down — so on the server a static child ends up holding <c>InputAuthority</c>
    /// while never joining the set. Dynamic children never join it either; they get a bare field
    /// assignment in <c>_NetworkPrepare</c>. This class therefore walks
    /// <c>StaticNetworkChildren</c> itself, exactly as the client's own input loop does, and a
    /// game whose input-carrying node is a DYNAMICALLY spawned child NetScene is still not
    /// described. Whether <c>OwnedNodes</c> ought to be complete is a semantics question about a
    /// live path, and deliberately not answered here.</para>
    /// </summary>
    public sealed class TestHarnessChannel
    {
        /// <summary>
        /// Valid channel ids run 0..250 (NetRunner requests 251 channels). 249 is Blastoff's admin
        /// channel, and the low band is where a game's own <see cref="NetRunner.ReserveChannel"/>
        /// plugins live, so Nebula's diagnostic channel sits next to Blastoff at the top.
        ///
        /// <para>250 is nominally in range and does NOT work in practice. Do not "tidy" this back
        /// up to the last id.</para>
        /// </summary>
        public const byte ChannelId = 248;

        // ── Enablement ───────────────────────────────────────────────────
        // Command line beats environment beats project setting, the same order and for the same
        // reason as NetworkImpairment: a project setting is process-global, and a play session
        // wants to configure one instance without touching the project.
        public const string EnableArg = "--testChannel";
        public const string EnableEnvVar = "NEBULA_TEST_CHANNEL";
        public const string EnableSetting = "Nebula/config/debug/test_channel";

        // ── Frame layout ─────────────────────────────────────────────────
        // frame := [opcode u8][payload]
        //   0x00-0x7F  client -> server
        //   0x80-0xFF  server -> client
        // The direction lives in the high bit so the opcode space stays legible as it grows.

        public const byte OpHello = 0x01;      // C->S: [kind u8][flags u8]
        public const byte OpInputMap = 0x81;   // S->C: [count u8] then count entries

        /// <summary>Identifies what kind of harness is on the other end. Room for more later.</summary>
        public const byte KindSyntheticPeer = 0x01;

        /// <summary>Hello flag: send me an <see cref="OpInputMap"/> whenever my owned set changes.</summary>
        public const byte HelloFlagSubscribeInputMap = 0x01;

        public const int OpcodeBytes = 1;
        public const int HelloKindBytes = 1;
        public const int HelloFlagsBytes = 1;
        public const int HelloPayloadBytes = HelloKindBytes + HelloFlagsBytes;

        public const int InputMapCountBytes = 1;
        private const int LocalNodeIdBytes = 2;
        private const int StaticChildIdBytes = 1;
        private const int SceneIdBytes = 1;
        private const int InputSizeBytes = 2;

        /// <summary>One <c>[localNodeId u16][staticChildId u8][sceneId u8][inputSize u16]</c>.</summary>
        public const int InputMapEntryBytes =
            LocalNodeIdBytes + StaticChildIdBytes + SceneIdBytes + InputSizeBytes;

        /// <summary>The count is one byte, so this is what a single message can carry.</summary>
        public const int MaxInputMapEntries = byte.MaxValue;

        // Opcodes reserved for messages a harness will want next, recorded here so the channel
        // reads as general from the start rather than as one message with a frame around it:
        //   0x02 RequestResend  C->S  ask for a fresh InputMap after a gap
        //   0x82 WorldInfo      S->C  worldId, CurrentTick, TPS, tick payload budget
        //   0x83 PeerBudget     S->C  this peer's deferral counters, to attribute an export stall

        /// <summary>One entry of an <see cref="OpInputMap"/>: how to address one input-capable node.</summary>
        public readonly struct InputMapEntry
        {
            public readonly ushort LocalNodeId;
            public readonly byte StaticChildId;
            public readonly byte SceneId;
            public readonly ushort InputSize;

            public InputMapEntry(ushort localNodeId, byte staticChildId, byte sceneId, ushort inputSize)
            {
                LocalNodeId = localNodeId;
                StaticChildId = staticChildId;
                SceneId = sceneId;
                InputSize = inputSize;
            }
        }

        /// <summary>
        /// An order-independent fingerprint of a peer's whole input map, used to decide whether the
        /// map is worth re-sending.
        ///
        /// <para>ORDER-INDEPENDENT IS THE POINT, not a nicety. The map is derived from
        /// <c>OwnedNodes</c>, a <see cref="System.Collections.Generic.HashSet{T}"/> whose iteration
        /// order shifts as it is mutated. A sequence hash would therefore report a change on ticks
        /// where nothing changed, and re-send the map forever. Combining per-entry hashes with SUM
        /// and XOR makes the fingerprint depend on the SET, which is what actually matters.</para>
        ///
        /// <para>It also has to cover the WHOLE entry. One Player yields two entries — the ship and
        /// the character — that share a single local node id, so a fingerprint over the node count,
        /// or over the set of ids, would compare equal while the map differed.</para>
        /// </summary>
        public struct InputMapSignature : IEquatable<InputMapSignature>
        {
            private int _count;
            private ulong _sum;
            private ulong _xor;

            public readonly int Count => _count;

            public void Add(in InputMapEntry entry)
            {
                ulong hash = EntryHash(entry);
                _sum += hash;
                _xor ^= hash;
                _count++;
            }

            public readonly bool Equals(InputMapSignature other)
                => _count == other._count && _sum == other._sum && _xor == other._xor;

            public readonly override bool Equals(object obj)
                => obj is InputMapSignature other && Equals(other);

            public readonly override int GetHashCode() => System.HashCode.Combine(_count, _sum, _xor);

            public static bool operator ==(InputMapSignature a, InputMapSignature b) => a.Equals(b);
            public static bool operator !=(InputMapSignature a, InputMapSignature b) => !a.Equals(b);

            /// <summary>FNV-1a over the entry's four fields, little-endian, matching the wire order.</summary>
            private static ulong EntryHash(in InputMapEntry entry)
            {
                const ulong FnvOffsetBasis = 14695981039346656037UL;
                const ulong FnvPrime = 1099511628211UL;

                ulong hash = FnvOffsetBasis;
                hash = (hash ^ (byte)(entry.LocalNodeId & 0xFF)) * FnvPrime;
                hash = (hash ^ (byte)(entry.LocalNodeId >> 8)) * FnvPrime;
                hash = (hash ^ entry.StaticChildId) * FnvPrime;
                hash = (hash ^ entry.SceneId) * FnvPrime;
                hash = (hash ^ (byte)(entry.InputSize & 0xFF)) * FnvPrime;
                hash = (hash ^ (byte)(entry.InputSize >> 8)) * FnvPrime;
                return hash;
            }
        }

        private readonly bool[] _subscribed;
        private readonly bool[] _mapDirty;
        private readonly InputMapSignature[] _signature;

        /// <summary>Reused across peers and ticks; only ever touched on a world's tick thread.</summary>
        private NetBuffer _mapBuffer;

        private TestHarnessChannel(int peerSlots)
        {
            _subscribed = new bool[peerSlots];
            _mapDirty = new bool[peerSlots];
            _signature = new InputMapSignature[peerSlots];
        }

        /// <summary>
        /// Returns a channel when this process was configured to run one, and null otherwise.
        /// Null is the normal answer.
        /// </summary>
        public static TestHarnessChannel FromProcessConfig(int peerSlots)
        {
            return Resolve() ? new TestHarnessChannel(peerSlots) : null;
        }

        private static bool Resolve()
        {
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument == EnableArg) return true;
            }

            if (Env.TryGetFlag(EnableEnvVar, out bool fromEnv)) return fromEnv;

            return ProjectSettings.GetSetting(EnableSetting, false).AsBool();
        }

        /// <summary>
        /// Handles a frame from a peer. Registered with <see cref="NetRunner.ReserveChannel"/>.
        ///
        /// <para>MUST NOT THROW. The pump turns any exception on any channel into a
        /// <c>MalformedPacketDisconnectCode</c> drop, so a frame this version does not recognise
        /// has to degrade to a logged no-op -- otherwise a newer Nebula would kill an older
        /// harness's peers instead of ignoring what it cannot read.</para>
        /// </summary>
        public void HandleClientMessage(NetPeer peer, byte[] data)
        {
            if (data == null || data.Length < OpcodeBytes) return;

            switch (data[0])
            {
                case OpHello:
                    if (data.Length < OpcodeBytes + HelloPayloadBytes)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.WARN,
                            $"[TestHarness] Short Hello from peer {peer.ID} ({data.Length} bytes); ignored.");
                        return;
                    }
                    OnHello(peer, kind: data[OpcodeBytes], flags: data[OpcodeBytes + HelloKindBytes]);
                    return;

                default:
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN,
                        $"[TestHarness] Unknown opcode 0x{data[0]:X2} from peer {peer.ID}; ignored.");
                    return;
            }
        }

        private void OnHello(NetPeer peer, byte kind, byte flags)
        {
            if (!TrySlotFor(peer, out int slot)) return;

            if ((flags & HelloFlagSubscribeInputMap) != 0)
            {
                _subscribed[slot] = true;
                _mapDirty[slot] = true;
            }

            Debugger.Instance.Log(
                $"[TestHarness] Peer {peer.ID} said hello (kind 0x{kind:X2}, flags 0x{flags:X2}).");
        }

        /// <summary>
        /// Notes that this peer's owned-input set may have changed, so the next send re-derives it.
        /// Called from the cold paths that can actually change it: local-id allocation and release,
        /// input-authority transfer, and world join.
        /// </summary>
        public void MarkInputMapDirty(NetPeer peer)
        {
            if (TrySlotFor(peer, out int slot)) _mapDirty[slot] = true;
        }

        /// <summary>Forgets a peer. ENet recycles peer ids, so a stale slot would leak into its successor.</summary>
        public void ForgetPeer(uint peerNativeId)
        {
            if (peerNativeId >= (uint)_subscribed.Length) return;
            _subscribed[peerNativeId] = false;
            _mapDirty[peerNativeId] = false;
            _signature[peerNativeId] = default;
        }

        private bool TrySlotFor(NetPeer peer, out int slot)
        {
            slot = (int)peer.ID;
            return slot >= 0 && slot < _subscribed.Length;
        }

        /// <summary>
        /// Sends this peer its input map if it asked for one and the map has changed.
        ///
        /// <para>Called per peer from the tick's send loop, AFTER export: local ids are allocated
        /// inside the spawn serializer's Export, so anything earlier would report a map missing the
        /// node that spawned this very tick. It is also the only place where the per-peer maps are
        /// coherent -- export itself now runs across several lanes at once, and sending from a lane
        /// would take the ENet send lock that is already the measured bottleneck.</para>
        /// </summary>
        public void MaybeSendInputMap(WorldRunner world, NetPeer peer, in WorldRunner.PeerState peerState)
        {
            if (!TrySlotFor(peer, out int slot)) return;
            if (!_subscribed[slot] || !_mapDirty[slot]) return;

            // Cleared BEFORE the state is read: a mark that lands while we are building simply
            // re-sends next tick. Clearing afterwards would swallow it.
            _mapDirty[slot] = false;

            var buffer = _mapBuffer ??= new NetBuffer();
            buffer.Reset();
            NetWriter.WriteByte(buffer, OpInputMap);
            int countPos = buffer.WritePosition;
            NetWriter.WriteByte(buffer, 0);

            int count = 0;
            var signature = default(InputMapSignature);

            // Walk each owned node AND its static children, rather than trusting OwnedNodes to
            // contain the children itself.
            //
            // It does not, on the server. SetInputAuthorityInternal only adds a node to OwnedNodes
            // when its CurrentWorld is already set, and a static child's CurrentWorld is assigned in
            // _NetworkPrepare — which runs AFTER InitializeStaticChildren has propagated authority
            // down. So the children end up holding InputAuthority while never joining the set. The
            // CLIENT's own input loop walks StaticNetworkChildren for the same reason
            // (WorldRunner.SendInput's callers), so mirroring it is both correct here and the
            // behaviour this map has to describe.
            foreach (var owned in peerState.OwnedNodes)
            {
                if (owned == null) continue;

                TryAppendEntry(buffer, owned, in peerState, ref signature, ref count);

                foreach (var staticChild in owned.StaticNetworkChildren)
                {
                    if (staticChild == null) continue;
                    TryAppendEntry(buffer, staticChild, in peerState, ref signature, ref count);
                }
            }

            if (signature == _signature[slot])
            {
                return;   // nothing the peer does not already know
            }
            _signature[slot] = signature;

            int endPos = buffer.WritePosition;
            buffer.WritePosition = countPos;
            NetWriter.WriteByte(buffer, (byte)count);
            buffer.WritePosition = endPos;

            NetRunner.SendReliable(peer, ChannelId, buffer);
        }

        /// <summary>
        /// Appends one node to the map, if it carries input and the peer can already address it.
        /// </summary>
        private static void TryAppendEntry(
            NetBuffer buffer,
            NetworkController node,
            in WorldRunner.PeerState peerState,
            ref InputMapSignature signature,
            ref int count)
        {
            if (count >= MaxInputMapEntries) return;
            if (!node.HasInputSupport) return;

            // Exactly the condition WorldRunner.SendInput uses to decide how to address a node. A
            // static child has no local id of its own: it rides its parent's, with StaticChildId
            // naming it. Diverging here would produce a map the server cannot resolve.
            bool isStaticChild = node.StaticChildId > 0 && node.NetParent != null;
            var ownerNetId = isStaticChild ? node.NetParent.NetId : node.NetId;
            byte staticChildId = isStaticChild ? node.StaticChildId : (byte)0;

            // Not exported to this peer yet. The peer stays dirty via the registration hook that
            // fires when it is, so this resolves on a later tick rather than being lost.
            if (!peerState.WorldToPeerNodeMap.TryGetValue(ownerNetId, out ushort localNodeId)) return;

            // TryGetSceneId, not PackScene: PackScene throws on a miss, and nothing here may throw.
            if (!Protocol.TryGetSceneId(node.NetSceneFilePath, out byte sceneId)) return;

            var entry = new InputMapEntry(
                localNodeId, staticChildId, sceneId, (ushort)node.GetInputBytes().Length);

            WriteInputMapEntry(buffer, in entry);
            signature.Add(in entry);
            count++;
        }

        /// <summary>Writes one input-map entry. The single definition of the entry's byte order.</summary>
        public static void WriteInputMapEntry(NetBuffer buffer, in InputMapEntry entry)
        {
            NetWriter.WriteUInt16(buffer, entry.LocalNodeId);
            NetWriter.WriteByte(buffer, entry.StaticChildId);
            NetWriter.WriteByte(buffer, entry.SceneId);
            NetWriter.WriteUInt16(buffer, entry.InputSize);
        }

        /// <summary>
        /// Builds a Hello frame. Lives here so the harness and the server read one layout rather
        /// than two that can drift.
        /// </summary>
        public static void WriteHello(NetBuffer buffer, byte kind, byte flags)
        {
            NetWriter.WriteByte(buffer, OpHello);
            NetWriter.WriteByte(buffer, kind);
            NetWriter.WriteByte(buffer, flags);
        }

        /// <summary>
        /// Parses an <see cref="OpInputMap"/> frame into <paramref name="destination"/>, returning
        /// false for anything malformed or larger than the caller's buffer.
        ///
        /// <para>Returning false rather than throwing is deliberate: the harness reads these on its
        /// own receive path, where an exception would take down a shard servicing many peers.</para>
        /// </summary>
        public static bool TryReadInputMap(
            ReadOnlySpan<byte> frame, Span<InputMapEntry> destination, out int count)
        {
            count = 0;
            if (frame.Length < OpcodeBytes + InputMapCountBytes) return false;
            if (frame[0] != OpInputMap) return false;

            int declared = frame[OpcodeBytes];
            if (frame.Length < OpcodeBytes + InputMapCountBytes + declared * InputMapEntryBytes) return false;
            if (declared > destination.Length) return false;

            int offset = OpcodeBytes + InputMapCountBytes;
            for (int i = 0; i < declared; i++)
            {
                destination[i] = new InputMapEntry(
                    (ushort)(frame[offset] | (frame[offset + 1] << 8)),
                    frame[offset + 2],
                    frame[offset + 3],
                    (ushort)(frame[offset + 4] | (frame[offset + 5] << 8)));
                offset += InputMapEntryBytes;
            }

            count = declared;
            return true;
        }
    }
}
