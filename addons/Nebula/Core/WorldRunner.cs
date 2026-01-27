using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
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
        /// Maximum time in seconds a peer can go without acknowledging a tick before being force disconnected.
        /// </summary>
        public const float PEER_ACK_TIMEOUT_SECONDS = 5.0f;

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

        public struct PeerState
        {
            public NetPeer Peer;
            public Tick Tick;
            public PeerSyncStatus Status;
            public UUID Id;
            public string Token;
            public Dictionary<NetId, ushort> WorldToPeerNodeMap;
            public Dictionary<ushort, NetId> PeerToWorldNodeMap;

            /// <summary>
            /// A list of nodes that the player is aware of in the world (i.e. has spawned locally)
            /// </summary>
            public Dictionary<NetId, bool> SpawnAware;

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
            public object[] Args;
            public NetPeer Sender;
        }

        public UUID WorldId { get; internal set; }

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

        internal long networkIdCounter = 1; // Start at 1 because NetId=0 is considered invalid
        private Dictionary<long, NetId> networkIds = [];
        internal Dictionary<NetId, NetworkController> NetScenes = [];

        // TCP debug server fields
        private TcpListener DebugTcpListener { get; set; }
        private List<TcpClient> DebugTcpClients { get; } = new();
        private readonly object _debugClientsLock = new();

        public enum DebugDataType
        {
            TICK,
            PAYLOADS,
            EXPORT,
            LOGS,
            PEERS,
            CALLS,
            DEBUG_EVENT
        }

        /// <summary>
        /// Sends debug events to connected debug clients (e.g., test runners).
        /// Buffers messages until a client connects, then flushes the buffer.
        /// </summary>
        public class DebugMessenger
        {
            private readonly WorldRunner _world;
            private readonly List<byte[]> _pendingMessages = new();
            private readonly object _bufferLock = new();
            private bool _hasSentBufferedMessages = false;

            public DebugMessenger(WorldRunner world)
            {
                _world = world;
            }

            /// <summary>
            /// Sends a debug event with a category and message to all connected debug peers.
            /// If no clients are connected, buffers the message until one connects.
            /// </summary>
            /// <param name="category">Event category (e.g., "Spawn", "Connect")</param>
            /// <param name="message">Event message/details</param>
            public void Send(string category, string message)
            {
                if (_world.DebugTcpListener == null) return;

                using var buffer = new NetBuffer();
                NetWriter.WriteByte(buffer, (byte)DebugDataType.DEBUG_EVENT);
                NetWriter.WriteString(buffer, category);
                NetWriter.WriteString(buffer, message);

                // Wrap with length prefix for TCP framing
                var framedData = CreateFramedPacket(buffer);

                lock (_bufferLock)
                {
                    if (_world.DebugTcpClients.Count == 0)
                    {
                        // No clients yet - buffer the message
                        _pendingMessages.Add(framedData);
                        return;
                    }
                }

                _world.SendToDebugClients(framedData);
            }

            /// <summary>
            /// Flushes any buffered messages to connected clients.
            /// Called when a new debug client connects.
            /// </summary>
            internal void FlushBuffer()
            {
                lock (_bufferLock)
                {
                    if (_pendingMessages.Count == 0 || _hasSentBufferedMessages) return;

                    foreach (var framedData in _pendingMessages)
                    {
                        _world.SendToDebugClients(framedData);
                    }

                    _pendingMessages.Clear();
                    _hasSentBufferedMessages = true;
                }
            }
        }

        /// <summary>
        /// Creates a TCP framed packet with a 4-byte length prefix.
        /// </summary>
        private static byte[] CreateFramedPacket(NetBuffer buffer)
        {
            var lengthPrefix = BitConverter.GetBytes(buffer.Length);
            var framedData = new byte[4 + buffer.Length];
            Array.Copy(lengthPrefix, 0, framedData, 0, 4);
            buffer.WrittenSpan.CopyTo(framedData.AsSpan(4));
            return framedData;
        }

        private void SendToDebugClients(byte[] data)
        {
            lock (_debugClientsLock)
            {
                var clientsToRemove = new List<TcpClient>();
                foreach (var client in DebugTcpClients)
                {
                    try
                    {
                        if (client.Connected)
                        {
                            var stream = client.GetStream();
                            stream.Write(data, 0, data.Length);
                            stream.Flush(); // Ensure data is sent immediately
                        }
                        else
                        {
                            clientsToRemove.Add(client);
                        }
                    }
                    catch
                    {
                        clientsToRemove.Add(client);
                    }
                }
                foreach (var client in clientsToRemove)
                {
                    DebugTcpClients.Remove(client);
                    try { client.Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// Debug messenger for sending test events via TCP.
        /// </summary>
        public DebugMessenger Debug { get; private set; }

        /// <summary>
        /// Port for the debug TCP connection. 0 means use a random available port.
        /// </summary>
        public int DebugPort { get; set; } = 0;

        // Diagnostic counter for RPC calls - remove after debugging
        public static long TotalRpcCallsProcessed = 0;
        public static long RpcCallsThisTick = 0;

        private List<TickLog> tickLogBuffer = [];
        public void Log(string message, Debugger.DebugLevel level = Debugger.DebugLevel.INFO)
        {
            if (NetRunner.Instance.IsServer)
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

        private int GetAvailablePort()
        {
            // Create a listener on port 0, which tells the OS to assign an available port
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        Action<uint> _onPeerDisconnectedHandler;

        public override void _Ready()
        {
            base._Ready();
            Name = "WorldRunner";
            Debug = new DebugMessenger(this);

            // Parse command line args
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument.StartsWith("--debugPort="))
                {
                    var value = argument.Substring("--debugPort=".Length);
                    if (int.TryParse(value, out int parsedPort))
                    {
                        DebugPort = parsedPort;
                    }
                }
                else if (argument.StartsWith("--clientId=") && !_clientIdParsed)
                {
                    var value = argument.Substring("--clientId=".Length);
                    if (int.TryParse(value, out int parsedId))
                    {
                        ClientId = parsedId;
                        _clientIdParsed = true;
                    }
                }
            }

            // Debug TCP server is opt-in (dedicated servers should not start it by default).
            // Enable via either:
            // - command line: --debugPort=XXXX
            // - project setting: Nebula/debug/enable_tcp = true
            bool enableDebugTcp =
                DebugPort > 0 ||
                ProjectSettings.GetSetting("Nebula/debug/enable_tcp", false).AsBool();

            if (enableDebugTcp)
            {
                int port = DebugPort > 0 ? DebugPort : GetAvailablePort();
                int attempts = 0;
                const int MAX_ATTEMPTS = 1000;

                while (attempts < MAX_ATTEMPTS)
                {
                    try
                    {
                        DebugTcpListener = new TcpListener(IPAddress.Loopback, port);
                        DebugTcpListener.Start();
                        Log(Debugger.DebugLevel.VERBOSE, $"World {WorldId} debug TCP server started on port {port}");
                        break;
                    }
                    catch (SocketException ex)
                    {
                        if (DebugPort > 0)
                        {
                            // Fixed port requested but failed - don't retry with random ports
                            Log(Debugger.DebugLevel.ERROR, $"Error starting debug TCP server on fixed port {DebugPort}: {ex.Message}");
                            DebugTcpListener = null;
                            break;
                        }
                        port = GetAvailablePort();
                        attempts++;
                    }
                }

                if (attempts >= MAX_ATTEMPTS)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Error starting debug TCP server after {attempts} attempts");
                    DebugTcpListener = null;
                }
            }
            else
            {
                DebugTcpListener = null;
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

            // Cleanup debug TCP server for both server and client
            if (DebugTcpListener != null)
            {
                lock (_debugClientsLock)
                {
                    foreach (var client in DebugTcpClients)
                    {
                        try { client.Close(); } catch { }
                    }
                    DebugTcpClients.Clear();
                }
                DebugTcpListener.Stop();
            }

            if (NetRunner.Instance.IsServer)
            {
                NetRunner.Instance.OnPeerDisconnected -= _onPeerDisconnectedHandler;
            }
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
        }

        /// <summary>
        /// Called when ClientProcessTick receives a new tick (resets accumulator).
        /// </summary>
        internal void OnWorldTickReceived(int tick)
        {
            // Reset accumulator when we receive a new tick
            TimeSinceLastTick = 0f;
        }

        /// <summary>
        /// Get the fractional render tick for interpolation (used by all entities).
        /// </summary>
        public float GetRenderTick()
        {
            float tickDuration = 1f / NetRunner.TPS;
            float fractionalTick = TimeSinceLastTick / tickDuration;
            // Clamp to avoid extrapolating too far if frame is slow
            fractionalTick = Math.Min(fractionalTick, 1.5f);
            return CurrentTick + fractionalTick - InterpolationDelayTicks;
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

            // Search for most recent input
            byte[] fallback = null;
            Tick bestTick = -1;
            for (int i = 0; i < SERVER_INPUT_BUFFER_SIZE; i++)
            {
                if (buffer.Ticks[i] >= 0 && buffer.Ticks[i] < tick && buffer.Ticks[i] > bestTick)
                {
                    bestTick = buffer.Ticks[i];
                    fallback = buffer.Inputs[i];
                }
            }

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
            bool childMispredicted = false;

            foreach (var staticChild in netController.StaticNetworkChildren)
            {
                if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                if (!staticChild.IsCurrentOwner) continue;
                if (staticChild.Reconcile(incomingTick, forceRestoreAll))
                {
                    childMispredicted = true;
                }
            }

            if (parentMispredicted || childMispredicted)
            {
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
                    // Phase 1: Apply all buffered inputs
                    var bufferedInput = netController.GetBufferedInput(resimTick);
                    if (bufferedInput != null)
                    {
                        netController.SetInputBytes(bufferedInput);
                    }

                    foreach (var staticChild in netController.StaticNetworkChildren)
                    {
                        if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                        if (!staticChild.IsCurrentOwner) continue;

                        var childInput = staticChild.GetBufferedInput(resimTick);
                        if (childInput != null)
                        {
                            staticChild.SetInputBytes(childInput);
                        }
                    }

                    // Phase 2: Run simulations
                    netController._NetworkProcess(resimTick);
                    netController.StorePredictedState(resimTick);

                    foreach (var staticChild in netController.StaticNetworkChildren)
                    {
                        if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                        if (!staticChild.IsCurrentOwner) continue;

                        staticChild._NetworkProcess(resimTick);
                        staticChild.StorePredictedState(resimTick);
                    }
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
                // Prediction correct - restore from prediction buffer
                netController.RestoreToPredictedState(incomingTick);

                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.IsMarkedForDeletion) continue;
                    if (!staticChild.IsCurrentOwner) continue;
                    staticChild.RestoreToPredictedState(incomingTick);
                }
            }
        }

        #endregion

        public NetworkController GetNodeFromNetId(NetId networkId)
        {
            if (networkId.IsNone || !networkId.IsValid)
                return null;
            // Fix #7: Use TryGetValue
            return NetScenes.TryGetValue(networkId, out var controller) ? controller : null;
        }

        public NetworkController GetNodeFromNetId(long networkId)
        {
            if (networkId == NetId.NONE)
                return null;
            // Fix #7: Use TryGetValue
            if (!networkIds.TryGetValue(networkId, out var netId))
                return null;
            return NetScenes.TryGetValue(netId, out var controller) ? controller : null;
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
        /// Invoked after each network tick completes.
        /// </summary>
        public event Action<Tick> OnAfterNetworkTick;

        /// <summary>
        /// Invoked when a player joins the world (sync status becomes IN_WORLD).
        /// </summary>
        public event Action<UUID> OnPlayerJoined;
        public event Action<UUID> OnPlayerCleanup;


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
                peer.Disconnect(0);
            }

            var peerState = PeerStates[peerId];
            foreach (var netController in peerState.OwnedNodes)
            {
                if (netController.DespawnOnUnowned)
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

            PeerStates.Remove(peerId);
            _peerLastAckTick.Remove(peerId);
            _peerPendingAcks.Remove(peerId); // Fix #5: Clean up pending acks tracking
            _peerNetBufferPool.Remove(peerId); // Clean up pooled export buffer
            _peerListDirty = true; // Fix #1: Mark peer list as dirty
            NetRunner.Instance.Peers.Remove(peerId);
            NetRunner.Instance.WorldPeerMap.Remove(peerId);
            NetRunner.Instance.PeerWorldMap.Remove(peerId);
            NetRunner.Instance.PeerIds.Remove(peer.ID);
            OnPlayerCleanup?.Invoke(peerId);
        }

        private int _frameCounter = 0;
        /// <summary>
        /// This method is executed every tick on the Server side, and kicks off all logic which processes and sends data to every client.
        /// </summary>
        public void ServerProcessTick()
        {
            // Check for peers that have timed out (no acks for too long)
            int ackTimeoutTicks = (int)(PEER_ACK_TIMEOUT_SECONDS * NetRunner.TPS);
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

                var ticksSinceLastAck = CurrentTick - _peerLastAckTick[peerId];
                if (ticksSinceLastAck > ackTimeoutTicks)
                {
                    Log(Debugger.DebugLevel.WARN, $"[ACK TIMEOUT] Peer {peerId} has not acknowledged for {ticksSinceLastAck} ticks ({ticksSinceLastAck / (float)NetRunner.TPS:F1}s). Force disconnecting.");
                    _peersToDisconnect.Add(peerState.Peer);
                }
            }

            foreach (var peer in _peersToDisconnect)
            {
                CleanupPlayer(peer);
            }

            foreach (var net_id in NetScenes.Keys)
            {
                var netController = NetScenes[net_id];
                if (netController == null)
                    continue;

                // Use cached flag to avoid Godot method call allocation
                if (!IsInstanceValid(netController.RawNode) || netController.IsMarkedForDeletion)
                {
                    NetScenes.Remove(net_id);
                    continue;
                }
                if (netController.RawNode.ProcessMode == ProcessModeEnum.Disabled)
                {
                    continue;
                }
                foreach (var networkChild in netController.StaticNetworkChildren)
                {
                    if (networkChild == null) continue;
                    if (networkChild.RawNode == null)
                    {
                        Log(Debugger.DebugLevel.ERROR, $"Network child node is unexpectedly null: {netController.RawNode.SceneFilePath}");
                    }
                    if (networkChild.RawNode.ProcessMode == ProcessModeEnum.Disabled)
                    {
                        continue;
                    }
                    
                    // Apply buffered input for this tick before processing
                    if (networkChild.HasInputSupport)
                    {
                        var bufferedInput = GetServerBufferedInput(new InputBufferKey(netController.NetId, networkChild.StaticChildId), CurrentTick);
                        if (bufferedInput != null)
                        {
                            networkChild.SetInputBytes(bufferedInput);
                        }
                    }
                    
                    networkChild._NetworkProcess(CurrentTick);
                }
                
                // Apply input for the root netController if it has input support
                if (netController.HasInputSupport)
                {
                    var rootInput = GetServerBufferedInput(new InputBufferKey(netController.NetId), CurrentTick);
                    if (rootInput != null)
                    {
                        netController.SetInputBytes(rootInput);
                    }
                }
                
                netController._NetworkProcess(CurrentTick);
            }

            if (DebugTcpListener != null && DebugTcpClients.Count > 0)
            {
                // Notify the Debugger of the incoming tick
                using var debugBuffer = new NetBuffer();
                NetWriter.WriteByte(debugBuffer, (byte)DebugDataType.TICK);
                NetWriter.WriteInt64(debugBuffer, DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond);
                NetWriter.WriteInt32(debugBuffer, CurrentTick);
                SendToDebugClients(CreateFramedPacket(debugBuffer));
            }

            foreach (var queuedFunction in queuedNetFunctions)
            {
                var functionNode = queuedFunction.Node.GetNode(queuedFunction.FunctionInfo.NodePath) as INetNodeBase;
                NetFunctionContext = new NetFunctionCtx
                {
                    Caller = queuedFunction.Sender,
                };
                functionNode.Network.IsInboundCall = true;
                // Convert object[] back to Variant[] at Godot boundary
                // Note: Godot's Call() requires Variant[], allocation is unavoidable here
                var variantArgs = new Variant[queuedFunction.Args.Length];
                for (int i = 0; i < queuedFunction.Args.Length; i++)
                {
                    variantArgs[i] = Variant.From(queuedFunction.Args[i]);
                }
                functionNode.Network.RawNode.Call(queuedFunction.FunctionInfo.Name, variantArgs);
                functionNode.Network.IsInboundCall = false;
                NetFunctionContext = new NetFunctionCtx { };

                if (DebugTcpListener != null && DebugTcpClients.Count > 0)
                {
                    // Notify the Debugger of the function call
                    using var debugBuffer = new NetBuffer();
                    NetWriter.WriteByte(debugBuffer, (byte)DebugDataType.CALLS);
                    NetWriter.WriteString(debugBuffer, queuedFunction.FunctionInfo.Name);
                    NetWriter.WriteByte(debugBuffer, (byte)queuedFunction.Args.Length);
                    foreach (var arg in queuedFunction.Args)
                    {
                        // Args are already C# objects, determine type and write
                        var serialType = GetSerialTypeFromObject(arg);
                        NetWriter.WriteWithType(debugBuffer, serialType, arg);
                    }
                    SendToDebugClients(CreateFramedPacket(debugBuffer));
                }
            }
            queuedNetFunctions.Clear();

            if (DebugTcpListener != null && DebugTcpClients.Count > 0)
            {
                foreach (var log in tickLogBuffer)
                {
                    using var logBuffer = new NetBuffer();
                    NetWriter.WriteByte(logBuffer, (byte)DebugDataType.LOGS);
                    NetWriter.WriteByte(logBuffer, (byte)log.Level);
                    NetWriter.WriteString(logBuffer, log.Message);
                    SendToDebugClients(CreateFramedPacket(logBuffer));
                }
            }
            tickLogBuffer.Clear();

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
                var exportedState = ExportState(_cachedPeerList);
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

                        using var buffer = new NetBuffer();
                        NetWriter.WriteInt32(buffer, CurrentTick);
                        NetWriter.WriteBytes(buffer, peerStateBuffer.WrittenSpan);
                        var size = buffer.Length;
                        if (size > NetRunner.MTU)
                        {
                            Log(Debugger.DebugLevel.ERROR, $"[MTU EXCEEDED] Peer {peer.ID} tick {CurrentTick}: Data size {size} exceeds MTU {NetRunner.MTU} - PACKET MAY BE CORRUPTED!");
                        }

                        NetRunner.SendUnreliableSequenced(peer, (byte)NetRunner.ENetChannelId.Tick, buffer);
                        if (DebugTcpListener != null && DebugTcpClients.Count > 0)
                        {
                            using var debugBuffer = new NetBuffer();
                            NetWriter.WriteByte(debugBuffer, (byte)DebugDataType.PAYLOADS);
                            NetWriter.WriteBytes(debugBuffer, peerState.Id.ToByteArray());
                            NetWriter.WriteBytes(debugBuffer, peerStateBuffer.WrittenSpan);
                            SendToDebugClients(CreateFramedPacket(debugBuffer));
                        }
                    }
                }
                finally
                {
                    // ExportState() now returns truly pooled NetBuffer instances that are reused between ticks.
                    // Do NOT dispose them - they will be Reset() and reused on the next tick.
                }
            }

            foreach (var netController in QueueDespawnedNodes)
            {
                foreach (var peerState in PeerStates.Values)
                {
                    var peer = peerState.Peer;
                    if (HasSpawnedForClient(netController.NetId, peer))
                    {
                        SendDespawn(peer, netController.NetId);
                        DeregisterPeerNode(netController, peer);
                    }
                }
                netController.NetParentId = NetId.None;
                netController.QueueNodeForDeletion();
            }
            QueueDespawnedNodes.Clear();
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

        internal HashSet<NetworkController> QueueDespawnedNodes = [];
        internal void QueueDespawn(NetworkController node)
        {
            QueueDespawnedNodes.Add(node);
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (NetRunner.Instance.IsClient)
            {
                AccumulateRenderTime((float)delta);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            base._PhysicsProcess(delta);

            // Accept pending TCP debug connections
            if (DebugTcpListener != null && DebugTcpListener.Pending())
            {
                try
                {
                    var client = DebugTcpListener.AcceptTcpClient();
                    lock (_debugClientsLock)
                    {
                        DebugTcpClients.Add(client);
                    }
                    Log(Debugger.DebugLevel.VERBOSE, $"Debug client connected");

                    // Flush any buffered debug messages now that we have a client
                    Debug?.FlushBuffer();
                }
                catch (Exception ex)
                {
                    Log(Debugger.DebugLevel.ERROR, $"Error accepting debug client: {ex.Message}");
                }
            }

            if (NetRunner.Instance.IsServer)
            {
                _frameCounter += 1;
                if (_frameCounter < NetRunner.PhysicsTicksPerNetworkTick)
                    return;
                _frameCounter = 0;
                CurrentTick += 1;
#if DEBUG
                // Simple benchmark: measure ServerProcessTick execution time
                // var stopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
                // Avoid allocating a Stopwatch object every tick.
                long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
                ServerProcessTick();
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs > 15)
                {
                    Log(Debugger.DebugLevel.WARN, $"ServerProcessTick took {elapsedMs:F2} ms");
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
        }

        public bool HasSpawnedForClient(NetId networkId, NetPeer peer)
        {
            // Fix #7: Use TryGetValue
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!PeerStates.TryGetValue(peerId, out var peerState))
            {
                return false;
            }
            return peerState.SpawnAware.TryGetValue(networkId, out var isSpawned) && isSpawned;
        }

        public void SetSpawnedForClient(NetId networkId, NetPeer peer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            PeerStates[peerId].SpawnAware[networkId] = true;
        }

        public void ChangeScene(NetworkController netController)
        {
            if (NetRunner.Instance.IsServer) return;

            if (RootScene != null)
            {
                RootScene.QueueNodeForDeletion();
            }
            Log("Changing scene to " + netController.RawNode.Name);
            // TODO: Support this more generally
            GetTree().CurrentScene.AddChild(netController.RawNode);
            RootScene = netController;
            netController._NetworkPrepare(this);
            netController._WorldReady();
            Debug?.Send("WorldJoined", netController.RawNode.SceneFilePath);
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

        /// <summary>
        /// Reusable list for peers to disconnect (avoids allocation each tick).
        /// </summary>
        private List<NetPeer> _peersToDisconnect = new(16);

        /// <summary>
        /// Cached peer list to avoid ToList() allocation every tick (Fix #1).
        /// Rebuilt only when peers join or leave.
        /// </summary>
        private List<NetPeer> _cachedPeerList = new(64);
        private bool _peerListDirty = true;

        /// <summary>
        /// Tracks which network objects have pending unacked data per peer (Fix #5).
        /// This allows PeerAcknowledge to only iterate relevant objects instead of all NetScenes.
        /// </summary>
        private Dictionary<UUID, HashSet<NetworkController>> _peerPendingAcks = new();
        public void SetPeerState(UUID peerId, PeerState state)
        {
            if (PeerStates[peerId].Status != state.Status)
            {
                OnPeerSyncStatusChange?.Invoke(peerId, state.Status);
                if (state.Status == PeerSyncStatus.IN_WORLD)
                {
                    OnPlayerJoined?.Invoke(peerId);
                }
            }
            PeerStates[peerId] = state;
        }
        public void SetPeerState(NetPeer peer, PeerState state)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            SetPeerState(peerId, state);
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
                NetScenes.Remove(node.NetId);
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
            NetScenes[node.NetId] = node;
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
            var peerId = NetRunner.Instance.GetPeerId(peer);
            NetRunner.Instance.PeerWorldMap[peerId] = this;
            PeerStates[peerId] = new PeerState
            {
                Id = peerId,
                Peer = peer,
                Tick = 0,
                Status = PeerSyncStatus.INITIAL,
                Token = token,
                WorldToPeerNodeMap = [],
                PeerToWorldNodeMap = [],
                SpawnAware = [],
                AvailableNodes = NodeIdUtils.CreateMasks(),
                OwnedNodes = []
            };

            // Fix #1: Mark peer list as dirty so it gets rebuilt
            _peerListDirty = true;

            // Fix #5: Initialize pending acks tracking for this peer
            _peerPendingAcks[peerId] = new HashSet<NetworkController>();
            
            // Initialize interest layers for the root scene immediately so properties
            // can be exported on the same tick as the spawn
            if (RootScene != null)
            {
                RootScene._OnPeerConnected(peerId);
            }
        }

        internal void ExitPeer(NetPeer peer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            NetRunner.Instance.PeerWorldMap.Remove(peerId);
            PeerStates.Remove(peerId);
        }

        // Declare these as fields, not locals - reuse across ticks
        private Dictionary<ushort, NetBuffer> _peerNodesBuffers = new();
        private Dictionary<ushort, byte> _peerNodesSerializersList = new();
        private NetBuffer _serializersBuffer;
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
        // Pooled list for net function args - avoids per-call allocation
        private List<object> _netFunctionArgsPool = new(8);

        internal Dictionary<UUID, NetBuffer> ExportState(List<NetPeer> peers)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Reuse pooled dictionary instead of allocating new each tick
            _exportPeerBuffers.Clear();

            // Lazy init the serializers buffers
            _serializersBuffer ??= new NetBuffer();
            _tempSerializerBuffer ??= new NetBuffer();

            foreach (var netController in NetScenes.Values)
            {
                // Initialize serializers
                foreach (var serializer in netController.NetNode.Serializers)
                {
                    serializer.Begin();
                }
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

                // Fix #5: Get or create pending acks set for this peer
                if (!_peerPendingAcks.TryGetValue(peerId, out var pendingAcks))
                {
                    pendingAcks = new HashSet<NetworkController>();
                    _peerPendingAcks[peerId] = pendingAcks;
                }

                foreach (var netController in NetScenes.Values)
                {
                    _serializersBuffer.Reset(); // Reuse instead of new
                    byte serializersRun = 0;

                    for (var serializerIdx = 0; serializerIdx < netController.NetNode.Serializers.Length; serializerIdx++)
                    {
                        var serializer = netController.NetNode.Serializers[serializerIdx];
                        _tempSerializerBuffer.Reset();
                        int beforePos = _tempSerializerBuffer.WritePosition;
                        serializer.Export(this, peer, _tempSerializerBuffer);
                        if (_tempSerializerBuffer.WritePosition == beforePos)
                        {
                            continue; // Nothing written
                        }
                        serializersRun |= (byte)(1 << serializerIdx);
                        NetWriter.WriteBytes(_serializersBuffer, _tempSerializerBuffer.WrittenSpan);
                    }

                    if (serializersRun == 0)
                    {
                        continue;
                    }

                    // Fix #5: Track that this object has pending data for this peer
                    pendingAcks.Add(netController);

                    ushort localNodeId = PeerStates[peerId].WorldToPeerNodeMap[netController.NetId];
                    NodeIdUtils.SetBit(_updatedNodesMask, localNodeId);
                    _peerNodesSerializersList[localNodeId] = serializersRun;

                    // Pool node buffers
                    if (!_nodeBufferPool.TryGetValue(localNodeId, out var nodeBuffer))
                    {
                        nodeBuffer = new NetBuffer();
                        _nodeBufferPool[localNodeId] = nodeBuffer;
                    }
                    nodeBuffer.Reset();
                    NetWriter.WriteBytes(nodeBuffer, _serializersBuffer.WrittenSpan);
                    _peerNodesBuffers[localNodeId] = nodeBuffer;
                }

                // Write hierarchical bitmask: groupMask (1 byte) + nodeMasks for active groups
                byte groupMask = NodeIdUtils.ComputeGroupMask(_updatedNodesMask);
                NetWriter.WriteByte(_exportPeerBuffers[peerId], groupMask);
                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) != 0)
                    {
                        NetWriter.WriteInt64(_exportPeerBuffers[peerId], _updatedNodesMask[g]);
                    }
                }

                // Write serializerMasks and node data in bitmask iteration order (ascending nodeId)
                // This is zero-allocation and produces sorted order since Combine(g,local) = (g<<6)|local
                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) == 0) continue;
                    for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                    {
                        if ((_updatedNodesMask[g] & (1L << local)) == 0) continue;
                        ushort nodeId = NodeIdUtils.Combine(g, local);
                        NetWriter.WriteByte(_exportPeerBuffers[peerId], _peerNodesSerializersList[nodeId]);
                    }
                }
                for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
                {
                    if ((groupMask & (1 << g)) == 0) continue;
                    for (int local = 0; local < NodeIdUtils.NODES_PER_GROUP; local++)
                    {
                        if ((_updatedNodesMask[g] & (1L << local)) == 0) continue;
                        ushort nodeId = NodeIdUtils.Combine(g, local);
                        NetWriter.WriteBytes(_exportPeerBuffers[peerId], _peerNodesBuffers[nodeId].WrittenSpan);
                    }
                }
            }

            var exportTime = sw.ElapsedMilliseconds;
            sw.Restart();

            // Debugger.Instance.Log($"Export: {exportTime}ms");

            foreach (var netController in NetScenes.Values)
            {
                // Finally, cleanup serializers
                foreach (var serializer in netController.NetNode.Serializers)
                {
                    serializer.Cleanup();
                }
            }

            return _exportPeerBuffers;
        }

        internal void ImportState(NetBuffer stateBytes)
        {
            // Read hierarchical bitmask: groupMask (1 byte) + nodeMasks for active groups
            var groupMask = NetReader.ReadByte(stateBytes);
            var nodeMasks = new long[NodeIdUtils.NODE_GROUPS];
            for (int g = 0; g < NodeIdUtils.NODE_GROUPS; g++)
            {
                if ((groupMask & (1 << g)) != 0)
                {
                    nodeMasks[g] = NetReader.ReadInt64(stateBytes);
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
                    var serializersRun = NetReader.ReadByte(stateBytes);
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
                        var blankScene = new NetNode3D();
                        blankScene.Network.NetId = AllocateNetId(localNodeId);
                        blankScene.SetupSerializers();
                        NetRunner.Instance.AddChild(blankScene);
                        TryRegisterPeerNode(blankScene.Network);
                        netController = blankScene.Network;
                    }

                    for (var serializerIdx = 0; serializerIdx < netController.NetNode.Serializers.Length; serializerIdx++)
                    {
                        if ((serializerMask & ((long)1 << serializerIdx)) == 0)
                        {
                            continue;
                        }
                        var serializerInstance = netController.NetNode.Serializers[serializerIdx];

                        try
                        {
                            serializerInstance.Import(this, stateBytes, out NetworkController nodeOut);
                            if (netController != nodeOut)
                            {
                                netController = nodeOut;
                                serializerIdx = 0;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // Log error with context and ABORT processing this tick entirely
                            // to prevent cascading errors from corrupted buffer position
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[ImportState ERROR] Failed to import node {localNodeId} serializer {serializerIdx}: {ex.Message}. Buffer pos={stateBytes.ReadPosition}/{stateBytes.Length}. Aborting tick import.");
                            return; // Don't continue processing - buffer position is corrupted
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
        }

        // Reusable list for objects that had all data acked (avoids modifying HashSet during iteration)
        private List<NetworkController> _ackedObjects = new(64);

        public void PeerAcknowledge(NetPeer peer, Tick tick)
        {
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

            // Fix #5: Only iterate objects that have pending data for this peer
            if (!_peerPendingAcks.TryGetValue(peerId, out var pendingAcks) || pendingAcks.Count == 0)
            {
                return;
            }

            _ackedObjects.Clear();
            foreach (var netController in pendingAcks)
            {
                if (netController == null || netController.NetNode?.Serializers == null)
                {
                    _ackedObjects.Add(netController); // Remove invalid entries
                    continue;
                }

                for (var serializerIdx = 0; serializerIdx < netController.NetNode.Serializers.Length; serializerIdx++)
                {
                    var serializer = netController.NetNode.Serializers[serializerIdx];
                    serializer.Acknowledge(this, peer, tick);
                }
            }

            // Remove invalid entries
            foreach (var obj in _ackedObjects)
            {
                pendingAcks.Remove(obj);
            }
        }

        public void ClientProcessTick(int incomingTick, byte[] stateBytes)
        {
            // Skip old/duplicate ticks
            if (incomingTick <= CurrentTick)
            {
                return;
            }

            // Initialize prediction on first tick
            if (!_predictionInitialized)
            {
                InitializeClientPrediction(incomingTick);
            }

            CurrentTick = incomingTick;
            OnWorldTickReceived(incomingTick); // Reset time accumulator for snapshot interpolation
            try
            {
                // Log(Debugger.DebugLevel.VERBOSE, $"Importing state bytes of size {stateBytes.Length}");
                using var stateBuffer = new NetBuffer(stateBytes);
                ImportState(stateBuffer);
            }
            catch (Exception ex)
            {
                Log(Debugger.DebugLevel.ERROR, $"[ImportState FAILED] tick {incomingTick}: {ex.Message}");
                // Still continue - send ack so server doesn't think we're dead
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

            // Process non-owned entities with server state
            foreach (var net_id in NetScenes.Keys)
            {
                var netController = NetScenes[net_id];
                if (netController == null) continue;

                if (netController.IsMarkedForDeletion)
                {
                    NetScenes.Remove(net_id);
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

            // ============================================================
            // PREDICTION: Advance predicted tick for owned entities
            // ============================================================
            _clientPredictedTick++;

            for (int i = 0; i < _ownedEntities.Count; i++)
            {
                var netController = _ownedEntities[i];
                if (netController == null || netController.IsMarkedForDeletion) continue;

                netController.IsPredicting = true;
                netController._NetworkProcess(_clientPredictedTick);
                netController.StorePredictedState(_clientPredictedTick);
                netController.IsPredicting = false;

                // Send input with redundancy
                SendInput(netController);

                // Also handle static children
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
                var variantArgs = new Variant[queuedFunction.Args.Length];
                for (int i = 0; i < queuedFunction.Args.Length; i++)
                {
                    variantArgs[i] = Variant.From(queuedFunction.Args[i]);
                }
                functionNode.Network.RawNode.Call(queuedFunction.FunctionInfo.Name, variantArgs);
                functionNode.Network.IsInboundCall = false;
                NetFunctionContext = new NetFunctionCtx { };
            }
            queuedNetFunctions.Clear();

            // ============================================================
            // PROCESS DESPAWNS
            // ============================================================
            foreach (var netController in QueueDespawnedNodes)
            {
                DeregisterPeerNode(netController);
                netController.QueueNodeForDeletion();
            }
            QueueDespawnedNodes.Clear();

            // ============================================================
            // ACKNOWLEDGE TICK (pooled buffer)
            // ============================================================
            _ackBuffer ??= new NetBuffer();
            _ackBuffer.Reset();
            NetWriter.WriteInt32(_ackBuffer, incomingTick);
            NetRunner.SendUnreliableSequenced(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Tick, _ackBuffer);
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
                NetScenes[network.NetId] = network;
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

            // Buffer this input for the predicted tick (for redundancy and rollback)
            netNode.BufferInput(_clientPredictedTick, inputBytes);

            // Only send if input has changed (but always buffer)
            if (!netNode.HasInputChanged)
            {
                return;
            }

            // Get pooled buffer to avoid allocation
            var inputBuffer = netNode.GetPooledInputBuffer();

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

            // Write input count and all recent inputs
            NetWriter.WriteByte(inputBuffer, (byte)recentInputs.Count);

            for (int i = 0; i < recentInputs.Count; i++)
            {
                var (tick, input) = recentInputs[i];
                NetWriter.WriteInt32(inputBuffer, tick);
                NetWriter.WriteInt32(inputBuffer, input.Length);
                NetWriter.WriteBytes(inputBuffer, input);
            }

            // Send unreliable - input redundancy handles packet loss
            NetRunner.SendUnreliable(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Input, inputBuffer);
            netNode.ClearInputChanged();
        }

        internal void ReceiveInput(NetPeer peer, NetBuffer buffer)
        {
            if (NetRunner.Instance.IsClient) return;

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

            // Read input count (redundancy - multiple inputs per packet)
            var inputCount = NetReader.ReadByte(buffer);

            // Read each tick-tagged input and buffer it
            for (int i = 0; i < inputCount; i++)
            {
                var tick = NetReader.ReadInt32(buffer);
                var inputSize = NetReader.ReadInt32(buffer);
                var inputBytes = NetReader.ReadBytes(buffer, inputSize);

                // Buffer the input for this tick using composite key (parentNetId, staticChildId)
                BufferServerInput(new InputBufferKey(worldNetId, staticChildId), tick, inputBytes);

                // Also set as current input if this is the most recent tick we've seen
                if (tick > node.LastConfirmedTick)
                {
                    node.SetInputBytes(inputBytes);
                }
            }

            Debug.Send("Input", $"Received {inputCount} inputs for node {worldNetId} (staticChild={staticChildId})");
        }

        // WARNING: These are not exactly tick-aligned for state reconcilliation. Could cause state issues because the assumed tick is when it is received?
        internal void SendNetFunction(NetId netId, byte functionId, Variant[] args)
        {
            if (NetRunner.Instance.IsServer)
            {
                var node = GetNodeFromNetId(netId);
                // TODO: Apply interest layers for network function, like network property
                foreach (var peer in node.InterestLayers.Keys)
                {
                    using var buffer = new NetBuffer();
                    NetId.NetworkSerialize(this, NetRunner.Instance.Peers[peer], netId, buffer);
                    NetWriter.WriteUInt16(buffer, GetPeerNodeId(NetRunner.Instance.Peers[peer], node));
                    NetWriter.WriteByte(buffer, functionId);
                    foreach (var arg in args)
                    {
                        var serialType = Protocol.FromGodotVariantType(arg.VariantType);
                        NetWriter.WriteByType(buffer, serialType, VariantToObject(arg));
                    }
                    NetRunner.SendReliable(NetRunner.Instance.Peers[peer], (byte)NetRunner.ENetChannelId.Function, buffer);
                }
            }
            else
            {
                using var buffer = new NetBuffer();
                NetId.NetworkSerialize(this, NetRunner.Instance.ServerPeer, netId, buffer);
                NetWriter.WriteByte(buffer, functionId);
                foreach (var arg in args)
                {
                    var serialType = Protocol.FromGodotVariantType(arg.VariantType);
                    NetWriter.WriteByType(buffer, serialType, VariantToObject(arg));
                }
                NetRunner.SendReliable(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Function, buffer);
            }
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
            foreach (var arg in functionInfo.Arguments)
            {
                var value = NetReader.ReadByType(buffer, arg.VariantType);
                _netFunctionArgsPool.Add(value);
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

        internal void SendDespawn(NetPeer peer, NetId netId)
        {
            if (!NetRunner.Instance.IsServer) return;
            using var buffer = new NetBuffer();
            NetId.NetworkSerialize(this, peer, netId, buffer);
            NetRunner.SendReliable(peer, (byte)NetRunner.ENetChannelId.Despawn, buffer);
        }

        internal void ReceiveDespawn(NetPeer peer, NetBuffer buffer)
        {
            var netId = NetId.NetworkDeserialize(this, peer, buffer);
            GetNodeFromNetId(netId)?.handleDespawn();
        }
    }
}
