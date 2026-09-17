using System;
using System.Collections.Generic;
using ENet;
using Nebula.Diagnostics;
using Nebula.Serialization;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// One synthetic peer: an ENet connection that performs the join, acknowledges every tick, and
    /// answers a world handoff — without a scene tree, an import, or prediction behind it.
    ///
    /// <para>Everything here runs on its shard's thread and must touch NO Godot API. Not a style
    /// rule: a shard thread is not Godot's main thread, and the engine's singletons are not safe to
    /// call from it. Timing therefore comes from the shard's <c>Stopwatch</c>, and anything worth
    /// printing is handed to the main thread as a counter.</para>
    ///
    /// <para>WHAT IT DELIBERATELY DOES NOT DO: parse the tick payload. Everything past the 4-byte
    /// header is bit-packed against the property schema and undecodable without it — which is the
    /// whole reason the peer learns how to address its input nodes from
    /// <see cref="TestHarnessChannel"/> instead.</para>
    /// </summary>
    public sealed class SyntheticPeer
    {
        public enum PeerPhase
        {
            /// <summary>Waiting for its slot in the ramp.</summary>
            Idle,
            /// <summary>Connect sent, no ENet Connect event yet.</summary>
            Connecting,
            /// <summary>ENet connected; hello sent; no tick packet seen yet.</summary>
            Connected,
            /// <summary>Acknowledging ticks. The server flips INITIAL to IN_WORLD on the first one.</summary>
            Acking,
            /// <summary>Knows how to address at least one input node.</summary>
            InWorld,
            /// <summary>Never connected within the timeout.</summary>
            Failed,
            /// <summary>Disconnected or timed out after having connected.</summary>
            Dead,
        }

        /// <summary>The tick channel carries a bare <c>int32</c> tick and nothing else.</summary>
        private const int TickHeaderBytes = sizeof(int);

        /// <summary>World-channel message: <c>[opcode u8][worldId 16]</c>.</summary>
        private const int WorldOpcodeBytes = 1;
        private const int WorldIdBytes = 16;
        private const int WorldMessageBytes = WorldOpcodeBytes + WorldIdBytes;

        /// <summary>Room for the largest input map a peer could be sent.</summary>
        private const int InputMapCapacity = 16;

        /// <summary>
        /// Redundant copies per input packet, and the ring they come from. Both match
        /// <see cref="NetworkController"/>'s own constants — the encoding sends a tick as a
        /// one-byte offset from the newest, so a deeper window than 255 could not be expressed
        /// anyway, and a shallower one would under-state the packet size the server has to parse.
        /// </summary>
        private const int RedundancyCount = 8;
        private const int InputRingSize = 64;   // power of two; slot is tick & (size - 1)

        public readonly int PeerIndex;
        public PeerPhase Phase { get; private set; } = PeerPhase.Idle;

        /// <summary>When this peer should connect, on the shard clock. Staggered by the ramp.</summary>
        public double ConnectAtMs;

        private Peer _peer;
        private bool _peerSet;
        private double _connectSentAtMs;

        /// <summary>Newest tick seen but not yet acknowledged, or -1.</summary>
        private int _pendingAckTick = -1;

        /// <summary>Newest tick seen at all, so an out-of-order or duplicate packet is ignored.</summary>
        private int _lastServerTick = -1;

        private readonly TestHarnessChannel.InputMapEntry[] _inputMap =
            new TestHarnessChannel.InputMapEntry[InputMapCapacity];
        private int _inputMapCount;

        /// <summary>Reused for every outbound frame; a peer only ever writes one at a time.</summary>
        private readonly NetBuffer _scratch = new();

        // ── Input ────────────────────────────────────────────────────────

        private readonly SyntheticInputSource _inputSource;

        /// <summary>Slot each map entry bound to, or -1 where the source declined it.</summary>
        private readonly int[] _boundSlot = new int[InputMapCapacity];

        /// <summary>Per map entry: the ring of recent inputs, and which tick each slot holds.</summary>
        private readonly byte[][][] _inputRing = new byte[InputMapCapacity][][];
        private readonly int[][] _inputRingTicks = new int[InputMapCapacity][];

        /// <summary>Working copy of the current input, one buffer per entry.</summary>
        private readonly byte[][] _currentInput = new byte[InputMapCapacity][];

        /// <summary>Set when the source reported a change that has not yet ridden a packet.</summary>
        private readonly bool[] _inputChanged = new bool[InputMapCapacity];

        /// <summary>
        /// Reused by <c>WriteInputRecords</c>. A List because that is the shared writer's shape;
        /// cleared and refilled with references into the ring, so the steady state allocates
        /// nothing.
        /// </summary>
        private readonly List<(Tick, byte[])> _recentInputs = new(RedundancyCount);

        private SyntheticInputCadence _cadence;

        /// <summary>The tick this peer is predicting, which leads the server's by the target lead.</summary>
        private int _predictedTick = -1;

        /// <summary>Counts frames eligible for the prediction slew, as the real client's does.</summary>
        private ulong _eligibleFrameIndex;

        public SyntheticPeer(int peerIndex, SyntheticInputSource inputSource)
        {
            PeerIndex = peerIndex;
            _inputSource = inputSource;
            if (_inputSource != null) _inputSource.PeerIndex = peerIndex;
        }

        public int InputMapCount => _inputMapCount;

        /// <summary>Round-trip time ENet has measured for this connection, in milliseconds.</summary>
        public uint RoundTripTimeMs => _peerSet ? _peer.RoundTripTime : 0;

        /// <summary>
        /// Opens the connection. The handshake IS the connect data — there is no join packet, and
        /// no authentication step: the default authenticator admits a peer as soon as it connects.
        /// A wrong hash here is refused with the PROT code before anything else happens.
        /// </summary>
        /// <returns>The ENet peer id this connection was given, or null if the connect failed.</returns>
        public uint? Connect(Host host, Address address, int channelLimit, double nowMs)
        {
            _peer = host.Connect(address, channelLimit, Protocol.HandshakeHash);
            _peerSet = _peer.IsSet;
            _connectSentAtMs = nowMs;
            Phase = _peerSet ? PeerPhase.Connecting : PeerPhase.Failed;
            return _peerSet ? _peer.ID : null;
        }

        public void OnConnected(LoadClientStats stats)
        {
            Phase = PeerPhase.Connected;

            // Ask the server to tell us how to address the nodes we hold input authority over. It
            // answers only if it was started with the test channel enabled; if it was not, this peer
            // still connects and acknowledges, it simply never sends input.
            _scratch.Reset();
            TestHarnessChannel.WriteHello(
                _scratch,
                TestHarnessChannel.KindSyntheticPeer,
                TestHarnessChannel.HelloFlagSubscribeInputMap);
            Send(TestHarnessChannel.ChannelId, _scratch, PacketFlags.Reliable);
        }

        public void OnDisconnected(uint reasonCode, LoadClientStats stats)
        {
            Phase = PeerPhase.Dead;
            _peerSet = false;
            stats.RecordDisconnect(reasonCode);
        }

        /// <summary>
        /// Routes one received packet. <paramref name="payload"/> is only valid for this call.
        /// </summary>
        public void OnPacket(byte channel, ReadOnlySpan<byte> payload, LoadClientStats stats)
        {
            switch (channel)
            {
                case (byte)NetRunner.ENetChannelId.Tick:
                    OnTickPacket(payload, stats);
                    return;

                case (byte)NetRunner.ENetChannelId.World:
                    OnWorldPacket(payload, stats);
                    return;

                case TestHarnessChannel.ChannelId:
                    OnTestHarnessPacket(payload);
                    return;

                default:
                    // Function-channel traffic and anything a plugin reserved: counted by the byte,
                    // never parsed. A synthetic peer has no nodes to dispatch an RPC to.
                    return;
            }
        }

        private void OnTickPacket(ReadOnlySpan<byte> payload, LoadClientStats stats)
        {
            if (payload.Length < TickHeaderBytes) return;

            stats.RecordTickPacket(payload.Length);

            int tick = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24);

            // Ignore anything at or behind what we have seen. The server rejects an ack for a tick
            // it has not reached and ignores one at or behind the peer's recorded tick, so replying
            // to a stale packet is at best wasted and at worst confusing in a log.
            if (tick <= _lastServerTick) return;
            _lastServerTick = tick;

            // A second tick arriving before the first was acknowledged: flush the older one on its
            // own rather than dropping it. A real client has the same rule, and it is load-bearing —
            // an ack the server never receives leaves a baseline uncommitted, so dropping acks
            // silently changes the server-side behaviour this harness exists to measure.
            if (_pendingAckTick >= 0) SendStandaloneAck(_pendingAckTick, stats);
            _pendingAckTick = tick;

            if (Phase == PeerPhase.Connected) Phase = PeerPhase.Acking;
        }

        private void OnWorldPacket(ReadOnlySpan<byte> payload, LoadClientStats stats)
        {
            if (payload.Length < WorldMessageBytes) return;
            if (payload[0] != NetRunner.WorldMsgChangeWorld) return;

            // Until this reply lands, the peer is in NO world and every packet it sends is dropped
            // on the floor. Answering is not optional politeness.
            _scratch.Reset();
            NetWriter.WriteByte(_scratch, NetRunner.WorldMsgReady);
            NetWriter.WriteBytes(_scratch, payload.Slice(WorldOpcodeBytes, WorldIdBytes));
            Send((byte)NetRunner.ENetChannelId.World, _scratch, PacketFlags.Reliable);

            // The new world knows nothing about us yet: our node ids, and the tick numbering, both
            // start again.
            _inputMapCount = 0;
            _pendingAckTick = -1;
            _lastServerTick = -1;
            _predictedTick = -1;
            _eligibleFrameIndex = 0;
            Phase = PeerPhase.Acking;
            stats.WorldHandoffsAcked++;
        }

        private void OnTestHarnessPacket(ReadOnlySpan<byte> payload)
        {
            if (!TestHarnessChannel.TryReadInputMap(payload, _inputMap, out int count)) return;

            _inputMapCount = count;
            BindInputMap();
            if (count > 0 && Phase == PeerPhase.Acking) Phase = PeerPhase.InWorld;
        }

        /// <summary>
        /// Offers each mapped node to the input source and sizes the rings for the ones it takes.
        ///
        /// <para>Re-run whenever a new map arrives, because the map changes when the peer's owned
        /// set does — a node it gains, loses, or has re-addressed after a world change.</para>
        /// </summary>
        private void BindInputMap()
        {
            for (int i = 0; i < _inputMapCount; i++)
            {
                _boundSlot[i] = -1;
                if (_inputSource == null) continue;

                ref readonly var entry = ref _inputMap[i];
                string scenePath = Protocol.GetScenePath(entry.SceneId);
                if (string.IsNullOrEmpty(scenePath)) continue;

                string childPath = entry.StaticChildId == 0
                    ? "."
                    : StaticChildPath(scenePath, entry.StaticChildId);
                if (childPath == null) continue;

                if (!_inputSource.TryBind(scenePath, childPath, entry.InputSize, out int slot)) continue;

                _boundSlot[i] = slot;
                EnsureInputStorage(i, entry.InputSize);
            }
        }

        /// <summary>
        /// Resolves a static child's path within its scene, so a source binds by node path rather
        /// than by guessing from a byte count. Null when the protocol does not know the pair.
        /// </summary>
        private static string StaticChildPath(string scenePath, byte staticChildId)
        {
            if (!GeneratedProtocol.StaticNetworkNodePathsMap.TryGetValue(scenePath, out var nodeMap))
                return null;
            return nodeMap.TryGetValue(staticChildId, out var path) ? path : null;
        }

        private void EnsureInputStorage(int entryIndex, int inputSize)
        {
            if (_currentInput[entryIndex] == null || _currentInput[entryIndex].Length != inputSize)
            {
                _currentInput[entryIndex] = new byte[inputSize];
            }

            if (_inputRing[entryIndex] == null)
            {
                _inputRing[entryIndex] = new byte[InputRingSize][];
                _inputRingTicks[entryIndex] = new int[InputRingSize];
            }

            var ring = _inputRing[entryIndex];
            var ticks = _inputRingTicks[entryIndex];
            for (int slot = 0; slot < InputRingSize; slot++)
            {
                if (ring[slot] == null || ring[slot].Length != inputSize) ring[slot] = new byte[inputSize];
                ticks[slot] = -1;
            }

            _inputChanged[entryIndex] = true;   // first packet always goes out
        }

        /// <summary>
        /// Runs this peer's own tick: advances prediction, sends input, and flushes any
        /// acknowledgement that did not ride an input packet.
        /// </summary>
        public void Tick(int tps, LoadClientStats stats)
        {
            if (!_peerSet) return;

            RunPredictionTicks(tps, stats);

            // Whatever no input packet took goes out alone. Outside any "is prediction running"
            // gate on purpose: the acknowledgement is what moves the peer from INITIAL to IN_WORLD
            // server-side, and before the peer owns anything there is no input packet to ride.
            if (_pendingAckTick >= 0)
            {
                SendStandaloneAck(_pendingAckTick, stats);
                _pendingAckTick = -1;
            }
        }

        private void RunPredictionTicks(int tps, LoadClientStats stats)
        {
            if (_inputMapCount == 0 || _lastServerTick < 0) return;

            // Seed the predicted timeline the first time the server's is known.
            if (_predictedTick < 0) _predictedTick = _lastServerTick;

            // Reuse the real client's own lead functions rather than reimplementing them. Calling
            // the SAME code is the point: a synthetic peer whose lead drifted from a real client's
            // would send input for different ticks and land in different server input-buffer slots,
            // which changes what is being measured.
            uint rtt = RoundTripTimeMs;
            int targetLead = WorldRunner.ComputeTargetLeadTicks(rtt, tps);
            int lead = _predictedTick - _lastServerTick;
            int ticksToRun = WorldRunner.PredictionTicksThisFrame(lead, targetLead, _eligibleFrameIndex++);

            for (int t = 0; t < ticksToRun; t++)
            {
                _predictedTick++;
                _cadence.BeginTick();

                for (int entry = 0; entry < _inputMapCount; entry++)
                {
                    SendInputForEntry(entry, stats);
                }
            }
        }

        private void SendInputForEntry(int entryIndex, LoadClientStats stats)
        {
            int slot = _boundSlot[entryIndex];
            if (slot < 0) return;

            var current = _currentInput[entryIndex];
            if (current == null) return;

            if (_inputSource.WriteInput(slot, _predictedTick, current)) _inputChanged[entryIndex] = true;

            // Buffer every tick, send only some. The server's input buffer falls back to the
            // nearest past record, so the ring has to be complete even where the wire is not.
            BufferInput(entryIndex, _predictedTick, current);

            if (!SyntheticInputCadence.ShouldSend(_inputChanged[entryIndex], _predictedTick)) return;

            bool carriesAck = _cadence.TryClaimAck(_pendingAckTick);
            int ackTick = _pendingAckTick;

            _scratch.Reset();
            ref readonly var entry = ref _inputMap[entryIndex];
            WorldRunner.WriteInputHeader(
                _scratch, carriesAck, ackTick,
                entry.LocalNodeId, entry.StaticChildId, entry.InputSize);

            CollectRecentInputs(entryIndex, entry.InputSize);
            WorldRunner.WriteInputRecords(_scratch, _recentInputs, entry.InputSize);

            if (!Send((byte)NetRunner.ENetChannelId.Input, _scratch, PacketFlags.None)) return;

            stats.InputPacketsSent++;
            _inputChanged[entryIndex] = false;
            if (carriesAck) _pendingAckTick = -1;
        }

        private void BufferInput(int entryIndex, int tick, byte[] input)
        {
            int slot = tick & (InputRingSize - 1);
            input.CopyTo(_inputRing[entryIndex][slot], 0);
            _inputRingTicks[entryIndex][slot] = tick;
        }

        /// <summary>
        /// Fills <see cref="_recentInputs"/> newest-first, skipping ring slots that hold a
        /// different tick — the shape <c>GetRecentInputs</c> produces, and the shape
        /// <c>WriteInputRecords</c> expects.
        /// </summary>
        private void CollectRecentInputs(int entryIndex, int inputSize)
        {
            _recentInputs.Clear();
            var ring = _inputRing[entryIndex];
            var ticks = _inputRingTicks[entryIndex];

            for (int back = 0; back < RedundancyCount; back++)
            {
                int tick = _predictedTick - back;
                if (tick < 0) break;
                int slot = tick & (InputRingSize - 1);
                if (ticks[slot] != tick) continue;
                _recentInputs.Add((tick, ring[slot]));
            }
        }

        /// <summary>
        /// Gives up on a peer that never connected. Reported rather than retried: a server at its
        /// peer limit replies to a connect with nothing at all, so silence is a real answer and it
        /// reads exactly like a slow ramp unless the harness names it.
        /// </summary>
        public void CheckConnectTimeout(double nowMs, double timeoutMs)
        {
            if (Phase != PeerPhase.Connecting) return;
            if (nowMs - _connectSentAtMs < timeoutMs) return;
            Phase = PeerPhase.Failed;
        }

        private void SendStandaloneAck(int tick, LoadClientStats stats)
        {
            _scratch.Reset();
            NetWriter.WriteInt32(_scratch, tick);
            if (Send((byte)NetRunner.ENetChannelId.Tick, _scratch, PacketFlags.Unsequenced))
            {
                stats.AcksSent++;
            }
        }

        private bool Send(byte channel, NetBuffer buffer, PacketFlags flags)
        {
            if (!_peerSet) return false;

            // Not NetRunner.SendPacket: that takes the process-wide ENet lock and applies the
            // process's impairment settings against a host this peer does not belong to. A shard
            // owns its host outright and is single-threaded, so it needs no lock of its own.
            var packet = default(Packet);
            packet.Create(buffer.RawBuffer, buffer.Length, flags);
            return _peer.Send(channel, ref packet);
        }

        public void Disconnect()
        {
            if (!_peerSet) return;
            _peer.DisconnectNow(0);
            _peerSet = false;
        }
    }
}
