global using NetPeer = ENet.Peer;
global using Tick = System.Int32;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Nebula.Serialization;
using System;
using Nebula.Utility.Tools;
using Nebula.Authentication;
using ENet;

namespace Nebula
{
    /// <summary>
    /// The primary network manager for server and client. NetRunner handles the ENet stream and passing that data to the correct objects. For more information on what kind of data is sent and received on what channels, see <see cref="ENetChannelId"/>.
    /// </summary>
    public partial class NetRunner : Node
    {
        /// <summary>
        /// A fully qualified domain (www.example.com) or IP address (192.168.1.1) of the host. Used for client connections.
        /// Can be overridden via SERVER_ADDRESS environment variable or .env file.
        /// </summary>
        [Export] public string DefaultServerAddress = "127.0.0.1";

        /// <summary>
        /// Gets the server address, checking environment variable first, then falling back to DefaultServerAddress.
        /// </summary>
        public string ServerAddress
        {
            get
            {
                var envAddress = Env.Instance?.GetValue("SERVER_ADDRESS");
                return string.IsNullOrEmpty(envAddress) ? DefaultServerAddress : envAddress;
            }
        }

        /// <summary>
        /// The port for the server to listen on, and the client to connect to.
        /// </summary>
        [Export] public int Port { get; private set; } = 8888;

        /// <summary>
        /// Manually/dynamically override the port for the server to listen on, and the client to connect to.
        /// </summary>
        public void OverridePort(int port)
        {
            Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"Overriding port to {port}");
            Port = port;
        }

        /// <summary>
        /// The maximum number of allowed connections before the server starts rejecting clients.
        /// </summary>
        [Export] public int MaxPeers = 100;

        /// <summary>
        /// Maximum number of channels per connection.
        /// Must be at least 250 to support Blastoff admin channel (249).
        /// </summary>
        private const int MaxChannels = 251;

        public Dictionary<UUID, WorldRunner> Worlds { get; private set; } = [];
        internal Host ENetHost;
        internal Peer ServerPeer;

        internal Dictionary<UUID, NetPeer> Peers = [];
        internal Dictionary<uint, UUID> PeerIds = [];  // Key is peer.ID (ENet native ID)
        internal Dictionary<uint, NetPeer> PeersByNativeId = [];
        /// <summary>Which world each peer is currently in. Keyed by peer id.</summary>
        internal Dictionary<UUID, WorldRunner> PeerWorldMap = [];

        public NetPeer GetPeer(UUID id)
        {
            if (Peers.TryGetValue(id, out var peer))
            {
                return peer;
            }
            return default;
        }

        public UUID GetPeerId(NetPeer peer)
        {
            if (PeerIds.TryGetValue(peer.ID, out var id))
            {
                return id;
            }
            return default;
        }

        /// <summary>
        /// This is set after <see cref="StartClient"/> or <see cref="StartServer"/> is called, i.e. when <see cref="NetStarted"/> == true. Before that, this value is unreliable.
        /// </summary>
        internal bool IsServer { get; private set; }

        internal bool IsClient => !IsServer;

        /// <summary>
        /// This is set to true once <see cref="StartClient"/> or <see cref="StartServer"/> have succeeded.
        /// </summary>
        public bool NetStarted { get; private set; }

        /// <summary>
        /// Describes the channels of communication used by the network.
        /// </summary>
        public enum ENetChannelId
        {
            /// <summary>
            /// Tick data sent by the server to the client, and from the client indicating the most recent tick it has received.
            /// </summary>
            Tick = 1,

            /// <summary>
            /// Input data sent from the client.
            /// </summary>
            Input = 2,

            /// <summary>
            /// NetFunction call.
            /// </summary>
            Function = 3,

            /// <summary>
            /// World-transfer control (reliable). Server→client "change world" and client→server
            /// "ready" ack for live cross-world migration. Kept off the tick stream so it is
            /// guaranteed-delivered and never bundled with per-tick state.
            /// See <see cref="MigratePeerToWorld"/>.
            /// </summary>
            World = 4,
        }

        /// <summary>
        /// This is only used to prevent plugins from using reserved channels or reserving each other's channels.
        /// </summary>
        private Dictionary<int, Action<NetPeer, byte[]>> ReservedChannels = [];

        /// <summary>
        /// Reserve a channel for custom use, e.g. within plugins. If the channel is already reserved, it will throw an exception.
        /// The handler receives (NetPeer peer, byte[] packetData).
        /// </summary>
        public void ReserveChannel(int channel, Action<NetPeer, byte[]> handler)
        {
            if (Enum.IsDefined(typeof(ENetChannelId), channel))
            {
                throw new Exception($"Failure to register ENET channel {channel}: it is reserved by Nebula.");
            }
            if (ReservedChannels.ContainsKey(channel))
            {
                throw new Exception($"Failure to register ENET channel {channel}: it is already reserved.");
            }
            ReservedChannels[channel] = handler;
        }

        /// <summary>
        /// The singleton instance.
        /// </summary>
        public static NetRunner Instance { get; internal set; }

        private static bool _libraryInitialized = false;

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            if (Instance != null)
            {
                QueueFree();
                return;
            }
            Instance = this;

            // NetRunner is an autoload, so this is the earliest reliable point at which Godot's
            // main thread can be identified for NebulaThread's assertions.
            NebulaThread.CaptureMainThread();

