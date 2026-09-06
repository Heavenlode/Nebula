using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using MongoDB.Bson;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Nebula.Utility.Tools;

namespace Nebula
{
    /**
    <summary>
    Manages the network state of all <see cref="NetNode"/>s in the scene.
    Inside the <see cref="NetRunner"/> are one or more "Worlds". Each World represents some part of the game that is isolated from other parts. For example, different maps, dungeon instances, etc. Worlds are dynamically created by calling <see cref="NetRunner.CreateWorld"/>.

    Worlds cannot directly interact with each other and do not share state.

    Players only exist in one World at a time, so it can be helpful to think of the clients as being connected to a World directly.
    </summary>
    */
    public partial class WorldRunner : Node
    {
        /// <summary>
        /// Client identifier for debugging. Set via --clientId=X command line argument.
        /// </summary>
        public static int ClientId { get; private set; } = -1;
        private static bool _clientIdParsed = false;
        public struct NetFunctionCtx
        {
            public NetPeer Caller;
        }
        /// <summary>
        /// Provides context about the current network function call.
        /// </summary>
        public NetFunctionCtx NetFunctionContext { get; private set; }

        public enum PeerSyncStatus
        {
            INITIAL,
            IN_WORLD,
            DISCONNECTED
        }

        /// <summary>
        /// Tracks the spawn lifecycle for a node per peer.
        /// </summary>
        public enum ClientSpawnState
        {
            /// <summary>Node not registered for this peer yet</summary>
            NotSpawned,
            /// <summary>Spawn data being sent (registered but not ACKed)</summary>
            Spawning,
            /// <summary>Spawn ACKed, client definitely has the node</summary>
            Spawned,
            /// <summary>Despawn data being sent, waiting for ACK</summary>
            Despawning,
            /// <summary>Despawn ACKed, safe to clean up</summary>
            Despawned
        }

        public struct PeerState
        {
            public NetPeer Peer;
            public Tick Tick;

            /// <summary>
            /// World tick at which this peer joined (JoinPeer). The ack-timeout sweep
            /// applies the lenient join window for the first JoinAckTimeoutSeconds after
            /// this tick: a client's heaviest silent stretch (world-scene load, spatial
            /// mirror build) happens AFTER its first few acks, so neither Status nor
            /// "has acked once" marks the end of joining - only elapsed time can.
            /// </summary>
            public Tick JoinedAtTick;

            public PeerSyncStatus Status;
            public UUID Id;
            public string Token;
            public Dictionary<NetId, ushort> WorldToPeerNodeMap;
            public Dictionary<ushort, NetId> PeerToWorldNodeMap;

            /// <summary>
            /// Tracks the spawn state of each node for this peer.
            /// </summary>
            public Dictionary<NetId, ClientSpawnState> SpawnState;

            /// <summary>
            /// A hierarchical bitmask of nodeIds that are in use by the peer.
            /// 8 groups of 64 nodes each (512 total).
            /// </summary>
            public long[] AvailableNodes;

            /// <summary>
            /// A list of nodes that the player owns (i.e. InputAuthority == peer
            /// </summary>
            public HashSet<NetworkController> OwnedNodes;
        }

        internal struct QueuedFunction
        {
            public Node Node;
            public ProtocolNetFunction FunctionInfo;
            public PropertyCache[] Args;
            public NetPeer Sender;
        }

        private UUID _worldId;

        /// <summary>
        /// Identity of this world.
        ///
        /// <para>On the server this is assigned at construction. On the client
        /// it starts empty — nothing in the initial join handshake carries it —
        /// and is only learned if the peer is later migrated to another world.
        /// The setter re-announces on the debug channel so an attached debugger
        /// follows the change rather than showing a stale entry.</para>
        /// </summary>
        public UUID WorldId
        {
            get => _worldId;
            internal set
            {
                if (_worldId == value)
                    return;
                var previous = _worldId;
                _worldId = value;
                Hub?.ReannounceWorld(this, previous);
            }
        }

        // A hierarchical bitmask of all nodes in use on the client side.
        // 8 groups of 64 nodes each (512 total).
        public long[] ClientAvailableNodes = NodeIdUtils.CreateMasks();
        private Dictionary<UUID, PeerState> PeerStates = [];

        /// <summary>
        /// Invoked when a peer's sync status changes. Parameters: (peerId, newStatus)
        /// </summary>
        public event Action<UUID, PeerSyncStatus> OnPeerSyncStatusChange;

        private List<QueuedFunction> queuedNetFunctions = [];


        /// <summary>
        /// Only applicable on the client side.
        /// </summary>
        public static WorldRunner CurrentWorld { get; internal set; }

        /// <summary>
        /// The root NetworkController for this world. Set during world creation.
        /// Used as the default parent when spawning nodes without an explicit parent.
        /// </summary>
        public NetworkController RootScene;

        /// <summary>How far along a world is in coming up. See <see cref="Lifecycle"/>.</summary>
        public enum WorldLifecycle
        {
            /// <summary>Registered and reserving its id, but still being built. Not tickable, not joinable.</summary>
            Generating,

            /// <summary>Fully built. The only state in which peers may be admitted.</summary>
            Live,

            /// <summary>Creation threw. The world is being torn down; nothing may reference it.</summary>
            Failed,
        }

        /// <summary>
        /// Where this world is in its creation. A world is registered in
        /// <see cref="NetRunner.Worlds"/> the instant creation starts -- so that concurrent callers
        /// resolve to one world rather than racing to build duplicates -- which means "registered"
        /// no longer implies "ready". This is what distinguishes them.
        ///
        /// Ticking is gated separately, by the world SubViewport's ProcessMode, because a
        /// Lifecycle check inside _PhysicsProcess would still let every gameplay node in a
        /// half-built world run. This flag is what gates <em>peers</em>: see JoinPeer,
        /// NetRunner.PeerJoinWorld and NetRunner.MigratePeerToWorld.
        ///
        /// Defaults to Generating; creation flips it to Live as its final step.
        /// </summary>
        public WorldLifecycle Lifecycle { get; internal set; } = WorldLifecycle.Generating;

        internal long networkIdCounter = 1; // Start at 1 because NetId=0 is considered invalid
        private Dictionary<long, NetId> networkIds = [];
        internal Dictionary<NetId, NetworkController> NetScenes = [];

        /// <summary>
        /// The process-wide debug channel, or null when debugging wasn't
        /// requested. Every emitter below gates on <c>is { HasClients: true }</c>
        /// so the debug path costs a null check when nobody is watching.
        /// </summary>
        private static DebugHub Hub => NetRunner.Instance?.DebugHub;

        /// <summary>
        /// Frame types on the debug channel. Values are explicit and MUST NOT
        /// be reordered: the integration harness
        /// (Testing/Integration/GodotProcess.cs) identifies DEBUG_EVENT frames
        /// by the literal byte 6.
        /// </summary>
        public enum DebugDataType : byte
        {
            TICK = 0,
            PAYLOADS = 1,
            EXPORT = 2,
            LOGS = 3,
            PEERS = 4,
            CALLS = 5,
            DEBUG_EVENT = 6,
            WORLD_ANNOUNCE = 7,
            WORLD_REMOVED = 8,

            /// <summary>One ServerMetrics JSON line, ~1/s per world, for the editor's Performance tab.</summary>
            METRICS = 9,
        }

        /// <summary>
        /// Sends named debug events (e.g. "Spawn", "Input") on this world's
        /// debug channel. Consumed by the integration harness, which waits on
        /// specific category/message pairs.
        ///
        /// Buffering of pre-connection events now lives in
        /// <see cref="DebugHub"/>, which owns the socket; this is a thin
        /// world-scoped facade over it.
        /// </summary>
        public class DebugMessenger
        {
            private readonly WorldRunner _world;

            public DebugMessenger(WorldRunner world)
            {
                _world = world;
            }

            /// <param name="category">Event category (e.g., "Spawn", "Connect")</param>
            /// <param name="message">Event message/details</param>
            public void Send(string category, string message)
            {
                var hub = Hub;
                if (hub is not { DebugFramesEnabled: true }) return;

                // Sized explicitly: NetBuffer throws on overflow rather than
                // growing, and its 1536-byte default is not guaranteed to fit
                // an arbitrary message.
                using var buffer = new NetBuffer((category.Length + message.Length) * 4 + 32, usePool: true);
                NetWriter.WriteString(buffer, category);
                NetWriter.WriteString(buffer, message);

                hub.Enqueue(_world.WorldId, DebugDataType.DEBUG_EVENT, buffer, lossy: false);
            }
        }

        /// <summary>
        /// Debug messenger for sending test events on the debug channel.
        /// </summary>
        public DebugMessenger Debug { get; private set; }

        /// <summary>
        /// Port this process' debug channel is listening on, or 0 when
        /// debugging is off. Read-only now that the channel is process-wide
        /// rather than per-world.
        /// </summary>
        public int DebugPort => Hub?.BoundPort ?? 0;

        private List<TickLog> tickLogBuffer = [];
        public void Log(string message, Debugger.DebugLevel level = Debugger.DebugLevel.INFO)
        {
            // Buffer for the debug channel only while something is there to read it:
            // ServerProcessTick drains this under `debugAttached` and clears it either
            // way, so with no debugger attached every entry was allocated and dropped.
            if (NetRunner.Instance.IsServer && Hub is { HasClients: true, DebugFramesEnabled: true })
            {
                tickLogBuffer.Add(new TickLog
                {
                    Message = message,
                    Level = level,
                });
            }

            Debugger.Instance.Log(message, level);
        }

        public void Log(Debugger.DebugLevel level, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("level")] ref Nebula.Utility.Tools.NebulaLogInterpolatedStringHandler handler)
        {
            if (!handler.Enabled) return;
            Log(handler.ToStringAndClear(), level);
        }

        Action<uint> _onPeerDisconnectedHandler;

