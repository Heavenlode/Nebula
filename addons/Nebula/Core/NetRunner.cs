global using NetPeer = Godot.ENetPacketPeer;
global using Tick = System.Int32;
using System.Collections.Generic;
using Godot;
using Nebula.Serialization;
using System;
using Nebula.Utility.Tools;
using Nebula.Authentication;

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
        /// <param name="port"></param>
        public void OverridePort(int port) {
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

        public Dictionary<UUID, WorldRunner> Worlds { get; private set; } = [];
        internal ENetConnection ENet;
        internal ENetPacketPeer ENetHost;

        internal Dictionary<UUID, NetPeer> Peers = [];
        internal Dictionary<NetPeer, UUID> PeerIds = [];
        internal Dictionary<UUID, List<NetPeer>> WorldPeerMap = [];
        internal Dictionary<NetPeer, WorldRunner> PeerWorldMap = [];

        public NetPeer GetPeer(UUID id)
        {
            if (Peers.ContainsKey(id))
            {
                return Peers[id];
            }
            return null;
        }

        public UUID GetPeerId(NetPeer peer)
        {
            if (PeerIds.ContainsKey(peer))
            {
                return PeerIds[peer];
            }
            return null;
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
        }

        /// <summary>
        /// This is only used to prevent plugins from using reserved channels or reserving each other's channels.
        /// </summary>
        private Dictionary<int, Callable> ReservedChannels = [];
        /// <summary>
        /// Reserve a channel for custom use, e.g. within plugins. If the channel is already reserved, it will throw an exception. 
        /// </summary>
        /// <param name="channel"></param>
        /// <exception cref="Exception"></exception>
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

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            if (Instance != null)
            {
                QueueFree();
            }
            Instance = this;
        }

        public override void _Ready() {
            ProtocolRegistry.Instance.Load();
        }

        private ENetConnection debugEnet;

        public IAuthenticator Authentication { get; private set; }
        public void SetAuthentication(IAuthenticator authentication) {
            if (Authentication != null) {
                Debugger.Instance.Log("Setting authentication on NetRunner after it was already set. This is only a bug if it was unintentional.", Debugger.DebugLevel.WARN);
            }
            Connect("OnPeerConnected", Callable.From((NetPeer peer) => {
                Authentication.ServerAuthenticateClient(peer);
            }));
            Connect("OnConnectedToServer", Callable.From(() => {
                Authentication.ClientAuthenticateWithServer();
            }));
            Authentication = authentication;
        }

        public void StartServer()
        {
            if (Authentication == null) {
                SetAuthentication(new DefaultAuthenticator());
            }

            IsServer = true;
            Debugger.Instance.Log("Starting Server");
            GetTree().MultiplayerPoll = false;

            ENet = new ENetConnection();
            var err = ENet.CreateHostBound(ServerAddress, Port, MaxPeers);
            if (err != Error.Ok)
            {
                Debugger.Instance.Log($"Error starting: {err}", Debugger.DebugLevel.ERROR);
                return;
            }
            ENet.Compress(ENetConnection.CompressionMode.RangeCoder);
            NetStarted = true;
            Debugger.Instance.Log($"Started on {ServerAddress}:{Port}");

            debugEnet = new ENetConnection();
            err = debugEnet.CreateHostBound(ServerAddress, DebugPort, MaxPeers);
            if (err != Error.Ok)
            {
                Debugger.Instance.Log($"Error starting debug server: {err}", Debugger.DebugLevel.ERROR);
                return;
            }
            debugEnet.Compress(ENetConnection.CompressionMode.RangeCoder);
            Debugger.Instance.Log($"Started debug server on {ServerAddress}:{DebugPort}", Debugger.DebugLevel.VERBOSE);
        }

        public void StartClient()
        {
            ENet = new ENetConnection();
            ENet.CreateHost();
            ENetHost = ENet.ConnectToHost(ServerAddress, Port);
            ENet.Compress(ENetConnection.CompressionMode.RangeCoder);
            if (ENetHost == null)
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
        public static int TPS = Engine.PhysicsTicksPerSecond / PhysicsTicksPerNetworkTick;

        /// <summary>
        /// Maximum Transferrable Unit. The maximum number of bytes that should be sent in a single ENet UDP Packet (i.e. a single tick)
        /// Not a hard limit.
        /// </summary>
        public static int MTU => ProjectSettings.GetSetting("Nebula/network/mtu", 1400).AsInt32();

        private void _debugService() {
            if (debugEnet == null) return;
            while (true)
            {
                var enetEvent = debugEnet.Service();
                var eventType = enetEvent[0].As<ENetConnection.EventType>();
                if (eventType == ENetConnection.EventType.None)
                {
                    break;
                }
                var packetPeer = enetEvent[1].As<ENetPacketPeer>();
                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                        foreach (var worldId in Worlds.Keys) {
                            var world = Worlds[worldId];
                            var buffer = new HLBuffer();
                            HLBytes.Pack(buffer, worldId.ToByteArray());
                            HLBytes.Pack(buffer, world.DebugPort);
                            packetPeer.Send(0, buffer.bytes, (int)ENetPacketPeer.FlagReliable);
                        }
                        break;
                }
                
            }
        }


        // We could enable this if the ReservedChannels system is insufficient.
        // [Signal]
        // public delegate void OnMessageReceivedEventHandler(NetPeer peer, int channel, byte[] data);


        [Signal]
        public delegate void OnPeerConnectedEventHandler(NetPeer peer);

        [Signal]
        public delegate void OnPeerDisconnectedEventHandler(NetPeer peer);

        [Signal]
        public delegate void OnConnectedToServerEventHandler();

        /// <inheritdoc/>
        public override void _PhysicsProcess(double delta)
        {
            if (!NetStarted)
                return;

            _debugService();

            while (true)
            {
                var enetEvent = ENet.Service();
                var eventType = enetEvent[0].As<ENetConnection.EventType>();
                if (eventType == ENetConnection.EventType.None)
                {
                    break;
                }
                var packetPeer = enetEvent[1].As<ENetPacketPeer>();
                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                        if (IsServer) {
                            Debugger.Instance.Log("Peer connected");
                            EmitSignal("OnPeerConnected", packetPeer);
                        } else {
                            Debugger.Instance.Log("Connected to server");
                            EmitSignal("OnConnectedToServer");
                        }
                        break;
                    case ENetConnection.EventType.Disconnect:
                        _OnPeerDisconnected(packetPeer);
                        break;
                    case ENetConnection.EventType.Receive:
                        var channel = enetEvent[3].As<int>();
                        var data = new HLBuffer(packetPeer.GetPacket());
                        switch ((ENetChannelId)channel)
                        {
                            case ENetChannelId.Tick:
                                if (IsServer)
                                {
                                    var tick = HLBytes.UnpackInt32(data);
                                    PeerWorldMap[packetPeer].PeerAcknowledge(packetPeer, tick);
                                }
                                else
                                {
                                    if (data.bytes.Length == 0)
                                    {
                                        break;
                                    }
                                    var tick = HLBytes.UnpackInt32(data);
                                    var bytes = HLBytes.UnpackByteArray(data, untilEnd: true);
                                    WorldRunner.CurrentWorld.ClientHandleTick(tick, bytes);
                                }
                                break;
                            case ENetChannelId.Input:
                                if (IsServer)
                                {
                                    PeerWorldMap[packetPeer].ReceiveInput(packetPeer, data);
                                }
                                else
                                {
                                    // Clients should never receive messages on the Input channel
                                    break;
                                }
                                break;
                            case ENetChannelId.Function:
                                if (IsServer) {
                                    PeerWorldMap[packetPeer].ReceiveNetFunction(packetPeer, data);
                                } else {
                                    WorldRunner.CurrentWorld.ReceiveNetFunction(ENetHost, data);
                                }
                                break;
                            default:
                                if (ReservedChannels.ContainsKey(channel)) {
                                    ReservedChannels[channel].Call(packetPeer, data.bytes);
                                }
                                break;
                        }
                        break;
                }
            }
        }

        public void PeerJoinWorld(NetPeer peer, UUID worldId, string token = "")
        {
            var peerId = new UUID();
            Peers[peerId] = peer;
            PeerIds[peer] = peerId;

            foreach (var node in GetTree().GetNodesInGroup("global_interest"))
            {
                var wrapper = new NetNodeWrapper(node);
                if (wrapper == null) continue;
                wrapper.SetPeerInterest(peerId, Int64.MaxValue, true);
            }
            Worlds[worldId].JoinPeer(peer, token);
        }

        [Signal]
        public delegate void OnWorldCreatedEventHandler(WorldRunner world);

        public WorldRunner CreateWorld(UUID worldId, PackedScene scene)
        {
            if (!IsServer) return null;
            var node = new NetNodeWrapper(scene.Instantiate());
            return SetupWorldInstance(worldId, node);
        }

        public WorldRunner SetupWorldInstance(UUID worldId, NetNodeWrapper node)
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
            // godotPhysicsWorld.ProcessThreadGroup = ProcessThreadGroupEnum.SubThread;
            godotPhysicsWorld.AddChild(worldRunner);
            godotPhysicsWorld.AddChild(node.Node);
            GetTree().CurrentScene.AddChild(godotPhysicsWorld);
            node._NetworkPrepare(worldRunner);
            node._WorldReady();
            EmitSignal("OnWorldCreated", worldRunner);
            if (debugEnet != null) {
                foreach (var peer in debugEnet.GetPeers()) {
                    GD.Print(worldRunner.DebugPort);
                    // peer.Send(0, , (int)ENetPacketPeer.FlagReliable);
                }
            }
            return worldRunner;
        }

        public void _OnPeerDisconnected(ENetPacketPeer peer)
        {
            Debugger.Instance.Log($"Peer disconnected peerId: {peer}");
            EmitSignal("OnPeerDisconnected", peer);
        }
    }
}