            if (!_libraryInitialized)
            {
                try
                {
                    if (!Library.Initialize())
                    {
                        return;
                    }
                    _libraryInitialized = true;
                }
                catch (Exception e)
                {
                    return;
                }
            }
        }

        public override void _Ready()
        {
            _ = MTU;
            // Protocol is fully static - no initialization needed
            StartTelemetryHub();

            // No-op unless this process was launched with --bot. Created here rather than added as
            // an autoload so that adopting Nebula's bots costs a project no project.godot changes,
            // and so a normal client or server never carries the runtime at all.
            Bots.BotRunner.TryCreate(this);
        }

        public override void _ExitTree()
        {
            lock (EnetLock) { ENetHost?.Flush(); }
            ENetHost?.Dispose();
            DebugHub?.Stop();
            DebugHub = null;

            if (_libraryInitialized && Instance == this)
            {
                Library.Deinitialize();
                _libraryInitialized = false;
            }
        }

        /// <summary>
        /// Process-wide debug channel (see <see cref="Nebula.DebugHub"/>), or
        /// null when debugging wasn't requested. Every world's traffic is
        /// multiplexed over this one socket.
        /// </summary>
        public DebugHub DebugHub { get; private set; }

        /// <summary>
        /// The debug server project setting (<c>Nebula/config/debug/enable_debug_server</c>),
        /// default on. This is a master OFF switch, ANDed with <c>--debugPort=N</c>
        /// rather than replacing it: leaving it on never opens a port by itself, so a
        /// dedicated server still exposes nothing unless it was explicitly launched
        /// with a debug port. Turning it off makes the whole channel inert — no
        /// listener, no frames built, no per-tick work anywhere.
        ///
        /// Cached on first read, so toggling it takes effect on the next run.
        /// </summary>
        public static bool DebugServerEnabled =>
            _debugServerEnabled ??= ResolveDebugServerEnabled();

        private static bool? _debugServerEnabled;

        /// <summary>
        /// Environment/.env switch for <see cref="DebugServerEnabled"/>, checked before
        /// the project setting so a deployment can turn the channel on or off per process
        /// kind (<c>.env.server</c> vs <c>.env.client</c>) without editing project.godot.
        /// Off means fully inert: no listener, no frames built, no per-tick debug work.
        ///
        /// <para>OFF unless something says otherwise — <c>NEBULA_DEBUG=1</c>, or the project
        /// setting turned on deliberately. It used to default ON, which meant every process
        /// handed a <c>--debugPort</c> got a debug channel whether or not the project had
        /// ever opted in.</para>
        /// </summary>
        public const string DEBUG_SERVER_ENV_VAR = "NEBULA_DEBUG";

        private static bool ResolveDebugServerEnabled()
            => Env.TryGetFlag(DEBUG_SERVER_ENV_VAR, out bool fromEnv)
                ? fromEnv
                : ProjectSettings.GetSetting(DEBUG_SERVER_SETTING, false).AsBool();

        /// <summary>Project setting key for <see cref="DebugServerEnabled"/>.</summary>
        public const string DEBUG_SERVER_SETTING = "Nebula/config/debug/enable_debug_server";

        /// <summary>
        /// Opt-in via <c>--debugPort=N</c>. The editor's Play button assigns a port per
        /// launched instance and the integration harness passes it explicitly; the port
        /// is used verbatim, never fallen back.
        ///
        /// <para>The socket carries two independently switchable features, so it starts
        /// when EITHER wants it: the debugger (<see cref="DebugServerEnabled"/>) and
        /// metrics reporting (<see cref="Diagnostics.ServerMetrics.Enabled"/>). Turning
        /// the debugger off while metrics are on leaves the listener up but suppresses
        /// every debug-class frame — see <see cref="DebugHub.DebugFramesEnabled"/>.</para>
        ///
        /// Parsed here rather than in WorldRunner, where it used to live: that
        /// ran once per world, so multiple worlds fought over the same port.
        /// </summary>
        private void StartTelemetryHub()
        {
            bool debugFrames = DebugServerEnabled;
            if (!debugFrames && !Diagnostics.ServerMetrics.Enabled)
                return;

            int explicitPort = 0;
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (!argument.StartsWith("--debugPort="))
                    continue;
                if (int.TryParse(argument.Substring("--debugPort=".Length), out int parsedPort))
                    explicitPort = parsedPort;
                break;
            }

            if (explicitPort <= 0)
                return;

            var hub = new DebugHub { DebugFramesEnabled = debugFrames };
            if (hub.Start(explicitPort))
                DebugHub = hub;
        }

        /// <summary>
        /// Peers (by native ENet id) that connected before any world was ready to take them.
        /// Authenticated as soon as one goes Live; see <see cref="AuthenticateWaitingPeers"/>.
        /// </summary>
        private readonly List<uint> _peersAwaitingWorld = [];
        private readonly List<uint> _waitingPeerScratch = [];

        /// <summary>
        /// The first world that is built and ready for peers, or null if none is yet.
        ///
        /// Prefer this over indexing <see cref="Worlds"/> directly: a world is registered from the
        /// moment its creation starts, so the registry can contain worlds that are still
        /// generating and must not be joined.
        /// </summary>
        public WorldRunner FirstLiveWorld()
        {
            // Enumerated as key-value pairs rather than through .Values: enumeration is a struct
            // enumerator (allocation-free) either way, but touching .Values would also materialize
            // the dictionary's cached ValueCollection wrapper on first use.
            foreach (var pair in Worlds)
            {
                var world = pair.Value;
                if (world != null && world.Lifecycle == WorldRunner.WorldLifecycle.Live)
                {
                    return world;
                }
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="worldId"/> names a world that exists <em>and</em> is ready for
        /// peers. Registered-but-generating worlds return false.
        /// </summary>
        public bool IsWorldLive(UUID worldId) =>
            Worlds.TryGetValue(worldId, out var world)
            && world != null
            && world.Lifecycle == WorldRunner.WorldLifecycle.Live;

        /// <summary>
        /// Authenticates everything that connected while the server had no world yet.
        ///
        /// Called when a world goes Live. World creation is asynchronous, so there is a real window
        /// between the socket opening and the first world being ready -- clients that connect in it
        /// wait here rather than being rejected, because from their side the server is simply still
        /// starting up.
        /// </summary>
        private void AuthenticateWaitingPeers()
        {
            if (_peersAwaitingWorld.Count == 0 || Authentication == null) return;

            // Drained into scratch first: authenticating a peer can re-enter this list (a rejected
            // peer disconnecting, say), and mutating it mid-iteration would throw.
            _waitingPeerScratch.Clear();
            _waitingPeerScratch.AddRange(_peersAwaitingWorld);
            _peersAwaitingWorld.Clear();

            Debugger.Instance.Log($"World ready; authenticating {_waitingPeerScratch.Count} peer(s) that connected while it was still generating.");

            foreach (var nativeId in _waitingPeerScratch)
            {
                var peer = GetPeerByNativeId(nativeId);
                if (peer.IsSet)
                {
                    Authentication.ServerAuthenticateClient(peer);
                }
            }
        }

        public IAuthenticator Authentication { get; private set; }

        public void SetAuthentication(IAuthenticator authentication)
        {
            if (Authentication != null)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Setting authentication on NetRunner after it was already set. This is only a bug if it was unintentional.");
            }
            OnPeerConnected += (uint peerId) =>
            {
                var peer = GetPeerByNativeId(peerId);
                if (!peer.IsSet) return;

                // Authenticators put peers into worlds, so there has to be a world to put them in.
                // Since world creation is asynchronous the socket can be open before the first one
                // is ready, and a client launched alongside the server routinely lands in that
                // window. Hold the peer rather than failing it -- AuthenticateWaitingPeers picks it
                // up the moment a world goes Live.
                if (FirstLiveWorld() == null)
                {
                    _peersAwaitingWorld.Add(peerId);
                    Debugger.Instance.Log($"Peer {peerId} connected before any world was ready; holding until one is.");
                    return;
                }

                Authentication.ServerAuthenticateClient(peer);
            };
            OnConnectedToServer += () =>
            {
                Authentication.ClientAuthenticateWithServer();
            };
            Authentication = authentication;
        }

        public void StartServer()
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            if (Authentication == null)
            {
                SetAuthentication(new DefaultAuthenticator());
            }

            IsServer = true;
            Debugger.Instance.Log("Starting Server");
            GetTree().MultiplayerPoll = false;

            ENetHost = new Host();
            var address = new Address();
            // Note: For server, only set Port. Do NOT call SetHost - this binds to all interfaces (0.0.0.0)
            address.Port = (ushort)Port;

            try
            {
                ENetHost.Create(address, MaxPeers, MaxChannels);
                // Note: ENet-CSharp doesn't have built-in compression like Godot's ENET wrapper
            }
            catch (Exception ex)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Error starting: {ex.Message}");
                return;
            }

            NetStarted = true;
            Debugger.Instance.Log($"Started on port {Port}");

            // A run under synthetic impairment must SAY SO. It is off by default and applies per
            // process, so without this line a log is indistinguishable from a healthy one and no
            // measurement taken from it can be attributed to anything.
            if (Impairment.IsActive)
            {
                Debugger.Instance.Log($"Network impairment ACTIVE: {Impairment}");
            }

            // The debug channel is not started here: it is process-wide (see
            // StartTelemetryHub) so that clients get one too, and so it is already
            // listening before the network starts.
        }

        public void StartClient()
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

            if (Authentication == null)
            {
                SetAuthentication(new DefaultAuthenticator());
            }

            ENetHost = new Host();
            ENetHost.Create();

            var address = new Address();
            address.SetHost(ServerAddress);
            address.Port = (ushort)Port;

            // The connect packet carries our protocol hash; the server validates it before
            // admitting the peer and rejects mismatched builds (see ProtocolMismatchException)
            ServerPeer = ENetHost.Connect(address, MaxChannels, Protocol.HandshakeHash);

            if (!ServerPeer.IsSet)
            {
                Debugger.Instance.Log($"Error connecting.");
                return;
            }

            NetStarted = true;
            // The client's single WorldRunner is a receiver, not something that gets generated: its
            // contents arrive over the spawn stream. Lifecycle only gates server-side peer
            // admission, but leaving it at the Generating default would be misleading here.
            var worldRunner = new WorldRunner { Lifecycle = WorldRunner.WorldLifecycle.Live };
            WorldRunner.CurrentWorld = worldRunner;
            GetTree().CurrentScene.AddChild(worldRunner);
            Debugger.Instance.Log("Started");

            // A run under synthetic impairment must SAY SO. It is off by default and applies per
            // process, so without this line a log is indistinguishable from a healthy one and no
            // measurement taken from it can be attributed to anything.
            if (Impairment.IsActive)
            {
                Debugger.Instance.Log($"Network impairment ACTIVE: {Impairment}");
            }
        }

        /// <summary>
        /// How many physics ticks elapse per network tick. Derived from the
        /// <c>Nebula/config/network/ticks_per_second</c> project setting (default 30): the
        /// network tick fires on whole physics frames, so the divisor is the physics rate
        /// over the requested rate, rounded to the nearest whole frame count. When the
        /// requested rate does not divide the physics rate evenly, the nearest achievable
        /// rate is used and a warning names it.
        ///
        /// Server and client must agree; both read the same project.godot, so this holds as
        /// long as builds ship the same settings. Cached on first read - checked every
        /// physics frame in WorldRunner, so it must not hit ProjectSettings per frame -
        /// meaning changes take effect on the next run.
        /// </summary>
        private static int? _physicsTicksPerNetworkTick;
        public static int PhysicsTicksPerNetworkTick
        {
            get
            {
                if (_physicsTicksPerNetworkTick == null)
                {
                    int physicsRate = Engine.PhysicsTicksPerSecond;
                    int requested = Math.Max(1,
                        ProjectSettings.GetSetting("Nebula/config/network/ticks_per_second", 30).AsInt32());
                    int divisor = Math.Max(1, (int)Math.Round(physicsRate / (double)requested));
                    int actual = physicsRate / divisor;
                    if (actual != requested)
                    {
                        Debugger.Instance?.Log(
                            $"Nebula/config/network/ticks_per_second={requested} does not divide the physics rate ({physicsRate}); running at {actual} TPS (one network tick every {divisor} physics ticks).",
                            Debugger.DebugLevel.WARN);
                    }
                    _physicsTicksPerNetworkTick = divisor;
                }
                return _physicsTicksPerNetworkTick.Value;
            }
        }

        /// <summary>
        /// Ticks Per Second: the ACHIEVED network tick rate - engine physics rate divided by
        /// <see cref="PhysicsTicksPerNetworkTick"/>. Equals the configured
        /// <c>Nebula/config/network/ticks_per_second</c> whenever that divides the physics
        /// rate evenly.
        /// </summary>
        private static int? _tps;
        public static int TPS
        {
            get
            {
                _tps ??= Engine.PhysicsTicksPerSecond / PhysicsTicksPerNetworkTick;
                return _tps.Value;
            }
        }

        /// <summary>
        /// Maximum Transferrable Unit. The maximum number of bytes that should be sent in a single ENet UDP Packet (i.e. a single tick)
        /// Not a hard limit.
        /// </summary>
        public const string MTU_SETTING = "Nebula/config/network/mtu";
        public const int DefaultMTU = 1400;
        private static int _mtu;
        public static int MTU
        {
            get
            {
                var cached = _mtu;
                if (cached != 0) return cached;
                return _mtu = ProjectSettings.GetSetting(MTU_SETTING, DefaultMTU).AsInt32();
            }
        }

        public const string ACK_TIMEOUT_SETTING = "Nebula/config/network/ack_timeout_seconds";
        public const string JOIN_ACK_TIMEOUT_SETTING = "Nebula/config/network/join_ack_timeout_seconds";
        public const float DefaultAckTimeoutSeconds = 5.0f;
        public const float DefaultJoinAckTimeoutSeconds = 30.0f;

        /// <summary>
        /// Seconds an IN-WORLD peer may go without acknowledging a tick before the server
        /// force-disconnects it. A healthy peer acks continuously (piggybacked on input or
        /// standalone), so this is a pure liveness cutoff.
        /// </summary>
        public static float AckTimeoutSeconds =>
            (float)ProjectSettings.GetSetting(ACK_TIMEOUT_SETTING, DefaultAckTimeoutSeconds).AsDouble();

        /// <summary>
        /// Seconds a JOINING peer (INITIAL status - never acked yet) gets before the same
        /// cutoff. A client's first ack only follows a successfully imported tick, which is
        /// after process boot, world-scene load, and spatial-mirror build - easily past the
        /// in-world cutoff on a loaded machine, so joins need their own, longer window.
        /// </summary>
        public static float JoinAckTimeoutSeconds =>
            (float)ProjectSettings.GetSetting(JOIN_ACK_TIMEOUT_SETTING, DefaultJoinAckTimeoutSeconds).AsDouble();

        /// <summary>Tick-channel packet header: the int32 tick number.</summary>
        private const int TickHeaderBytes = sizeof(int);

        /// <summary>
        /// Headroom subtracted from the tick payload budget to absorb small framing
        /// variations (future flags).
        /// </summary>
        public const int TickBudgetHeadroom = 16;

        /// <summary>
        /// Per-peer byte budget for one tick's serialized payload (the ExportState output),
        /// derived from the MTU: MTU minus the tick header and <see cref="TickBudgetHeadroom"/>.
        /// The export path keeps every peer payload within this so the packet never exceeds
        /// the MTU. A tick packet is [tick:int32][payload]; there is no compression layer, the
        /// payload is bit-packed by the serializers.
        /// </summary>
        public static int TickPayloadBudget(int mtu)
            => mtu - TickHeaderBytes - TickBudgetHeadroom;

        private static bool? _logTickPayloads;
        /// <summary>
        /// Debug: when enabled via the <c>Nebula/config/debug/log_tick_payloads</c> project setting, the
        /// client logs the full hex of every server tick payload. Cached on first read, so toggling
        /// it takes effect on the next run.
        /// </summary>
        private static int? _debugExportInterval;
        /// <summary>
        /// How many network ticks between full world-state exports on the debug
        /// channel (<c>Nebula/config/debug/export_interval</c>). 1 = every tick.
        /// Raising it is close to free for the editor, which carries the last
        /// known state forward between exports. Cached on first read.
        /// </summary>
        public static int DebugExportInterval =>
            _debugExportInterval ??= Math.Max(1,
                ProjectSettings.GetSetting("Nebula/config/debug/export_interval", 1).AsInt32());

        public static bool LogTickPayloads =>
            _logTickPayloads ??= ProjectSettings.GetSetting("Nebula/config/debug/log_tick_payloads", false).AsBool();

        private static Diagnostics.NetworkImpairment _impairment;

        /// <summary>
        /// Synthetic network impairment applied to INBOUND packets -- added latency, jitter and loss.
        ///
        /// <para>Exists because everything about the render clock and the interpolation buffer was
        /// developed on localhost, where jitter is small and loss is zero. A jitter buffer that has
        /// never seen jitter is a guess. Configured per process (command line first, so one play
        /// session can run a healthy client beside a bad one), inert when unset, and superseding the
        /// older <c>simulate_incoming_tick_loss</c> setting, which still feeds its loss knob.</para>
        /// </summary>
        public static Diagnostics.NetworkImpairment Impairment =>
            _impairment ??= Diagnostics.NetworkImpairment.FromProcessConfig();

        private static bool? _traceSpawnIds;
        /// <summary>
        /// Debug: arms the server-side spawn-contract breach detector in ExportState
        /// ("a packet may carry data without spawn bytes only for a node whose id the
        /// client provably has or gets") - the export-side counterpart of the client's
        /// "[ImportState] Data for unknown node" tripwire, catching the leak at the source
        /// with the node named. Enable via the <c>NEBULA_TRACE_SPAWN_IDS</c> environment
        /// variable or <c>Nebula/config/debug/trace_spawn_ids</c>. Allocates log strings
        /// when a breach fires; default off. Cached on first read.
        /// </summary>
        public static bool TraceSpawnIds =>
            _traceSpawnIds ??= OS.HasEnvironment("NEBULA_TRACE_SPAWN_IDS")
                || ProjectSettings.GetSetting("Nebula/config/debug/trace_spawn_ids", false).AsBool();

        private static bool? _perWorldThreadGroup;
        /// <summary>
        /// When enabled via <c>Nebula/config/threading/per_world_thread_group</c>, each server
        /// world's SubViewport gets its own <see cref="Node.ProcessThreadGroupEnum.SubThread"/>
        /// group, so worlds run their ticks concurrently instead of being walked one after another
        /// by the SceneTree on the main thread.
        ///
        /// What this does and does not do is worth being precise about: Godot's thread groups move
        /// <c>_process</c>/<c>_physics_process</c> *callbacks* off the main thread. They do not
        /// parallelize physics simulation -- PhysicsServer3D steps every active space sequentially
        /// regardless of which thread requested it, so per-world World3Ds still simulate serially.
        /// The win is ServerProcessTick (state serialization dominates) and gameplay scripts.
        ///
        /// Off by default. Everything the flag depends on is written to be correct in both modes --
        /// an uncontended lock is nearly free and the inbound queue preserves ordering either way --
        /// so flipping it changes timing, never behavior. Cached on first read.
        /// </summary>
        public static bool PerWorldThreadGroup =>
            _perWorldThreadGroup ??= ProjectSettings.GetSetting("Nebula/config/threading/per_world_thread_group", false).AsBool();

        /// <summary>
        /// Serializes every touch of the shared ENet <see cref="Host"/>.
        ///
        /// One host serves all worlds, but sends originate from inside each world's tick
        /// (WorldRunner.ExportState, net function dispatch) while the event pump services the same
        /// host from the main thread. ENet is not thread-safe, so once worlds tick concurrently
        /// those overlap.
        ///
        /// Held as briefly as possible -- around the ENet call itself and nothing else. In
        /// particular the pump takes it to pull an event and releases it before dispatching, because
        /// dispatch re-enters world code that sends, and holding it across that would deadlock.
        /// </summary>
        internal static readonly object EnetLock = new();

        /// <summary>
        /// Reusable wrapper for parsing inbound packets in place on the client. Re-pointed at each
        /// rented payload via <see cref="NetBuffer.Attach"/> -- one long-lived object instead of a
        /// NetBuffer allocation per packet. Only ever touched by the pump (main thread), and every
        /// consumer copies what it keeps, so re-attaching for the next packet is safe.
        /// </summary>
        private readonly NetBuffer _inboundParseBuffer = new(System.Array.Empty<byte>());

        /// <summary>
        /// Work deferred from a world thread to the main thread, drained at the top of
        /// <see cref="_PhysicsProcess"/>.
        ///
        /// This is for rare lifecycle events -- peer join/leave mutating the shared registries,
        /// world creation touching the SceneTree -- NOT for anything on the per-tick path. Each
        /// entry allocates a closure, which is fine at join/leave frequency and would not be inside
        /// ExportState. Callers already on the main thread run inline and never queue.
        /// </summary>
        private readonly Queue<MainThreadItem> _mainThreadWork = new();
        private readonly object _mainThreadWorkLock = new();

        /// <summary>
        /// One queued job, in whichever of the two forms the caller had.
        ///
        /// A struct so the queue itself never allocates per item, and ONE queue rather than two so
        /// both forms keep a single, obvious execution order -- a peer join queued as a closure and a
        /// node's state change queued as an interface still run in the order they were asked for.
        /// </summary>
        private readonly struct MainThreadItem
        {
            private readonly Action _action;
            private readonly IMainThreadWork _work;
            private readonly int _tag;

            internal MainThreadItem(Action action) { _action = action; _work = null; _tag = 0; }
            internal MainThreadItem(IMainThreadWork work, int tag) { _action = null; _work = work; _tag = tag; }

            internal void Run()
            {
                if (_action != null) _action();
                else _work?.OnMainThread(_tag);
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread: inline if already there, otherwise
        /// queued for the next <see cref="_PhysicsProcess"/>.
        ///
        /// Allocates a delegate (and a display class, if the lambda captures) per call, which is why
        /// this form is for one-off lifecycle events. A caller that defers REPEATEDLY should implement
        /// <see cref="IMainThreadWork"/> and use the overload below instead.
        ///
        /// This overload cannot go away even so: <c>INotifyCompletion.OnCompleted</c> hands its
        /// continuation over as an <see cref="Action"/>, so <see cref="SwitchToMainThread"/> needs it.
        /// </summary>
        internal void RunOnMainThread(Action work)
        {
            if (NebulaThread.IsMain)
            {
                work();
                return;
            }
            Enqueue(new MainThreadItem(work));
        }

        /// <summary>
        /// The same hop, with NOTHING allocated.
        ///
        /// The caller is the work: a long-lived object implements <see cref="IMainThreadWork"/> and
        /// hands over <c>this</c>, so there is no delegate to construct and no closure to capture. Use
        /// it wherever the deferral happens often enough that a per-call allocation would show up --
        /// anything reached from a world tick rather than from a join or a world creation.
        /// </summary>
        /// <param name="tag">Which job, for an object that defers more than one kind of work. Passed
        /// straight back, so the callee needs no state to tell them apart.</param>
        internal void RunOnMainThread(IMainThreadWork work, int tag = 0)
        {
            if (work == null) return;

            if (NebulaThread.IsMain)
            {
                work.OnMainThread(tag);
                return;
            }
            Enqueue(new MainThreadItem(work, tag));
        }

        private void Enqueue(in MainThreadItem item)
        {
            lock (_mainThreadWorkLock)
            {
                _mainThreadWork.Enqueue(item);
            }
        }

        /// <summary>
        /// Awaitable hop to the main thread. Completes inline when already there, so awaiting it
        /// from the main thread costs nothing and never defers by a frame.
        ///
        /// A custom awaitable rather than a TaskCompletionSource, deliberately: a task only
        /// signals completion -- WHERE the awaiter resumes is decided by the scheduler it captured
        /// at the await, and a world tick thread has no SynchronizationContext, so a TCS completed
        /// from main would resume the caller on the ThreadPool. This awaitable hands the
        /// continuation itself to <see cref="RunOnMainThread"/>, so resuming on main is structural
        /// rather than a scheduling accident.
        ///
        /// Used by world creation, which may be initiated from a world tick thread (a
        /// [NetFunction] is dispatched from inside ServerProcessTick) but has to do its SceneTree
        /// work on the main thread.
        /// </summary>
        internal MainThreadAwaitable SwitchToMainThread() => new(this);

        /// <summary>See <see cref="SwitchToMainThread"/>.</summary>
        internal readonly struct MainThreadAwaitable
        {
            private readonly NetRunner _runner;
            internal MainThreadAwaitable(NetRunner runner) { _runner = runner; }
            public Awaiter GetAwaiter() => new(_runner);

            internal readonly struct Awaiter : INotifyCompletion
            {
                private readonly NetRunner _runner;
                internal Awaiter(NetRunner runner) { _runner = runner; }

                /// <summary>Already on main: the await is a no-op and never defers a frame.</summary>
                public bool IsCompleted => NebulaThread.IsMain;

                /// <summary>
                /// Only reached off-main (IsCompleted short-circuits otherwise), so this always
                /// enqueues: the continuation runs during the next <see cref="DrainMainThreadWork"/>.
                /// </summary>
                public void OnCompleted(Action continuation) => _runner.RunOnMainThread(continuation);

                public void GetResult()
                {
                    // Runs as the resumed continuation's first act. Anything but main here means
                    // the marshal itself is broken -- fail loudly at the hop, not at whatever
                    // SceneTree call would have corrupted state three frames later.
                    NebulaThread.AssertMain(nameof(SwitchToMainThread));
                }
            }
        }

        /// <summary>
        /// Drains <see cref="_mainThreadWork"/>. Each item runs outside the lock so that work which
        /// queues more work cannot deadlock, and the drain is bounded to the items present on entry
        /// so a self-requeueing item cannot spin the frame.
        /// </summary>
        private void DrainMainThreadWork()
        {
            int pending;
            lock (_mainThreadWorkLock)
            {
                pending = _mainThreadWork.Count;
            }

            // H8/H1: everything marshalled to main runs HERE, in one frame, however much of it
            // piled up. A batch of joins each awaiting SwitchToMainThread lands as one long drain,
            // and on a server that also stops the SceneTree walking - which is what dispatches the
            // world's SubThread process group. A blocked main thread therefore STARVES the tick
            // rather than slowing it, which no "ServerProcessTick took Xms" guard can see.
            int drained = pending;
            long drainTs = pending > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            while (pending-- > 0)
            {
                MainThreadItem work;
                lock (_mainThreadWorkLock)
                {
                    if (!_mainThreadWork.TryDequeue(out work)) return;
                }

                try
                {
                    work.Run();
                }
                catch (Exception ex)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"[Nebula] Deferred main-thread work threw: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (drainTs != 0)
            {
                var drainMs = (System.Diagnostics.Stopwatch.GetTimestamp() - drainTs)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (drainMs >= Diagnostics.MainThreadWork.ReportThresholdMs)
                {
                    int stillQueued;
                    lock (_mainThreadWorkLock) stillQueued = _mainThreadWork.Count;
                    Debugger.Instance.Log(
                        $"[MainThreadDrain] ran {drained} queued item(s) in {drainMs:F0} ms "
                        + $"({stillQueued} still queued). Nothing else ran on main during this, "
                        + "including the SceneTree walk that dispatches world tick groups.",
                        Debugger.DebugLevel.WARN);
                }
            }
        }

        /// <summary>
        /// Accepts debug clients. Runs in _Process rather than _PhysicsProcess
        /// because _PhysicsProcess bails out until the network has started, and
        /// both the editor and the test harness attach before that.
        /// </summary>
        public override void _Process(double delta)
        {
            DebugHub?.Poll();
        }

        public event Action<uint> OnPeerConnected;

        public event Action<uint> OnPeerDisconnected;

        public event Action OnConnectedToServer;

        /// <summary>
        /// ENet disconnect reason code the server sends when rejecting a client whose
        /// protocol hash doesn't match ("PROT" in ASCII). Clients receiving this raise
        /// <see cref="OnProtocolMismatch"/> (or throw <see cref="ProtocolMismatchException"/>
        /// if no handler is subscribed).
        /// </summary>
        public const uint ProtocolMismatchDisconnectCode = 0x50524F54;

        /// <summary>
        /// ENet disconnect reason code the server sends when a peer's packet fails to
        /// deserialize ("MALP" in ASCII). A protocol-compliant client should never produce an
        /// unparseable packet post-handshake, so we treat it as hostile/broken and drop the peer.
        /// </summary>
        public const uint MalformedPacketDisconnectCode = 0x4D414C50;

        /// <summary>
        /// Client-side. Raised when the server rejects the connection due to a protocol
        /// hash mismatch. Subscribe to handle it gracefully (e.g. an "update required"
        /// screen); with no subscribers, the exception is thrown from the event pump.
        /// </summary>
        public event Action<ProtocolMismatchException> OnProtocolMismatch;

        /// <summary>
        /// Get a peer by its native ENet ID (used for signal handling).
        /// </summary>
        public NetPeer GetPeerByNativeId(uint nativeId)
        {
            if (PeersByNativeId.TryGetValue(nativeId, out var peer))
            {
                return peer;
            }
            return default;
        }

        /// <summary>
        /// Applies <see cref="Impairment"/> to an outgoing packet.
        ///
        /// <para>Deliberately at the LAST possible moment, after the caller has finished building the
        /// packet and mutating whatever state that took. A packet is lost on the wire, not un-sent:
        /// an input packet that has already consumed the pending tick ack must lose that ack too, or
        /// the simulation quietly tests a failure mode the network cannot produce.</para>
        ///
        /// <para>Egress covers what ingress structurally cannot. Impairment is per process so one bad
        /// client can run beside a healthy one, which leaves the SERVER unimpaired -- so without this
        /// a client kept delivering flawless input straight through a "100% loss" outage.</para>
        /// </summary>
        private static bool TrySendOutbound(byte channelId)
        {
            if (!Impairment.IsActive) return true;
            return Impairment.TrySendOutbound(channelId, Godot.Time.GetTicksMsec());
        }

        /// <summary>
        /// Applies <see cref="Impairment"/> to an inbound packet destined for a world's queue.
        /// Returns false when the packet is dropped; otherwise <paramref name="releaseAtMsec"/> is
        /// when the world may apply it (0 = immediately, the unimpaired path).
        /// </summary>
        private static bool TryScheduleInbound(byte channel, out ulong releaseAtMsec)
        {
            releaseAtMsec = 0;
            if (!Impairment.IsActive) return true;

            var now = Time.GetTicksMsec();
            if (!Impairment.TryScheduleInbound(channel, now, out var releaseAt)) return false;
            if (releaseAt > now) releaseAtMsec = releaseAt;
            return true;
        }

        // ------------------------------------------------------- client-side delayed tick delivery

        /// <summary>
        /// A server tick held back by synthetic impairment.
        ///
        /// <para>The client parses ticks inline in the pump and has no inbound queue of its own -- the
        /// server's ring cannot be reused here because it is per world and applies server-side
        /// channels. This is the smallest thing that can hold a tick: the payload is already a
        /// materialized copy (<c>NetReader.ReadRemainingBytes</c>), so nothing pooled is pinned.</para>
        /// </summary>
        private readonly struct DelayedClientTick
        {
            public readonly ulong ReleaseAtMsec;
            public readonly int Tick;
            public readonly byte[] Payload;

            public DelayedClientTick(ulong releaseAtMsec, int tick, byte[] payload)
            {
                ReleaseAtMsec = releaseAtMsec;
                Tick = tick;
                Payload = payload;
            }
        }

        private readonly List<DelayedClientTick> _delayedClientTicks = new();

        private void EnqueueDelayedClientTick(ulong releaseAtMsec, int tick, byte[] payload)
            => _delayedClientTicks.Add(new DelayedClientTick(releaseAtMsec, tick, payload));

        /// <summary>
        /// Delivers held ticks that have come due.
        ///
        /// <para>Released in RELEASE order, not arrival order, which is the point: jitter is what
        /// produces reordering on a real link. On the tick channel a reordered-late tick is then
        /// discarded by ClientProcessTick's "ignore ticks at or behind the current one" guard, so it
        /// presents as loss -- again, as in production.</para>
        /// </summary>
        private void DrainDelayedClientTicks()
        {
            if (_delayedClientTicks.Count == 0) return;

            var now = Time.GetTicksMsec();
            for (int i = _delayedClientTicks.Count - 1; i >= 0; i--)
            {
                var held = _delayedClientTicks[i];
                if (held.ReleaseAtMsec > now) continue;

                _delayedClientTicks.RemoveAt(i);
                WorldRunner.CurrentWorld?.ClientProcessTick(held.Tick, held.Payload);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            // Before anything else, and regardless of whether the network is up: work deferred from
            // world threads (peer registry mutations, world creation) is owed a main-thread turn.
            DrainMainThreadWork();
            if (IsClient && Impairment.IsActive) DrainDelayedClientTicks();

            if (!NetStarted)
                return;

            Event netEvent;
            int checkResult;
            int serviceResult = 0;

            // Pull an event under the lock, then release it before dispatching -- dispatch re-enters
            // world code that sends on this same host.
            lock (EnetLock)
            {
                checkResult = ENetHost.CheckEvents(out netEvent);
                if (checkResult <= 0)
                {
                    serviceResult = ENetHost.Service(0, out netEvent);
                }
            }

            while (checkResult > 0 || serviceResult > 0)
            {
                switch (netEvent.Type)
                {
                    case EventType.None:
                        return;

                    case EventType.Connect:
                        if (IsServer)
                        {
                            // Protocol handshake: the connect packet's data field carries the
                            // client's protocol hash. Reject mismatched builds before auth or
                            // world admission - a mismatched client would misparse everything.
                            if (netEvent.Data != Protocol.HandshakeHash)
                            {
                                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                                    $"Rejecting peer {netEvent.Peer.ID}: protocol hash mismatch (server 0x{Protocol.HandshakeHash:X8}, client 0x{netEvent.Data:X8}). Client is running a different build.");
                                DisconnectPeer(netEvent.Peer, ProtocolMismatchDisconnectCode);
                                break;
                            }

                            Debugger.Instance.Log("Peer connected");
                            PeersByNativeId[netEvent.Peer.ID] = netEvent.Peer;
                            OnPeerConnected?.Invoke(netEvent.Peer.ID);
                        }
                        else
                        {
                            Debugger.Instance.Log("Connected to server");
                            OnConnectedToServer?.Invoke();
                        }
                        break;

                    case EventType.Disconnect:
                    case EventType.Timeout:
                        if (!IsServer
                            && netEvent.Type == EventType.Disconnect
                            && netEvent.Data == ProtocolMismatchDisconnectCode)
                        {
                            _OnPeerDisconnected(netEvent.Peer);

                            var mismatch = new ProtocolMismatchException(Protocol.Hash, Protocol.HandshakeHash);
                            Debugger.Instance.Log(mismatch.Message, Debugger.DebugLevel.ERROR);
                            if (OnProtocolMismatch != null)
                            {
                                OnProtocolMismatch.Invoke(mismatch);
                                break;
                            }
                            throw mismatch;
                        }
                        _OnPeerDisconnected(netEvent.Peer);
                        break;

                    case EventType.Receive:
                    {
                        var channel = netEvent.ChannelID;
                        var packetLength = netEvent.Packet.Length;

                        // The per-tick channels (acks, inputs, functions) arrive every network tick
                        // from every peer, so their payloads rent from the shared pool instead of
                        // allocating -- a fresh byte[] per packet was steady-state garbage. The rare
                        // channels (World handoffs, reserved custom handlers) keep exact-size
                        // allocations, because their consumers receive a bare byte[] and rely on
                        // its .Length.
                        bool pooledPayload = channel == (byte)ENetChannelId.Tick
                            || channel == (byte)ENetChannelId.Input
                            || channel == (byte)ENetChannelId.Function;
                        var packetData = pooledPayload
                            ? ArrayPool<byte>.Shared.Rent(packetLength)
                            : new byte[packetLength];
                        // Packet.CopyTo copies exactly packet-Length bytes, so an oversized rented
                        // array is fine; only [0, packetLength) is ever read downstream.
                        netEvent.Packet.CopyTo(packetData);
                        netEvent.Packet.Dispose();

                        // A rented payload is owned by whoever consumes it: EnqueueInboundPacket
                        // takes ownership (the world's drain returns it to the pool), everything
                        // else parses inline here and the finally below returns it.
                        bool payloadHandedOff = false;

                        // A malformed packet must never abort the event pump: an unhandled
                        // exception here would drop every remaining queued event this frame for
                        // ALL peers. Catch per-packet so one bad sender can't stall everyone.
                        try
                        {
                        switch ((ENetChannelId)channel)
                        {
                            case ENetChannelId.Tick:
                                if (IsServer)
                                {
                                    // Queued, not applied: this world may be mid-tick on its own
                                    // thread. See WorldRunner.EnqueueInboundPacket.
                                    var peerId = GetPeerId(netEvent.Peer);
                                    if (PeerWorldMap.TryGetValue(peerId, out var world)
                                        && TryScheduleInbound(channel, out var ackReleaseAt))
                                    {
                                        world.EnqueueInboundPacket(
                                            netEvent.Peer, channel, packetData, packetLength, ackReleaseAt);
                                        payloadHandedOff = true;
                                    }
                                }
                                else
                                {
                                    if (packetLength == 0)
                                    {
                                        break;
                                    }
                                    _inboundParseBuffer.Attach(packetData, packetLength);
                                    var tick = NetReader.ReadInt32(_inboundParseBuffer);
                                    var bytes = NetReader.ReadRemainingBytes(_inboundParseBuffer);
                                    // Debug: dump the full payload hex for every server tick
                                    // (gated behind the Nebula/config/debug/log_tick_payloads setting).
                                    if (LogTickPayloads)
                                    {
                                        Debugger.Instance.Log(Debugger.DebugLevel.INFO,
                                            $"[Nebula][TickPayload] tick={tick} ({bytes.Length} bytes) {Convert.ToHexString(bytes)}");
                                    }
                                    // Synthetic impairment: drop, hold, or pass through. `bytes` is
                                    // already a materialized copy, so holding it is safe and the
                                    // rented packetData still returns to the pool below.
                                    if (Impairment.IsActive)
                                    {
                                        if (!Impairment.TryScheduleInbound(
                                                channel, Time.GetTicksMsec(), out var releaseAt))
                                        {
                                            break;
                                        }
                                        if (releaseAt > Time.GetTicksMsec())
                                        {
                                            EnqueueDelayedClientTick(releaseAt, tick, bytes);
                                            break;
                                        }
                                    }
                                    WorldRunner.CurrentWorld.ClientProcessTick(tick, bytes);
                                }
                                break;

                            case ENetChannelId.Input:
                                if (IsServer)
                                {
                                    var peerId = GetPeerId(netEvent.Peer);
                                    if (PeerWorldMap.TryGetValue(peerId, out var world))
                                    {
                                        if (TryScheduleInbound(channel, out var inputReleaseAt))
                                        {
                                            world.EnqueueInboundPacket(
                                                netEvent.Peer, channel, packetData, packetLength, inputReleaseAt);
                                            payloadHandedOff = true;
                                        }
                                    }
                                }
                                // Clients should never receive messages on the Input channel
                                break;

                            case ENetChannelId.Function:
                                if (IsServer)
                                {
                                    var peerId = GetPeerId(netEvent.Peer);
                                    if (PeerWorldMap.TryGetValue(peerId, out var world))
                                    {
                                        if (TryScheduleInbound(channel, out var fnReleaseAt))
                                        {
                                            world.EnqueueInboundPacket(
                                                netEvent.Peer, channel, packetData, packetLength, fnReleaseAt);
                                            payloadHandedOff = true;
                                        }
                                    }
                                }
                                else
                                {
                                    _inboundParseBuffer.Attach(packetData, packetLength);
                                    WorldRunner.CurrentWorld.ReceiveNetFunction(ServerPeer, _inboundParseBuffer);
                                }
                                break;

                            case ENetChannelId.World:
                                HandleWorldChannel(netEvent.Peer, packetData);
                                break;

                            default:
                                if (ReservedChannels.TryGetValue(channel, out var handler))
                                {
                                    var peer = GetPeerByNativeId(netEvent.Peer.ID);
                                    if (peer.IsSet)
                                    {
                                        handler(peer, packetData);
                                    }
                                }
                                break;
                        }
                        }
                        catch (Exception ex)
                        {
                            // Server: drop the offending peer (see MalformedPacketDisconnectCode).
                            // Client: the server is trusted, so a malformed packet is a bug, not
                            // an attack - log it but stay connected.
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                                $"[Nebula][MalformedPacket] Failed to parse packet on channel {channel} from peer {netEvent.Peer.ID}: {ex.Message}");
                            if (IsServer)
                            {
                                DisconnectPeer(netEvent.Peer, MalformedPacketDisconnectCode);
                            }
                        }
                        finally
                        {
                            // Covers every non-handoff exit: parsed inline, no world found for the
                            // peer, empty packet, simulated loss, or a parse exception.
                            if (pooledPayload && !payloadHandedOff)
                            {
                                ArrayPool<byte>.Shared.Return(packetData);
                            }
                        }
                        break;
                    }
                }

                // Check for more events
                lock (EnetLock)
                {
                    checkResult = ENetHost.CheckEvents(out netEvent);
                    if (checkResult <= 0)
                    {
                        serviceResult = ENetHost.Service(0, out netEvent);
                    }
                }
            }
        }

        /// <summary>
        /// Helper method to send a packet to a peer.
        /// </summary>
        public static void SendPacket(Peer peer, byte channelId, byte[] data, PacketFlags flags)
        {
            if (!TrySendOutbound(channelId)) return;

            // Reached from world tick threads (ExportState, net functions) as well as the main
            // thread. See EnetLock.
            lock (EnetLock)
            {
                var packet = default(Packet);
                packet.Create(data, flags);
                peer.Send(channelId, ref packet);
            }
        }

        /// <summary>
        /// Helper method to send a packet using a NetBuffer directly (zero-allocation).
        /// Uses the buffer's internal array with proper length to avoid ToArray() allocation.
        /// </summary>
        public static void SendPacket(Peer peer, byte channelId, NetBuffer buffer, PacketFlags flags)
        {
            if (!TrySendOutbound(channelId)) return;

            // See EnetLock. packet.Create copies the bytes out of the buffer synchronously, so the
            // caller's pooled NetBuffer stays reusable the moment this returns.
            lock (EnetLock)
            {
                var packet = default(Packet);
                packet.Create(buffer.RawBuffer, buffer.Length, flags);
                peer.Send(channelId, ref packet);
            }
        }

        /// <summary>
        /// Disconnects a peer. Goes through <see cref="EnetLock"/> like every other host touch,
        /// because the ack-timeout sweep drops peers from inside a world's tick.
        /// </summary>
        internal static void DisconnectPeer(Peer peer, uint data)
        {
            lock (EnetLock)
            {
                peer.Disconnect(data);
            }
        }

        /// <summary>
        /// Helper method to send a reliable packet.
        /// </summary>
        public static void SendReliable(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.Reliable);
        }

        /// <summary>
        /// Helper method to send a reliable packet using a NetBuffer directly (zero-allocation).
        /// </summary>
        public static void SendReliable(Peer peer, byte channelId, NetBuffer buffer)
        {
            SendPacket(peer, channelId, buffer, PacketFlags.Reliable);
        }

        /// <summary>
        /// Helper method to send an unreliable packet.
        /// </summary>
        public static void SendUnreliable(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.None);
        }

        /// <summary>
        /// Helper method to send an unreliable packet using a NetBuffer directly (zero-allocation).
        /// </summary>
        public static void SendUnreliable(Peer peer, byte channelId, NetBuffer buffer)
        {
            SendPacket(peer, channelId, buffer, PacketFlags.None);
        }

        /// <summary>
        /// Helper method to send an unreliable sequenced packet (newer packets discard older ones).
        /// </summary>
        public static void SendUnreliableSequenced(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.Unsequenced);
        }

        /// <summary>
        /// Helper method to send an unreliable sequenced packet using a NetBuffer directly (zero-allocation).
        /// </summary>
        public static void SendUnreliableSequenced(Peer peer, byte channelId, NetBuffer buffer)
        {
            SendPacket(peer, channelId, buffer, PacketFlags.Unsequenced);
        }

        public void PeerJoinWorld(NetPeer peer, UUID worldId, string token = "")
        {
            // Admission mutates the shared peer registries, which belong to the main thread. The
            // pump-driven callers are already there and run inline; a caller resuming from an await
            // (whose continuation lands wherever its scheduler chose) is deferred a frame instead.
            // Same posture as MigratePeerToWorld.
            if (!NebulaThread.IsMain)
            {
                RunOnMainThread(() => PeerJoinWorld(peer, worldId, token));
                return;
            }

            if (!Worlds.TryGetValue(worldId, out var world) || world == null)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"PeerJoinWorld: no world {worldId}.");
                return;
            }
            if (world.Lifecycle != WorldRunner.WorldLifecycle.Live)
            {
                // A world is registered from the moment creation starts, so being findable is not
                // the same as being ready. Await the creation task instead of joining early.
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"PeerJoinWorld: world {worldId} is {world.Lifecycle}, not Live. Refusing to admit peer {peer.ID}.");
                return;
            }

            var peerId = new UUID();
            Peers[peerId] = peer;
            PeerIds[peer.ID] = peerId;
            world.JoinPeer(peer, token);
        }

        // --- Live cross-world migration (World ENet channel) ---

        private const byte WorldMsgChangeWorld = 0x00; // server -> client: reset and expect <worldId>
        private const byte WorldMsgReady = 0x01;       // client -> server: reset done, ready to join

        private readonly struct PendingHandoff
        {
            public readonly WorldRunner Target;
            public readonly string Token;
            public PendingHandoff(WorldRunner target, string token) { Target = target; Token = token; }
        }

        // Peers awaiting a world handoff (sent ChangeWorld, waiting for the client's ready ack).
        private readonly Dictionary<UUID, PendingHandoff> _pendingHandoffs = new();

        // Reused buffer for the tiny 17-byte World-channel messages (no per-send allocation).
        private NetBuffer _worldChannelBuffer;

        /// <summary>
        /// Server-only. Migrates a connected peer from its current world to <paramref name="target"/>
        /// over the SAME connection. The source (hub) world keeps running for other/returning players.
        /// The peer's owned nodes are freed from the source; the client is told to reset (World channel),
        /// and only once it acks is the peer admitted to the target — so the target streams no state into
        /// a not-yet-reset client (the World channel and the tick channel are not cross-channel ordered).
        /// The peer joins the target as INITIAL and transitions to IN_WORLD on its first tick ack, which
        /// raises OnPlayerJoined on the target so it can (re)spawn the player under the same identity.
        /// </summary>
        public void MigratePeerToWorld(NetPeer peer, WorldRunner target)
        {
            if (!IsServer || target == null) return;

            // The documented pattern is "await CreateWorld, then migrate" -- and when that await
            // was entered on a world tick thread, the continuation resumes wherever the scheduler
            // put it, not necessarily on main. Everything below touches the shared peer registries
            // and the source world's peer state, so marshal here rather than trusting every caller
            // to. Inline when already on main, so the pump-driven paths are unchanged.
            if (!NebulaThread.IsMain)
            {
                RunOnMainThread(() => MigratePeerToWorld(peer, target));
                return;
            }

            if (target.Lifecycle != WorldRunner.WorldLifecycle.Live)
            {
                // Callers get a Live world by awaiting CreateWorld, so this is a guard against
                // holding on to a world across a failure rather than a path anyone takes normally.
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"MigratePeerToWorld: target world {target.WorldId} is {target.Lifecycle}, not Live. Refusing to migrate peer {peer.ID}.");
                return;
            }

            var peerId = GetPeerId(peer);
            if (!PeerWorldMap.TryGetValue(peerId, out var source) || source == null)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"MigratePeerToWorld: peer {peerId} is not in any world.");
                return;
            }
            if (source == target) return;

            // Capture the token before the source clears the peer's state — the destination reuses it
            // to load the same character (identity persists; no re-auth in this path).
            var sourceState = source.GetPeerWorldState(peerId);
            var token = sourceState.HasValue ? sourceState.Value.Token : "";

            source.PreparePeerDeparture(peer);
            _pendingHandoffs[peerId] = new PendingHandoff(target, token);

            SendWorldMessage(peer, WorldMsgChangeWorld, target.WorldId);
        }

        private void HandleWorldChannel(NetPeer peer, byte[] data)
        {
            // Message format: [opcode:1B][worldId:16B]
            if (data.Length < 1)
            {
                Debugger.Instance.Log($"[WorldMigration] HandleWorldChannel: empty packet from peer {peer.ID}", Debugger.DebugLevel.WARN);
                return;
            }
            var opcode = data[0];

            if (IsServer)
            {
                if (opcode == WorldMsgReady)
                {
                    CompletePeerHandoff(peer);
                }
                return;
            }

            if (opcode == WorldMsgChangeWorld)
            {
                UUID worldId = default;
                if (data.Length >= 17)
                {
                    worldId = new UUID(new Guid(new ReadOnlySpan<byte>(data, 1, 16)));
                }
                // Reset the single client world container, then ack with the same worldId so the
                // server can match this peer's pending handoff.
                // Adopt the id too: the client's WorldRunner is constructed in StartClient and
                // otherwise never learns which world it is in, which left every client-side debug
                // frame tagged with an empty UUID.
                if (WorldRunner.CurrentWorld != null)
                    WorldRunner.CurrentWorld.WorldId = worldId;
                WorldRunner.CurrentWorld?.ResetForWorldChange();
                SendWorldMessage(ServerPeer, WorldMsgReady, worldId);
            }
            else
            {
                Debugger.Instance.Log($"[WorldMigration][Client] Unexpected World-channel opcode={opcode}", Debugger.DebugLevel.WARN);
            }
        }

        private void CompletePeerHandoff(NetPeer peer)
        {
            var peerId = GetPeerId(peer);
            if (!_pendingHandoffs.TryGetValue(peerId, out var handoff))
            {
                Debugger.Instance.Log($"[WorldMigration][Server] CompletePeerHandoff: no pending handoff for peer={peerId} (peer.ID={peer.ID})", Debugger.DebugLevel.WARN);
                return;
            }
            _pendingHandoffs.Remove(peerId);
            // JoinPeer sets PeerWorldMap[peerId] = target and creates the peer's world state (INITIAL).
            handoff.Target.JoinPeer(peer, handoff.Token);
        }

        private void SendWorldMessage(NetPeer peer, byte opcode, in UUID worldId)
        {
            _worldChannelBuffer ??= new NetBuffer();
            _worldChannelBuffer.Reset();
            NetWriter.WriteByte(_worldChannelBuffer, opcode);
            Span<byte> guidBytes = stackalloc byte[16];
            worldId.Guid.TryWriteBytes(guidBytes);
            NetWriter.WriteBytes(_worldChannelBuffer, (ReadOnlySpan<byte>)guidBytes);
            SendReliable(peer, (byte)ENetChannelId.World, _worldChannelBuffer);
        }

        public event Action<WorldRunner> OnWorldCreated;

        /// <summary>
        /// Creates a world from a scene, instantiating it off the main thread.
        ///
        /// <para>The returned task completes only once the world is fully built and ready for
        /// peers, including any <see cref="IAsyncWorldGenerator"/> work its root scene does. Await
        /// it before migrating anyone in.</para>
        ///
        /// <para>Safe to call from a world's tick thread -- which is the normal case, since a
        /// [NetFunction] like "start an expedition" is dispatched from inside ServerProcessTick.
        /// Everything that touches the SceneTree is marshalled to the main thread internally.</para>
        /// </summary>
        /// <param name="onTreeReady">
        /// Runs on the main thread once the world is in the tree and network-prepared, but before
        /// it goes Live. This is where per-world infrastructure a caller wants in place before any
        /// peer can join belongs. Attaching it after awaiting would be a race against the first join.
        /// </param>
        public async Task<WorldRunner> CreateWorld(
            UUID worldId,
            PackedScene scene,
            Action<WorldRunner> onTreeReady = null,
            CancellationToken ct = default)
        {
            if (!IsServer) return null;

            // Off the main thread: instantiating a PackedScene is permitted anywhere, so long as
            // the result is not added to the tree there (SetupWorldInstance does that on main).
            //
            // LongRunning rather than Task.Run deliberately -- it gets a dedicated thread instead of
            // consuming a ThreadPool worker, and world generation code below this tends to use the
            // pool itself (Parallel.For, Task.Run), so a pool-bound outer call would sit waiting on
            // the very pool it is starving.
            var node = await Task.Factory.StartNew(
                () => scene.Instantiate(),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            if (node is not INetNodeBase netNodeBase)
            {
                node?.Free();
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Failed to create world: root node is not a NetworkController");
                return null;
            }

            return await SetupWorldInstance(worldId, netNodeBase.Network, onTreeReady, ct);
        }

        /// <summary>
        /// Brings up a world around an already-instantiated root scene -- the restored-world path,
        /// where the node came from deserialization rather than a PackedScene.
        /// See <see cref="CreateWorld"/> for the contract.
        /// </summary>
        public async Task<WorldRunner> SetupWorldInstance(
            UUID worldId,
            NetworkController node,
            Action<WorldRunner> onTreeReady = null,
            CancellationToken ct = default)
        {
            if (!IsServer) return null;

            // Everything from here to the end of generation touches the SceneTree and the world
            // registry, so it belongs on the main thread. Completes inline when already there.
            await SwitchToMainThread();
            NebulaThread.AssertMain(nameof(SetupWorldInstance));
            var godotPhysicsWorld = new SubViewport
            {
                OwnWorld3D = true,
                World3D = new World3D(),
                Name = worldId.ToString()
            };

            if (PerWorldThreadGroup)
            {
                // The whole world -- its WorldRunner and every node in its scene -- processes on
                // this group's own thread instead of the main SceneTree walk.
                godotPhysicsWorld.ProcessThreadGroup = Node.ProcessThreadGroupEnum.SubThread;

                // Order 1 puts every world group strictly after the default main-thread group
                // (order 0), which is where the NetRunner autoload and therefore the ENet pump
                // live. Worlds share an order, so they run concurrently with each other.
                //
                // This is an ordering convenience, not a synchronization mechanism: the inbound
                // queue is what actually makes pump/tick overlap safe, because the pump is not the
                // only thing that touches world state (the World channel completes peer handoffs).
                godotPhysicsWorld.ProcessThreadGroupOrder = 1;

                // Flush CallDeferredThreadGroup work immediately before this group's
                // _physics_process, so anything marshalled into the group lands before its tick.
                godotPhysicsWorld.ProcessThreadMessages = Node.ProcessThreadMessagesEnum.MessagesPhysics;
            }

            // Nothing in a half-built world may tick: not the WorldRunner, and not the gameplay
            // nodes underneath it. Disabling the SubViewport is what actually achieves that -- a
            // Lifecycle check inside WorldRunner._PhysicsProcess would only stop the former.
            godotPhysicsWorld.ProcessMode = ProcessModeEnum.Disabled;

            var worldRunner = new WorldRunner
            {
                WorldId = worldId,
                RootScene = node,
                Lifecycle = WorldRunner.WorldLifecycle.Generating,
            };

            // Registered before the first await below, so two callers racing to create the same
            // world resolve to one entry instead of each building their own.
            Worlds[worldId] = worldRunner;
            worldRunner.Debug?.Send("WorldGenerating", worldId.ToString());

            try
            {
                godotPhysicsWorld.AddChild(worldRunner);
                godotPhysicsWorld.AddChild(node.RawNode);
                GetTree().CurrentScene.AddChild(godotPhysicsWorld);
                node._NetworkPrepare(worldRunner);

                // Before _WorldReady and before the world can go Live, so callers can install
                // whatever must exist ahead of the first join.
                onTreeReady?.Invoke(worldRunner);

                node._WorldReady();

                if (node.RawNode is IAsyncWorldGenerator generator)
                {
                    await generator.GenerateWorldAsync(ct);
                    // Generation is free to hop threads; come back before touching the tree again.
                    await SwitchToMainThread();
                }

                ct.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                // Registration and its compensating removal live in the same method, so there is no
                // path that leaves a half-registered world behind for someone to find later.
                await SwitchToMainThread();
                worldRunner.Lifecycle = WorldRunner.WorldLifecycle.Failed;
                Worlds.Remove(worldId);
                // Frees the WorldRunner, the world's scene and its World3D along with it.
                godotPhysicsWorld.QueueFree();
                throw;
            }

            worldRunner.Lifecycle = WorldRunner.WorldLifecycle.Live;
            godotPhysicsWorld.ProcessMode = ProcessModeEnum.Inherit;

            worldRunner.Debug?.Send("WorldCreated", worldId.ToString());
            // Fired only once the world is genuinely usable: subscribers include the autosave hook,
            // which must never serialize a world that is still being built.
            OnWorldCreated?.Invoke(worldRunner);

            // Anyone who connected while this was still generating can now be let in.
            AuthenticateWaitingPeers();

            return worldRunner;
        }

        public void _OnPeerDisconnected(Peer peer)
        {
            Debugger.Instance.Log($"Peer disconnected peerId: {peer.ID}");
            OnPeerDisconnected?.Invoke(peer.ID);
            PeersByNativeId.Remove(peer.ID);
            // A peer that gave up while waiting for the first world must not be authenticated into
            // it later.
            _peersAwaitingWorld.Remove(peer.ID);
        }
    }
}