        public override void _Ready()
        {
            base._Ready();
            Name = "WorldRunner";
            Debug = new DebugMessenger(this);

            // Parse command line args (--debugPort is handled once, process-wide,
            // in NetRunner.StartTelemetryHub)
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument.StartsWith("--clientId=") && !_clientIdParsed)
                {
                    var value = argument.Substring("--clientId=".Length);
                    if (int.TryParse(value, out int parsedId))
                    {
                        ClientId = parsedId;
                        _clientIdParsed = true;
                    }
                }
            }

            // Announce this world on the process-wide debug channel. Registering
            // from here rather than from NetRunner.Worlds is what makes it work
            // on clients, where Worlds is never populated.
            var hub = Hub;
            if (hub != null)
            {
                hub.RegisterWorld(this);
                TreeExiting += OnTreeExitingUnregisterDebug;
            }

            if (NetRunner.Instance.IsServer)
            {
                _onPeerDisconnectedHandler = (uint nativePeerId) =>
                {
                    var peer = NetRunner.Instance.GetPeerByNativeId(nativePeerId);
                    if (!peer.IsSet) return;
                    var peerId = NetRunner.Instance.GetPeerId(peer);
                    if (!PeerStates.ContainsKey(peerId)) return; // Already cleaned up

                    if (AutoPlayerCleanup)
                    {
                        CleanupPlayer(peer);
                        return;
                    }
                    var newPeerState = PeerStates[peerId];
                    newPeerState.Tick = CurrentTick;
                    newPeerState.Status = PeerSyncStatus.DISCONNECTED;
                    SetPeerState(peer, newPeerState);
                };
                NetRunner.Instance.OnPeerDisconnected += _onPeerDisconnectedHandler;
            }
        }

        public override void _ExitTree()
        {
            base._ExitTree();

            if (NetRunner.Instance.IsServer)
            {
                NetRunner.Instance.OnPeerDisconnected -= _onPeerDisconnectedHandler;
                ReleaseInboundPackets();
            }
        }

        /// <summary>
        /// There is no world-destroyed event on NetRunner (Worlds is never
        /// pruned), so tree exit is the signal that a world went away — the
        /// same approach PeerPhysicsRunner uses.
        /// </summary>
        private void OnTreeExitingUnregisterDebug()
        {
            Hub?.UnregisterWorld(this);
        }

        private int _debugExportCounter;

        /// <summary>
        /// Cycle-guard set for the world-state export, reused between exports.
        /// </summary>
        private readonly HashSet<Node> _debugVisited = new();

        /// <summary>
        /// Writes an id's raw 16 bytes into a debug payload.
        /// <see cref="UUID.ToByteArray"/> would allocate an array per call — once
        /// per peer per emit on these paths — so the debug channel writes through
        /// the span instead.
        /// </summary>
        private static void WriteIdBytes(NetBuffer buffer, in UUID id)
        {
            id.TryWriteBytes(buffer.GetWriteSpan(DebugFrame.WorldIdSize));
            buffer.AdvanceWrite(DebugFrame.WorldIdSize);
        }

        /// <summary>
        /// Full world state for the debugger's node tree and property
        /// inspector. Reuses the persistence serializer
        /// (<c>NetNodeCommon.ToBSONDocument</c>) rather than a bespoke walk, so
        /// the property-to-value mapping stays generated in one place.
        ///
        /// <para>Shipped as RelaxedExtendedJson, not BSON bytes: the editor
        /// converts to JSON immediately anyway, and the two BSON
        /// implementations in play (MongoDB here, LiteDB there) do not agree on
        /// their type sets. Relaxed mode specifically — the default Shell mode
        /// emits <c>NumberLong(5)</c>, which is not valid JSON.</para>
        ///
        /// <para>Only runs while a debug client is attached, and never throws
        /// into the tick loop.</para>
        /// </summary>
        private void EmitDebugWorldState(DebugHub hub)
        {
            if (RootScene?.NetNode is not IBsonSerializableBase root)
                return;
            if (_debugExportCounter++ % NetRunner.DebugExportInterval != 0)
                return;

            string json;
            try
            {
                // Visited set guards against reference cycles: a NetNode-typed
                // [NetProperty] pointing back at an ancestor would otherwise
                // recurse until the stack blows, taking the process with it.
                // Reused across exports (cleared, not reallocated) so the guard
                // itself doesn't become the thing that churns the heap.
                _debugVisited.Clear();
                var state = root.BsonSerialize(new NetBsonContext
                {
                    Recurse = true,
                    Visited = _debugVisited,
                });
                json = state.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
                {
                    OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson,
                });
            }
            catch (Exception ex)
            {
                Log(Debugger.DebugLevel.ERROR, $"Debug world-state export failed: {ex.Message}");
                return;
            }

            using var buffer = new NetBuffer(json.Length * 4 + 64, usePool: true);
            NetWriter.WriteString(buffer, json);
            hub.Enqueue(WorldId, DebugDataType.EXPORT, buffer, lossy: true);
        }

        /// <summary>
        /// Per-peer sync status for the debugger's Peers tab. Live only — this
        /// is a "what is happening right now" view, so it is not persisted into
        /// tick frames. Emitted at roughly 1 Hz.
        /// </summary>
        private void EmitDebugPeers(DebugHub hub)
        {
            if (PeerStates.Count == 0)
                return;
            if (NetRunner.TPS <= 0 || CurrentTick % NetRunner.TPS != 0)
                return;

            const int BytesPerPeer = 16 + 4 + 1 + 2;
            int count = Math.Min(PeerStates.Count, byte.MaxValue);

            using var buffer = new NetBuffer(1 + count * BytesPerPeer + 16, usePool: true);
            NetWriter.WriteByte(buffer, (byte)count);

            int written = 0;
            foreach (var peerState in PeerStates.Values)
            {
                if (written >= count)
                    break;
                WriteIdBytes(buffer, peerState.Id);
                NetWriter.WriteInt32(buffer, peerState.Tick);
                NetWriter.WriteByte(buffer, (byte)peerState.Status);
                NetWriter.WriteUInt16(buffer, (ushort)Math.Min(peerState.OwnedNodes?.Count ?? 0, ushort.MaxValue));
                written++;
            }

            hub.Enqueue(WorldId, DebugDataType.PEERS, buffer, lossy: true);
        }

        /// <summary>
        /// The current network tick. On the client side, this does not represent the server's current tick, which will always be slightly ahead.
        /// </summary>
        public int CurrentTick { get; internal set; } = 0;

        #region Snapshot Interpolation

        /// <summary>
        /// Time accumulator for sub-tick interpolation (global for all entities).
        /// </summary>
        internal float TimeSinceLastTick = 0f;

        /// <summary>
        /// Number of ticks to delay rendering behind the latest received tick.
        /// Default 2 (~33ms at 60Hz). Lower = less latency, Higher = smoother.
        /// </summary>
        public int InterpolationDelayTicks { get; set; } = 2;

        /// <summary>
        /// Called in WorldRunner._Process to accumulate time between ticks.
        /// </summary>
        internal void AccumulateRenderTime(float delta)
        {
            TimeSinceLastTick += delta;

            // Peak silence this window. Sampled here rather than on arrival because the gap that
            // matters is the one still OPEN -- a stream that has stopped reports nothing at all.
        }

        /// <summary>
        /// Called when ClientProcessTick receives a new tick (resets accumulator).
        /// </summary>
        internal void OnWorldTickReceived(int tick)
        {
            // The gap that just ended is what the jitter buffer is sized from, so it is tallied here --
            // the one place that knows an arrival happened.
            if (NetRunner.Instance.IsClient) RecordTickGap(TimeSinceLastTick);

            // Reset accumulator when we receive a new tick
            TimeSinceLastTick = 0f;
        }

        // ---------------------------------------------------------------- the render clock

        /// <summary>
        /// Continuous render tick. Advanced once per frame in <see cref="_Process"/>, never derived
        /// per call -- see <see cref="AdvanceRenderClock"/>.
        /// </summary>
        private float _renderClock = float.NaN;

        private float _renderClockError;
        private int _renderClockSampledTick = int.MinValue;

        /// <summary>How hard the clock is pulled toward the simulated tick, per tick of error.</summary>
        private const float RenderClockGain = 0.25f;

        /// <summary>Ceiling on that correction as a fraction of normal speed.</summary>
        private const float RenderClockMaxRateAdjust = 0.05f;

        /// <summary>Error past which the clock is re-seeded instead of corrected: a world change, a
        /// long hitch, the first frame. Evaluated ONLY when a tick arrives -- see
        /// <see cref="AdvanceRenderClock"/> for why silence must never trigger it.</summary>
        private const float RenderClockResyncTicks = 3f;

        /// <summary>How much of each once-per-tick error sample is taken.</summary>
        private const float RenderClockSampleWeight = 0.25f;

        /// <summary>
        /// Advances the render clock one frame.
        ///
        /// <para>This replaced "last RECEIVED tick plus how long ago it arrived", which is exact and
        /// stateless while packets arrive on a perfectly regular cadence -- the accumulator reaches one
        /// tick exactly as the tick counter increments, so the result is continuous with no state at
        /// all. Under jitter that coincidence breaks, and it breaks in a way that is easy to miss:
        /// the value never went BACKWARD in measurement, but it advanced UNEVENLY. Measured at 50fps
        /// against 30Hz ticks, remote entities should move 0.6 ticks per frame and instead alternated
        /// between 0.4 and 1.0 -- a couple of units of positional wobble at half the frame rate, which
        /// is a shimmer rather than a jump. Arrival jitter of 19-43ms against a 33.3ms nominal went
        /// straight onto the screen.</para>
        ///
        /// <para>So the clock free-runs on real time and is pulled toward the simulated tick by a
        /// small capped RATE adjustment. It cannot drift, because a standing error is always being
        /// corrected, and it cannot judder, because it only ever advances -- slightly fast or slightly
        /// slow. The error is sampled ONCE PER TICK, at arrival, because that is the only instant whose
        /// meaning is unambiguous: continuous time is exactly T when tick T lands. Sampled every frame
        /// against the tick counter instead, the error sawtooths by a full tick every tick however
        /// well the clock tracks, and steering the rate with that reintroduces the very unevenness
        /// this removes.</para>
        /// </summary>
        internal static (float Tick, float Error, int SampledTick) AdvanceRenderClock(
            float clock, float error, int sampledTick, int currentTick, int delayTicks, float delta)
        {
            int targetTick = currentTick - delayTicks;

            if (float.IsNaN(clock)) return (targetTick, 0f, currentTick);

            // A RE-SEED IS ONLY EVER JUSTIFIED BY AN ARRIVAL, so both it and the error sample live
            // behind this one gate.
            //
            // SILENCE IS ABSENCE OF NEWS, NOT EVIDENCE THAT RENDER TIME IS WRONG. While the stream is
            // quiet the target stands still, so a free-running clock necessarily outruns it -- and a
            // symmetric every-frame check read that as a three-tick error and "corrected" it by
            // snapping render time BACKWARD, replaying motion already drawn. Measured under 200ms
            // bursts every ~8s: a -3.4 tick step (~115ms of motion) on every single burst, and it
            // persisted at the deepest buffer the controller can reach, because buffer depth and this
            // are unrelated faults. The clock was right and the target was merely stale: the server
            // kept ticking, we just had not heard, and when the stream resumes the target jumps by
            // exactly the length of the silence and lands back on the clock -- no correction needed,
            // which is why coasting costs nothing to recover from.
            //
            // A genuine pause is still caught, just one instant later: when the stream resumes with
            // the target STILL far behind the clock, that arrival proves the server did not tick
            // through the gap, and the re-seed below fires then.
            //
            // THE GATE IS THE TICK COUNTER, NOT THE TARGET, and the difference is not pedantic. The
            // target is `currentTick - delayTicks`, so the ADAPTIVE BUFFER moves it too -- and the one
            // moment it is most likely to move is the window a dropout was just detected in, which is
            // exactly when the clock is coasting furthest ahead. Keyed on the target, a delay change
            // mid-dropout opens this gate with no arrival behind it, and the re-seed fires against a
            // stale target: observed once as a -8.3 tick step on a 300ms burst that grew the buffer
            // from 4 to 5. Keyed on the counter, resizing the buffer only moves where the clock is
            // AIMED, and the next real arrival slews to it.
            if (currentTick != sampledTick)
            {
                if (Math.Abs(targetTick - clock) > RenderClockResyncTicks) return (targetTick, 0f, currentTick);

                sampledTick = currentTick;
                error = error + (targetTick - clock - error) * RenderClockSampleWeight;
            }

            var rate = 1f + Math.Clamp(
                error * RenderClockGain, -RenderClockMaxRateAdjust, RenderClockMaxRateAdjust);
            return (clock + delta * NetRunner.TPS * rate, error, sampledTick);
        }

        /// <summary>
        /// Get the fractional render tick for interpolation (used by all entities).
        ///
        /// <para>A pure read of the clock advanced in <see cref="_Process"/>; every entity in the frame
        /// therefore gets the same answer no matter when it asks.</para>
        ///
        /// <para>THE DELAY IS ALREADY IN THE CLOCK -- it is part of what the clock aims at, not
        /// subtracted here. That is what lets <see cref="InterpolationDelayTicks"/> change at runtime:
        /// a one-tick change moves the target by one tick and the rate correction absorbs it over
        /// about a second, instead of teleporting render time the instant the buffer is resized.</para>
        /// </summary>
        public float GetRenderTick()
        {
            if (float.IsNaN(_renderClock))
            {
                // Before the first frame has advanced it, fall back to the original derivation so a
                // caller during startup still gets a sane answer.
                float fallback = Math.Min(TimeSinceLastTick * NetRunner.TPS, 1.5f);
                return CurrentTick + fallback - InterpolationDelayTicks;
            }
            return _renderClock;
        }

        // ------------------------------------------------------------- adaptive interpolation delay

        /// <summary>
        /// Shallowest buffer the controller will settle on, and the value shipped as the default.
        ///
        /// <para>NOT the theoretical minimum. One tick technically interpolates -- there is still a
        /// snapshot on each side -- but it leaves no slack whatsoever: a single packet arriving late
        /// starves it, the buffer grows back, and the controller hunts between one and two forever on
        /// any link with jitter at all. Two ticks is the shallowest depth that absorbs one late
        /// packet, so it is the right floor for a policy whose whole job is avoiding starvation.</para>
        /// </summary>
        private const int MinInterpolationDelayTicks = 2;

        /// <summary>
        /// Largest usable buffer, and a real ceiling rather than a chosen one:
        /// <c>NetworkController.SNAPSHOT_BUFFER_SIZE</c> is 8 PER ENTITY, so beyond this the delay
        /// would point past the oldest snapshot the buffer can still hold. At 30 TPS that caps the
        /// jitter buffer at roughly 200ms. If the controller pins here under test, the buffer size is
        /// the next thing to look at -- growing it costs memory per entity per property, so it is a
        /// deliberate separate decision rather than something to raise quietly.
        /// </summary>
        private const int MaxInterpolationDelayTicks = 6;

        /// <summary>Windows the target must stay below the current depth before a tick is given back.</summary>
        private const int CleanWindowsBeforeShrink = 5;

        /// <summary>
        /// Arrival gaps are tallied in whole ticks; anything at or beyond the last bucket lands in it.
        /// Sized past <see cref="MaxInterpolationDelayTicks"/> so an outage the buffer could never
        /// cover is still COUNTED (it has to be, or it could not be outvoted by the ordinary traffic
        /// around it) without needing a bucket per possible length.
        /// </summary>
        private const int GapHistogramBuckets = 16;

        /// <summary>
        /// Fraction of arrivals the buffer is sized to absorb without a freeze.
        ///
        /// <para>THE WHOLE POINT OF THE REWRITE. Sizing for the WORST gap means paying its latency on
        /// every frame forever, and a rare outage is the one case where that trade is clearly bad: a
        /// 200ms burst every 8s is 0.4% of arrivals, so covering it costs 100ms of permanent lag to
        /// improve four seconds in a thousand. Sizing for a high percentile instead covers ordinary
        /// jitter -- which is continuous, and what a jitter buffer is actually for -- and lets the rare
        /// outage be the freeze it honestly is.</para>
        /// </summary>
        private const float TargetGapCoverage = 0.99f;

        /// <summary>
        /// Arrivals the percentile is taken over: a true sliding window, ~17s at 30 TPS.
        ///
        /// <para>SIZED BY WHAT THE PERCENTILE HAS TO RESOLVE, not by feel. A 99th percentile can only
        /// step over an event that is genuinely rarer than 1% of samples, so the window has to hold
        /// enough arrivals for a rare fault to BE rare in it. A burst every 8s is one arrival in 240;
        /// measured against a decaying tally worth only ~56 effective samples, that single gap was 1.8%
        /// of the distribution and dragged the target to the ceiling on every burst -- the percentile
        /// was right and the sample count was too small for it to mean anything.</para>
        /// </summary>
        private const int GapWindowSamples = 512;

        /// <summary>
        /// Most the buffer may move in one window.
        ///
        /// <para>Kept strictly under <see cref="RenderClockResyncTicks"/>, and that is the entire
        /// reason it exists: the delay is part of what the render clock aims at, so resizing the buffer
        /// by more than the clock can absorb re-seeds it at the next arrival -- a visible jump
        /// BACKWARD, which is exactly what the clock work removed. Observed as a -4.5 tick step when a
        /// four-tick growth landed in one window.</para>
        /// </summary>
        private const int MaxDelayStepTicks = 2;

        private ulong _delayWindowStartMsec;

        /// <summary>How many arrivals in the window were separated by n whole ticks. Maintained
        /// incrementally against <see cref="_gapSamples"/>, so the percentile is exact and costs
        /// nothing to read.</summary>
        private readonly int[] _gapHistogram = new int[GapHistogramBuckets];

        /// <summary>The window itself: one bucket index per arrival, oldest overwritten first.
        /// A byte because <see cref="GapHistogramBuckets"/> is 16.</summary>
        private readonly byte[] _gapSamples = new byte[GapWindowSamples];
        private int _gapSampleIndex;
        private int _gapSampleCount;

        /// <summary>Consecutive windows whose target sat below the current depth.</summary>
        private int _windowsBelowTarget;

        /// <summary>
        /// Tallies one arrival gap. Called from <see cref="OnWorldTickReceived"/>, which is the only
        /// place that knows an arrival happened -- and the level at which a dropout is distinguishable
        /// from an idle entity at all. A per-entity view cannot tell them apart: both look like running
        /// past the newest snapshot. An idle entity is quiet while every OTHER entity keeps arriving;
        /// a dropout is the whole stream stopping, which is precisely what this measures.
        /// </summary>
        private void RecordTickGap(float gapSeconds)
        {
            int gapTicks = (int)(gapSeconds * NetRunner.TPS);
            if (gapTicks < 0) gapTicks = 0;
            if (gapTicks >= GapHistogramBuckets) gapTicks = GapHistogramBuckets - 1;

            // Evict the sample this slot is about to overwrite, so the histogram always describes
            // exactly the arrivals still in the window.
            if (_gapSampleCount == GapWindowSamples) _gapHistogram[_gapSamples[_gapSampleIndex]]--;
            else _gapSampleCount++;

            _gapSamples[_gapSampleIndex] = (byte)gapTicks;
            _gapHistogram[gapTicks]++;
            _gapSampleIndex = (_gapSampleIndex + 1) % GapWindowSamples;
        }

        /// <summary>
        /// The shallowest buffer that would have absorbed <paramref name="coverage"/> of the arrivals
        /// tallied in <paramref name="histogram"/>, clamped to the usable range.
        ///
        /// <para>THE FIX FOR A BUFFER THAT HUNTS. The previous policy grew on ANY starvation and shrank
        /// after a fixed clean run, which cannot settle against a repeating fault: measured against a
        /// 200ms burst every 8s, it sawtoothed between 3 and 6 ticks forever -- 100ms of lag appearing
        /// and disappearing -- while never once being deep enough, because nine of eleven bursts
        /// exceeded even the ceiling. It was paying latency for coverage it could not reach.</para>
        ///
        /// <para>A percentile settles by construction. Ordinary jitter is continuous, so it dominates
        /// the distribution and fixes the answer; a rare outage is a handful of samples that the
        /// percentile steps over, so the buffer stops chasing what it cannot catch. When the link
        /// genuinely degrades -- when big gaps stop being rare -- they cross the percentile on their
        /// own and the buffer grows, which is the adaptation actually worth having.</para>
        ///
        /// <para>Pure so the policy is testable without a network, the same way
        /// <see cref="AdvanceRenderClock"/> is.</para>
        /// </summary>
        internal static int DelayForCoverage(ReadOnlySpan<int> histogram, float coverage, int min, int max)
        {
            int total = 0;
            for (int i = 0; i < histogram.Length; i++) total += histogram[i];

            // No arrivals measured yet: the shipped default is the honest answer, not a guess.
            if (total == 0) return min;

            // Ceiling, so a coverage of 1.0 means every sample rather than all-but-rounding.
            int needed = (int)Math.Ceiling(total * coverage);

            int running = 0;
            for (int gapTicks = 0; gapTicks < histogram.Length; gapTicks++)
            {
                running += histogram[gapTicks];

                // A gap of n ticks is absorbed by a buffer of n ticks -- see the starvation rule in
                // NetworkController.GetInterpolationSnapshots -- so the bucket index IS the depth.
                if (running >= needed) return Math.Clamp(gapTicks, min, max);
            }

            return max;
        }

        /// <summary>
        /// Moves the buffer toward <paramref name="target"/>: straight up, one tick at a time down.
        ///
        /// <para>The asymmetry survives the rewrite because its reasoning does. Under-buffering is
        /// visible the moment it happens, so there is no case for approaching it slowly;
        /// over-buffering only costs latency nobody can see directly, so there is no case for racing to
        /// reclaim it, and a slow release keeps a link that is merely between faults from being
        /// re-measured as healthy.</para>
        /// </summary>
        internal static int NextInterpolationDelay(int current, int target, int windowsBelowTarget)
        {
            if (target > current)
                return Math.Min(Math.Min(target, current + MaxDelayStepTicks), MaxInterpolationDelayTicks);

            if (target < current && windowsBelowTarget >= CleanWindowsBeforeShrink)
                return Math.Max(current - 1, MinInterpolationDelayTicks);

            return current;
        }

        /// <summary>
        /// Resizes the jitter buffer once a second from what interpolation actually experienced.
        /// Client-only: the server neither interpolates nor renders.
        /// </summary>
        private void UpdateInterpolationDelay()
        {
            var now = Time.GetTicksMsec();
            if (_delayWindowStartMsec == 0) { _delayWindowStartMsec = now; return; }
            if (now - _delayWindowStartMsec < 1000) return;
            _delayWindowStartMsec = now;

            int target = DelayForCoverage(
                _gapHistogram, TargetGapCoverage, MinInterpolationDelayTicks, MaxInterpolationDelayTicks);

            _windowsBelowTarget = target < InterpolationDelayTicks ? _windowsBelowTarget + 1 : 0;

            int next = NextInterpolationDelay(InterpolationDelayTicks, target, _windowsBelowTarget);
            if (next == InterpolationDelayTicks) return;

            // Any move restarts the release run, so a buffer that steps down does not immediately
            // qualify to step down again on the following window.
            _windowsBelowTarget = 0;
            InterpolationDelayTicks = next;
        }

        #endregion

        #region Server Input Buffering

        // ============================================================
        // HOT PATH OPTIMIZATION: Avoid LINQ, minimize allocations
        // ============================================================

        private const int SERVER_INPUT_BUFFER_SIZE = 64;  // Power of 2 for fast modulo

        /// <summary>
        /// Per-entity input buffer structure for server-side input buffering.
        /// </summary>
        private struct EntityInputBuffer
        {
            public byte[][] Inputs;      // Circular buffer of input byte arrays
            public Tick[] Ticks;         // Tick for each slot
            public Tick LastReceivedTick;
            public Tick LastFallbackTick; // Cache for fallback lookup
            public byte[] LastFallbackInput;

            public void Initialize()
            {
                Inputs = new byte[SERVER_INPUT_BUFFER_SIZE][];
                Ticks = new Tick[SERVER_INPUT_BUFFER_SIZE];
                for (int i = 0; i < SERVER_INPUT_BUFFER_SIZE; i++)
                {
                    Ticks[i] = -1;
                }
                LastReceivedTick = -1;
                LastFallbackTick = -1;
                LastFallbackInput = null;
            }
        }

        /// <summary>
        /// Composite key for server input buffers.
        /// For NetScenes: (NetId, 0)
        /// For static children: (parentNetId, staticChildId)
        /// </summary>
        internal readonly struct InputBufferKey : IEquatable<InputBufferKey>
        {
            public readonly NetId ParentNetId;
            public readonly byte StaticChildId;

            public InputBufferKey(NetId parentNetId, byte staticChildId = 0)
            {
                ParentNetId = parentNetId;
                StaticChildId = staticChildId;
            }

            public bool Equals(InputBufferKey other) => 
                ParentNetId == other.ParentNetId && StaticChildId == other.StaticChildId;

            public override bool Equals(object obj) => obj is InputBufferKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(ParentNetId.Value, StaticChildId);
        }

        /// <summary>
        /// Input buffers per-entity on the server side.
        /// Key is composite (parentNetId, staticChildId) to support static children.
        /// </summary>
        private Dictionary<InputBufferKey, EntityInputBuffer> _serverInputBuffers = new();

        /// <summary>
        /// Buffers input from a client for a specific entity and tick.
        /// </summary>
        private void BufferServerInput(InputBufferKey key, Tick tick, byte[] input)
        {
            // Use ref access to avoid struct copy on modification
            ref var buffer = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_serverInputBuffers, key, out bool exists);
            if (!exists)
            {
                buffer.Initialize();
            }

            int slot = (int)(tick & (SERVER_INPUT_BUFFER_SIZE - 1));

            // Only accept if newer than what we have in this slot
            if (buffer.Ticks[slot] < tick)
            {
                // Reuse or allocate byte array
                if (buffer.Inputs[slot] == null || buffer.Inputs[slot].Length != input.Length)
                {
                    buffer.Inputs[slot] = new byte[input.Length];
                }
                Array.Copy(input, buffer.Inputs[slot], input.Length);
                buffer.Ticks[slot] = tick;

                if (tick > buffer.LastReceivedTick)
                {
                    buffer.LastReceivedTick = tick;
                }
                // No need to copy back - we modified via ref
            }
        }

        /// <summary>
        /// Gets buffered input for an entity at a specific tick.
        /// If not available, falls back to most recent input.
        /// </summary>
        private byte[] GetServerBufferedInput(InputBufferKey key, Tick tick)
        {
            // Use ref access to avoid struct copy when caching fallback
            ref var buffer = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_serverInputBuffers, key);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref buffer))
            {
                return null;
            }

            int slot = (int)(tick & (SERVER_INPUT_BUFFER_SIZE - 1));

            // Exact match
            if (buffer.Ticks[slot] == tick)
            {
                return buffer.Inputs[slot];
            }

            // Fallback: find most recent input before this tick
            // Use cached fallback if available for this tick
            if (buffer.LastFallbackTick == tick && buffer.LastFallbackInput != null)
            {
                return buffer.LastFallbackInput;
            }

            // Search for the most recent input before this tick; failing that, the nearest future
            // input. When the client's stamps have run ahead of consumption, every slot holds a
            // future tick — returning null there would leave _inputData frozen on the last applied
            // input, silently replaying stale held keys until the stream realigns.
            byte[] fallback = null;
            Tick bestTick = -1;
            byte[] nearestFuture = null;
            Tick bestFutureTick = -1;
            for (int i = 0; i < SERVER_INPUT_BUFFER_SIZE; i++)
            {
                if (buffer.Ticks[i] < 0) continue;
                if (buffer.Ticks[i] < tick)
                {
                    if (buffer.Ticks[i] > bestTick)
                    {
                        bestTick = buffer.Ticks[i];
                        fallback = buffer.Inputs[i];
                    }
                }
                else if (buffer.Ticks[i] > tick
                    && (bestFutureTick < 0 || buffer.Ticks[i] < bestFutureTick))
                {
                    bestFutureTick = buffer.Ticks[i];
                    nearestFuture = buffer.Inputs[i];
                }
            }
            if (fallback == null)
                fallback = nearestFuture;

            // Cache the fallback for this tick (modified via ref, no copy needed)
            buffer.LastFallbackTick = tick;
            buffer.LastFallbackInput = fallback;

            return fallback;
        }

        /// <summary>
        /// Cleans up input buffer for a despawned entity.
        /// </summary>
        internal void CleanupEntityInputBuffer(InputBufferKey key)
        {
            _serverInputBuffers.Remove(key);
        }

        #endregion

        #region Client Prediction

        /// <summary>
        /// The client's predicted tick (ahead of last received server tick).
        /// </summary>
        private Tick _clientPredictedTick = -1;

        /// <summary>
        /// Read-only view of the client's predicted tick, for diagnostics and for game code that
        /// needs to reason about the gap between prediction and confirmed state (-1 on the server
        /// or before prediction initializes).
        /// </summary>
        public Tick PredictedTick => _clientPredictedTick;

        /// <summary>
        /// Whether prediction has been initialized on the client.
        /// </summary>
        private bool _predictionInitialized = false;

        /// <summary>
        /// Cached list of owned entities for prediction (avoid allocation every tick).
        /// </summary>
        private List<NetworkController> _ownedEntities = new(16);
        private bool _ownedEntitiesDirty = true;

        /// <summary>
        /// Pooled buffer for acknowledgment packets.
        /// </summary>
        private NetBuffer _ackBuffer;

        /// <summary>
        /// A tick the client has applied but not yet acknowledged, held so it can ride along on the
        /// next outgoing input packet instead of costing its own datagram. -1 when nothing is
        /// waiting. Never held longer than one physics frame — _PhysicsProcess flushes it.
        /// </summary>
        private Tick _pendingAckTick = -1;

        /// <summary>
        /// Whether this prediction tick already attached the pending ack to an input packet.
        /// SendInput runs once per owned node, and only the first should carry it.
        /// </summary>
        private bool _ackAttachedThisFrame;

        /// <summary>
        /// Initializes client prediction state from the first received server tick.
        /// </summary>
        private void InitializeClientPrediction(Tick serverTick)
        {
            if (_predictionInitialized) return;

            CurrentTick = serverTick;
            _clientPredictedTick = serverTick;
            _predictionInitialized = true;
            // Log(Debugger.DebugLevel.VERBOSE, $"[Prediction] Initialized: serverTick={serverTick}");
        }

        /// <summary>
        /// Rebuilds the cached list of owned entities.
        /// </summary>
        private void RebuildOwnedEntitiesCache()
        {
            _ownedEntities.Clear();
            foreach (var kvp in NetScenes)
            {
                if (kvp.Value?.IsCurrentOwner == true)
                {
                    _ownedEntities.Add(kvp.Value);
                }
            }
            _ownedEntitiesDirty = false;
        }

        /// <summary>
        /// Call this when ownership changes to trigger cache rebuild.
        /// </summary>
        public void MarkOwnedEntitiesDirty()
        {
            _ownedEntitiesDirty = true;
        }

        /// <summary>
        /// The nodes this client owns. Backed by the same cache prediction uses, and refreshed on
        /// read when stale — callers outside the prediction loop (a <see cref="Bots.BotBehavior"/>,
        /// for one) can run before it has rebuilt for this tick and would otherwise see a list that
        /// is an ownership change behind.
        /// </summary>
        public IReadOnlyList<NetworkController> OwnedNodes
        {
            get
            {
                if (_ownedEntitiesDirty)
                    RebuildOwnedEntitiesCache();
                return _ownedEntities;
            }
        }

        /// <summary>
        /// Runs one prediction tick for all owned entities.
        /// Called from the independent client tick loop in _PhysicsProcess.
        /// </summary>
        /// <summary>
        /// Hard ceiling on how far prediction may run ahead of the confirmed tick — a
        /// last-resort backstop behind the adaptive slew below. Past
        /// SERVER_INPUT_BUFFER_SIZE (64) the server's input ring evicts stamped inputs
        /// before consuming them, so movement would run on frozen stale inputs.
        /// </summary>
        private const int MaxPredictionLeadTicks = 30;

        // ============================================================
        // ADAPTIVE PREDICTION LEAD (slew)
        // ============================================================
        // The lead (_clientPredictedTick - CurrentTick) is a ratchet without correction:
        // server stalls and clock-rate drift between machines only ever grow it, and a
        // stale lead never decays on its own — clients ended up pinned at the hard cap,
        // carrying (lead - RTT) ticks of pointless input latency and max-width resim
        // windows forever. The slew continuously steers the lead toward an RTT-derived
        // target by occasionally skipping one prediction tick (sheds debt; the server
        // holds last-known input across the gap) or running one extra (builds the lead
        // at session start so inputs arrive before the server needs them).

        /// <summary>Extra ticks above raw RTT so jitter doesn't starve the server of inputs.</summary>
        private const int LEAD_JITTER_MARGIN_TICKS = 2;
        /// <summary>Dead zone around the target before the slew engages, to avoid oscillation.</summary>
        private const int LEAD_SLACK = 2;
        /// <summary>Slew at most one tick per this many eligible frames (~7.5 ticks/s at TPS 30).</summary>
        private const int SLEW_INTERVAL = 4;
        /// <summary>Minimum target lead even at zero measured RTT (loopback).</summary>
        private const int MIN_TARGET_LEAD = 2;

        /// <summary>Counts eligible prediction frames (divider hits), phasing the slew.</summary>
        private ulong _eligibleFrameIndex = 0;

        /// <summary>
        /// How many ticks of prediction lead this client should hold: enough for an input
        /// stamped now to cross the wire before the server's timeline reaches its tick,
        /// plus a jitter margin. Clamped safely inside the hard cap.
        /// </summary>
        internal static int ComputeTargetLeadTicks(uint rttMs, int tps)
        {
            int rttTicks = (int)Math.Ceiling(rttMs / 1000.0 * tps);
            return Math.Clamp(rttTicks + LEAD_JITTER_MARGIN_TICKS, MIN_TARGET_LEAD, MaxPredictionLeadTicks - 2);
        }

        /// <summary>
        /// Round-trip time as the APPLICATION experiences it: what the transport measured, plus any
        /// delay the synthetic impairment is holding packets for.
        ///
        /// <para>WITHOUT THIS THE IMPAIRMENT SIMULATES A LINK THAT CANNOT EXIST. ENet measures RTT
        /// inside the native transport, and <see cref="Diagnostics.NetworkImpairment"/> holds packets
        /// AFTER ENet has delivered them -- so on loopback the peer still reports ~0ms however severe
        /// the configured latency is. The lead is derived from that reading, so an impaired client
        /// aimed for the loopback minimum of two ticks while its view of the server ran 80ms (2.4
        /// ticks) behind: it stamped every input for a tick the server had ALREADY simulated, and the
        /// server fell back to the previous tick's input (see GetServerBufferedInput) essentially
        /// forever. Held keys survive that unharmed -- the repeated input is the same input -- but
        /// every TRANSITION lands a tick late on the server, and a mistimed turn is a permanent
        /// heading disagreement, because one tick of turning is a smaller change than the forward
        /// direction's prediction tolerance and is therefore never reconciled.</para>
        ///
        /// <para>Only the configured latency is added, not the jitter: LEAD_JITTER_MARGIN_TICKS
        /// already exists to cover variance, and paying for the worst jitter on every tick is the
        /// same bad trade the interpolation buffer avoids.</para>
        /// </summary>
        internal static uint EffectiveRoundTripMs(uint measuredRttMs, int simulatedLatencyMs)
            => measuredRttMs + (uint)Math.Max(0, simulatedLatencyMs);

        /// <summary>
        /// Slew decision for one eligible frame: 1 is steady state; 0 sheds one tick of
        /// excess lead; 2 builds one tick of missing lead. Correction is rate-limited to
        /// one tick per SLEW_INTERVAL eligible frames so a 25-tick debt drains in a few
        /// seconds without perceptible stutter.
        /// </summary>
        internal static int PredictionTicksThisFrame(int lead, int targetLead, ulong eligibleFrameIndex)
        {
            if (eligibleFrameIndex % SLEW_INTERVAL != 0)
            {
                return 1;
            }
            if (lead > targetLead + LEAD_SLACK)
            {
                return 0;
            }
            if (lead < targetLead - LEAD_SLACK)
            {
                return 2;
            }
            return 1;
        }

        private int _predictionThrottleLogCounter = 0;

        /// <summary>Wall-clock msec of the last confirmed-stall warning (rate limit).</summary>
        private ulong _lastStallLogMsec = 0;

        private void RunClientPredictionTick()
        {
            if (_clientPredictedTick - CurrentTick >= MaxPredictionLeadTicks)
            {
                // With the slew active, hitting the cap means correction is losing the
                // race against lead growth — the server is losing time faster than the
                // client can shed it. Warn on first engage, then every ~10s while pinned.
                if ((_predictionThrottleLogCounter++ % 300) == 0)
                {
                    uint rtt = EffectiveRoundTripMs(
                        NetRunner.Instance.ServerPeer.IsSet ? NetRunner.Instance.ServerPeer.RoundTripTime : 0,
                        NetRunner.Impairment.LatencyMs);
                    Log(Debugger.DebugLevel.WARN,
                        $"[Prediction] Lead capped: predicted {_clientPredictedTick} is {_clientPredictedTick - CurrentTick} ahead of confirmed {CurrentTick} " +
                        $"(rtt {rtt}ms, target lead {ComputeTargetLeadTicks(rtt, NetRunner.TPS)}) — confirmed timeline is falling behind faster than the slew can shed lead");
                }
                return;
            }

            if (_ownedEntitiesDirty)
            {
                RebuildOwnedEntitiesCache();
            }

            _clientPredictedTick++;

            for (int i = 0; i < _ownedEntities.Count; i++)
            {
                var netController = _ownedEntities[i];
                if (netController == null || netController.IsMarkedForDeletion) continue;

                // Restore latest client input before prediction — reconciliation's
                // SetInputBytes may have overwritten _inputData with stale buffered input
                RestoreClientInputsForEntity(netController);

                netController.IsPredicting = true;
                netController._NetworkProcess(_clientPredictedTick);
                netController.StorePredictedState(_clientPredictedTick);
                netController.IsPredicting = false;
                SendInput(netController);

                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                    if (!staticChild.IsCurrentOwner) continue;

                    staticChild.IsPredicting = true;
                    staticChild._NetworkProcess(_clientPredictedTick);
                    staticChild.StorePredictedState(_clientPredictedTick);
                    staticChild.IsPredicting = false;
                    SendInput(staticChild);
                }
            }
        }

        /// <summary>
        /// Reconciles a single owned entity: compares predicted state with server state,
        /// performs rollback if needed, and resimulates.
        /// </summary>
        private void ReconcileOwnedEntity(NetworkController netController, Tick incomingTick)
        {
            // Store confirmed state from server
            netController.StoreConfirmedState(incomingTick);

            foreach (var staticChild in netController.StaticNetworkChildren)
            {
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                staticChild.StoreConfirmedState(incomingTick);
            }

            // If incoming tick is beyond what we've predicted, we can't compare - force restore all
            bool canCompare = incomingTick <= _clientPredictedTick;
            bool forceRestoreAll = !canCompare;

            // Reconcile compares predicted vs confirmed and restores mispredicted properties
            // Returns true if any misprediction occurred (or if forceRestoreAll is set)
            bool parentMispredicted = netController.Reconcile(incomingTick, forceRestoreAll);
            bool anyChildMispredicted = false;

            var children = netController.StaticNetworkChildren;
            Span<bool> childMispredicted = stackalloc bool[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                var staticChild = children[i];
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                
                childMispredicted[i] = staticChild.Reconcile(incomingTick, forceRestoreAll);
                if (childMispredicted[i])
                {
                    anyChildMispredicted = true;
                }
            }

            if (parentMispredicted || anyChildMispredicted)
            {
                // Restore non-mispredicted nodes to incomingTick so resimulation
                // starts from a temporally consistent baseline
                if (!parentMispredicted)
                {
                    netController.RestoreToPredictedState(incomingTick);
                }
                for (int i = 0; i < children.Length; i++)
                {
                    var staticChild = children[i];
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                    if (!staticChild.IsCurrentOwner) continue;
                    if (!childMispredicted[i])
                    {
                        staticChild.RestoreToPredictedState(incomingTick);
                    }
                }

                // Misprediction detected - resimulate
                netController.IsResimulating = true;
                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                    if (!staticChild.IsCurrentOwner) continue;
                    staticChild.IsResimulating = true;
                }

                if (_clientPredictedTick < incomingTick)
                {
                    _clientPredictedTick = incomingTick;
                }

                // Resimulate from confirmed tick to predicted tick
                for (var resimTick = incomingTick + 1; resimTick <= _clientPredictedTick; resimTick++)
                {
                    ApplyClientBufferedInputsForEntity(netController, resimTick);
                    SimulateAndStoreOwnedEntity(netController, resimTick);
                }

                netController.IsResimulating = false;
                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                    if (!staticChild.IsCurrentOwner) continue;
                    staticChild.IsResimulating = false;
                }
            }
            else
            {
                // Prediction correct - no action needed.
                // The entity's current state is already at the latest predicted tick,
                // and import didn't modify predicted properties for owned entities.
                // Do NOT call RestoreToPredictedState(incomingTick) here - that would
                // reset the entity to an old tick's state, causing visual jumps.
            }
        }

        /// <summary>
        /// Restores the latest client input (from SetInput) for an entity and its owned static
        /// children, undoing any SetInputBytes overwrites that reconciliation may have applied.
        /// Call this once per prediction tick, before running _NetworkProcess.
        /// </summary>
        private void RestoreClientInputsForEntity(NetworkController netController)
        {
            netController.RestorePendingClientInput();
            foreach (var staticChild in netController.StaticNetworkChildren)
            {
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                staticChild.RestorePendingClientInput();
            }
        }

        /// <summary>
        /// Applies client-side buffered inputs for an entity and its owned static children.
        /// Used during resimulation to replay the recorded inputs for a given tick.
        /// </summary>
        private void ApplyClientBufferedInputsForEntity(NetworkController netController, int tick)
        {
            var bufferedInput = netController.GetBufferedInput(tick);
            if (bufferedInput != null) netController.SetInputBytes(bufferedInput);

            foreach (var staticChild in netController.StaticNetworkChildren)
            {
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                var childInput = staticChild.GetBufferedInput(tick);
                if (childInput != null) staticChild.SetInputBytes(childInput);
            }
        }

        /// <summary>
        /// Simulates one tick for an owned entity and its owned static children (root first, then
        /// children), and stores the predicted state for each. The caller is responsible for setting
        /// IsResimulating or IsPredicting flags before calling this method.
        /// </summary>
        private void SimulateAndStoreOwnedEntity(NetworkController netController, int tick)
        {
            netController._NetworkProcess(tick);
            netController.StorePredictedState(tick);

            foreach (var staticChild in netController.StaticNetworkChildren)
            {
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                staticChild._NetworkProcess(tick);
                staticChild.StorePredictedState(tick);
            }
        }

        #endregion

        public NetworkController GetNodeFromNetId(NetId networkId)
        {
            if (networkId.IsNone || !networkId.IsValid)
                return null;
            
            // First check NetScenes
            if (NetScenes.TryGetValue(networkId, out var controller))
                return controller;
            
            // If not found and we're processing, check pending adds
            // This handles the case where a node is spawned during _NetworkProcess
            // and tries to look up its parent before FlushPendingNetSceneChanges runs
            if (_isProcessingNetScenes)
            {
                foreach (var pending in _pendingNetSceneAdds)
                {
                    if (pending.Id == networkId)
                        return pending.Controller;
                }
            }
            
            return null;
        }

        public NetworkController GetNodeFromNetId(long networkId)
        {
            if (networkId == NetId.NONE)
                return null;
            // Fix #7: Use TryGetValue
            if (!networkIds.TryGetValue(networkId, out var netId))
                return null;
            
            // Use the main overload which handles pending adds
            return GetNodeFromNetId(netId);
        }

        public NetId AllocateNetId()
        {
            var networkId = new NetId(networkIdCounter);
            networkIds[networkIdCounter] = networkId;
            networkIdCounter++;
            return networkId;
        }

        public NetId AllocateNetId(ushort id)
        {
            var networkId = new NetId(id);
            networkIds[id] = networkId;
            return networkId;
        }

        public NetId GetNetId(long id)
        {
            // Fix #7: Use TryGetValue
            return networkIds.TryGetValue(id, out var netId) ? netId : NetId.None;
        }

        public NetId GetNetIdFromPeerId(NetPeer peer, ushort id)
        {
            // Fix #7: Use TryGetValue
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
                return NetId.None;
            return peerState.PeerToWorldNodeMap.TryGetValue(id, out var netId) ? netId : NetId.None;
        }

        /// <summary>
        /// Per-world load instrumentation. Null unless the process was launched with --metrics;
        /// created on this world's first server tick.
        /// </summary>
        private Diagnostics.ServerMetrics _metrics;

        /// <summary>
        /// Per-phase tick timing. Null unless <see cref="Diagnostics.TickProfiler.EnableEnvVar"/>
        /// is set, so every <c>_profiler?.Record(...)</c> below costs a null check when off.
        /// </summary>
        private Diagnostics.TickProfiler _profiler;

        /// <summary>
        /// Peer count and round-trip spread, for the metrics line. Only called on the tick that
        /// actually emits, so the scan is once per interval rather than once per tick.
        /// </summary>
        private void CollectPeerRtt(out int peers, out double rttMean, out uint rttMax)
        {
            peers = 0;
            rttMax = 0;
            ulong rttSum = 0;
            foreach (var peerState in PeerStates.Values)
            {
                if (peerState.Status == PeerSyncStatus.DISCONNECTED)
                    continue;
                peers++;
                uint rtt = peerState.Peer.IsSet ? peerState.Peer.RoundTripTime : 0;
                rttSum += rtt;
                if (rtt > rttMax) rttMax = rtt;
            }
            rttMean = peers > 0 ? rttSum / (double)peers : 0;
        }

        /// <summary>
        /// Invoked after each network tick completes.
        /// </summary>
        public event Action<Tick> OnAfterNetworkTick;

        /// <summary>
        /// Client counterpart to <see cref="OnAfterNetworkTick"/>: raised once per network tick,
        /// immediately before the prediction pass. That placement is the point of it — it is where
        /// a human's input has just been sampled, so a scripted client acting here produces input
        /// indistinguishable from a played one.
        /// </summary>
        public event Action<Tick> OnClientNetworkTick;

        /// <summary>
        /// Invoked when a player joins the world (sync status becomes IN_WORLD).
        /// </summary>
        public event Action<UUID, UUID> OnPlayerJoined;
        public event Action<UUID, UUID> OnPlayerCleanup;


        /// <summary>
        /// When a player disconnects, we automatically dispose of their data in the World. If you wish to manually handle this,
        /// (e.g. you wish to save their data first), then set this to false, and call <see cref="CleanupPlayer"/> when you are ready to dispose of their data yourself.
        /// <see cref="CleanupPlayer"/> is all that is needed to fully dispose of their data on the server, including freeing their owned nodes (when <see cref="NetworkController.DespawnOnUnowned"/> is true).
        /// </summary>
        public bool AutoPlayerCleanup = true;

        /// <summary>
        /// Immediately disconnects the player from the world and frees all of their data from the server, including freeing their owned nodes (when <see cref="NetworkController.DespawnOnUnowned"/> is true).
        /// Safe to call multiple times - will return early if peer was already cleaned up.
        /// </summary>
        /// <param name="peer"></param>
        public void CleanupPlayer(NetPeer peer)
        {
            if (!NetRunner.Instance.IsServer) return;

            var peerId = NetRunner.Instance.GetPeerId(peer);

            // Already cleaned up (e.g. by ack timeout, then ENet disconnect event fires)
            if (!PeerStates.ContainsKey(peerId)) return;

            if (peer.State == ENet.PeerState.Connected)
            {
                NetRunner.DisconnectPeer(peer, 0);
            }

            // forgetIdentity: true — the peer is leaving for good, so also drop its global
            // ENet identity (Peers/PeerIds) alongside its per-world state.
            // despawnOwnedNodes: false — preserve DespawnOnUnowned semantics on disconnect
            // (nodes flagged to persist stay in the world, unowned).
            TeardownPeer(peer, peerId, forgetIdentity: true, despawnOwnedNodes: false);
        }

        /// <summary>
        /// Removes a peer from THIS world without disconnecting the ENet connection or forgetting the
        /// peer's global identity. Used for live cross-world migration (see <see cref="NetRunner.MigratePeerToWorld"/>):
        /// frees the peer's owned nodes here and cleans per-peer state, but keeps the connection alive so the
        /// peer can immediately <see cref="JoinPeer"/> into the destination world over the same socket.
        /// The hub world itself keeps running for other/returning players.
        /// </summary>
        public void PreparePeerDeparture(NetPeer peer)
        {
            if (!NetRunner.Instance.IsServer) return;

            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.ContainsKey(peerId)) return;

            // forgetIdentity: false — keep Peers/PeerIds so the same connection migrates worlds.
            // despawnOwnedNodes: true — the peer is leaving THIS world entirely and re-spawns fresh in
            // the destination, so its owned nodes (the Player and its subtree) must be removed from this
            // world's tree regardless of DespawnOnUnowned (which defaults false).
            TeardownPeer(peer, peerId, forgetIdentity: false, despawnOwnedNodes: true);
        }

        /// <summary>
        /// Shared teardown of a peer's presence in this world: frees owned nodes, clears per-peer
        /// serializer/controller caches, reconciles pending despawns, and removes per-world routing.
        /// <paramref name="forgetIdentity"/> additionally drops the peer's global ENet identity
        /// (used by full disconnect, NOT by migration). <paramref name="despawnOwnedNodes"/> forces the
        /// peer's owned nodes to despawn even when their DespawnOnUnowned is false (used by migration).
        /// </summary>
        private void TeardownPeer(NetPeer peer, UUID peerId, bool forgetIdentity, bool despawnOwnedNodes)
        {
            // Deliberately NOT asserted main-thread: the ack-timeout sweep calls this from inside
            // ServerProcessTick. The shared-registry mutations at the end are deferred instead.
            var peerState = PeerStates[peerId];
            foreach (var netController in peerState.OwnedNodes)
            {
                if (despawnOwnedNodes || netController.DespawnOnUnowned)
                {
                    netController.QueueNodeForDeletion();
                }
                else
                {
                    netController.SetInputAuthority(default);
                }
            }

            // Clean up per-peer cached data from all network controllers and serializers to prevent memory leaks
            foreach (var netController in NetScenes.Values)
            {
                if (netController == null) continue;

                // Clean up NetworkController's per-peer state
                netController.CleanupPeerState(peerId);

                // Clean up serializers' per-peer state
                if (netController.NetNode?.Serializers != null)
                {
                    foreach (var serializer in netController.NetNode.Serializers)
                    {
                        serializer.CleanupPeer(peerId);
                    }
                }
            }

            // Treat any pending despawns as acknowledged for the departing peer.
            // Check if any nodes queued for despawn can now be deleted
            foreach (var netController in QueueDespawnedNodes)
            {
                // The peer's SpawnState entry will be removed with PeerStates below
                // Check if all REMAINING peers have despawned (after this peer is removed)
                bool allRemainingDespawned = true;
                foreach (var otherPeerState in PeerStates.Values)
                {
                    if (otherPeerState.Id == peerId) continue; // Skip the departing peer
                    var state = GetClientSpawnState(netController.NetId, otherPeerState.Peer);
                    if (state != ClientSpawnState.Despawned && state != ClientSpawnState.NotSpawned)
                    {
                        allRemainingDespawned = false;
                        break;
                    }
                }

                if (allRemainingDespawned)
                {
                    _pendingDeletion.Add(netController);
                }
            }

            PeerStates.Remove(peerId);
            _peerLastAckTick.Remove(peerId);
            _peerSentRings.Remove(peerId); // Per-tick ack routing
            _peerNetBufferPool.Remove(peerId); // Clean up pooled export buffer
            _peerPropsCursors.Remove(peerId); // Round-robin cursor for the props phase
            _peerListDirty = true; // Fix #1: Mark peer list as dirty

            // Everything above is this world's own state, so it belongs on whichever thread is
            // running this world. NetRunner's registries are not: the ENet pump reads them every
            // frame on the main thread, and the ack-timeout sweep reaches here from inside
            // ServerProcessTick. RunOnMainThread executes inline when already on the main thread,
            // so with per-world thread groups off this is exactly the previous behavior.
            //
            // Deferring by a frame is harmless: a stale PeerWorldMap entry only means the pump
            // routes one more frame of this peer's packets into this world, where they queue and
            // then find no PeerState.
            var nativePeerId = peer.ID;
            NetRunner.Instance.RunOnMainThread(() =>
            {
                NetRunner.Instance.PeerWorldMap.Remove(peerId);
                if (forgetIdentity)
                {
                    NetRunner.Instance.Peers.Remove(peerId);
                    NetRunner.Instance.PeerIds.Remove(nativePeerId);
                }
            });

            OnPlayerCleanup?.Invoke(WorldId, peerId);
        }

        private int _frameCounter = 0;
        private int _clientFrameCounter = 0;
        
        /// <summary>
        /// This method is executed every tick on the Server side, and kicks off all logic which processes and sends data to every client.
        /// </summary>
        public void ServerProcessTick()
        {
            // Bind the profiler to this tick's thread so library code reached from here (the
            // serializers, NetBuffer) can report into it without threading a parameter through
            // every signature.
            _profiler?.MakeCurrent();

            // Process buffered player joins FIRST (tick-aligned)
            // This ensures OnPlayerJoined fires at a safe, predictable point before any Export iteration
            var phaseTs = Diagnostics.TickProfiler.Now();
            // Timed unconditionally as well as through the profiler: the profiler averages over a
            // second, which is exactly how a single multi-hundred-millisecond join tick disappears.
            var joinsTs = System.Diagnostics.Stopwatch.GetTimestamp();
            ProcessPendingPlayerJoins();
            var joinsMs = (System.Diagnostics.Stopwatch.GetTimestamp() - joinsTs)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (joinsMs >= Diagnostics.MainThreadWork.ReportThresholdMs)
            {
                Log(Debugger.DebugLevel.WARN,
                    $"[JoinsPhase] ProcessPendingPlayerJoins took {joinsMs:F0} ms INSIDE the tick.");
            }
            _profiler?.Record(Diagnostics.TickProfiler.Phase.Joins, phaseTs);

            phaseTs = Diagnostics.TickProfiler.Now();

            // Check for peers that have timed out (no acks for too long)
            int ackTimeoutTicks = (int)(NetRunner.AckTimeoutSeconds * NetRunner.TPS);
            int joinAckTimeoutTicks = (int)(NetRunner.JoinAckTimeoutSeconds * NetRunner.TPS);
            _peersToDisconnect.Clear();

            foreach (var peerId in PeerStates.Keys)
            {
                var peerState = PeerStates[peerId];
                if (peerState.Status == PeerSyncStatus.DISCONNECTED)
                    continue;

                // Initialize tracking for new peers
                if (!_peerLastAckTick.ContainsKey(peerId))
                {
                    _peerLastAckTick[peerId] = CurrentTick;
                    continue;
                }

                // The lenient window applies for the first JoinAckTimeoutSeconds after the
                // peer JOINED, not merely while it has never acked: a client acks a few
                // small early ticks before the world-scene change arrives, and only then
                // goes silent for seconds in scene load + spatial mirror build. Status
                // (INITIAL flips to IN_WORLD on the first ack) therefore cannot mark the
                // end of joining - only elapsed time since join can.
                bool inJoinWindow = CurrentTick - peerState.JoinedAtTick <= joinAckTimeoutTicks;
                int timeoutTicks = inJoinWindow ? joinAckTimeoutTicks : ackTimeoutTicks;

                var ticksSinceLastAck = CurrentTick - _peerLastAckTick[peerId];
                if (ticksSinceLastAck > timeoutTicks)
                {
                    Log(Debugger.DebugLevel.WARN, $"[ACK TIMEOUT] Peer {peerId} ({(inJoinWindow ? "joining" : "in world")}) has not acknowledged for {ticksSinceLastAck} ticks ({ticksSinceLastAck / (float)NetRunner.TPS:F1}s, limit {timeoutTicks / (float)NetRunner.TPS:F1}s). Force disconnecting.");
                    _metrics?.RecordAckTimeout();
                    _peersToDisconnect.Add(peerState.Peer);
                }
            }

            foreach (var peer in _peersToDisconnect)
            {
                CleanupPlayer(peer);
            }
            _profiler?.Record(Diagnostics.TickProfiler.Phase.AckSweep, phaseTs);

            phaseTs = Diagnostics.TickProfiler.Now();
            _netIdsToRemove.Clear();
            _isProcessingNetScenes = true;
            foreach (var net_id in NetScenes.Keys)
            {
                if (!NetScenes.TryGetValue(net_id, out var netController) || netController == null)
                    continue;

                // Use cached flag to avoid Godot method call allocation
                if (!IsInstanceValid(netController.RawNode) || netController.IsMarkedForDeletion)
                {
                    _netIdsToRemove.Add(net_id);
                    continue;
                }
                if (netController.RawNode.ProcessMode == ProcessModeEnum.Disabled)
                {
                    continue;
                }

                // Auto-despawn nodes that no connected peer is interested in anymore.
                // Guarded by HadInterestedPeer so a freshly-spawned node isn't despawned before
                // the granting code (e.g. AddInterestPeer on zone-enter) has had a chance to run.
                if (netController.DespawnOnNoInterestPeers && !netController.IsQueuedForDespawn)
                {
                    bool anyInterested = false;
                    foreach (var peerState in PeerStates.Values)
                    {
                        if (peerState.Status == PeerSyncStatus.DISCONNECTED) continue;
                        if (netController.IsPeerInterested(peerState.Peer))
                        {
                            anyInterested = true;
                            break;
                        }
                    }
                    if (anyInterested)
                    {
                        netController.HadInterestedPeer = true;
                    }
                    else if (netController.HadInterestedPeer)
                    {
                        QueueDespawn(netController);
                    }
                }

                // Phase 1: Apply all buffered inputs (root first, then children — must match simulation order)
                if (netController.HasInputSupport)
                {
                    var rootInput = GetServerBufferedInput(new InputBufferKey(netController.NetId), CurrentTick);
                    if (rootInput != null) netController.SetInputBytes(rootInput);
                }
                foreach (var networkChild in netController.StaticNetworkChildren)
                {
                    if (networkChild == null) continue;
                    if (networkChild.RawNode == null)
                    {
                        Log(Debugger.DebugLevel.ERROR, $"Network child node is unexpectedly null: {netController.RawNode.SceneFilePath}");
                        continue;
                    }
                    if (networkChild.RawNode.ProcessMode == ProcessModeEnum.Disabled) continue;
                    if (!networkChild.HasInputSupport) continue;
                    var bufferedInput = GetServerBufferedInput(new InputBufferKey(netController.NetId, networkChild.StaticChildId), CurrentTick);
                    if (bufferedInput != null) networkChild.SetInputBytes(bufferedInput);
                }

                // Phase 2: Simulate (root first, then children — must match client prediction/resim order)
                // Timed separately from the scan around it: this is game code, and telling it apart
                // from Nebula's own per-node bookkeeping is the whole point of the breakdown.
                var gameplayTs = Diagnostics.TickProfiler.Now();
                var censusTs = Diagnostics.PayloadCensus.Enabled
                    ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                netController._NetworkProcess(CurrentTick);
                foreach (var networkChild in netController.StaticNetworkChildren)
                {
                    if (networkChild == null) continue;
                    if (networkChild.RawNode == null) continue;
                    if (networkChild.RawNode.ProcessMode == ProcessModeEnum.Disabled) continue;
                    networkChild._NetworkProcess(CurrentTick);
                }
                if (censusTs != 0L)
                {
                    // Charged to the ROOT scene including its static children, which is
                    // the unit a reader can actually go and open.
                    Diagnostics.PayloadCensus.RecordGameplay(
                        netController.RawNode?.SceneFilePath,
                        System.Diagnostics.Stopwatch.GetTimestamp() - censusTs);
                }
                _profiler?.Record(Diagnostics.TickProfiler.Phase.Gameplay, gameplayTs);
            }
            _isProcessingNetScenes = false;
            FlushPendingNetSceneChanges();
            _profiler?.Record(Diagnostics.TickProfiler.Phase.SceneScan, phaseTs);

            var debugHub = Hub;
            // DebugFramesEnabled distinguishes a metrics-only channel: the socket is up
            // (metrics still flow) but none of this per-tick debug work should run.
            bool debugAttached = debugHub is { HasClients: true, DebugFramesEnabled: true };

            if (debugAttached)
            {
                // Notify the Debugger of the incoming tick. Reliable: the editor
                // keys every other frame off the tick that opened it.
                using var debugBuffer = new NetBuffer(16, usePool: true);
                NetWriter.WriteInt64(debugBuffer, DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond);
                NetWriter.WriteInt32(debugBuffer, CurrentTick);
                debugHub.Enqueue(WorldId, DebugDataType.TICK, debugBuffer, lossy: false);

                EmitDebugWorldState(debugHub);
                EmitDebugPeers(debugHub);
            }

            phaseTs = Diagnostics.TickProfiler.Now();
            foreach (var queuedFunction in queuedNetFunctions)
            {
                var functionNode = queuedFunction.Node.GetNode(queuedFunction.FunctionInfo.NodePath) as INetNodeBase;
                NetFunctionContext = new NetFunctionCtx
                {
                    Caller = queuedFunction.Sender,
                };
                functionNode.Network.IsInboundCall = true;
                // Use source-generated dispatch - no Variant conversion, no Godot boundary crossing
                var rawNode = functionNode.Network.RawNode;
                if (rawNode is NetNode3D n3d)
                    n3d.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                else if (rawNode is NetNode2D n2d)
                    n2d.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                else if (rawNode is NetNode n)
                    n.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                functionNode.Network.IsInboundCall = false;
                NetFunctionContext = new NetFunctionCtx { };

                if (debugAttached)
                {
                    // Notify the Debugger of the function call
                    using var debugBuffer = new NetBuffer(NetRunner.MTU + 64, usePool: true);
                    NetWriter.WriteString(debugBuffer, queuedFunction.FunctionInfo.Name);
                    NetWriter.WriteByte(debugBuffer, (byte)queuedFunction.Args.Length);
                    for (int i = 0; i < queuedFunction.Args.Length; i++)
                    {
                        var cache = queuedFunction.Args[i];
                        NetWriter.WriteByte(debugBuffer, (byte)cache.Type);
                        WriteFromPropertyCache(debugBuffer, queuedFunction.FunctionInfo.Arguments[i], ref cache);
                    }
                    debugHub.Enqueue(WorldId, DebugDataType.CALLS, debugBuffer, lossy: false);
                }
            }
            queuedNetFunctions.Clear();

            if (debugAttached)
            {
                foreach (var log in tickLogBuffer)
                {
                    using var logBuffer = new NetBuffer(log.Message.Length * 4 + 32, usePool: true);
                    NetWriter.WriteByte(logBuffer, (byte)log.Level);
                    NetWriter.WriteString(logBuffer, log.Message);
                    debugHub.Enqueue(WorldId, DebugDataType.LOGS, logBuffer, lossy: false);
                }
            }
            tickLogBuffer.Clear();
            _profiler?.Record(Diagnostics.TickProfiler.Phase.NetFunctions, phaseTs);

            // If nobody is connected, skip ExportState entirely to avoid per-tick allocations.
            // Under SustainedLowLatency GC, these allocations can look like a leak in snapshots.
            if (PeerStates.Count > 0)
            {
                // Fix #1: Use cached peer list instead of ToList() allocation every tick
                if (_peerListDirty)
                {
                    _cachedPeerList.Clear();
                    foreach (var peerState in PeerStates.Values)
                    {
                        _cachedPeerList.Add(peerState.Peer);
                    }
                    _peerListDirty = false;
                }
                phaseTs = Diagnostics.TickProfiler.Now();
                var exportedState = ExportState(_cachedPeerList);
                _profiler?.Record(Diagnostics.TickProfiler.Phase.Export, phaseTs);

                phaseTs = Diagnostics.TickProfiler.Now();
                try
                {
                    foreach (var peer in _cachedPeerList)
                    {
                        var peerId = NetRunner.Instance.GetPeerId(peer);
                        // Fix #7: Use TryGetValue instead of indexer
                        if (!PeerStates.TryGetValue(peerId, out var peerState) || peerState.Status == PeerSyncStatus.DISCONNECTED)
                        {
                            continue;
                        }
                        if (!exportedState.TryGetValue(peerId, out var peerStateBuffer) || peerStateBuffer == null)
                        {
                            continue;
                        }

                        var payload = peerStateBuffer.WrittenSpan;

                        // Tick packet: [tick:int32][bit-packed payload]. The payload is already
                        // as dense as the serializers make it; there is no per-packet
                        // compression layer.
                        var buffer = _sendPacketBuffer ??= new NetBuffer();
                        buffer.Reset();
                        NetWriter.WriteInt32(buffer, CurrentTick);
                        NetWriter.WriteBytes(buffer, payload);

                        if (buffer.Length > NetRunner.MTU)
                        {
                            Log(Debugger.DebugLevel.ERROR, $"[MTU EXCEEDED] Peer {peer.ID} tick {CurrentTick}: packet {buffer.Length} exceeds MTU {NetRunner.MTU} - PACKET MAY BE CORRUPTED!");
                            _metrics?.RecordMtuExceeded();
                        }

                        _metrics?.RecordPacket(buffer.Length);
                        var transmitTs = Diagnostics.TickProfiler.Now();
                        NetRunner.SendUnreliableSequenced(peer, (byte)NetRunner.ENetChannelId.Tick, buffer);
                        _profiler?.Record(Diagnostics.TickProfiler.Phase.Transmit, transmitTs);

                        if (debugAttached)
                        {
                            // Sized from the payload, not NetBuffer's 1536-byte
                            // default: that default only cleared the old header
                            // by ~119 bytes at the stock MTU, so any project
                            // raising Nebula/config/mtu made this throw.
                            using var debugBuffer = new NetBuffer(16 + 4 + peerStateBuffer.Length + 16, usePool: true);
                            WriteIdBytes(debugBuffer, peerState.Id);
                            // The size actually put on the wire for this peer, which is
                            // what the debugger charts against the MTU. The state bytes
                            // that follow are the pre-pack payload kept for inspection,
                            // so their length is NOT the transmitted size.
                            NetWriter.WriteInt32(debugBuffer, buffer.Length);
                            NetWriter.WriteBytes(debugBuffer, peerStateBuffer.WrittenSpan);
                            debugHub.Enqueue(WorldId, DebugDataType.PAYLOADS, debugBuffer, lossy: true);
                        }
                    }
                }
                finally
                {
                    // ExportState() now returns truly pooled NetBuffer instances that are reused between ticks.
                    // Do NOT dispose them - they will be Reset() and reused on the next tick.
                }
                _profiler?.Record(Diagnostics.TickProfiler.Phase.Send, phaseTs);
            }

            phaseTs = Diagnostics.TickProfiler.Now();

            // Note: Despawns are now handled by SpawnSerializer through the tick channel.
            // QueueDespawnedNodes tells SpawnSerializer.Export to send despawn data.
            // The node is NOT deleted here - it stays in NetScenes so SpawnSerializer can continue exporting.
            // Once all peers have acknowledged the despawn, the node is moved to _pendingDeletion.
            
            // For peers that are NotSpawned (never received spawn), mark them as Despawned immediately
            foreach (var netController in QueueDespawnedNodes)
            {
                foreach (var peerState in PeerStates.Values)
                {
                    var state = GetClientSpawnState(netController.NetId, peerState.Peer);
                    if (state == ClientSpawnState.NotSpawned)
                    {
                        // Peer never received spawn, mark as despawned immediately
                        SetClientSpawnState(netController.NetId, peerState.Peer, ClientSpawnState.Despawned);
                    }
                }
                
                // Check if already all peers are despawned (e.g., no peers connected, or all were NotSpawned)
                if (AreAllPeersDespawned(netController.NetId))
                {
                    _pendingDeletion.Add(netController);
                }
            }
            // Note: We don't clear QueueDespawnedNodes here - SpawnSerializer checks IsQueuedForDespawn
            // The node stays in QueueDespawnedNodes until it's added to _pendingDeletion
            
            // Process nodes that all peers have acknowledged despawn for
            foreach (var netController in _pendingDeletion)
            {
                QueueDespawnedNodes.Remove(netController);
                netController.NetParentId = NetId.None;
                RemoveNetScene(netController.NetId);
                netController.QueueNodeForDeletion();
            }
            _pendingDeletion.Clear();
            _profiler?.Record(Diagnostics.TickProfiler.Phase.Despawn, phaseTs);
        }

        /// <summary>
        /// Converts a Godot Variant to a C# object for serialization.
        /// </summary>
        private static object VariantToObject(Variant value)
        {
            return value.VariantType switch
            {
                Variant.Type.Bool => (bool)value,
                Variant.Type.Int => (long)value,
                Variant.Type.Float => (float)value,
                Variant.Type.String => (string)value,
                Variant.Type.Vector2 => (Vector2)value,
                Variant.Type.Vector3 => (Vector3)value,
                Variant.Type.Quaternion => (Quaternion)value,
                Variant.Type.PackedByteArray => (byte[])value,
                Variant.Type.PackedInt32Array => (int[])value,
                Variant.Type.PackedInt64Array => (long[])value,
                _ => value.Obj
            };
        }

        /// <summary>
        /// Gets the SerialVariantType from a C# object's runtime type.
        /// </summary>
        private static SerialVariantType GetSerialTypeFromObject(object value)
        {
            return value switch
            {
                bool => SerialVariantType.Bool,
                long or int or short or byte => SerialVariantType.Int,
                float or double => SerialVariantType.Float,
                string => SerialVariantType.String,
                Vector2 => SerialVariantType.Vector2,
                Vector3 => SerialVariantType.Vector3,
                Quaternion => SerialVariantType.Quaternion,
                byte[] => SerialVariantType.PackedByteArray,
                int[] => SerialVariantType.PackedInt32Array,
                long[] => SerialVariantType.PackedInt64Array,
                _ => SerialVariantType.Object
            };
        }

        /// <summary>
        /// Writes a PropertyCache value to a buffer using the function argument metadata.
        /// </summary>
        private static void WriteFromPropertyCache(NetBuffer buffer, NetFunctionArgument argInfo, ref PropertyCache cache)
        {
            switch (cache.Type)
            {
                case SerialVariantType.Bool:
                    NetWriter.WriteBool(buffer, cache.BoolValue);
                    break;
                case SerialVariantType.Int:
                    switch (argInfo.Metadata.TypeIdentifier)
                    {
                        case "Byte":
                            NetWriter.WriteByte(buffer, cache.ByteValue);
                            break;
                        case "Short":
                            NetWriter.WriteInt16(buffer, (short)cache.IntValue);
                            break;
                        case "Int":
                        case "Enum":
                            NetWriter.WriteInt32(buffer, cache.IntValue);
                            break;
                        default:
                            NetWriter.WriteInt64(buffer, cache.LongValue);
                            break;
                    }
                    break;
                case SerialVariantType.Float:
                    NetWriter.WriteFloat(buffer, cache.FloatValue);
                    break;
                case SerialVariantType.String:
                    NetWriter.WriteString(buffer, cache.StringValue);
                    break;
                case SerialVariantType.Vector2:
                    NetWriter.WriteVector2(buffer, cache.Vec2Value);
                    break;
                case SerialVariantType.Vector3:
                    NetWriter.WriteVector3(buffer, cache.Vec3Value);
                    break;
                case SerialVariantType.Quaternion:
                    NetWriter.WriteQuaternion(buffer, cache.QuatValue);
                    break;
                case SerialVariantType.PackedByteArray:
                    NetWriter.WriteBytesWithLength(buffer, (byte[])cache.RefValue);
                    break;
                case SerialVariantType.PackedInt32Array:
                    NetWriter.WriteInt32Array(buffer, (int[])cache.RefValue);
                    break;
                case SerialVariantType.PackedInt64Array:
                    NetWriter.WriteInt64Array(buffer, (long[])cache.RefValue);
                    break;
            }
        }

        internal HashSet<NetworkController> QueueDespawnedNodes = [];
        internal void QueueDespawn(NetworkController node)
        {
            QueueDespawnedNodes.Add(node);
        }
        
        /// <summary>
        /// Nodes that have been despawned by all peers and are ready for deletion.
        /// </summary>
        internal HashSet<NetworkController> _pendingDeletion = [];
        
        /// <summary>
        /// Client-side: NetIds that received despawn before spawn (due to packet loss).
        /// When a spawn arrives for a NetId in this set, it should be immediately despawned.
        /// </summary>
        private HashSet<NetId> _pendingClientDespawns = new();
        
        /// <summary>
        /// Checks if all peers have acknowledged the despawn for a node.
        /// Returns true if all peers are in Despawned or NotSpawned state.
        /// </summary>
        internal bool AreAllPeersDespawned(NetId netId)
        {
            foreach (var peerState in PeerStates.Values)
            {
                var state = GetClientSpawnState(netId, peerState.Peer);
                if (state != ClientSpawnState.Despawned && state != ClientSpawnState.NotSpawned)
                    return false;
            }
            return true;
        }
        
        /// <summary>
        /// Adds a NetId to the pending client despawns set (called when despawn arrives before spawn).
        /// </summary>
        internal void AddPendingClientDespawn(NetId netId)
        {
            _pendingClientDespawns.Add(netId);
        }
        
        /// <summary>
        /// Checks if a NetId has a pending despawn and removes it from the set.
        /// Returns true if there was a pending despawn.
        /// </summary>
        internal bool CheckAndRemovePendingClientDespawn(NetId netId)
        {
            return _pendingClientDespawns.Remove(netId);
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (NetRunner.Instance.IsClient)
            {
                AccumulateRenderTime((float)delta);
                UpdateInterpolationDelay();

                // The delay is part of the TARGET, so resizing the buffer is slewed by the rate
                // correction rather than jumping render time. See GetRenderTick.
                var advanced = AdvanceRenderClock(
                    _renderClock, _renderClockError, _renderClockSampledTick,
                    CurrentTick, InterpolationDelayTicks, (float)delta);
                _renderClock = advanced.Tick;
                _renderClockError = advanced.Error;
                _renderClockSampledTick = advanced.SampledTick;

            }
        }

        /// <summary>An inbound packet routed to this world, awaiting the world's next physics frame.</summary>
        private readonly struct InboundPacket
        {
            public readonly NetPeer Peer;
            public readonly byte Channel;
            /// <summary>Rented from ArrayPool&lt;byte&gt;.Shared and OWNED by this queue from the
            /// moment of enqueue; returned to the pool on drop, after apply, or at teardown.</summary>
            public readonly byte[] Payload;
            /// <summary>Valid byte count -- rented arrays are oversized, only [0, Length) is packet data.</summary>
            public readonly int Length;

            /// <summary>
            /// Wall clock at which this may be applied. Zero for the normal path -- only synthetic
            /// impairment ever sets it, so an unimpaired build compares against zero and moves on.
            /// </summary>
            public readonly ulong ReleaseAtMsec;

            public InboundPacket(NetPeer peer, byte channel, byte[] payload, int length, ulong releaseAtMsec = 0)
            {
                Peer = peer;
                Channel = channel;
                Payload = payload;
                Length = length;
                ReleaseAtMsec = releaseAtMsec;
            }
        }

        /// <summary>
        /// Sized for the worst realistic frame: every peer's input plus an ack, with headroom.
        /// Overflow drops rather than grows -- an unbounded queue turns a burst into a memory
        /// problem, and this mirrors the pump's existing posture that one misbehaving sender must
        /// never stall everyone.
        /// </summary>
        private const int InboundQueueCapacity = 1024;

        private readonly InboundPacket[] _inboundPackets = new InboundPacket[InboundQueueCapacity];
        private int _inboundHead;
        private int _inboundTail;
        private int _inboundCount;
        private readonly object _inboundLock = new();
        private bool _loggedInboundOverflow;
        private bool _loggedTickThread;

        /// <summary>
        /// Hands a packet to this world from the ENet pump.
        ///
        /// The pump runs on the main thread while this world may be mid-tick on its own thread, so
        /// packets are queued here instead of being applied inline. Parsing deliberately happens at
        /// drain time, on the world's thread, so the pump stays a pure router.
        ///
        /// Fully allocation-free: <paramref name="payload"/> is RENTED from
        /// ArrayPool&lt;byte&gt;.Shared by the pump, and this call transfers ownership -- the queue
        /// returns it to the pool on drop, after apply, or at world teardown. Callers must not
        /// touch the array after this returns.
        /// </summary>
        internal void EnqueueInboundPacket(
            NetPeer peer, byte channel, byte[] payload, int length, ulong releaseAtMsec = 0)
        {
            lock (_inboundLock)
            {
                if (_inboundCount == InboundQueueCapacity)
                {
                    if (!_loggedInboundOverflow)
                    {
                        _loggedInboundOverflow = true;
                        Log(Debugger.DebugLevel.WARN,
                            $"Inbound packet queue for world {WorldId} overflowed at {InboundQueueCapacity}; dropping packets. Logged once.");
                    }
                    // Dropped, so ownership ends here: the rented payload goes back to the pool.
                    System.Buffers.ArrayPool<byte>.Shared.Return(payload);
                    return;
                }

                _inboundPackets[_inboundTail] = new InboundPacket(peer, channel, payload, length, releaseAtMsec);
                _inboundTail = (_inboundTail + 1) % InboundQueueCapacity;
                _inboundCount++;
            }
        }

        /// <summary>
        /// Applies everything queued by <see cref="EnqueueInboundPacket"/>, in arrival order.
        ///
        /// Bounded to the count present on entry: the pump can enqueue concurrently, and an
        /// unbounded loop would let a busy frame of inbound traffic hold this world's thread
        /// indefinitely.
        /// </summary>
        private void DrainInboundPackets()
        {
            int pending;
            lock (_inboundLock)
            {
                pending = _inboundCount;
            }

            ulong nowMsec = Time.GetTicksMsec();

            while (pending-- > 0)
            {
                InboundPacket packet;
                lock (_inboundLock)
                {
                    if (_inboundCount == 0) return;
                    packet = _inboundPackets[_inboundHead];

                    // Held back by synthetic impairment. Stop rather than skip: this is a queue, and
                    // releasing later packets around a held one would reorder every channel including
                    // the reliable ones, which is not what a delayed link does.
                    if (packet.ReleaseAtMsec > nowMsec) return;

                    // Release the payload reference so a quiet world doesn't pin up to
                    // InboundQueueCapacity packets until the slot is reused.
                    _inboundPackets[_inboundHead] = default;
                    _inboundHead = (_inboundHead + 1) % InboundQueueCapacity;
                    _inboundCount--;
                }

                ApplyInboundPacket(packet);

                // Every consumer copies what it keeps (NetReader.ReadBytes materializes copies;
                // acks are values), so the rented payload can go straight back to the pool.
                System.Buffers.ArrayPool<byte>.Shared.Return(packet.Payload);
            }
        }

        /// <summary>
        /// Reusable wrapper for parsing queued payloads in place. Re-pointed at each packet via
        /// <see cref="NetBuffer.Attach"/> -- one long-lived object instead of a NetBuffer
        /// allocation per packet. Only ever touched by this world's drain (single-threaded).
        /// </summary>
        private readonly NetBuffer _inboundParseBuffer = new(System.Array.Empty<byte>());

        private void ApplyInboundPacket(in InboundPacket packet)
        {
            // Per-packet catch, matching the pump's original contract: a malformed packet must never
            // abort the drain and take out every other peer's traffic this frame.
            try
            {
                _inboundParseBuffer.Attach(packet.Payload, packet.Length);
                switch ((NetRunner.ENetChannelId)packet.Channel)
                {
                    case NetRunner.ENetChannelId.Tick:
                        if (packet.Length == 0) break;
                        PeerAcknowledge(packet.Peer, NetReader.ReadInt32(_inboundParseBuffer));
                        break;

                    case NetRunner.ENetChannelId.Input:
                        ReceiveInput(packet.Peer, _inboundParseBuffer);
                        break;

                    case NetRunner.ENetChannelId.Function:
                        ReceiveNetFunction(packet.Peer, _inboundParseBuffer);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log(Debugger.DebugLevel.ERROR,
                    $"[Nebula][MalformedPacket] Failed to parse packet on channel {packet.Channel} from peer {packet.Peer.ID}: {ex.Message}");
                NetRunner.DisconnectPeer(packet.Peer, NetRunner.MalformedPacketDisconnectCode);
            }
        }

        /// <summary>
        /// Returns any still-queued rented payloads to the pool. Without this, packets that arrived
        /// after this world's final drain would leak their pool arrays when the world is freed.
        /// </summary>
        private void ReleaseInboundPackets()
        {
            lock (_inboundLock)
            {
                while (_inboundCount > 0)
                {
                    var packet = _inboundPackets[_inboundHead];
                    _inboundPackets[_inboundHead] = default;
                    _inboundHead = (_inboundHead + 1) % InboundQueueCapacity;
                    _inboundCount--;
                    if (packet.Payload != null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet.Payload);
                    }
                }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            base._PhysicsProcess(delta);

            // Debug clients are accepted process-wide by NetRunner._Process.

            if (NetRunner.Instance.IsServer)
            {
                if (!_loggedTickThread)
                {
                    // One line per world, on its first server frame, naming the thread it ticks on.
                    // Whether per-world thread groups actually took effect is otherwise invisible
                    // until something goes wrong, and "did the flag do anything?" is the first
                    // question worth being able to answer from a log.
                    _loggedTickThread = true;
                    Log(Debugger.DebugLevel.INFO,
                        $"World {WorldId} ticking on thread {System.Environment.CurrentManagedThreadId}"
                        + $" ({(NebulaThread.IsMain ? "main" : "worker")}).");
                }

                // Drained every physics frame, deliberately ahead of the network-tick gate below.
                // The ENet pump used to apply acks and inputs inline as it read them, so they landed
                // on the frame they arrived; draining only on tick frames would add up to
                // PhysicsTicksPerNetworkTick-1 frames of latency to every one of them.
                var inboundTs = Diagnostics.TickProfiler.Now();
                DrainInboundPackets();
                _profiler?.Record(Diagnostics.TickProfiler.Phase.Inbound, inboundTs);

                _frameCounter += 1;
                if (_frameCounter < NetRunner.PhysicsTicksPerNetworkTick)
                    return;
                _frameCounter = 0;
                CurrentTick += 1;
#if DEBUG
                // Simple benchmark: measure ServerProcessTick execution time
                // var stopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
                // Created before the tick, not after: ServerProcessTick binds it to this thread so
                // library code can report into it, which the first tick would otherwise miss.
                if (Diagnostics.TickProfiler.Enabled) _profiler ??= new Diagnostics.TickProfiler();

                // Avoid allocating a Stopwatch object every tick.
                long startTs = System.Diagnostics.Stopwatch.GetTimestamp();

                // STARVATION vs OVERRUN. The guard below measures how long the tick TOOK; this
                // measures how long since the previous one STARTED. They answer different
                // questions and only the second can see a world that simply was not dispatched:
                // the tick body runs on this world's SubThread process group, but the SceneTree
                // walk that dispatches that group runs on MAIN, so a blocked main thread stops
                // ticks happening at all while every "took Xms" number stays perfectly healthy.
                // That is exactly the shape of a multi-second freeze that leaves no slow-tick line.
                if (_lastTickEntryTs != 0)
                {
                    var gapMs = (startTs - _lastTickEntryTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    var expectedMs = 1000.0 / NetRunner.TPS;
                    if (gapMs > expectedMs * 3)
                    {
                        // GC deltas across the gap: a pause this long is either someone doing
                        // work on main or a collection stopping every thread, and those have
                        // completely different fixes. Reading the counters costs nothing and
                        // removes the guess.
                        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                        Log(Debugger.DebugLevel.WARN,
                            $"[TickStarved] {gapMs:F0} ms since the previous tick started "
                            + $"(expected ~{expectedMs:F0} ms) - this world was NOT dispatched for "
                            + $"~{gapMs / expectedMs:F0} ticks. The time was spent OUTSIDE the tick. "
                            + $"gc across gap=[{g0 - _lastTickGc0},{g1 - _lastTickGc1},{g2 - _lastTickGc2}] "
                            + $"heap={GC.GetTotalMemory(false) / (1024 * 1024)}MiB");
                    }
                }
                _lastTickEntryTs = startTs;
                _lastTickGc0 = GC.CollectionCount(0);
                _lastTickGc1 = GC.CollectionCount(1);
                _lastTickGc2 = GC.CollectionCount(2);

                ServerProcessTick();
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs > 15)
                {
                    Log(Debugger.DebugLevel.WARN, $"ServerProcessTick took {elapsedMs:F2} ms");
                }

                if (_profiler != null)
                {
                    // elapsedMs is the whole tick, so phase shares are against what actually
                    // happened rather than against the sum of the phases -- the gap between the
                    // two is tick time no phase claims, which is exactly what you want to see.
                    _profiler.EndTick(elapsedMs);
                    if (_profiler.IsDue(out double phaseWindow))
                    {
                        _profiler.Emit(WorldId, CurrentTick, PeerStates.Count, phaseWindow);
                    }
                }

                if (Diagnostics.ServerMetrics.Enabled)
                {
                    _metrics ??= new Diagnostics.ServerMetrics();
                    _metrics.RecordTick(elapsedMs);
                    if (_metrics.IsDue(out double metricsWindow))
                    {
                        CollectPeerRtt(out int metricsPeers, out double rttMean, out uint rttMax);
                        var metricsJson = _metrics.Emit(WorldId, CurrentTick, metricsPeers, rttMean, rttMax, metricsWindow);

                        // Same window, so the census shares the metrics line's peers/duration.
                        if (Diagnostics.PayloadCensus.Enabled)
                        {
                            Diagnostics.PayloadCensus.Emit(
                                metricsPeers, metricsWindow, _metrics.TicksInLastWindow);
                        }

                        // Also ship it to any attached debugger for the Performance tab.
                        // ~400 bytes once a second - negligible next to the tick frames.
                        // Priority + reliable: the main queue carries hundreds of frames
                        // per second on a loaded world (one PAYLOADS frame per peer per
                        // tick), and past its hard backstop it drops from the front,
                        // where accumulated reliable frames sit. A once-a-second frame in
                        // that queue is starved exactly when the numbers matter most -
                        // measured with 22 bots, where the server logged metrics but the
                        // editor's Performance tab received none.
                        var metricsHub = Hub;
                        if (metricsHub is { HasClients: true })
                        {
                            using var metricsBuffer = new NetBuffer(metricsJson.Length * 4 + 16, usePool: true);
                            NetWriter.WriteString(metricsBuffer, metricsJson);
                            metricsHub.Enqueue(WorldId, DebugDataType.METRICS, metricsBuffer,
                                lossy: false, priority: true);
                        }
                    }
                }
#if DEBUG
                // stopwatch.Stop();
                // if (_frameCounter == 0) // Only log once per network tick
                // {
                //      Log(Debugger.DebugLevel.VERBOSE, $"ServerProcessTick took {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
                // }
#endif
                OnAfterNetworkTick?.Invoke(CurrentTick);
            }

            // CLIENT: Independent prediction tick loop
            if (NetRunner.Instance.IsClient)
            {
                if (_predictionInitialized)
                {
                    _clientFrameCounter += 1;
                    if (_clientFrameCounter >= NetRunner.PhysicsTicksPerNetworkTick)
                    {
                        _clientFrameCounter = 0;
                        _eligibleFrameIndex++;

                        // Ahead of prediction, so anything a subscriber does to the input state is
                        // picked up by this tick rather than the next one.
                        OnClientNetworkTick?.Invoke(CurrentTick);

                        // Adaptive lead slew: steer the predicted timeline toward an
                        // RTT-derived lead instead of free-running (see the slew block
                        // above RunClientPredictionTick for why).
                        uint rttMs = EffectiveRoundTripMs(
                            NetRunner.Instance.ServerPeer.IsSet
                                ? NetRunner.Instance.ServerPeer.RoundTripTime
                                : 0,
                            NetRunner.Impairment.LatencyMs);
                        int targetLead = ComputeTargetLeadTicks(rttMs, NetRunner.TPS);
                        int lead = _clientPredictedTick - CurrentTick;
                        int ticksToRun = PredictionTicksThisFrame(lead, targetLead, _eligibleFrameIndex);

                        for (int t = 0; t < ticksToRun; t++)
                        {
                            // Per prediction tick, not per frame: SendInput refuses to
                            // piggyback an ack once this is set, so a double-tick frame
                            // must clear it before each run.
                            _ackAttachedThisFrame = false;
                            RunClientPredictionTick();
                        }
                    }
                }

                // Anything RunClientPredictionTick didn't manage to attach to an input packet goes
                // out on its own. This sits OUTSIDE the _predictionInitialized check on purpose:
                // PeerAcknowledge is what moves a peer from INITIAL to IN_WORLD, and before
                // prediction starts the client owns nothing, so there is no input packet to ride
                // on. Gating this would deadlock the join.
                if (_pendingAckTick >= 0)
                {
                    SendStandaloneAck(_pendingAckTick);
                    _pendingAckTick = -1;
                }
            }
        }

        /// <summary>
        /// Sends a tick acknowledgement as its own packet, the way every ack used to go out.
        /// Used when no input packet was available to carry it.
        /// </summary>
        private void SendStandaloneAck(Tick tick)
        {
            _ackBuffer ??= new NetBuffer();
            _ackBuffer.Reset();
            NetWriter.WriteInt32(_ackBuffer, tick);
            NetRunner.SendUnreliableSequenced(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Tick, _ackBuffer);
        }

        /// <summary>
        /// Gets the spawn state for a node for a specific peer.
        /// </summary>
        public ClientSpawnState GetClientSpawnState(NetId networkId, NetPeer peer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return ClientSpawnState.NotSpawned;
            }
            return peerState.SpawnState.TryGetValue(networkId, out var state) ? state : ClientSpawnState.NotSpawned;
        }

        /// <summary>
        /// Sets the spawn state for a node for a specific peer.
        /// </summary>
        public void SetClientSpawnState(NetId networkId, NetPeer peer, ClientSpawnState state)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            PeerStates[peerId].SpawnState[networkId] = state;
        }

        /// <summary>
        /// Returns true if the spawn has been acknowledged by the peer (state == Spawned).
        /// </summary>
        public bool HasSpawnedForClient(NetId networkId, NetPeer peer)
        {
            return GetClientSpawnState(networkId, peer) == ClientSpawnState.Spawned;
        }

        /// <summary>
        /// Checks if a node has been registered for a peer (spawn data was sent).
        /// This is true when SpawnSerializer has exported for this peer, regardless of ACK.
        /// </summary>
        public bool IsNodeRegisteredForPeer(NetId networkId, NetPeer peer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return false;
            }
            return peerState.WorldToPeerNodeMap.ContainsKey(networkId);
        }

        /// <summary>
        /// Sets the spawn state to Spawned (for backward compatibility).
        /// </summary>
        public void SetSpawnedForClient(NetId networkId, NetPeer peer)
        {
            SetClientSpawnState(networkId, peer, ClientSpawnState.Spawned);
        }

        public void ChangeScene(NetworkController netController)
        {
            if (NetRunner.Instance.IsServer) return;

            if (RootScene != null)
            {
                RootScene.QueueNodeForDeletion();
            }
            Log("Changing scene to " + netController.RawNode.Name);

            // Timed as one region because it is one: putting a world's root into the tree runs every
            // _Ready under it, uploads every mesh and hands every collider to the physics server, and
            // none of that is deferred. On a client arriving in a world this is the frame a loading
            // screen cannot animate through.
            using (Nebula.Diagnostics.MainThreadWork.Time($"ChangeScene({netController.RawNode.Name})"))
            {
                // TODO: Support this more generally
                GetTree().CurrentScene.AddChild(netController.RawNode);
                RootScene = netController;
                netController._NetworkPrepare(this);
                netController._WorldReady();
            }
            Debug?.Send("WorldJoined", netController.RawNode.SceneFilePath);
        }

        // Reusable free-list for ResetForWorldChange (avoids allocating while iterating NetScenes).
        // Sized to the per-peer node cap so a full world never reallocates during reset.
        private readonly List<NetworkController> _worldChangeFreeList = new(NodeIdUtils.MAX_NETWORK_NODES);

        /// <summary>
        /// Client-only. Raised at the start of <see cref="ResetForWorldChange"/>, before any node is
        /// freed, so game-side singletons can drop cached references to nodes in the outgoing world
        /// (e.g. a "current player" pointer) and avoid touching disposed objects.
        /// </summary>
        public event Action OnWorldReset;

        /// <summary>
        /// Client-only. Fully resets this world container so the client can receive a brand-new world
        /// (a different root scene) over the same connection — used for live world migration
        /// (see <see cref="NetRunner.MigratePeerToWorld"/> and the World ENet channel).
        ///
        /// The client keeps a single persistent WorldRunner (<see cref="CurrentWorld"/>); when the
        /// server moves the peer to another world, that world hands out fresh local node ids starting
        /// at 1, which would collide with the stale entries left behind by the previous world. This
        /// flushes every client-side node and all per-world bookkeeping so the incoming spawn stream
        /// rebuilds cleanly. Allocation-free: iterates existing collections into a reused free-list.
        /// </summary>
        internal void ResetForWorldChange()
        {
            if (NetRunner.Instance.IsServer) return;


            // Let game-side singletons drop cached references to nodes we're about to free
            // (e.g. WorldPlayers.CurrentPlayer) before the nodes are disposed.
            OnWorldReset?.Invoke();

            // Collect first, then free — freeing mutates the tree, and QueueNodeForDeletion may touch
            // NetScenes, so we must not free while enumerating it.
            _worldChangeFreeList.Clear();
            foreach (var netController in NetScenes.Values)
            {
                if (netController != null)
                {
                    _worldChangeFreeList.Add(netController);
                }
            }
            for (int i = 0; i < _worldChangeFreeList.Count; i++)
            {
                var raw = _worldChangeFreeList[i].RawNode;
                // QueueFree defers to end of frame and is subtree-safe, so freeing a parent and a
                // descendant here is fine — Godot frees the whole subtree once.
                if (raw != null && IsInstanceValid(raw))
                {
                    raw.QueueFree();
                }
            }
            _worldChangeFreeList.Clear();

            // Defensive: free the root if it somehow wasn't registered in NetScenes.
            if (RootScene != null && RootScene.RawNode != null && IsInstanceValid(RootScene.RawNode))
            {
                RootScene.RawNode.QueueFree();
            }

            // Clear all per-world bookkeeping so the destination world starts from a blank slate.
            NetScenes.Clear();
            networkIds.Clear();
            networkIdCounter = 1;
            Array.Clear(ClientAvailableNodes, 0, ClientAvailableNodes.Length);
            RootScene = null;

            // Drop any queued work that referenced the old world's nodes: a stale net function would
            // resolve against a freed node, and a stale pending-despawn could kill a new-world node that
            // happens to reuse the same local id.
            queuedNetFunctions.Clear();
            _pendingClientDespawns.Clear();
            _pendingNetSceneAdds.Clear();

            // Reset the tick stream. The destination world's tick counter starts low (near 0), so without
            // this the "skip old/duplicate ticks" guard in ClientProcessTick (incomingTick <= CurrentTick)
            // would reject every tick from the new world and it would never load. -1 lets tick 0 through;
            // the first accepted tick re-runs InitializeClientPrediction.
            CurrentTick = -1;
            _predictionInitialized = false;
            _clientPredictedTick = -1;


            // A parked ack still references an old-world tick. Acks are routed by peer, not by
            // world, so flushing it after the migration would land it in the destination world's
            // PeerAcknowledge - and if the tick number happens to be valid there, it marks state
            // this client never applied as acked (delta baselines, spawn commits).
            _pendingAckTick = -1;
            TimeSinceLastTick = 0f;
            _ownedEntities.Clear();
            _ownedEntitiesDirty = true;

            Debug?.Send("WorldReset", WorldId.ToString());
        }

        public PeerState? GetPeerWorldState(UUID peerId)
        {
            // Fix #7: Use TryGetValue
            return PeerStates.TryGetValue(peerId, out var state) ? state : null;
        }

        public PeerState? GetPeerWorldState(NetPeer peer)
        {
            // Fix #7: Use TryGetValue
            var peerId = NetRunner.Instance.GetPeerId(peer);
            return PeerStates.TryGetValue(peerId, out var state) ? state : null;
        }

        readonly private Dictionary<UUID, PeerState> pendingSyncStates = [];

        /// <summary>
        /// Tracks the last tick each peer acknowledged. Used for timeout detection.
        /// </summary>
        private Dictionary<UUID, Tick> _peerLastAckTick = new();

        /// <summary>Reused per-peer send buffer; the array is pooled but the object used to be allocated per peer per tick.</summary>
        private NetBuffer _sendPacketBuffer;

        /// <summary>
        /// Client-side wrapper over the received tick body, re-attached per packet (no copy, no
        /// allocation). The body is the bit-packed payload straight from ExportState.
        /// </summary>
        private readonly NetBuffer _clientTickBuffer = new(Array.Empty<byte>());

        /// <summary>
        /// Server-side: the last tick this peer acknowledged receiving, or -1 if none yet.
        /// Approximates the peer's confirmed tick — useful for bounding how far behind a client's
        /// view of non-owned entities can legitimately be (its prediction lead free-runs and
        /// varies per session, so measuring beats guessing).
        /// </summary>
        public Tick GetPeerLastAckedTick(UUID peerId)
        {
            if (_peerLastAckTick.TryGetValue(peerId, out var acked))
                return acked;
            return -1;
        }

        /// <summary>
        /// Reusable list for peers to disconnect (avoids allocation each tick).
        /// </summary>
        private List<NetPeer> _peersToDisconnect = new(32);

        /// <summary>
        /// Reusable list for net IDs to remove from NetScenes (avoids allocation each tick).
        /// </summary>
        private List<NetId> _netIdsToRemove = new(64);

        /// <summary>
        /// Flag to track when we're iterating NetScenes to defer modifications.
        /// </summary>
        private bool _isProcessingNetScenes = false;

        /// <summary>
        /// Pending NetScene additions queued during iteration (applied after loop completes).
        /// </summary>
        private List<(NetId Id, NetworkController Controller)> _pendingNetSceneAdds = new(16);

        /// <summary>
        /// Adds a network controller to NetScenes. Defers the add if currently iterating.
        /// </summary>
        internal void AddNetScene(NetId id, NetworkController controller)
        {
            if (_isProcessingNetScenes)
                _pendingNetSceneAdds.Add((id, controller));
            else
                NetScenes[id] = controller;
        }

        /// <summary>
        /// Removes a network controller from NetScenes. Defers the remove if currently iterating.
        /// Also cleans up networkIds on the client side.
        /// </summary>
        internal void RemoveNetScene(NetId id)
        {
            if (_isProcessingNetScenes)
                _netIdsToRemove.Add(id);
            else
                NetScenes.Remove(id);
            
            // Clean up networkIds (used on client for GetNodeFromNetId(long) lookups)
            networkIds.Remove(id.Value);
        }

        /// <summary>
        /// Applies all pending NetScenes additions and removals after iteration completes.
        /// </summary>
        private void FlushPendingNetSceneChanges()
        {
            foreach (var (id, ctrl) in _pendingNetSceneAdds)
                NetScenes[id] = ctrl;
            _pendingNetSceneAdds.Clear();

            foreach (var id in _netIdsToRemove)
                NetScenes.Remove(id);
            _netIdsToRemove.Clear();
        }

        /// <summary>
        /// Cached peer list to avoid ToList() allocation every tick (Fix #1).
        /// Rebuilt only when peers join or leave.
        /// </summary>
        private List<NetPeer> _cachedPeerList = new(64);
        private bool _peerListDirty = true;

        /// <summary>
        /// Per peer: which nodes had a section committed into each recent tick's packet, so an
        /// ack for tick T visits exactly those nodes (see <see cref="SentNodeRing"/>). Created
        /// lazily inside ExportState on the world thread - never from JoinPeer, which runs on
        /// main while this world may be mid-export - and dropped with the rest of the per-peer
        /// state in TeardownPeer/ExitPeer.
        /// </summary>
        private readonly Dictionary<UUID, SentNodeRing> _peerSentRings = new();

        /// <summary>
        /// Controller behind each peer-local node id that has a section in the packet being
        /// assembled. Written by TryAppendSection on a node's first section; only ever read
        /// behind a set bit of <c>_updatedNodesMask</c>, so entries left over from an earlier
        /// peer are never observed.
        /// </summary>
        private readonly NetworkController[] _peerNodesControllers = new NetworkController[NodeIdUtils.MAX_NETWORK_NODES];

        /// <summary>
        /// Nested scenes that rode an ancestor's spawn table in the packet being assembled
        /// without committing a section of their own (SpawnSerializer.CommitExport reports
        /// them via <see cref="NoteNestedSpawnRider"/>). Registered into the ack ring after
        /// the mask walk, minus any that also committed a section. Cleared per peer.
        /// </summary>
        private readonly List<NetworkController> _tickNestedRiders = new(16);
        private readonly long[] _tickRiderMask = NodeIdUtils.CreateMasks();

        /// <summary>
        /// Buffer for tick-aligned player joined events.
        /// Player joins are buffered here and fired at the start of ServerProcessTick()
        /// to ensure they occur at a predictable point in the tick cycle.
        /// </summary>
        private readonly List<UUID> _pendingPlayerJoined = new();

        public void SetPeerState(UUID peerId, PeerState state)
        {
            if (PeerStates[peerId].Status != state.Status)
            {
                OnPeerSyncStatusChange?.Invoke(peerId, state.Status);
                if (state.Status == PeerSyncStatus.IN_WORLD)
                {
                    // Buffer instead of firing immediately - will be processed at start of ServerProcessTick
                    _pendingPlayerJoined.Add(peerId);
                }
            }
            PeerStates[peerId] = state;
        }
        public void SetPeerState(NetPeer peer, PeerState state)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            SetPeerState(peerId, state);
        }

        /// <summary>
        /// Processes buffered player join events. Called at the start of ServerProcessTick()
        /// to ensure OnPlayerJoined fires at a predictable, tick-aligned point.
        /// </summary>
        private void ProcessPendingPlayerJoins()
        {
            if (_pendingPlayerJoined.Count == 0) return;

            foreach (var peerId in _pendingPlayerJoined)
            {
                OnPlayerJoined?.Invoke(WorldId, peerId);
            }

            _pendingPlayerJoined.Clear();
        }

        public ushort GetPeerNodeId(NetPeer peer, NetworkController node)
        {
            if (node == null) return 0;
            // Fix #7: Use TryGetValue
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return 0;
            }
            return peerState.WorldToPeerNodeMap.TryGetValue(node.NetId, out var nodeId) ? nodeId : (ushort)0;
        }

        /// <summary>
        /// Get the network node from a peer and a network ID relative to that peer.
        /// </summary>
        /// <param name="peer"></param>
        /// <param name="networkId"></param>
        /// <returns></returns>
        public NetworkController GetPeerNode(NetPeer peer, ushort networkId)
        {
            // Fix #7: Use TryGetValue
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return null;
            }
            if (!peerState.PeerToWorldNodeMap.TryGetValue(networkId, out var netId))
            {
                return null;
            }
            return NetScenes.TryGetValue(netId, out var controller) ? controller : null;
        }

        internal void DeregisterPeerNode(NetworkController node, NetPeer peer = default)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (!peer.IsSet)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Server must specify a peer when deregistering a node.");
                    return;
                }
                var peerId = NetRunner.Instance.GetPeerId(peer);
                if (PeerStates[peerId].WorldToPeerNodeMap.TryGetValue(node.NetId, out var nodeId))
                {
                    NodeIdUtils.ClearBit(PeerStates[peerId].AvailableNodes, nodeId);
                    PeerStates[peerId].WorldToPeerNodeMap.Remove(node.NetId);
                    PeerStates[peerId].PeerToWorldNodeMap.Remove(nodeId);
                }
            }
            else
            {
                RemoveNetScene(node.NetId);
            }
        }

        // A local peer node ID is assigned to each node that a peer owns
        // This allows us to sync nodes across the network without sending long integers
        // 0 indicates that the node is not registered. Node ID starts at 1
        // Up to 512 nodes can be networked per peer at a time (8 groups × 64 nodes).
        internal ushort TryRegisterPeerNode(NetworkController node, NetPeer peer = default)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (!peer.IsSet)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Server must specify a peer when registering a node.");
                    return 0;
                }
                var peerId = NetRunner.Instance.GetPeerId(peer);
                if (PeerStates[peerId].WorldToPeerNodeMap.TryGetValue(node.NetId, out var existingId))
                {
                    return existingId;
                }

                // Find first available node ID using hierarchical bitmask
                var localNodeId = NodeIdUtils.FindFirstAvailable(PeerStates[peerId].AvailableNodes);
                if (localNodeId == 0)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Peer {peerId} has reached the maximum amount of nodes ({NodeIdUtils.MAX_NETWORK_NODES}).");
                    return 0;
                }

                PeerStates[peerId].WorldToPeerNodeMap[node.NetId] = localNodeId;
                PeerStates[peerId].PeerToWorldNodeMap[localNodeId] = node.NetId;
                NodeIdUtils.SetBit(PeerStates[peerId].AvailableNodes, localNodeId);
                return localNodeId;
            }

            if (NetScenes.ContainsKey(node.NetId))
            {
                return 0;
            }

            // On client, also register in networkIds so GetNodeFromNetId(long) works
            networkIds[node.NetId.Value] = node.NetId;
            AddNetScene(node.NetId, node);
            return 1;
        }
        
        public T Spawn<T>(
            T node,
            NetworkController parent = null,
            NetPeer inputAuthority = default,
            NodePath netNodePath = default
        ) where T : Node, INetNodeBase
        {
            if (NetRunner.Instance.IsClient) return null;

            // Live-tree AddChild plus NetId allocation plus a pass over NetRunner.Instance.Peers --
            // none of which is safe off the main thread.
            NebulaThread.AssertMain(nameof(Spawn));

            if (!node.Network.IsNetScene())
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Only Net Scenes can be spawned (i.e. a scene where the root node is an NetNode). Attempting to spawn node that isn't a Net Scene: {node.Network.RawNode.Name} on {parent.RawNode.Name}/{netNodePath}");
                return null;
            }

            if (parent != null && !parent.IsNetScene())
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"You can only spawn a Net Scene as a child of another Net Scene. Attempting to spawn node on a parent that isn't a Net Scene: {node.Network.RawNode.Name} on {parent.RawNode.Name}/{netNodePath}");
                return null;
            }

            node.Network.IsClientSpawn = true;
            node.Network.CurrentWorld = this;
            if (inputAuthority.IsSet)
            {
                node.Network.SetInputAuthority(inputAuthority);
            }
            if (parent == null)
            {
                if (RootScene == null)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Cannot spawn {node.Network.RawNode.Name}: RootScene is null on WorldRunner {WorldId}. Was the world created via SetupWorldInstance?");
                    return null;
                }
                node.Network.NetParent = RootScene;
                var targetNode = netNodePath == default || netNodePath.IsEmpty ? RootScene.RawNode : RootScene.RawNode.GetNode(netNodePath);
                targetNode.AddChild(node);
                
                // Cache node path ID for spawn serialization
                if (netNodePath != default && !netNodePath.IsEmpty)
                {
                    if (Protocol.PackNode(RootScene.NetSceneFilePath, netNodePath, out var pathId))
                    {
                        node.Network.CachedNodePathIdInParent = pathId;
                    }
                    else
                    {
                        node.Network.CachedNodePathIdInParent = 255;
                    }
                }
                else
                {
                    node.Network.CachedNodePathIdInParent = 255;
                }
            }
            else
            {
                node.Network.NetParent = parent;
                var targetNode = netNodePath == default || netNodePath.IsEmpty ? parent.RawNode : parent.RawNode.GetNode(netNodePath);
                targetNode.AddChild(node);
                
                // Cache node path ID for spawn serialization
                if (netNodePath != default && !netNodePath.IsEmpty)
                {
                    if (Protocol.PackNode(parent.NetSceneFilePath, netNodePath, out var pathId))
                    {
                        node.Network.CachedNodePathIdInParent = pathId;
                    }
                    else
                    {
                        node.Network.CachedNodePathIdInParent = 255;
                    }
                }
                else
                {
                    node.Network.CachedNodePathIdInParent = 255;
                }
            }
            node.Network._NetworkPrepare(this);
            node.Network._WorldReady();
            return node;
        }

        internal void JoinPeer(NetPeer peer, string token)
        {
            // Mutates NetRunner's peer registries, which the ENet pump reads every frame.
            NebulaThread.AssertMain(nameof(JoinPeer));

            if (Lifecycle != WorldLifecycle.Live)
            {
                Log(Debugger.DebugLevel.ERROR,
                    $"JoinPeer: world {WorldId} is {Lifecycle}, not Live. Refusing to admit peer {peer.ID}.");
                return;
            }

            var peerId = NetRunner.Instance.GetPeerId(peer);
            NetRunner.Instance.PeerWorldMap[peerId] = this;
            PeerStates[peerId] = new PeerState
            {
                Id = peerId,
                Peer = peer,
                Tick = 0,
                JoinedAtTick = CurrentTick,
                Status = PeerSyncStatus.INITIAL,
                Token = token,
                WorldToPeerNodeMap = [],
                PeerToWorldNodeMap = [],
                SpawnState = [],
                AvailableNodes = NodeIdUtils.CreateMasks(),
                OwnedNodes = []
            };

            // Fix #1: Mark peer list as dirty so it gets rebuilt
            _peerListDirty = true;

            // Deliberately no per-peer export state here (ack ring, buffer pool, cursors): this
            // runs on main, ExportState owns those on the world thread and creates them lazily.

            // Initialize interest layers for the root scene immediately so properties
            // can be exported on the same tick as the spawn
            if (RootScene != null)
            {
                RootScene._OnPeerConnected(WorldId, peerId);
            }
        }

        internal void ExitPeer(NetPeer peer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            NetRunner.Instance.PeerWorldMap.Remove(peerId);
            PeerStates.Remove(peerId);
            // Per-peer export state, same set TeardownPeer drops. These used to leak here.
            _peerSentRings.Remove(peerId);
            _peerNetBufferPool.Remove(peerId);
            _peerPropsCursors.Remove(peerId);
            _peerListDirty = true;
        }

        /// <summary>
        /// Test seam: registers a minimal PeerState (the JoinPeer shape, minus the ENet
        /// connection and root-scene hooks) so serializer Export/Acknowledge paths can run
        /// against this world in unit tests. The caller is responsible for mapping the
        /// peer in NetRunner.Instance.PeerIds and removing it again.
        /// </summary>
        internal void CreatePeerStateForTests(NetPeer peer, UUID peerId)
        {
            PeerStates[peerId] = new PeerState
            {
                Id = peerId,
                Peer = peer,
                Tick = 0,
                JoinedAtTick = CurrentTick,
                Status = PeerSyncStatus.INITIAL,
                WorldToPeerNodeMap = [],
                PeerToWorldNodeMap = [],
                SpawnState = [],
                AvailableNodes = NodeIdUtils.CreateMasks(),
                OwnedNodes = []
            };
        }

        // Declare these as fields, not locals - reuse across ticks
        private Dictionary<ushort, NetBuffer> _peerNodesBuffers = new();
        private Dictionary<ushort, byte> _peerNodesSerializersList = new();

        /// <summary>
        /// Per-peer round-robin cursor for the props phase of ExportState: the NetId of
        /// the next node owed property service. Without it, whichever nodes iterate first
        /// would monopolize every budget-limited packet and later nodes would starve.
        /// </summary>
        private Dictionary<UUID, long> _peerPropsCursors = new();

        /// <summary>
        /// Per-peer partition of <see cref="_tickNodeList"/>, rebuilt once per peer per tick:
        /// the nodes this peer has input authority over, and everything else. Reused buffers -
        /// ExportPartition.Partition clears them on entry.
        /// </summary>
        private readonly List<NetworkController> _tickOwnedList = new(8);
        private readonly List<NetworkController> _tickSharedList = new(64);

        /// <summary>Snapshot of NetScenes.Values, stable across the phases of one export.</summary>
        private readonly List<NetworkController> _tickNodeList = new(64);

        /// <summary>
        /// Serializer indices fixed by NetNode.SetupSerializers; the export phases run in
        /// this order, which is also the per-node section order the client consumes.
        /// </summary>
        private const int SpawnSerializerIndex = 0;
        private const int PropsSerializerIndex = 1;
        private const int InterestResyncSerializerIndex = 2;

        /// <summary>
        /// Props phase stops serving (and defers the rest of the rotation) once the
        /// remaining section budget drops below this. Ceiling of the smallest useful
        /// props section: presence mask reservation (max 64 props = 8 bytes, plus the
        /// two-level header = 9; see PresenceMask) + age byte + smallest property write
        /// (2 bytes), rounded up for slack.
        /// </summary>
        private const int PropsSectionFloorBits = 16 * BitConstants.BitsInByte;

        /// <summary>
        /// Backstop for a misconfigured MTU so small the budget math goes non-positive;
        /// splitting still works, packets just exceed such an MTU.
        /// </summary>
        private const int MinTickPayloadBudget = 128;

        /// <summary>One-shot guard for the "spawn record can never fit" diagnostic.</summary>
        private bool _loggedUnfittableSpawnRecord;

        /// <summary>Stopwatch timestamp at which the previous tick STARTED, for starvation detection.</summary>
        private long _lastTickEntryTs;
        private int _lastTickGc0, _lastTickGc1, _lastTickGc2;
        private NetBuffer _tempSerializerBuffer;
        private Dictionary<ushort, NetBuffer> _nodeBufferPool = new();
        // Hierarchical bitmask for tracking updated nodes per peer
        private long[] _updatedNodesMask = NodeIdUtils.CreateMasks();
        // Pooled dictionary for ExportState return value - avoids per-tick allocation
        private Dictionary<UUID, NetBuffer> _exportPeerBuffers = new();
        // Pooled NetBuffer instances per peer - avoids per-tick allocation
        private Dictionary<UUID, NetBuffer> _peerNetBufferPool = new();
        // Pooled dictionary for ImportState - avoids per-tick allocation
        private Dictionary<ushort, byte> _importNodeSerializerMap = new();
        private readonly long[] _importNodeMasks = new long[NodeIdUtils.NODE_GROUPS];
        // Pooled list for net function args - avoids per-call allocation
        private List<PropertyCache> _netFunctionArgsPool = new(8);

        internal Dictionary<UUID, NetBuffer> ExportState(List<NetPeer> peers)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Reuse pooled dictionary instead of allocating new each tick
            _exportPeerBuffers.Clear();

            // Lazy init the serializers buffers
            _tempSerializerBuffer ??= new NetBuffer();

            // Stable node snapshot for this tick (world order: parents precede children)
            // plus the once-per-tick Begin pass.
            _tickNodeList.Clear();
            foreach (var netController in NetScenes.Values)
            {
                _tickNodeList.Add(netController);
                foreach (var serializer in netController.NetNode.Serializers)
                {
                    serializer.Begin();
                }
            }

            // MTU reads ProjectSettings - fetch once per tick, not per node.
            var payloadBudget = NetRunner.TickPayloadBudget(NetRunner.MTU);
            if (payloadBudget < MinTickPayloadBudget)
            {
                payloadBudget = MinTickPayloadBudget;
            }

            foreach (NetPeer peer in peers)
            {
                var peerId = NetRunner.Instance.GetPeerId(peer);

                // Reset hierarchical bitmask for this peer
                Array.Clear(_updatedNodesMask, 0, NodeIdUtils.NODE_GROUPS);

                // Get or create pooled NetBuffer for this peer
                if (!_peerNetBufferPool.TryGetValue(peerId, out var peerBuffer))
                {
                    peerBuffer = new NetBuffer();
                    _peerNetBufferPool[peerId] = peerBuffer;
                }
                peerBuffer.Reset();
                _exportPeerBuffers[peerId] = peerBuffer;

                _peerNodesBuffers.Clear();
                _peerNodesSerializersList.Clear();

                // Owner-priority: this peer's own nodes are served before the crowd, in every
                // phase. Disjoint and exhaustive - see ExportPartition.Partition for why both
                // properties are load-bearing rather than tidy.
                ExportPartition.Partition(_tickNodeList, peer, _tickOwnedList, _tickSharedList);

                // Ack routing for this packet: every node that commits a section below is
                // registered into this tick's slot after the phases run (mask walk at the
                // end of the loop), so the ack for CurrentTick visits exactly those nodes.
                if (!_peerSentRings.TryGetValue(peerId, out var sentRing))
                {
                    sentRing = new SentNodeRing();
                    _peerSentRings[peerId] = sentRing;
                }
                sentRing.Begin(CurrentTick);
                _tickNestedRiders.Clear();
                Array.Clear(_tickRiderMask, 0, NodeIdUtils.NODE_GROUPS);

                var ledger = new TickBudgetLedger(payloadBudget * BitConstants.BitsInByte);
                var peerState = PeerStates[peerId];
                _traceWirePeer = peerId;
                int spawnSectionsDeferred = 0;
                int propsSectionsDeferred = 0;
                // Counted apart from the shared totals: a peer's OWN node being deferred is the
                // regression signal for owner-priority, and it is invisible inside a total that
                // is dominated by the crowd.
                int ownedSpawnSectionsDeferred = 0;
                int ownedPropsSectionsDeferred = 0;

                var exportPhaseTs = Diagnostics.TickProfiler.Now();

                // ---- PHASE 1: spawns/despawns, OWNED then SHARED, world order, first-fit
                // No cap and no cursor: the spawn set drains (each committed record
                // leaves it within one RTT of shipping), records are small, and world
                // order maximizes parent-before-child delivery for the child-spawn
                // parent gate. A record that doesn't fit is dropped whole - the spawn
                // serializer is atomic, and its packet-coupled stamps only happen in
                // CommitExport, so a dropped record retries cleanly next tick.
                //
                // The owned/shared split costs nothing here: the child-spawn gate requires the
                // parent to be Spawned (ACKED, see HasSpawnedForClient), so a child can never
                // ride the same tick as its parent whatever the iteration order - world order
                // within each partition is all that gate can use.
                //
                // INVARIANT: each node's spawn serializer Exports at most ONCE per peer per
                // tick. PropsMayRidePacket reads an ancestor's in-flight nested-table membership
                // from state that a second Export would reset, so a "retry deferred spawns with
                // leftover budget" pass here would silently corrupt that read.
                for (var spawnPass = 0; spawnPass < 2; spawnPass++)
                {
                    var spawnPassList = spawnPass == 0 ? _tickOwnedList : _tickSharedList;
                    for (var i = 0; i < spawnPassList.Count; i++)
                    {
                        var netController = spawnPassList[i];
                        var serializers = netController.NetNode.Serializers;
                        if (serializers.Length <= SpawnSerializerIndex) continue;
                        var serializer = serializers[SpawnSerializerIndex];

                        // Framing guess for the section budget: exact when the node already
                        // has a local id, conservative (worst case +9) when registration
                        // happens inside the spawn Export itself.
                        bool hasLocalId = peerState.WorldToPeerNodeMap.TryGetValue(netController.NetId, out var knownLocalId);
                        bool guessFirst = !hasLocalId || !NodeIdUtils.IsBitSet(_updatedNodesMask, knownLocalId);
                        bool guessOpens = !hasLocalId || GroupIsClosed(knownLocalId);

                        _tempSerializerBuffer.Reset();
                        var result = serializer.Export(this, peer, _tempSerializerBuffer,
                            ledger.SectionBudget(guessFirst, guessOpens));
                        if (result == ExportResult.None || _tempSerializerBuffer.WrittenBits == 0)
                        {
                            continue;
                        }

                        // Safety check: ensure node is registered before lookup
                        if (!peerState.WorldToPeerNodeMap.TryGetValue(netController.NetId, out var localNodeId))
                        {
                            Log(Debugger.DebugLevel.ERROR,
                                $"[ExportState] Node {netController.RawNode?.Name} (NetId={netController.NetId}) wrote data but isn't registered for peer {peerId}.");
                            continue;
                        }

                        if (!TryAppendSection(netController, localNodeId, SpawnSerializerIndex, ref ledger))
                        {
                            // Over budget: retries next tick. Should only ever be transient
                            // (a crowded packet) - a record too big for an EMPTY packet can
                            // never ship and means the budget math or the nested-table split
                            // is broken. One loud line, not one per tick.
                            if (!_loggedUnfittableSpawnRecord
                                && _tempSerializerBuffer.WrittenBits > payloadBudget * BitConstants.BitsInByte - TickBudgetLedger.MaxSectionOverheadBits)
                            {
                                _loggedUnfittableSpawnRecord = true;
                                Log(Debugger.DebugLevel.ERROR,
                                    $"[ExportState] BUG: spawn record for {netController.RawNode?.Name} (NetId={netController.NetId}) is {_tempSerializerBuffer.WrittenBits} bits and exceeds the whole tick budget ({payloadBudget} bytes); it can never be delivered. Further occurrences suppressed.");
                            }
                            if (spawnPass == 0) ownedSpawnSectionsDeferred++; else spawnSectionsDeferred++;
                            continue;
                        }
                        serializer.CommitExport(this, peer, CurrentTick);
                    }
                }

                _profiler?.Record(Diagnostics.TickProfiler.Phase.ExportSpawn, exportPhaseTs);
                exportPhaseTs = Diagnostics.TickProfiler.Now();

                // ---- PHASE 2a: props for the nodes this peer OWNS ------------------
                // Served before any SHARED spawn or prop, which is the whole point: a player
                // walking into a crowd must not have their own character's updates pushed into
                // next tick by twenty strangers arriving. Running here rather than reserving
                // budget up front makes it exact instead of predicted - a props section cannot
                // be measured without committing it, because Export stamps delta-chain, lossy
                // and chunk-frontier state that is only valid if the bytes actually ship.
                //
                // No rotation and no `serving` latch: the owned set is small (one net scene per
                // player in practice), so a node skipped by PropsMayRidePacket must not stop the
                // ones behind it. See the cursor note in 2b for why that latch exists there.
                //
                // "Never deferred" has real exceptions, all of them correct:
                //   - a node still Spawning whose spawn record has not been committed cannot
                //     have props ride (the client could not resolve the id);
                //   - a section can still be Partial - owner-priority lands the section, it does
                //     not guarantee every property inside it fits;
                //   - interest can zero the section out, since owning a node and being
                //     interested in it are independent.
                // Start offset rotates with the tick so a fixed order cannot starve the tail
                // deterministically. There is no per-peer cursor here on purpose: a player owns
                // one net scene in practice, so a cursor would be per-peer state maintained for
                // a case that does not arise, while this costs one modulo and removes the
                // deterministic-starvation failure mode outright. If owned sets ever grow large
                // enough that a partial rotation matters, promote this to a real cursor like 2b's.
                var ownedCount = _tickOwnedList.Count;
                var ownedStart = ownedCount > 1 ? (int)((uint)CurrentTick % (uint)ownedCount) : 0;
                for (var i = 0; i < ownedCount; i++)
                {
                    var netController = _tickOwnedList[(ownedStart + i) % ownedCount];
                    var serializers = netController.NetNode.Serializers;
                    if (serializers.Length <= PropsSerializerIndex) continue;
                    var serializer = serializers[PropsSerializerIndex];

                    // Settled pre-gate: nothing owed, so skip PropsMayRidePacket and the
                    // Export call outright. Safe to skip the bank-only call too - a
                    // settled node has no dirty bits to bank by definition.
                    if (serializer.NothingForPeer(peerId)) continue;

                    bool hasRoom = ledger.Remaining >= PropsSectionFloorBits;
                    ushort ownedLocalId = 0;
                    bool ownedMaySail = hasRoom
                        && PropsMayRidePacket(netController, peer, ref peerState, out ownedLocalId);

                    _tempSerializerBuffer.Reset();
                    if (!ownedMaySail)
                    {
                        // Bank this tick's dirty bits exactly as the shared path does - skipping
                        // a peer without banking loses the tick's changes outright.
                        serializer.Export(this, peer, _tempSerializerBuffer, 0);
                        ownedPropsSectionsDeferred++;
                        continue;
                    }

                    bool ownedFirst = !NodeIdUtils.IsBitSet(_updatedNodesMask, ownedLocalId);
                    var ownedResult = serializer.Export(this, peer, _tempSerializerBuffer,
                        ledger.SectionBudget(ownedFirst, ownedFirst && GroupIsClosed(ownedLocalId)));
                    if (ownedResult == ExportResult.None || _tempSerializerBuffer.WrittenBits == 0)
                    {
                        continue;
                    }

                    if (!TryAppendSection(netController, ownedLocalId, PropsSerializerIndex, ref ledger))
                    {
                        Log(Debugger.DebugLevel.ERROR,
                            $"[ExportState] BUG: owned props section for {netController.RawNode?.Name} (NetId={netController.NetId}) exceeded its budget and was dropped.");
                        continue;
                    }
                    serializer.CommitExport(this, peer, CurrentTick);
                }

                // ---- PHASE 2b: props for everything else, round-robin from the cursor
                var nodeCount = _tickSharedList.Count;
                if (nodeCount > 0)
                {
                    var startIdx = 0;
                    if (_peerPropsCursors.TryGetValue(peerId, out var cursorNetId))
                    {
                        startIdx = ExportRotation.FindStartIndex(_tickSharedList, cursorNetId);
                    }

                    bool serving = true;
                    long nextCursorNetId = 0;
                    bool cursorPinned = false;

                    for (var k = 0; k < nodeCount; k++)
                    {
                        var netController = _tickSharedList[(startIdx + k) % nodeCount];
                        var serializers = netController.NetNode.Serializers;
                        if (serializers.Length <= PropsSerializerIndex) continue;
                        var serializer = serializers[PropsSerializerIndex];

                        if (serving && ledger.Remaining < PropsSectionFloorBits)
                        {
                            // Out of room: this node is first in line next tick, and the
                            // rest of the rotation defers below.
                            serving = false;
                            nextCursorNetId = netController.NetId.Value;
                            cursorPinned = true;
                        }

                        // Settled pre-gate, after the budget latch so the cursor still
                        // pins fairly: nothing owed means no PropsMayRidePacket lookups,
                        // no Export, and no bank-only call (a settled node has no dirty
                        // bits to bank by definition).
                        if (serializer.NothingForPeer(peerId)) continue;

                        // A props section for an uncommitted spawn may only ride a packet
                        // that also carries the spawn data teaching the client the id.
                        ushort localNodeId = 0;
                        bool maySail = serving
                            && PropsMayRidePacket(netController, peer, ref peerState, out localNodeId);

                        _tempSerializerBuffer.Reset();
                        if (!maySail)
                        {
                            // Defer: banks this tick's broadcast dirty bits for the peer
                            // (they would otherwise die with processingDirtyMask).
                            serializer.Export(this, peer, _tempSerializerBuffer, 0);
                            propsSectionsDeferred++;
                            continue;
                        }

                        bool first = !NodeIdUtils.IsBitSet(_updatedNodesMask, localNodeId);
                        var result = serializer.Export(this, peer, _tempSerializerBuffer,
                            ledger.SectionBudget(first, first && GroupIsClosed(localNodeId)));
                        if (result == ExportResult.None || _tempSerializerBuffer.WrittenBits == 0)
                        {
                            continue;
                        }

                        if (!TryAppendSection(netController, localNodeId, PropsSerializerIndex, ref ledger))
                        {
                            // Contract breach: a self-limiting serializer wrote past its
                            // section budget. The bytes are dropped and never committed,
                            // so its in-write stamps (chunk frontiers) may now be ahead
                            // of what the peer will receive - loud, not silent.
                            Log(Debugger.DebugLevel.ERROR,
                                $"[ExportState] BUG: props section for {netController.RawNode?.Name} (NetId={netController.NetId}) exceeded its budget and was dropped.");
                            continue;
                        }
                        serializer.CommitExport(this, peer, CurrentTick);

                        if (result == ExportResult.Partial && !cursorPinned)
                        {
                            // Node still has data queued - it resumes first next tick.
                            nextCursorNetId = netController.NetId.Value;
                            cursorPinned = true;
                            serving = false;
                        }
                    }

                    if (!cursorPinned)
                    {
                        // Full rotation served: rotate the start anyway so a fixed
                        // iteration bias can't freeze in.
                        nextCursorNetId = _tickSharedList[(startIdx + 1) % nodeCount].NetId.Value;
                    }
                    _peerPropsCursors[peerId] = nextCursorNetId;
                }

                _profiler?.Record(Diagnostics.TickProfiler.Phase.ExportProps, exportPhaseTs);
                exportPhaseTs = Diagnostics.TickProfiler.Now();

                // ---- PHASE 3: interest resync (1-byte sections) --------------------
                for (var i = 0; i < _tickNodeList.Count; i++)
                {
                    var netController = _tickNodeList[i];
                    var serializers = netController.NetNode.Serializers;
                    if (serializers.Length <= InterestResyncSerializerIndex) continue;
                    var serializer = serializers[InterestResyncSerializerIndex];

                    // Resync only exports for Spawned nodes, which are always registered.
                    if (!peerState.WorldToPeerNodeMap.TryGetValue(netController.NetId, out var localNodeId))
                    {
                        continue;
                    }

                    bool first = !NodeIdUtils.IsBitSet(_updatedNodesMask, localNodeId);
                    bool opensGroup = first && GroupIsClosed(localNodeId);
                    _tempSerializerBuffer.Reset();
                    var result = serializer.Export(this, peer, _tempSerializerBuffer,
                        ledger.SectionBudget(first, opensGroup));
                    if (result == ExportResult.None || _tempSerializerBuffer.WrittenBits == 0)
                    {
                        continue;
                    }
                    int resyncSectionBits = _tempSerializerBuffer.WrittenBits;
                    if (!TryAppendSection(netController, localNodeId, InterestResyncSerializerIndex, ref ledger))
                    {
                        continue; // dropped: resent next tick, no packet-coupled state stamped
                    }
                    if (_profiler != null)
                    {
                        _profiler.Add(Diagnostics.TickProfiler.Counter.ResyncSections, 1);
                        _profiler.Add(Diagnostics.TickProfiler.Counter.ResyncBits,
                            resyncSectionBits + TickBudgetLedger.FramingCostForDiagnostics(first, opensGroup));
                    }
                    serializer.CommitExport(this, peer, CurrentTick);
                }

                _profiler?.Record(Diagnostics.TickProfiler.Phase.ExportResync, exportPhaseTs);

                    if (_metrics != null)
                {
                    _metrics.RecordTickBudget(ledger.UsedBytes, ledger.BudgetBytes);
                    _metrics.RecordDeferredSections(spawnSectionsDeferred, propsSectionsDeferred,
                        ownedSpawnSectionsDeferred, ownedPropsSectionsDeferred);
                    int spawningCount = 0;
                    foreach (var spawnState in peerState.SpawnState.Values)
                    {
                        if (spawnState == ClientSpawnState.Spawning) spawningCount++;
                    }
                    _metrics.RecordSpawnBacklog(spawningCount);
                }

                // Bit-packed framing (see PacketFraming): group presence, then per present
                // group its node set (dense or gap-coded, whichever is shorter), then per
                // node its serializers-run word, then the node bodies at bit granularity,
                // padded to a byte once at the end. The ledger charged the worst case of each
                // framing word, so the assembled packet can only come in under budget.
                var packet = _exportPeerBuffers[peerId];
                byte groupMask = NodeIdUtils.ComputeGroupMask(_updatedNodesMask);
                packet.WriteBits(groupMask, PacketFraming.GroupPresenceBits);
                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) != 0)
                    {
                        PacketFraming.WriteNodeSet(packet, _updatedNodesMask[g]);
                    }
                }

                // Write serializer words and node data in bitmask iteration order (ascending nodeId)
                // This is zero-allocation and produces sorted order since Combine(g,local) = (g<<6)|local
                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) == 0) continue;
                    for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                    {
                        if ((_updatedNodesMask[g] & (1L << local)) == 0) continue;
                        ushort nodeId = NodeIdUtils.Combine(g, local);
                        var serializersRun = _peerNodesSerializersList[nodeId];
                        PacketFraming.WriteSerializersRun(packet, serializersRun, PropsSerializerIndex);

                        // This mask is, by construction, the deduplicated set of nodes with
                        // a committed section in this packet - so it is also the exact set
                        // the ack for CurrentTick must visit.
                        sentRing.Add(_peerNodesControllers[nodeId]);

                        // Spawn-contract breach detector: a packet may carry data WITHOUT
                        // the spawn bit only for a node whose id the client provably has
                        // or gets - spawn committed (Spawned), or riding an in-flight
                        // ancestor's nested table in this same packet. Anything else and
                        // the payload length is unknowable client-side - the exact
                        // precondition of the "[ImportState] Data for unknown node"
                        // abort. The props phase gate (PropsMayRidePacket) makes this
                        // unreachable by construction; the detector stays as a backstop
                        // that names the node and state at the SOURCE if it ever leaks.
                        if (NetRunner.TraceSpawnIds && (serializersRun & (1 << SpawnSerializerIndex)) == 0
                            && PeerStates[peerId].PeerToWorldNodeMap.TryGetValue(nodeId, out var worldNetId)
                            && NetScenes.TryGetValue(worldNetId, out var tracedController))
                        {
                            var contractState = GetClientSpawnState(worldNetId, peer);
                            if (contractState != ClientSpawnState.Spawned)
                            {
                                bool ridesInFlightAncestorTable = false;
                                for (var ancestor = tracedController.NetParent; ancestor != null; ancestor = ancestor.NetParent)
                                {
                                    var ancestorState = GetClientSpawnState(ancestor.NetId, peer);
                                    if (ancestorState == ClientSpawnState.NotSpawned || ancestorState == ClientSpawnState.Spawning)
                                    {
                                        ridesInFlightAncestorTable = contractState == ClientSpawnState.Spawning;
                                        break;
                                    }
                                }
                                if (!ridesInFlightAncestorTable)
                                {
                                    Log(Debugger.DebugLevel.ERROR,
                                        $"[IdTrace] BREACH tick={CurrentTick} peer={peerId} id={nodeId} NetId={worldNetId} node={tracedController.RawNode?.Name} mask=0b{Convert.ToString(serializersRun, 2)} state={contractState}: exported without spawn data while spawn is not committed");
                                }
                            }
                        }
                    }
                }
                // Nested scenes that rode an ancestor's spawn table this packet had their
                // spawn windows stamped for CurrentTick without a section of their own, so
                // the mask walk above cannot see them. Register the ones it did not: a
                // rider that ALSO committed its own section (its props phase ran after the
                // parent's spawn commit) is already in the ring. Must run after the mask
                // walk so the dedup reads the final mask.
                for (int r = 0; r < _tickNestedRiders.Count; r++)
                {
                    var rider = _tickNestedRiders[r];
                    if (!peerState.WorldToPeerNodeMap.TryGetValue(rider.NetId, out var riderLocalId)) continue;
                    if (NodeIdUtils.IsBitSet(_updatedNodesMask, riderLocalId)) continue;
                    if (NodeIdUtils.IsBitSet(_tickRiderMask, riderLocalId)) continue;
                    NodeIdUtils.SetBit(_tickRiderMask, riderLocalId);
                    sentRing.Add(rider);
                }

                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) == 0) continue;
                    for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                    {
                        if ((_updatedNodesMask[g] & (1L << local)) == 0) continue;
                        ushort nodeId = NodeIdUtils.Combine(g, local);
                        // The final stream: pad at the sections' align marks here.
                        packet.AppendBitsApplyingMarks(_peerNodesBuffers[nodeId]);
                    }
                }
                // Whole bytes go on the wire; the parser is mask-driven and ignores the pad.
                packet.AlignWrite();
            }

            var exportTime = sw.ElapsedMilliseconds;
            sw.Restart();

            // Debugger.Instance.Log($"Export: {exportTime}ms");

            var cleanupTs = Diagnostics.TickProfiler.Now();
            foreach (var netController in NetScenes.Values)
            {
                // Finally, cleanup serializers
                foreach (var serializer in netController.NetNode.Serializers)
                {
                    serializer.Cleanup();
                }
            }
            _profiler?.Record(Diagnostics.TickProfiler.Phase.ExportCleanup, cleanupTs);

            return _exportPeerBuffers;
        }

        /// <summary>True when the node's 64-node group has no included node yet (its int64 node mask is unwritten).</summary>
        private bool GroupIsClosed(ushort nodeId)
        {
            var (group, _) = NodeIdUtils.Split(nodeId);
            return _updatedNodesMask[group] == 0;
        }

        /// <summary>
        /// Charges the ledger for the section sitting in _tempSerializerBuffer and appends
        /// it to the node's per-peer buffer, opening the node's packet entry on its first
        /// section. Returns false - nothing charged or appended - when it doesn't fit.
        /// </summary>
        /// <summary>
        /// <c>NEBULA_TRACE_WIRE=1</c>: logs every appended section on the server ([Wire][S]) and
        /// every section import on the client ([Wire][C]) with tick, node id, serializer, length
        /// and leading bytes; NetPropertiesSerializer adds per-property [Props.W]/[Props.R]
        /// lines. Diff the two for one tick to find the first property whose size differs -
        /// the only way to localise a framing desync ("invalid baseline age", "Scene ID not
        /// found", "Parent node not found" bursts). Off by default; costs one static bool test.
        /// </summary>
        private static readonly bool TraceWire = System.Environment.GetEnvironmentVariable("NEBULA_TRACE_WIRE") != null;
        private UUID _traceWirePeer;
        private bool TryAppendSection(NetworkController netController, ushort localNodeId, int serializerIdx, ref TickBudgetLedger ledger)
        {
            if (TraceWire)
            {
                var span = _tempSerializerBuffer.WrittenSpan;
                Log($"[Wire][S] tick={CurrentTick} peer={_traceWirePeer} node={localNodeId} ser={serializerIdx} bits={_tempSerializerBuffer.WrittenBits} bytes={System.Convert.ToHexString(span.Length > 48 ? span.Slice(0,48) : span)}");
            }
            bool firstSection = !NodeIdUtils.IsBitSet(_updatedNodesMask, localNodeId);
            bool opensGroup = firstSection && GroupIsClosed(localNodeId);
            // Each align mark can cost up to 7 pad bits at assembly; charge them here.
            int sectionBits = _tempSerializerBuffer.WrittenBits
                + _tempSerializerBuffer.AlignMarkCount * (BitConstants.BitsInByte - 1);
            if (!ledger.TryCommitSection(sectionBits, firstSection, opensGroup))
            {
                return false;
            }

            if (firstSection)
            {
                NodeIdUtils.SetBit(_updatedNodesMask, localNodeId);
                _peerNodesControllers[localNodeId] = netController;
                if (!_nodeBufferPool.TryGetValue(localNodeId, out var nodeBuffer))
                {
                    nodeBuffer = new NetBuffer();
                    _nodeBufferPool[localNodeId] = nodeBuffer;
                }
                nodeBuffer.Reset();
                _peerNodesBuffers[localNodeId] = nodeBuffer;
                _peerNodesSerializersList[localNodeId] = 0;
            }

            // Sections concatenate at bit granularity; the node buffer holds bits.
            _peerNodesBuffers[localNodeId].AppendBits(_tempSerializerBuffer);
            _peerNodesSerializersList[localNodeId] |= (byte)(1 << serializerIdx);
            return true;
        }

        /// <summary>
        /// Gate for the props phase: may a props section for this node ride the packet
        /// currently being assembled? For a committed spawn (Spawned), always. While the
        /// spawn is in flight (Spawning), only when the packet itself carries the spawn
        /// data that teaches the client the node's id - the node's own spawn section, or
        /// an in-flight ancestor's spawn table that provably includes this node. Anything
        /// else risks the client-side "Data for unknown node" tick-import abort, which
        /// kills the whole tick and its ack. Nodes refused here are deferred instead
        /// (their dirty bits bank in PendingDirtyMask).
        /// </summary>
        private bool PropsMayRidePacket(NetworkController netController, NetPeer peer, ref PeerState peerState, out ushort localNodeId)
        {
            localNodeId = 0;
            var state = GetClientSpawnState(netController.NetId, peer);
            if (state == ClientSpawnState.Spawned)
            {
                return peerState.WorldToPeerNodeMap.TryGetValue(netController.NetId, out localNodeId);
            }
            if (state != ClientSpawnState.Spawning)
            {
                // The props serializer refuses these states anyway; nothing to ride.
                return false;
            }
            if (!peerState.WorldToPeerNodeMap.TryGetValue(netController.NetId, out localNodeId))
            {
                return false;
            }

            // Own spawn section committed into this packet?
            if (_peerNodesSerializersList.TryGetValue(localNodeId, out var ownMask)
                && (ownMask & (1 << SpawnSerializerIndex)) != 0)
            {
                return true;
            }

            // Riding an in-flight ancestor's spawn table in this packet? The ancestor's
            // SpawnSerializer still holds the nested set of its last Export for this
            // peer (valid: phase 1 for this peer ran immediately before this phase), so
            // membership is checked exactly, not inferred from the hierarchy.
            //
            // Only AUTHORED nested scenes can answer yes now - runtime spawns no longer ride
            // an ancestor's table (see SpawnSerializer.CollectNestedNetScenesRecursive) and
            // reach Spawning through their own record instead, which the check above already
            // covers. The walk stays because authored nesting is still arbitrarily deep.
            for (var ancestor = netController.NetParent; ancestor != null; ancestor = ancestor.NetParent)
            {
                if (GetClientSpawnState(ancestor.NetId, peer) != ClientSpawnState.Spawning)
                {
                    continue;
                }
                if (!peerState.WorldToPeerNodeMap.TryGetValue(ancestor.NetId, out var ancestorLocalId)
                    || !_peerNodesSerializersList.TryGetValue(ancestorLocalId, out var ancestorMask)
                    || (ancestorMask & (1 << SpawnSerializerIndex)) == 0)
                {
                    continue;
                }
                if (ancestor.NetNode?.Serializers is { Length: > 0 } ancestorSerializers
                    && ancestorSerializers[SpawnSerializerIndex] is SpawnSerializer ancestorSpawn
                    && ancestorSpawn.NestedSceneRodeLastSpawnExport(netController))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Client-side. Imports a full tick's state payload.
        /// Returns true if the whole payload was applied; false if import aborted partway
        /// (corrupt buffer). A failed import must NOT be acked - the server would mark the
        /// data as delivered and never resend it.
        /// </summary>
        internal bool ImportState(NetBuffer stateBytes)
        {
            // Set when any serializer parses its payload but reports it was not applied
            // (e.g. a props delta whose baseline this client doesn't have). The stream stays
            // aligned - unlike the corrupted-buffer aborts below - but the tick must not be
            // acked: an ack tells the server "I have this tick's data", and acking a
            // discarded payload latches delta encoding onto a baseline we never recorded.
            bool anyDiscarded = false;

            // Bit-packed framing (see PacketFraming): group presence, then a node set per
            // present group. Pooled scratch - this ran once per received tick.
            var groupMask = (byte)stateBytes.ReadBits(PacketFraming.GroupPresenceBits);
            var nodeMasks = _importNodeMasks;
            Array.Clear(nodeMasks, 0, nodeMasks.Length);
            for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
            {
                if ((groupMask & (1 << g)) != 0)
                {
                    nodeMasks[g] = PacketFraming.ReadNodeSet(stateBytes);
                }
            }

            // Build list of affected node IDs with their serializer masks (pooled dictionary)
            _importNodeSerializerMap.Clear();
            for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
            {
                if ((groupMask & (1 << g)) == 0) continue;

                for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                {
                    if ((nodeMasks[g] & (1L << local)) == 0) continue;

                    ushort nodeId = NodeIdUtils.Combine(g, local);
                    var serializersRun = PacketFraming.ReadSerializersRun(stateBytes, PropsSerializerIndex);
                    _importNodeSerializerMap[nodeId] = serializersRun;
                }
            }

            // Process nodes in bitmask iteration order (ascending nodeId) to match export order
            for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
            {
                if ((groupMask & (1 << g)) == 0) continue;
                for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                {
                    if ((nodeMasks[g] & (1L << local)) == 0) continue;
                    
                    ushort localNodeId = NodeIdUtils.Combine(g, local);
                    var serializerMask = _importNodeSerializerMap[localNodeId];
                    var netController = GetNodeFromNetId(localNodeId);
                    bool isNewNode = netController == null;

                    if (netController == null)
                    {
                        // Data for a node this client doesn't know. Legitimate only when the
                        // payload carries the node's spawn data (serializer bit 0): the blank
                        // placeholder below gets replaced by the real scene mid-import and the
                        // stream stays aligned. Props-only data for an unknown id means state
                        // desync - the blank node's zero-property serializer would consume the
                        // wrong byte count and garble every node after it in this packet (the
                        // "invalid baseline age N" bursts, N being a mask byte misread as an
                        // age). The payload's length is unknowable here, so abort the tick:
                        // it is not acked, and resend machinery re-delivers everything.
                        if ((serializerMask & 1) == 0)
                        {
                            Log(Debugger.DebugLevel.ERROR,
                                $"[ImportState] tick={CurrentTick} Data for unknown node {localNodeId} without spawn data (mask=0b{Convert.ToString(serializerMask, 2)}). Aborting tick import.");
                            return false;
                        }

                        var blankScene = new NetNode3D();
                        blankScene.Network.NetId = AllocateNetId(localNodeId);
                        blankScene.Network.CurrentWorld = this; // Set CurrentWorld so handleDespawn uses QueueDespawn instead of immediate QueueFree
                        blankScene.SetupSerializers();
                        NetRunner.Instance.AddChild(blankScene);
                        TryRegisterPeerNode(blankScene.Network);
                        netController = blankScene.Network;
                    }

                    // Log($"[ImportState] Processing node {localNodeId}: isNewNode={isNewNode}, serializerMask=0b{Convert.ToString(serializerMask, 2)}, scenePath='{netController.NetSceneFilePath}'");
                    
                    for (var serializerIdx = 0; serializerIdx < netController.NetNode.Serializers.Length; serializerIdx++)
                    {
                        if ((serializerMask & ((long)1 << serializerIdx)) == 0)
                        {
                            // Log($"[ImportState] Node {localNodeId}: Skipping serializer {serializerIdx} (bit not set)");
                            continue;
                        }

                        // A node queued for despawn (mid-import by SpawnSerializer, or earlier by a
                        // pending client despawn) still has its serializer bytes in this packet -
                        // the mask bit proves the server wrote them. The serializers must still
                        // run so those bytes are consumed; breaking out here would leave them in
                        // the buffer and misalign every node after this one. Applying values to a
                        // dying node is harmless: QueueDespawnedNodes is drained at the end of
                        // this same ClientProcessTick and the free is deferred, so the node is
                        // alive for the duration of the import. We just don't let a discard from
                        // a doomed node veto the tick ack.
                        bool nodeIsDoomed = netController.IsQueuedForDespawn || netController.IsMarkedForDeletion;

                        var serializerInstance = netController.NetNode.Serializers[serializerIdx];
                        // Log($"[ImportState] Node {localNodeId}: Running serializer {serializerIdx} ({serializerInstance.GetType().Name})");

                        if (TraceWire)
                        {
                            var remaining = stateBytes.Length - stateBytes.ReadPosition;
                            var peek = new System.ReadOnlySpan<byte>(stateBytes.RawBuffer, stateBytes.ReadPosition, System.Math.Min(48, remaining));
                            Log($"[Wire][C] tick={CurrentTick} node={localNodeId} ser={serializerIdx} mask=0b{Convert.ToString(serializerMask, 2)} pos={stateBytes.ReadPosition}/{stateBytes.Length} scene={netController.NetSceneFilePath} next={System.Convert.ToHexString(peek)}");
                        }
                        try
                        {
                            bool applied = serializerInstance.Import(this, stateBytes, out NetworkController nodeOut);
                            if (!applied && !nodeIsDoomed)
                            {
                                anyDiscarded = true;
                            }
                            if (netController != nodeOut)
                            {
                                // Log($"[ImportState] Node {localNodeId}: Serializer {serializerIdx} replaced node, new scenePath='{nodeOut.NetSceneFilePath}', restarting loop");
                                netController = nodeOut;
                                serializerIdx = 0;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // Log error with FULL STACK TRACE and context, then ABORT processing this tick entirely
                            // to prevent cascading errors from corrupted buffer position
                            var scenePath = netController?.NetSceneFilePath ?? "(null)";
                            var nodeType = netController?.RawNode?.GetType().Name ?? "(null)";
                            var nodeName = netController?.RawNode?.Name ?? "(null)";
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[ImportState ERROR] Failed to import node {localNodeId} serializer {serializerIdx}: {ex.Message}. Buffer pos={stateBytes.ReadPosition}/{stateBytes.Length}. Node info: scenePath='{scenePath}', type={nodeType}, name={nodeName}, isNewNode={isNewNode}. Aborting tick import.\nStack trace:\n{ex.StackTrace}");
                            return false; // Don't continue processing - buffer position is corrupted
                        }
                    }
                }
            }

            // Call _WorldReady on new nodes in bitmask iteration order
            for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
            {
                if ((groupMask & (1 << g)) == 0) continue;
                for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                {
                    if ((nodeMasks[g] & (1L << local)) == 0) continue;
                    
                    ushort localNodeId = NodeIdUtils.Combine(g, local);
                    var netController = GetNodeFromNetId(localNodeId);
                    if (!netController.IsWorldReady)
                    {
                        // Ensure newly spawned nodes are now world-ready
                        // We don't run this in SpawnSerializer because subsequent serializers may need to run before "ready"
                        netController._WorldReady();
                    }
                }
            }

            return !anyDiscarded;
        }

        /// <summary>
        /// Client-side: deregisters a despawned node AND its nested NetScene subtree.
        /// QueueFree takes the whole Godot subtree with it, nested NetScenes included - but
        /// nothing despawns those children individually. A stale NetScenes entry for a
        /// freed child both routes incoming data into a disposed node (aborting the tick
        /// import) and blocks re-registration of the authored replacement when the parent
        /// later respawns (client TryRegisterPeerNode refuses ids that appear registered).
        /// </summary>
        private void DeregisterDespawnedSubtree(NetworkController netController)
        {
            // Safe to iterate directly: nothing below mutates DynamicNetworkChildren
            // (deregistration and deletion-queueing never touch NetParentId).
            foreach (var child in netController.DynamicNetworkChildren)
            {
                DeregisterDespawnedSubtree(child);
            }
            DeregisterPeerNode(netController);
            // A child that already went through its own despawn may be freed by now -
            // deregistering is still required, but don't touch the disposed node.
            if (IsInstanceValid(netController.RawNode))
            {
                netController.QueueNodeForDeletion();
            }
        }

        /// <summary>
        /// Called by SpawnSerializer.CommitExport, mid-peer and mid-tick on the world thread,
        /// for a nested scene whose spawn window it just stamped because the child rode this
        /// packet inside an ancestor's spawn table. ExportState folds these into the tick's
        /// ack routing after its phases run (see the rider block at the end of the peer loop).
        /// </summary>
        internal void NoteNestedSpawnRider(NetworkController child)
        {
            _tickNestedRiders.Add(child);
        }

        /// <summary>
        /// Test seam: registers <paramref name="node"/> as having shipped in the packet for
        /// <paramref name="tick"/> to <paramref name="peerId"/>, exactly as ExportState's mask
        /// walk would, so PeerAcknowledge can be driven without a Protocol registry.
        /// </summary>
        internal void RegisterSentNodeForTests(UUID peerId, Tick tick, NetworkController node)
        {
            if (!_peerSentRings.TryGetValue(peerId, out var ring))
            {
                ring = new SentNodeRing();
                _peerSentRings[peerId] = ring;
            }
            if (!ring.TryGet(tick, out _))
            {
                ring.Begin(tick);
            }
            ring.Add(node);
        }

        public void PeerAcknowledge(NetPeer peer, Tick tick)
        {
            // A peer cannot legitimately acknowledge a tick the server hasn't produced yet, nor a
            // negative one. Without this, a hostile ack (e.g. int.MaxValue) would set peerState.Tick
            // to a huge value and make every serializer believe all pending state was delivered.
            if (tick < 0 || tick > CurrentTick)
            {
                Log(Debugger.DebugLevel.ERROR, $"[Nebula][InvalidAck] Peer acknowledged out-of-range tick {tick} (currentTick {CurrentTick})");
                return;
            }

            var peerId = NetRunner.Instance.GetPeerId(peer);

            // Fix #7: Use TryGetValue
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return;
            }

            if (peerState.Tick >= tick)
            {
                // Duplicate or old ack - skip
                return;
            }

            // Update last ack tick for timeout tracking
            _peerLastAckTick[peerId] = tick;

            var isFirstAck = peerState.Status == PeerSyncStatus.INITIAL;
            if (isFirstAck)
            {
                var newPeerState = peerState;
                newPeerState.Tick = tick;
                newPeerState.Status = PeerSyncStatus.IN_WORLD;
                // The first time a peer acknowledges a tick, we know they are in the World
                SetPeerState(peerId, newPeerState);
            }

            // Route the ack to exactly the nodes whose bytes rode packet `tick`. Every
            // serializer's Acknowledge only acts on ticks it committed in (spawn windows,
            // props sent-history, object props inside a committed props section), so nothing
            // else can have anything to do with this ack. An ack older than the ring's depth
            // finds no slot and is dropped; every consumer resends until acked, so that
            // costs one extra round (see SentNodeRing.Depth).
            if (!_peerSentRings.TryGetValue(peerId, out var sentRing) || !sentRing.TryGet(tick, out var sentNodes))
            {
                return;
            }

            for (var n = 0; n < sentNodes.Count; n++)
            {
                var netController = sentNodes[n];
                // A node can be freed between commit and ack (despawn completed, peer left).
                if (netController == null || netController.IsMarkedForDeletion || netController.NetNode?.Serializers == null)
                {
                    continue;
                }

                _profiler?.Add(Diagnostics.TickProfiler.Counter.AckNodesVisited, 1);
                var serializers = netController.NetNode.Serializers;
                for (var serializerIdx = 0; serializerIdx < serializers.Length; serializerIdx++)
                {
                    serializers[serializerIdx].Acknowledge(this, peer, tick);
                }
            }
        }


        public void ClientProcessTick(int incomingTick, byte[] stateBytes)
        {
            // Skip old/duplicate ticks. Load-bearing beyond dedup: the server stops
            // exporting a despawned parent's nested children from the tick the despawn
            // marker first ships (SpawnSerializer's send-time cascade), which only closes
            // the orphan-props window if a packet older than an applied despawn is never
            // applied after it. The tick channel's ENet flag is Unsequenced, so this
            // guard - not the transport - is what enforces send-order apply.
            if (incomingTick <= CurrentTick)
            {
                return;
            }

            // Initialize prediction on first tick
            if (!_predictionInitialized)
            {
                InitializeClientPrediction(incomingTick);
            }

            // Confirmed-timeline stall diagnostic. Distinguishes "server/network stalled"
            // (this fires, repeatedly if it keeps happening) from "old debt the slew is
            // still shedding" (this stays quiet). TimeSinceLastTick is wall-clock seconds
            // since the previous confirmed tick arrived; read before OnWorldTickReceived
            // resets it below.
            float stallThreshold = 4f / NetRunner.TPS;
            if (_predictionInitialized && TimeSinceLastTick > stallThreshold)
            {
                ulong nowMsec = Time.GetTicksMsec();
                if (nowMsec - _lastStallLogMsec >= 1000)
                {
                    _lastStallLogMsec = nowMsec;
                    Log(Debugger.DebugLevel.INFO,
                        $"[Prediction] Confirmed timeline stalled for {TimeSinceLastTick * 1000f:F0}ms " +
                        $"(tick jump {CurrentTick} -> {incomingTick}); prediction lead grew by ~{incomingTick - CurrentTick - 1} ticks");
                }
            }

            CurrentTick = incomingTick;
            OnWorldTickReceived(incomingTick); // Reset time accumulator for snapshot interpolation
            bool importSucceeded = false;
            try
            {
                // Log(Debugger.DebugLevel.VERBOSE, $"Importing state bytes of size {stateBytes.Length}");
                _clientTickBuffer.Attach(stateBytes, stateBytes.Length);
                // Whole-import timing, so a stall can be attributed: SpawnImportProfiler
                // reports the scene-building share, and the difference between that and this
                // is everything else (property apply, change notifications, despawns).
                var importTs = System.Diagnostics.Stopwatch.GetTimestamp();
                importSucceeded = ImportState(_clientTickBuffer);
                Diagnostics.SpawnImportProfiler.EndTick(incomingTick,
                    Diagnostics.SpawnImportProfiler.Elapsed(importTs));
            }
            catch (Exception ex)
            {
                Log(Debugger.DebugLevel.ERROR, $"[ImportState FAILED] tick {incomingTick}: {ex.Message}");
                // Still continue processing the tick locally, but do NOT ack it (below)
            }

            // Rebuild owned entities cache if needed
            if (_ownedEntitiesDirty)
            {
                RebuildOwnedEntitiesCache();
            }

            // Reconciliation: check predictions and rollback if needed
            for (int i = 0; i < _ownedEntities.Count; i++)
            {
                var netController = _ownedEntities[i];
                if (netController == null || netController.IsMarkedForDeletion) continue;
                ReconcileOwnedEntity(netController, incomingTick);
            }

            // Process non-owned entities with server state.
            _netIdsToRemove.Clear();
            _isProcessingNetScenes = true;
            foreach (var net_id in NetScenes.Keys)
            {
                if (!NetScenes.TryGetValue(net_id, out var netController) || netController == null)
                    continue;

                if (netController.IsMarkedForDeletion)
                {
                    _netIdsToRemove.Add(net_id);
                    continue;
                }

                // Only process non-owned entities here (owned are handled in prediction)
                if (!netController.IsCurrentOwner)
                {
                    netController._NetworkProcess(CurrentTick);
                }

                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;

                    if (!staticChild.IsCurrentOwner)
                    {
                        staticChild._NetworkProcess(CurrentTick);
                    }
                }
            }
            _isProcessingNetScenes = false;
            FlushPendingNetSceneChanges();

            // NOTE: Prediction advancement has been moved to RunClientPredictionTick()
            // which runs independently in _PhysicsProcess at a consistent rate.
            // This method (ClientProcessTick) now only handles reconciliation.

            // ============================================================
            // PROCESS QUEUED NET FUNCTIONS
            // ============================================================
            foreach (var queuedFunction in queuedNetFunctions)
            {
                var functionNode = queuedFunction.Node.GetNode(queuedFunction.FunctionInfo.NodePath) as INetNodeBase;
                NetFunctionContext = new NetFunctionCtx
                {
                    Caller = queuedFunction.Sender,
                };
                functionNode.Network.IsInboundCall = true;
                // Use source-generated dispatch - no Variant conversion, no Godot boundary crossing
                var rawNode = functionNode.Network.RawNode;
                if (rawNode is NetNode3D n3d)
                    n3d.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                else if (rawNode is NetNode2D n2d)
                    n2d.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                else if (rawNode is NetNode n)
                    n.InvokeNetFunctionByName(queuedFunction.FunctionInfo.Name, queuedFunction.Args);
                functionNode.Network.IsInboundCall = false;
                NetFunctionContext = new NetFunctionCtx { };
            }
            queuedNetFunctions.Clear();

            // ============================================================
            // PROCESS DESPAWNS
            // ============================================================
            foreach (var netController in QueueDespawnedNodes)
            {
                DeregisterDespawnedSubtree(netController);
            }
            QueueDespawnedNodes.Clear();

            // ============================================================
            // ACKNOWLEDGE TICK (pooled buffer)
            // ============================================================
            // Only ack fully-applied imports. An ack tells the server "I have this tick's
            // data" - acking a failed import would disarm the resend machinery and lose
            // the state permanently. If failures persist, the server's ack-timeout will
            // eventually drop this peer, which is the correct outcome for a broken stream.
            if (importSucceeded)
            {
                // Don't send yet - hand it to the next outgoing input packet if there is one, so we
                // pay ~4 bytes inside a packet we're already sending instead of a whole 44-byte
                // datagram (a 4-byte payload in 40 bytes of IPv4 + UDP + ENet framing).
                //
                // If an ack is already waiting, this frame received two state packets. Flush the
                // older one standalone rather than overwriting it: the server marks baselines
                // per-tick; dropping one would cost the props serializers a baseline they could have used.
                if (_pendingAckTick >= 0) SendStandaloneAck(_pendingAckTick);
                _pendingAckTick = incomingTick;
            }
        }

        /// <summary>
        /// This is called for nodes that are initialized in a scene by default.
        /// Clients automatically dequeue all network nodes on initialization.
        /// All network nodes on the client side must come from the server by gaining Interest in the node.
        /// </summary>
        /// <param name="wrapper"></param>
        /// <returns></returns>
        public bool CheckStaticInitialization(NetworkController network)
        {
            if (NetRunner.Instance.IsServer)
            {
                network.NetId = AllocateNetId();
                AddNetScene(network.NetId, network);
            }
            else
            {
                if (!network.IsClientSpawn)
                {
                    network.QueueNodeForDeletion();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Input packet flags. The packet carries the client's tick acknowledgement when
        /// <see cref="InputFlagHasAck"/> is set, so a 4-byte ack rides inside a packet already
        /// being sent rather than costing its own 44-byte datagram.
        ///
        /// Layout:
        /// <code>
        ///   [flags u8][ackTick i32 if HasAck][netId u16][staticChildId u8]
        ///   [inputSize u16][count u8][baseTick i32]  then count x [tickDelta u8][payload]
        /// </code>
        /// </summary>
        private const byte InputFlagHasAck = 0x01;
        private const byte InputFlagMask = InputFlagHasAck;

        /// <summary>
        /// Writes the redundant-input section: <c>[count u8][baseTick i32]</c> then one
        /// <c>[tickDelta u8][payload]</c> per record. Returns how many records were written.
        ///
        /// <paramref name="records"/> comes from GetRecentInputs, which is newest-first and may
        /// skip gaps in the input ring, so a tick can fall further back than a single byte can
        /// express. Everything after the newest record is pure redundancy, so the tail is simply
        /// dropped when that happens rather than falling back to wider ticks.
        ///
        /// The count is backfilled because how many records fit isn't known until the deltas have
        /// been walked.
        /// </summary>
        internal static byte WriteInputRecords(NetBuffer buffer, List<(Tick, byte[])> records, int inputSize)
        {
            Tick baseTick = records.Count > 0 ? records[0].Item1 : 0;

            int countPos = buffer.WritePosition;
            NetWriter.WriteByte(buffer, 0);
            NetWriter.WriteInt32(buffer, baseTick);

            byte written = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var (tick, input) = records[i];
                long delta = (long)baseTick - tick;

                if (delta < 0 || delta > byte.MaxValue) break;
                if (input == null || input.Length != inputSize) break;  // size is sent once; must agree

                NetWriter.WriteByte(buffer, (byte)delta);
                NetWriter.WriteBytes(buffer, input);
                written++;
            }

            int endPos = buffer.WritePosition;
            buffer.WritePosition = countPos;
            NetWriter.WriteByte(buffer, written);
            buffer.WritePosition = endPos;
            return written;
        }

        internal void SendInput(NetworkController netNode)
        {
            if (NetRunner.Instance.IsServer) return;

            // Check if the node supports input
            if (!netNode.HasInputSupport)
            {
                return;
            }

            // Get current input
            var inputBytes = netNode.GetInputBytes();

            // Buffer input for the current tick only.
            // During resimulation, each tick uses the input that was actually active at that time.
            // This matches server behavior where inputs arrive and are applied at specific ticks.
            netNode.BufferInput(_clientPredictedTick, inputBytes);

            // Only send if input has changed (but always buffer) — with a periodic keepalive.
            // Packets are unreliable, and the redundancy window only protects a change that is
            // followed by more sends within 8 ticks. Without a keepalive, losing the single packet
            // that carried the *last* change (e.g. releasing a strafe key before holding steady
            // thrust) leaves the server's input fallback replaying the previous held keys until
            // the next change.
            if (!netNode.HasInputChanged && ((int)(_clientPredictedTick & 3)) != 0)
            {
                return;
            }

            // Get pooled buffer to avoid allocation
            var inputBuffer = netNode.GetPooledInputBuffer();

            // Carry the pending tick ack if nothing else has this frame. Only the first input
            // packet takes it - SendInput runs once per owned node, and the ack is per-peer.
            bool carriesAck = _pendingAckTick >= 0 && !_ackAttachedThisFrame;
            NetWriter.WriteByte(inputBuffer, carriesAck ? InputFlagHasAck : (byte)0);
            if (carriesAck)
            {
                NetWriter.WriteInt32(inputBuffer, _pendingAckTick);
                _pendingAckTick = -1;
                _ackAttachedThisFrame = true;
            }

            // Static children don't have their own NetId - use parent's NetId + StaticChildId
            bool isStaticChild = netNode.StaticChildId > 0 && netNode.NetParent != null;
            if (isStaticChild)
            {
                NetId.NetworkSerialize(this, NetRunner.Instance.ServerPeer, netNode.NetParent.NetId, inputBuffer);
                NetWriter.WriteByte(inputBuffer, netNode.StaticChildId);
            }
            else
            {
                NetId.NetworkSerialize(this, NetRunner.Instance.ServerPeer, netNode.NetId, inputBuffer);
                NetWriter.WriteByte(inputBuffer, 0); // StaticChildId = 0 means not a static child
            }

            // Get recent inputs for redundancy
            var recentInputs = netNode.GetRecentInputs(NetworkController.INPUT_REDUNDANCY_COUNT);

            // Every record has the same length (the input struct is fixed size per node), so send it
            // once rather than repeating a 4-byte length on all 8 redundant copies.
            NetWriter.WriteUInt16(inputBuffer, (ushort)inputBytes.Length);

            WriteInputRecords(inputBuffer, recentInputs, inputBytes.Length);

            // Send unreliable - input redundancy handles packet loss
            NetRunner.SendUnreliable(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Input, inputBuffer);
            netNode.ClearInputChanged();
        }

        internal void ReceiveInput(NetPeer peer, NetBuffer buffer)
        {
            if (NetRunner.Instance.IsClient) return;

            // Read the ack FIRST. Every guard below returns early, and an acknowledgement must not
            // be lost just because the input half of the packet was rejected - acks drive the
            // INITIAL -> IN_WORLD transition and property resend clearing.
            var inputFlags = NetReader.ReadByte(buffer);
            if ((inputFlags & ~InputFlagMask) != 0)
            {
                Log(Debugger.DebugLevel.ERROR, $"[Nebula][InvalidInput] Unknown input flag bits 0x{inputFlags:X2} from peer {peer.ID}");
                return;
            }
            if ((inputFlags & InputFlagHasAck) != 0)
            {
                PeerAcknowledge(peer, NetReader.ReadInt32(buffer));
            }

            var networkId = NetReader.ReadUInt16(buffer);
            var staticChildId = NetReader.ReadByte(buffer);
            var worldNetId = GetNetIdFromPeerId(peer, networkId);
            var node = GetNodeFromNetId(worldNetId);
            if (node == null)
            {
                Log(Debugger.DebugLevel.ERROR, $"Received input for unknown node {worldNetId}");
                return;
            }

            // If this is input for a static child, look it up
            if (staticChildId > 0)
            {
                if (staticChildId >= node.StaticNetworkChildren.Length)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Received input for invalid static child {staticChildId} on node {worldNetId}");
                    return;
                }
                node = node.StaticNetworkChildren[staticChildId];
                if (node == null)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Static child {staticChildId} is null on node {worldNetId}");
                    return;
                }
            }

            // Use ID comparison instead of Equals - more reliable for ENet.Peer structs
            if (!node.InputAuthority.IsSet || node.InputAuthority.ID != peer.ID)
            {
                Log(Debugger.DebugLevel.ERROR, $"Received input for node {worldNetId} (staticChild={staticChildId}) from unauthorized peer {peer}");
                return;
            }

            // Check if the node supports input
            if (!node.HasInputSupport)
            {
                Log(Debugger.DebugLevel.ERROR, $"Received input for node {worldNetId} (staticChild={staticChildId}) that doesn't support input");
                return;
            }

            // Input size is sent once for the whole packet - every redundant record is the same
            // fixed-size input struct. Validate it against what this node actually expects: the
            // size now drives every subsequent read, so a wrong value would misalign the rest of
            // the packet rather than just producing one bad record.
            var inputSize = NetReader.ReadUInt16(buffer);
            var expectedSize = node.GetInputBytes().Length;
            if (inputSize != expectedSize)
            {
                Log(Debugger.DebugLevel.ERROR, $"[Nebula][InvalidInput] Input size {inputSize} for node {worldNetId} (staticChild={staticChildId}) does not match the expected {expectedSize}");
                return;
            }

            // Read input count (redundancy - multiple inputs per packet)
            var inputCount = NetReader.ReadByte(buffer);

            // Ticks are sent as one-byte offsets back from the newest.
            var baseTick = NetReader.ReadInt32(buffer);

            // Read each tick-tagged input and buffer it
            for (int i = 0; i < inputCount; i++)
            {
                var tick = baseTick - NetReader.ReadByte(buffer);
                var inputBytes = NetReader.ReadBytes(buffer, inputSize);

                // Clients run ahead of the server, so input ticks are legitimately in the future,
                // but only up to the ring-buffer depth (anything beyond aliases onto occupied
                // slots). Reject out-of-range ticks - a far-future/negative tick would otherwise
                // poison a ring slot (buffer.Ticks[slot] < tick) so real inputs are dropped forever.
                // Read fields first (above) so buffer alignment for later inputs is preserved.
                if (tick < 0 || tick > CurrentTick + SERVER_INPUT_BUFFER_SIZE)
                {
                    Log(Debugger.DebugLevel.ERROR, $"[Nebula][InvalidInput] Ignoring out-of-range input tick {tick} (currentTick {CurrentTick}) for node {worldNetId}");
                    continue;
                }

                // Buffer the input for this tick using composite key (parentNetId, staticChildId)
                BufferServerInput(new InputBufferKey(worldNetId, staticChildId), tick, inputBytes);

                // Also set as current input if this is the most recent tick we've seen
                if (tick > node.LastConfirmedTick)
                {
                    node.SetInputBytes(inputBytes);
                }
            }

            // Debug.Send("Input", $"Received {inputCount} inputs for node {worldNetId} (staticChild={staticChildId})");
        }

        // WARNING: These are not exactly tick-aligned for state reconcilliation. Could cause state issues because the assumed tick is when it is received?
        /// <summary>
        /// Sends a network function. On the server, <paramref name="targetPeers"/> (when non-null)
        /// restricts delivery to those specific peers instead of broadcasting to every interested peer
        /// — used by generated peer-targeted overloads. Peers that don't have the node (no interest)
        /// are skipped, since the peer-local netId wouldn't resolve on their client.
        ///
        /// <b>An RPC never leaves the world that raised it.</b> Recipients are resolved through THIS
        /// world's <see cref="PeerStates"/> rather than the global <c>NetRunner.Instance.Peers</c>
        /// registry, so a peer belonging to another world is not a candidate in the first place. That
        /// scoping is load-bearing, not defensive: a node's interest is seeded from the GLOBAL peer
        /// registry (<c>NetworkController._NetworkPrepare</c>), so with more than one world live -- an
        /// expedition alongside the hub -- every node's InterestLayers names peers that are elsewhere,
        /// and a peer mid-migration is briefly in no world at all. Addressing one of them is not merely
        /// wasteful: the recipient's peer-local node id is looked up in the world's own PeerState, which
        /// a foreign peer does not have, and <see cref="NetId.NetworkSerialize"/> has no graceful failure
        /// once it starts writing.
        /// </summary>
        internal void SendNetFunction(NetId netId, ProtocolNetFunction functionInfo, object[] args, UUID[] targetPeers = null)
        {
            if (NetRunner.Instance.IsServer)
            {
                var node = GetNodeFromNetId(netId);
                if (targetPeers == null)
                {
                    // Default: broadcast to this world's interested peers. Not logged when a peer is
                    // skipped -- interest legitimately names every peer on the server, so in a two-world
                    // session that would be a warning per bystander per RPC.
                    // TODO: Apply interest layers for network function, like network property
                    foreach (var peerId in node.InterestLayers.Keys)
                    {
                        if (PeerStates.TryGetValue(peerId, out var state))
                        {
                            SendNetFunctionToPeer(netId, functionInfo, args, state.Peer);
                        }
                    }
                }
                else
                {
                    // Peer-targeted: only the listed peers, and only those that actually have the node.
                    for (int i = 0; i < targetPeers.Length; i++)
                    {
                        var peerId = targetPeers[i];
                        if (!node.InterestLayers.ContainsKey(peerId))
                        {
                            Log(Debugger.DebugLevel.WARN, $"SendNetFunction: target peer {peerId} has no interest in node {netId} for {functionInfo.Name}; skipping (node not spawned for them).");
                            continue;
                        }
                        // Named explicitly by the caller, so being in another world IS worth reporting:
                        // it means the caller resolved a peer from outside this world's membership.
                        if (!PeerStates.TryGetValue(peerId, out var state))
                        {
                            Log(Debugger.DebugLevel.WARN, $"SendNetFunction: target peer {peerId} is not in world {WorldId} for {functionInfo.Name}; skipping (RPCs are world-scoped).");
                            continue;
                        }
                        SendNetFunctionToPeer(netId, functionInfo, args, state.Peer);
                    }
                }
            }
            else
            {
                // A client only ever sends to the server; targetPeers is meaningless here and ignored.
                SendNetFunctionToPeer(netId, functionInfo, args, NetRunner.Instance.ServerPeer);
            }
        }

        private void SendNetFunctionToPeer(NetId netId, ProtocolNetFunction functionInfo, object[] args, NetPeer peer)
        {
            using var buffer = new NetBuffer();
            NetId.NetworkSerialize(this, peer, netId, buffer);
            NetWriter.WriteByte(buffer, functionInfo.Index);
            for (int i = 0; i < args.Length; i++)
            {
                // Use protocol metadata directly, no Variant conversion. The subtype is not optional:
                // ReceiveNetFunction reads each argument at the width the subtype implies, so
                // omitting it here misaligns every argument after the first.
                var argInfo = functionInfo.Arguments[i];
                NetWriter.WriteByType(buffer, argInfo.VariantType, args[i], argInfo.Metadata.TypeIdentifier);
            }
            NetRunner.SendReliable(peer, (byte)NetRunner.ENetChannelId.Function, buffer);
        }

        internal void ReceiveNetFunction(NetPeer peer, NetBuffer buffer)
        {
            var netId = NetReader.ReadUInt16(buffer);
            var functionId = NetReader.ReadByte(buffer);
            var netController = NetRunner.Instance.IsServer ? GetPeerNode(peer, netId) : GetNodeFromNetId(netId);
            if (netController == null)
            {
                Log(Debugger.DebugLevel.ERROR, $"Received net function for unknown node {netId}");
                return;
            }
            _netFunctionArgsPool.Clear();
            var functionInfo = Protocol.UnpackFunction(netController.RawNode.SceneFilePath, functionId);
            for (int i = 0; i < functionInfo.Arguments.Length; i++)
            {
                var arg = functionInfo.Arguments[i];
                var cache = new PropertyCache { Type = arg.VariantType };
                NetReader.ReadAbsoluteValue(buffer, arg.VariantType, arg.Metadata.TypeIdentifier, ref cache);
                _netFunctionArgsPool.Add(cache);
            }
            if (NetRunner.Instance.IsServer && (functionInfo.Sources & NetworkSources.Client) == 0)
            {
                return;
            }
            if (NetRunner.Instance.IsClient && (functionInfo.Sources & NetworkSources.Server) == 0)
            {
                return;
            }
            // Note: ToArray() still allocates, but this is acceptable for RPCs which are infrequent
            queuedNetFunctions.Add(new QueuedFunction
            {
                Node = netController.RawNode,
                FunctionInfo = functionInfo,
                Args = _netFunctionArgsPool.ToArray(),
                Sender = peer
            });
        }
    }
}
