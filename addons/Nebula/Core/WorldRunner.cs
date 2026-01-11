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
            public Dictionary<NetId, byte> WorldToPeerNodeMap;
            public Dictionary<byte, NetId> PeerToWorldNodeMap;

            /// <summary>
            /// A list of nodes that the player is aware of in the world (i.e. has spawned locally)
            /// </summary>
            public Dictionary<NetId, bool> SpawnAware;

            /// <summary>
            /// A bit list of nodeIds that are available to the peer.
            /// </summary>
            public long AvailableNodes;

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

        // A bit list of all nodes in use by each peer
        // For example, 0 0 0 0 (... etc ...) 0 1 0 1 would mean that the first and third nodes are in use
        public long ClientAvailableNodes = 0;
        readonly static byte MAX_NETWORK_NODES = 64;
        private Dictionary<NetPeer, PeerState> PeerStates = [];

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

            // Initialize debug TCP server for both server and client
            int port = DebugPort > 0 ? DebugPort : GetAvailablePort();
            int attempts = 0;
            const int MAX_ATTEMPTS = 1000;

            while (attempts < MAX_ATTEMPTS)
            {
                try
                {
                    DebugTcpListener = new TcpListener(IPAddress.Loopback, port);
                    DebugTcpListener.Start();
                    Log($"World {WorldId} debug TCP server started on port {port}", Debugger.DebugLevel.VERBOSE);
                    break;
                }
                catch (SocketException ex)
                {
                    if (DebugPort > 0)
                    {
                        // Fixed port requested but failed - don't retry with random ports
                        Log($"Error starting debug TCP server on fixed port {DebugPort}: {ex.Message}", Debugger.DebugLevel.ERROR);
                        DebugTcpListener = null;
                        break;
                    }
                    port = GetAvailablePort();
                    attempts++;
                }
            }

            if (attempts >= MAX_ATTEMPTS)
            {
                Log($"Error starting debug TCP server after {attempts} attempts", Debugger.DebugLevel.ERROR);
                DebugTcpListener = null;
            }

            if (NetRunner.Instance.IsServer)
            {
                _onPeerDisconnectedHandler = (uint peerId) =>
                {
                    var peer = NetRunner.Instance.GetPeerByNativeId(peerId);
                    if (!peer.IsSet) return;
                    if (!PeerStates.ContainsKey(peer)) return; // Already cleaned up
                    
                    if (AutoPlayerCleanup)
                    {
                        CleanupPlayer(peer);
                        return;
                    }
                    var newPeerState = PeerStates[peer];
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

        public NetworkController GetNodeFromNetId(NetId networkId)
        {
            if (networkId.IsNone || !networkId.IsValid)
                return null;
            if (!NetScenes.ContainsKey(networkId))
                return null;
            return NetScenes[networkId];
        }

        public NetworkController GetNodeFromNetId(long networkId)
        {
            if (networkId == NetId.NONE)
                return null;
            if (!networkIds.ContainsKey(networkId))
                return null;
            return NetScenes[networkIds[networkId]];
        }

        public NetId AllocateNetId()
        {
            var networkId = new NetId(networkIdCounter);
            networkIds[networkIdCounter] = networkId;
            networkIdCounter++;
            return networkId;
        }

        public NetId AllocateNetId(byte id)
        {
            var networkId = new NetId(id);
            networkIds[id] = networkId;
            return networkId;
        }

        public NetId GetNetId(long id)
        {
            if (!networkIds.ContainsKey(id))
                return NetId.None;
            return networkIds[id];
        }

        public NetId GetNetIdFromPeerId(NetPeer peer, byte id)
        {
            if (!PeerStates[peer].PeerToWorldNodeMap.ContainsKey(id))
                return NetId.None;
            return PeerStates[peer].PeerToWorldNodeMap[id];
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
            
            // Already cleaned up (e.g. by ack timeout, then ENet disconnect event fires)
            if (!PeerStates.ContainsKey(peer)) return;

            if (peer.State == ENet.PeerState.Connected)
            {
                peer.Disconnect(0);
            }

            var peerState = PeerStates[peer];
            foreach (var netController in peerState.OwnedNodes)
            {
                if (netController.DespawnOnUnowned)
                {
                    netController.RawNode.QueueFree();
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
                netController.CleanupPeerState(peer);
                
                // Clean up serializers' per-peer state
                if (netController.NetNode?.Serializers != null)
                {
                    foreach (var serializer in netController.NetNode.Serializers)
                    {
                        serializer.CleanupPeer(peer);
                    }
                }
            }
            
            var peerId = NetRunner.Instance.GetPeerId(peer);
            PeerStates.Remove(peer);
            _peerLastAckTick.Remove(peer);
            NetRunner.Instance.Peers.Remove(peerId);
            NetRunner.Instance.WorldPeerMap.Remove(peerId);
            NetRunner.Instance.PeerWorldMap.Remove(peer);
            NetRunner.Instance.PeerIds.Remove(peer);
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
            
            foreach (var peer in PeerStates.Keys)
            {
                if (PeerStates[peer].Status == PeerSyncStatus.DISCONNECTED)
                    continue;
                    
                // Initialize tracking for new peers
                if (!_peerLastAckTick.ContainsKey(peer))
                {
                    _peerLastAckTick[peer] = CurrentTick;
                    continue;
                }
                
                var ticksSinceLastAck = CurrentTick - _peerLastAckTick[peer];
                if (ticksSinceLastAck > ackTimeoutTicks)
                {
                    Log($"[ACK TIMEOUT] Peer {peer.ID} has not acknowledged for {ticksSinceLastAck} ticks ({ticksSinceLastAck / (float)NetRunner.TPS:F1}s). Force disconnecting.", Debugger.DebugLevel.WARN);
                    _peersToDisconnect.Add(peer);
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

                if (!IsInstanceValid(netController.RawNode) || netController.RawNode.IsQueuedForDeletion())
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
                        Log($"Network child node is unexpectedly null: {netController.RawNode.SceneFilePath}", Debugger.DebugLevel.ERROR);
                    }
                    if (networkChild.RawNode.ProcessMode == ProcessModeEnum.Disabled)
                    {
                        continue;
                    }
                    networkChild._NetworkProcess(CurrentTick);
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

            var peers = PeerStates.Keys.ToList();
            var exportedState = ExportState(peers);
            foreach (var peer in peers)
            {
                if (PeerStates[peer].Status == PeerSyncStatus.DISCONNECTED)
                {
                    continue;
                }
                using var buffer = new NetBuffer();
                NetWriter.WriteInt32(buffer, CurrentTick);
                NetWriter.WriteBytes(buffer, exportedState[peer].WrittenSpan);
                var size = buffer.Length;
                if (size > NetRunner.MTU)
                {
                    Log($"[MTU EXCEEDED] Peer {peer.ID} tick {CurrentTick}: Data size {size} exceeds MTU {NetRunner.MTU} - PACKET MAY BE CORRUPTED!", Debugger.DebugLevel.ERROR);
                }

                NetRunner.SendUnreliableSequenced(peer, (byte)NetRunner.ENetChannelId.Tick, buffer.ToArray());
                if (DebugTcpListener != null && DebugTcpClients.Count > 0)
                {
                    using var debugBuffer = new NetBuffer();
                    NetWriter.WriteByte(debugBuffer, (byte)DebugDataType.PAYLOADS);
                    NetWriter.WriteBytes(debugBuffer, PeerStates[peer].Id.ToByteArray());
                    NetWriter.WriteBytes(debugBuffer, exportedState[peer].WrittenSpan);
                    SendToDebugClients(CreateFramedPacket(debugBuffer));
                }
            }

            foreach (var netController in QueueDespawnedNodes)
            {
                foreach (var peer in PeerStates.Keys)
                {
                    if (HasSpawnedForClient(netController.NetId, peer))
                    {
                        SendDespawn(peer, netController.NetId);
                        DeregisterPeerNode(netController, peer);
                    }
                }
                netController.NetParentId = NetId.None;
                netController.RawNode.QueueFree();
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
                    Log($"Debug client connected", Debugger.DebugLevel.VERBOSE);

                    // Flush any buffered debug messages now that we have a client
                    Debug?.FlushBuffer();
                }
                catch (Exception ex)
                {
                    Log($"Error accepting debug client: {ex.Message}", Debugger.DebugLevel.ERROR);
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ServerProcessTick();
                sw.Stop();
                Log($"ServerProcessTick took {sw.Elapsed.TotalMilliseconds:F2} ms", Debugger.DebugLevel.VERBOSE);
#if DEBUG
                // stopwatch.Stop();
                // if (_frameCounter == 0) // Only log once per network tick
                // {
                //     Log($"ServerProcessTick took {stopwatch.Elapsed.TotalMilliseconds:F2} ms", Debugger.DebugLevel.VERBOSE);
                // }
#endif
                OnAfterNetworkTick?.Invoke(CurrentTick);
            }
        }

        public bool HasSpawnedForClient(NetId networkId, NetPeer peer)
        {
            if (!PeerStates.ContainsKey(peer))
            {
                return false;
            }
            if (!PeerStates[peer].SpawnAware.ContainsKey(networkId))
            {
                return false;
            }
            return PeerStates[peer].SpawnAware[networkId];
        }

        public void SetSpawnedForClient(NetId networkId, NetPeer peer)
        {
            PeerStates[peer].SpawnAware[networkId] = true;
        }

        public void ChangeScene(NetworkController netController)
        {
            if (NetRunner.Instance.IsServer) return;

            if (RootScene != null)
            {
                RootScene.RawNode.QueueFree();
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
            var peer = NetRunner.Instance.GetPeer(peerId);
            if (!peer.IsSet || !PeerStates.ContainsKey(peer))
            {
                return null;
            }
            return PeerStates[peer];
        }

        public PeerState? GetPeerWorldState(NetPeer peer)
        {
            if (!PeerStates.ContainsKey(peer))
            {
                return null;
            }
            return PeerStates[peer];
        }

        readonly private Dictionary<NetPeer, PeerState> pendingSyncStates = [];
        
        /// <summary>
        /// Tracks the last tick each peer acknowledged. Used for timeout detection.
        /// </summary>
        private Dictionary<NetPeer, Tick> _peerLastAckTick = new();
        
        /// <summary>
        /// Reusable list for peers to disconnect (avoids allocation each tick).
        /// </summary>
        private List<NetPeer> _peersToDisconnect = new(16);
        public void SetPeerState(UUID peerId, PeerState state)
        {
            var peer = NetRunner.Instance.GetPeer(peerId);
            SetPeerState(peer, state);
        }
        public void SetPeerState(NetPeer peer, PeerState state)
        {
            if (PeerStates[peer].Status != state.Status)
            {
                var peerId = NetRunner.Instance.GetPeerId(peer);
                OnPeerSyncStatusChange?.Invoke(peerId, state.Status);
                if (state.Status == PeerSyncStatus.IN_WORLD)
                {
                    OnPlayerJoined?.Invoke(peerId);
                }
            }
            PeerStates[peer] = state;
        }

        public byte GetPeerNodeId(NetPeer peer, NetworkController node)
        {
            if (node == null) return 0;
            if (!PeerStates.ContainsKey(peer))
            {
                return 0;
            }
            if (!PeerStates[peer].WorldToPeerNodeMap.ContainsKey(node.NetId))
            {
                return 0;
            }
            return PeerStates[peer].WorldToPeerNodeMap[node.NetId];
        }

        /// <summary>
        /// Get the network node from a peer and a network ID relative to that peer.
        /// </summary>
        /// <param name="peer"></param>
        /// <param name="networkId"></param>
        /// <returns></returns>
        public NetworkController GetPeerNode(NetPeer peer, byte networkId)
        {
            if (!PeerStates.ContainsKey(peer))
            {
                return null;
            }
            if (!PeerStates[peer].PeerToWorldNodeMap.ContainsKey(networkId))
            {
                return null;
            }
            return NetScenes[PeerStates[peer].PeerToWorldNodeMap[networkId]];
        }

        internal void DeregisterPeerNode(NetworkController node, NetPeer peer = default)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (!peer.IsSet)
                {
                    Log("Server must specify a peer when deregistering a node.", Debugger.DebugLevel.ERROR);
                    return;
                }
                if (PeerStates[peer].WorldToPeerNodeMap.ContainsKey(node.NetId))
                {
                    var peerState = PeerStates[peer];
                    peerState.AvailableNodes &= ~(1 << PeerStates[peer].WorldToPeerNodeMap[node.NetId]);
                    PeerStates[peer] = peerState;
                    PeerStates[peer].WorldToPeerNodeMap.Remove(node.NetId);
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
        // Up to 64 nodes can be networked per peer at a time.
        // TODO: Consider supporting more
        // TODO: Handle de-registration of nodes (e.g. despawn, and object interest)
        internal byte TryRegisterPeerNode(NetworkController node, NetPeer peer = default)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (!peer.IsSet)
                {
                    Log("Server must specify a peer when registering a node.", Debugger.DebugLevel.ERROR);
                    return 0;
                }
                if (PeerStates[peer].WorldToPeerNodeMap.ContainsKey(node.NetId))
                {
                    return PeerStates[peer].WorldToPeerNodeMap[node.NetId];
                }
                for (byte i = 0; i < MAX_NETWORK_NODES; i++)
                {
                    byte localNodeId = (byte)(i + 1);
                    if ((PeerStates[peer].AvailableNodes & ((long)1 << localNodeId)) == 0)
                    {
                        PeerStates[peer].WorldToPeerNodeMap[node.NetId] = localNodeId;
                        PeerStates[peer].PeerToWorldNodeMap[localNodeId] = node.NetId;
                        var peerState = PeerStates[peer];
                        peerState.AvailableNodes |= (long)1 << localNodeId;
                        PeerStates[peer] = peerState;
                        return localNodeId;
                    }
                }

                Log($"Peer {peer} has reached the maximum amount of nodes.", Debugger.DebugLevel.ERROR);
                return 0;
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
                Debugger.Instance.Log($"Only Net Scenes can be spawned (i.e. a scene where the root node is an NetNode). Attempting to spawn node that isn't a Net Scene: {node.Network.RawNode.Name} on {parent.RawNode.Name}/{netNodePath}", Debugger.DebugLevel.ERROR);
                return null;
            }

            if (parent != null && !parent.IsNetScene())
            {
                Debugger.Instance.Log($"You can only spawn a Net Scene as a child of another Net Scene. Attempting to spawn node on a parent that isn't a Net Scene: {node.Network.RawNode.Name} on {parent.RawNode.Name}/{netNodePath}", Debugger.DebugLevel.ERROR);
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
                    Debugger.Instance.Log($"Cannot spawn {node.Network.RawNode.Name}: RootScene is null on WorldRunner {WorldId}. Was the world created via SetupWorldInstance?", Debugger.DebugLevel.ERROR);
                    return null;
                }
                node.Network.NetParent = RootScene;
                var targetNode = netNodePath == default || netNodePath.IsEmpty ? RootScene.RawNode : RootScene.RawNode.GetNode(netNodePath);
                targetNode.AddChild(node);
            }
            else
            {
                node.Network.NetParent = parent;
                var targetNode = netNodePath == default || netNodePath.IsEmpty ? parent.RawNode : parent.RawNode.GetNode(netNodePath);
                targetNode.AddChild(node);
            }
            node.Network._NetworkPrepare(this);
            node.Network._WorldReady();
            return node;
        }

        internal void JoinPeer(NetPeer peer, string token)
        {
            NetRunner.Instance.PeerWorldMap[peer] = this;
            PeerStates[peer] = new PeerState
            {
                Id = new UUID(),
                Peer = peer,
                Tick = 0,
                Status = PeerSyncStatus.INITIAL,
                Token = token,
                WorldToPeerNodeMap = [],
                PeerToWorldNodeMap = [],
                SpawnAware = [],
                OwnedNodes = []
            };
        }

        internal void ExitPeer(NetPeer peer)
        {
            NetRunner.Instance.PeerWorldMap.Remove(peer);
            PeerStates.Remove(peer);
        }

        // Declare these as fields, not locals - reuse across ticks
        private Dictionary<long, NetBuffer> _peerNodesBuffers = new();
        private Dictionary<long, byte> _peerNodesSerializersList = new();
        private List<long> _orderedNodeKeys = new();
        private NetBuffer _serializersBuffer;
        private NetBuffer _tempSerializerBuffer;
        private Dictionary<long, NetBuffer> _nodeBufferPool = new();

        internal Dictionary<NetPeer, NetBuffer> ExportState(List<NetPeer> peers)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<NetPeer, NetBuffer> peerBuffers = [];

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
                long updatedNodes = 0;
                peerBuffers[peer] = new NetBuffer(); // Need separate buffer per peer for output

                _peerNodesBuffers.Clear();
                _peerNodesSerializersList.Clear();

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

                    byte localNodeId = PeerStates[peer].WorldToPeerNodeMap[netController.NetId];
                    updatedNodes |= (long)1 << localNodeId;
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

                NetWriter.WriteInt64(peerBuffers[peer], updatedNodes);

                // Replace LINQ with manual sort
                _orderedNodeKeys.Clear();
                foreach (var key in _peerNodesBuffers.Keys)
                {
                    _orderedNodeKeys.Add(key);
                }
                _orderedNodeKeys.Sort();

                foreach (var nodeKey in _orderedNodeKeys)
                {
                    NetWriter.WriteByte(peerBuffers[peer], _peerNodesSerializersList[nodeKey]);
                }
                foreach (var nodeKey in _orderedNodeKeys)
                {
                    NetWriter.WriteBytes(peerBuffers[peer], _peerNodesBuffers[nodeKey].WrittenSpan);
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

            return peerBuffers;
        }

        internal void ImportState(NetBuffer stateBytes)
        {
            var affectedNodes = NetReader.ReadInt64(stateBytes);
            var nodeIdToSerializerList = new Dictionary<byte, byte>();
            for (byte i = 0; i < MAX_NETWORK_NODES; i++)
            {
                if ((affectedNodes & ((long)1 << i)) == 0)
                {
                    continue;
                }
                var serializersRun = NetReader.ReadByte(stateBytes);
                nodeIdToSerializerList[i] = serializersRun;
            }

            foreach (var nodeIdSerializerList in nodeIdToSerializerList)
            {
                var localNodeId = nodeIdSerializerList.Key;
                var serializerMask = nodeIdSerializerList.Value;
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
                    var serializerType = serializerInstance.GetType().Name;
                    
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
                        Debugger.Instance.Log($"[ImportState ERROR] Failed to import node {localNodeId} serializer {serializerIdx} ({serializerType}): {ex.Message}. Buffer pos={stateBytes.ReadPosition}/{stateBytes.Length}. Aborting tick import.", Debugger.DebugLevel.ERROR);
                        return; // Don't continue processing - buffer position is corrupted
                    }
                }
            }
        }

        public void PeerAcknowledge(NetPeer peer, Tick tick)
        {
            if (PeerStates[peer].Tick >= tick)
            {
                // Duplicate or old ack - skip
                return;
            }
            
            // Update last ack tick for timeout tracking
            _peerLastAckTick[peer] = tick;
            
            var isFirstAck = PeerStates[peer].Status == PeerSyncStatus.INITIAL;
            if (isFirstAck)
            {
                var newPeerState = PeerStates[peer];
                newPeerState.Tick = tick;
                newPeerState.Status = PeerSyncStatus.IN_WORLD;
                // The first time a peer acknowledges a tick, we know they are in the World
                SetPeerState(peer, newPeerState);
            }

            foreach (var netController in NetScenes.Values)
            {
                for (var serializerIdx = 0; serializerIdx < netController.NetNode.Serializers.Length; serializerIdx++)
                {
                    var serializer = netController.NetNode.Serializers[serializerIdx];
                    serializer.Acknowledge(this, peer, tick);
                }
            }
        }

        public void ClientProcessTick(int incomingTick, byte[] stateBytes)
        {
            if (incomingTick <= CurrentTick)
            {
                return;
            }
            
            CurrentTick = incomingTick;
            
            try
            {
                ImportState(new NetBuffer(stateBytes));
            }
            catch (Exception ex)
            {
                Log($"[ImportState FAILED] tick {incomingTick}: {ex.Message}", Debugger.DebugLevel.ERROR);
                // Still send ack so server doesn't think we're dead - we just couldn't process this tick
            }
            
            foreach (var net_id in NetScenes.Keys)
            {
                var netController = NetScenes[net_id];
                if (netController == null)
                    continue;
                if (netController.RawNode.IsQueuedForDeletion())
                {
                    NetScenes.Remove(net_id);
                    continue;
                }
                netController._NetworkProcess(CurrentTick);
                SendInput(netController);

                foreach (var staticChild in netController.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.RawNode.IsQueuedForDeletion())
                    {
                        continue;
                    }
                    staticChild._NetworkProcess(CurrentTick);
                    SendInput(staticChild);
                }
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

            foreach (var netController in QueueDespawnedNodes)
            {
                DeregisterPeerNode(netController);
                netController.RawNode.QueueFree();
            }
            QueueDespawnedNodes.Clear();

            // Acknowledge tick
            using var buffer = new NetBuffer();
            NetWriter.WriteInt32(buffer, incomingTick);
            NetRunner.SendUnreliableSequenced(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Tick, buffer.ToArray());
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
                    network.RawNode.QueueFree();
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

            if (!netNode.HasInputChanged)
            {
                return;
            }

            using var inputBuffer = new NetBuffer();
            
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
            
            // Write the input size followed by the raw bytes
            var inputBytes = netNode.GetInputBytes();
            NetWriter.WriteInt32(inputBuffer, inputBytes.Length);
            NetWriter.WriteBytes(inputBuffer, inputBytes);

            NetRunner.SendReliable(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Input, inputBuffer.ToArray());
            netNode.ClearInputChanged();
        }

        internal void ReceiveInput(NetPeer peer, NetBuffer buffer)
        {
            if (NetRunner.Instance.IsClient) return;
            var networkId = NetReader.ReadByte(buffer);
            var staticChildId = NetReader.ReadByte(buffer);
            var worldNetId = GetNetIdFromPeerId(peer, networkId);
            var node = GetNodeFromNetId(worldNetId);
            if (node == null)
            {
                Log($"Received input for unknown node {worldNetId}", Debugger.DebugLevel.ERROR);
                return;
            }

            // If this is input for a static child, look it up
            if (staticChildId > 0)
            {
                if (staticChildId >= node.StaticNetworkChildren.Length)
                {
                    Log($"Received input for invalid static child {staticChildId} on node {worldNetId}", Debugger.DebugLevel.ERROR);
                    return;
                }
                node = node.StaticNetworkChildren[staticChildId];
                if (node == null)
                {
                    Log($"Static child {staticChildId} is null on node {worldNetId}", Debugger.DebugLevel.ERROR);
                    return;
                }
            }

            if (!node.InputAuthority.Equals(peer))
            {
                Log($"Received input for node {worldNetId} (staticChild={staticChildId}) from unauthorized peer {peer}", Debugger.DebugLevel.ERROR);
                return;
            }

            // Check if the node supports input
            if (!node.HasInputSupport)
            {
                Log($"Received input for node {worldNetId} (staticChild={staticChildId}) that doesn't support input", Debugger.DebugLevel.ERROR);
                return;
            }

            // Read the input size and bytes
            var inputSize = NetReader.ReadInt32(buffer);
            var inputBytes = NetReader.ReadBytes(buffer, inputSize);
            node.SetInputBytes(inputBytes);
            
            Debug.Send("Input", $"Received {inputSize} bytes for node {worldNetId} (staticChild={staticChildId})");
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
                    NetWriter.WriteByte(buffer, GetPeerNodeId(NetRunner.Instance.Peers[peer], node));
                    NetWriter.WriteByte(buffer, functionId);
                    foreach (var arg in args)
                    {
                        var serialType = Protocol.FromGodotVariantType(arg.VariantType);
                        NetWriter.WriteByType(buffer, serialType, VariantToObject(arg));
                    }
                    NetRunner.SendReliable(NetRunner.Instance.Peers[peer], (byte)NetRunner.ENetChannelId.Function, buffer.ToArray());
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
                NetRunner.SendReliable(NetRunner.Instance.ServerPeer, (byte)NetRunner.ENetChannelId.Function, buffer.ToArray());
            }
        }

        internal void ReceiveNetFunction(NetPeer peer, NetBuffer buffer)
        {
            var netId = NetReader.ReadByte(buffer);
            var functionId = NetReader.ReadByte(buffer);
            var netController = NetRunner.Instance.IsServer ? GetPeerNode(peer, netId) : GetNodeFromNetId(netId);
            if (netController == null)
            {
                Log($"Received net function for unknown node {netId}", Debugger.DebugLevel.ERROR);
                return;
            }
            List<object> args = [];
            var functionInfo = Protocol.UnpackFunction(netController.RawNode.SceneFilePath, functionId);
            foreach (var arg in functionInfo.Arguments)
            {
                var value = NetReader.ReadByType(buffer, arg.VariantType);
                args.Add(value);
            }
            if (NetRunner.Instance.IsServer && (functionInfo.Sources & NetworkSources.Client) == 0)
            {
                return;
            }
            if (NetRunner.Instance.IsClient && (functionInfo.Sources & NetworkSources.Server) == 0)
            {
                return;
            }
            queuedNetFunctions.Add(new QueuedFunction
            {
                Node = netController.RawNode,
                FunctionInfo = functionInfo,
                Args = args.ToArray(),
                Sender = peer
            });
        }

        internal void SendDespawn(NetPeer peer, NetId netId)
        {
            if (!NetRunner.Instance.IsServer) return;
            using var buffer = new NetBuffer();
            NetId.NetworkSerialize(this, peer, netId, buffer);
            NetRunner.SendReliable(peer, (byte)NetRunner.ENetChannelId.Despawn, buffer.ToArray());
        }

        internal void ReceiveDespawn(NetPeer peer, NetBuffer buffer)
        {
            var netId = NetId.NetworkDeserialize(this, peer, buffer);
            GetNodeFromNetId(netId)?.handleDespawn();
        }
    }
}
