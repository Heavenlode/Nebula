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
        /// </summary>
        [Export] public string ServerAddress = "127.0.0.1";

        /// <summary>
        /// The port for the server to listen on, and the client to connect to.
        /// </summary>
        [Export] public int Port { get; private set; } = 8888;

        /// <summary>
        /// Manually/dynamically override the port for the server to listen on, and the client to connect to.
        /// </summary>
        public void OverridePort(int port)
        {
            Debugger.Instance.Log($"Overriding port to {port}", Debugger.DebugLevel.VERBOSE);
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
        /// </summary>
        private const int MaxChannels = 16;

        public Dictionary<UUID, WorldRunner> Worlds { get; private set; } = [];
        internal Host ENetHost;
        internal Peer ServerPeer;

        internal Dictionary<UUID, NetPeer> Peers = [];
        internal Dictionary<NetPeer, UUID> PeerIds = [];
        internal Dictionary<uint, NetPeer> PeersByNativeId = [];
        internal Dictionary<UUID, List<NetPeer>> WorldPeerMap = [];
        internal Dictionary<NetPeer, WorldRunner> PeerWorldMap = [];

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
            if (PeerIds.TryGetValue(peer, out var id))
            {
                return id;
            }
            return default;
        }

        /// <summary>
        /// This is set after <see cref="StartClient"/> or <see cref="StartServer"/> is called, i.e. when <see cref="NetStarted"/> == true. Before that, this value is unreliable.
        /// </summary>
        public bool IsServer { get; private set; }

        public bool IsClient => !IsServer;

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
            /// Despawn request.
            /// </summary>
            Despawn = 4,
        }

        /// <summary>
        /// This is only used to prevent plugins from using reserved channels or reserving each other's channels.
        /// </summary>
        private Dictionary<int, Callable> ReservedChannels = [];

        /// <summary>
        /// Reserve a channel for custom use, e.g. within plugins. If the channel is already reserved, it will throw an exception.
        /// The handler Callable receives (uint peerId, byte[] packetData). Use GetPeerByNativeId() to get the peer.
        /// </summary>
        public void ReserveChannel(int channel, Callable handler)
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
                Debugger.Instance.Log("Setting authentication on NetRunner after it was already set. This is only a bug if it was unintentional.", Debugger.DebugLevel.WARN);
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
                Debugger.Instance.Log($"Error starting: {ex.Message}", Debugger.DebugLevel.ERROR);
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
                Debugger.Instance.Log($"Started debug server on {ServerAddress}:{DebugPort}", Debugger.DebugLevel.VERBOSE);
            }
            catch (Exception ex)
            {
                Debugger.Instance.Log($"Error starting debug server: {ex.Message}", Debugger.DebugLevel.ERROR);
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

            ServerPeer = ENetHost.Connect(address, MaxChannels);

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
        public static int MTU => ProjectSettings.GetSetting("Nebula/network/mtu", 1400).AsInt32();

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
                            SendPacket(netEvent.Peer, 0, buffer.ToArray(), PacketFlags.Reliable);
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
                        _OnPeerDisconnected(netEvent.Peer);
                        break;

                    case EventType.Receive:
                        var channel = netEvent.ChannelID;
                        var packetData = new byte[netEvent.Packet.Length];
                        netEvent.Packet.CopyTo(packetData);
                        netEvent.Packet.Dispose();

                        var data = new NetBuffer(packetData);

                        switch ((ENetChannelId)channel)
                        {
                            case ENetChannelId.Tick:
                                if (IsServer)
                                {
                                    var tick = NetReader.ReadInt32(data);
                                    PeerWorldMap[netEvent.Peer].PeerAcknowledge(netEvent.Peer, tick);
                                }
                                else
                                {
                                    if (packetData.Length == 0)
                                    {
                                        break;
                                    }
                                    var tick = NetReader.ReadInt32(data);
                                    var bytes = NetReader.ReadRemainingBytes(data);
                                    WorldRunner.CurrentWorld.ClientProcessTick(tick, bytes);
                                }
                                break;

                            case ENetChannelId.Input:
                                if (IsServer)
                                {
                                    PeerWorldMap[netEvent.Peer].ReceiveInput(netEvent.Peer, data);
                                }
                                // Clients should never receive messages on the Input channel
                                break;

                            case ENetChannelId.Function:
                                if (IsServer)
                                {
                                    PeerWorldMap[netEvent.Peer].ReceiveNetFunction(netEvent.Peer, data);
                                }
                                else
                                {
                                    WorldRunner.CurrentWorld.ReceiveNetFunction(ServerPeer, data);
                                }
                                break;

                            case ENetChannelId.Despawn:
                                if (!IsServer)
                                {
                                    WorldRunner.CurrentWorld.ReceiveDespawn(ServerPeer, data);
                                }
                                // Server should never receive despawn requests
                                break;

                            default:
                                if (ReservedChannels.TryGetValue(channel, out var handler))
                                {
                                    handler.Call(netEvent.Peer.ID, packetData);
                                }
                                break;
                        }
                        break;
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
        /// Helper method to send a reliable packet.
        /// </summary>
        public static void SendReliable(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.Reliable);
        }

        /// <summary>
        /// Helper method to send an unreliable packet.
        /// </summary>
        public static void SendUnreliable(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.None);
        }

        /// <summary>
        /// Helper method to send an unreliable sequenced packet (newer packets discard older ones).
        /// </summary>
        public static void SendUnreliableSequenced(Peer peer, byte channelId, byte[] data)
        {
            SendPacket(peer, channelId, data, PacketFlags.Unsequenced);
        }

        public void PeerJoinWorld(NetPeer peer, UUID worldId, string token = "")
        {
            var peerId = new UUID();
            Peers[peerId] = peer;
            PeerIds[peer] = peerId;
            Worlds[worldId].JoinPeer(peer, token);
        }

        public event Action<WorldRunner> OnWorldCreated;

        public WorldRunner CreateWorld(UUID worldId, PackedScene scene)
        {
            if (!IsServer) return null;
            var node = scene.Instantiate();
            if (node is not INetNodeBase netNodeBase)
            {
                Debugger.Instance.Log($"Failed to create world: root node is not a NetworkController", Debugger.DebugLevel.ERROR);
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
