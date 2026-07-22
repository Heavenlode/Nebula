global using NetPeer = ENet.Peer;
global using Tick = System.Int32;
using System.Collections.Generic;
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
        /// The port for the debug server to listen on.
        /// </summary>
        public const int DebugPort = 59910;

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
        internal Dictionary<UUID, List<NetPeer>> WorldPeerMap = [];
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
            // Protocol is fully static - no initialization needed
        }

        public override void _ExitTree()
        {
            ENetHost?.Flush();
            ENetHost?.Dispose();
            debugEnet?.Flush();
            debugEnet?.Dispose();

            if (_libraryInitialized && Instance == this)
            {
                Library.Deinitialize();
                _libraryInitialized = false;
            }
        }

        private Host debugEnet;

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
                if (peer.IsSet)
                {
                    Authentication.ServerAuthenticateClient(peer);
                }
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

            // Debug server
            debugEnet = new Host();
            var debugAddress = new Address();
            // Note: For server, only set Port. Do NOT call SetHost - this binds to all interfaces
            debugAddress.Port = (ushort)DebugPort;

            try
            {
                debugEnet.Create(debugAddress, MaxPeers, MaxChannels);
                Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"Started debug server on {ServerAddress}:{DebugPort}");
            }
            catch (Exception ex)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Error starting debug server: {ex.Message}");
                debugEnet.Dispose();
                debugEnet = null;
            }
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
            var worldRunner = new WorldRunner();
            WorldRunner.CurrentWorld = worldRunner;
            GetTree().CurrentScene.AddChild(worldRunner);
            Debugger.Instance.Log("Started");
        }

        /// <summary>
        /// This determines how fast the network sends data. When physics runs at 60 ticks per second, then at 2 PhysicsTicksPerNetworkTick, the network runs at 30hz.
        /// </summary>
        public const int PhysicsTicksPerNetworkTick = 2;

        /// <summary>
        /// Ticks Per Second. The number of Ticks which are expected to elapse every second.
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
        public static int MTU => ProjectSettings.GetSetting("Nebula/config/mtu", 1400).AsInt32();

        private static bool? _logTickPayloads;
        /// <summary>
        /// Debug: when enabled via the <c>Nebula/config/log_tick_payloads</c> project setting, the
        /// client logs the full hex of every server tick payload. Cached on first read, so toggling
        /// it takes effect on the next run.
        /// </summary>
        public static bool LogTickPayloads =>
            _logTickPayloads ??= ProjectSettings.GetSetting("Nebula/config/log_tick_payloads", false).AsBool();

        private void _debugService()
        {
            if (debugEnet == null) return;

            Event netEvent;
            while (debugEnet.CheckEvents(out netEvent) > 0 || debugEnet.Service(0, out netEvent) > 0)
            {
                switch (netEvent.Type)
                {
                    case EventType.None:
                        return;

                    case EventType.Connect:
                        foreach (var worldId in Worlds.Keys)
                        {
                            var world = Worlds[worldId];
                            using var buffer = new NetBuffer();
                            NetWriter.WriteBytes(buffer, worldId.ToByteArray());
                            NetWriter.WriteInt32(buffer, world.DebugPort);
                            SendPacket(netEvent.Peer, 0, buffer, PacketFlags.Reliable);
                        }
                        break;

                    case EventType.Receive:
                        netEvent.Packet.Dispose();
                        break;
                }
            }
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

        /// <inheritdoc/>
        public override void _PhysicsProcess(double delta)
        {
            if (!NetStarted)
                return;

            _debugService();

            Event netEvent;
            int checkResult = ENetHost.CheckEvents(out netEvent);
            int serviceResult = 0;
            
            if (checkResult <= 0)
            {
                serviceResult = ENetHost.Service(0, out netEvent);
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
                                netEvent.Peer.Disconnect(ProtocolMismatchDisconnectCode);
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
                        var packetData = new byte[netEvent.Packet.Length];
                        netEvent.Packet.CopyTo(packetData);
                        netEvent.Packet.Dispose();

                        using var data = new NetBuffer(packetData);

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
                                    if (packetData.Length == 0)
                                    {
                                        break;
                                    }
                                    var tick = NetReader.ReadInt32(data);
                                    var peerId = GetPeerId(netEvent.Peer);
                                    if (PeerWorldMap.TryGetValue(peerId, out var world))
                                    {
                                        world.PeerAcknowledge(netEvent.Peer, tick);
                                    }
                                }
                                else
                                {
                                    if (packetData.Length == 0)
                                    {
                                        break;
                                    }
                                    var tick = NetReader.ReadInt32(data);
                                    var bytes = NetReader.ReadRemainingBytes(data);
                                    // Debug: dump the full payload hex for every server tick
                                    // (gated behind the Nebula/config/log_tick_payloads setting).
                                    if (LogTickPayloads)
                                    {
                                        Debugger.Instance.Log(Debugger.DebugLevel.INFO,
                                            $"[Nebula][TickPayload] tick={tick} ({bytes.Length} bytes) {Convert.ToHexString(bytes)}");
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
                                        world.ReceiveInput(netEvent.Peer, data);
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
                                        world.ReceiveNetFunction(netEvent.Peer, data);
                                    }
                                }
                                else
                                {
                                    WorldRunner.CurrentWorld.ReceiveNetFunction(ServerPeer, data);
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
                                netEvent.Peer.Disconnect(MalformedPacketDisconnectCode);
                            }
                        }
                        break;
                    }
                }

                // Check for more events
                checkResult = ENetHost.CheckEvents(out netEvent);
                if (checkResult <= 0)
                {
                    serviceResult = ENetHost.Service(0, out netEvent);
                }
            }
        }

        /// <summary>
        /// Helper method to send a packet to a peer.
        /// </summary>
        public static void SendPacket(Peer peer, byte channelId, byte[] data, PacketFlags flags)
        {
            var packet = default(Packet);
            packet.Create(data, flags);
            peer.Send(channelId, ref packet);
        }

        /// <summary>
        /// Helper method to send a packet using a NetBuffer directly (zero-allocation).
        /// Uses the buffer's internal array with proper length to avoid ToArray() allocation.
        /// </summary>
        public static void SendPacket(Peer peer, byte channelId, NetBuffer buffer, PacketFlags flags)
        {
            var packet = default(Packet);
            packet.Create(buffer.RawBuffer, buffer.Length, flags);
            peer.Send(channelId, ref packet);
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
            var peerId = new UUID();
            Peers[peerId] = peer;
            PeerIds[peer.ID] = peerId;
            Worlds[worldId].JoinPeer(peer, token);
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
        /// fires the target's PlayerSpawnManager to (re)spawn the player under the same identity.
        /// </summary>
        public void MigratePeerToWorld(NetPeer peer, WorldRunner target)
        {
            if (!IsServer || target == null) return;

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

        public WorldRunner CreateWorld(UUID worldId, PackedScene scene)
        {
            if (!IsServer) return null;
            var node = scene.Instantiate();
            if (node is not INetNodeBase netNodeBase)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Failed to create world: root node is not a NetworkController");
                return null;
            }
            return SetupWorldInstance(worldId, netNodeBase.Network);
        }

        public WorldRunner SetupWorldInstance(UUID worldId, NetworkController node)
        {
            if (!IsServer) return null;
            var godotPhysicsWorld = new SubViewport
            {
                OwnWorld3D = true,
                World3D = new World3D(),
                Name = worldId.ToString()
            };
            var worldRunner = new WorldRunner
            {
                WorldId = worldId,
                RootScene = node,
            };
            Worlds[worldId] = worldRunner;
            WorldPeerMap[worldId] = [];
            godotPhysicsWorld.AddChild(worldRunner);
            godotPhysicsWorld.AddChild(node.RawNode);
            GetTree().CurrentScene.AddChild(godotPhysicsWorld);
            node._NetworkPrepare(worldRunner);
            node._WorldReady();
            worldRunner.Debug?.Send("WorldCreated", worldId.ToString());
            OnWorldCreated?.Invoke(worldRunner);
            return worldRunner;
        }

        public void _OnPeerDisconnected(Peer peer)
        {
            Debugger.Instance.Log($"Peer disconnected peerId: {peer.ID}");
            OnPeerDisconnected?.Invoke(peer.ID);
            PeersByNativeId.Remove(peer.ID);
        }
    }
}
