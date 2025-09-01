using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using MongoDB.Bson;

namespace Nebula
{
    /**
    <summary>
    Manages the network state of all <see cref="NetNode"/>s in the scene.
    Inside the <see cref="NetRunner"/> are one or more “Worlds”. Each World represents some part of the game that is isolated from other parts. For example, different maps, dungeon instances, etc. Worlds are dynamically created by calling <see cref="NetRunner.CreateWorld"/>.

    Worlds cannot directly interact with each other and do not share state.

    Players only exist in one World at a time, so it can be helpful to think of the clients as being connected to a World directly.
    </summary>
    */
    public partial class WorldRunner : Node
    {
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
            public HashSet<INetNode> OwnedNodes;
        }

        internal struct QueuedFunction
        {
            public Node Node;
            public ProtocolNetFunction FunctionInfo;
            public Variant[] Args;
            public NetPeer Sender;
        }

        public UUID WorldId { get; internal set; }

        // A bit list of all nodes in use by each peer
        // For example, 0 0 0 0 (... etc ...) 0 1 0 1 would mean that the first and third nodes are in use
        public long ClientAvailableNodes = 0;
        readonly static byte MAX_NETWORK_NODES = 64;
        private Dictionary<NetPeer, PeerState> PeerStates = [];

        [Signal]
        public delegate void OnPeerSyncStatusChangeEventHandler(string peerId, int status);

        private List<QueuedFunction> queuedNetFunctions = [];


        /// <summary>
        /// Only applicable on the client side.
        /// </summary>
        public static WorldRunner CurrentWorld { get; internal set; }

        /// <summary>
        /// Only used by the client to determine the current root scene.
        /// </summary>
        public NetNodeWrapper RootScene;

        internal long networkIdCounter = 0;
        private Dictionary<long, NetId> networkIds = [];
        internal Dictionary<NetId, NetNodeWrapper> NetScenes = [];
        private Godot.Collections.Dictionary<NetPeer, Godot.Collections.Dictionary<byte, Godot.Collections.Dictionary<int, Variant>>> inputStore = [];
        public Godot.Collections.Dictionary<NetPeer, Godot.Collections.Dictionary<byte, Godot.Collections.Dictionary<int, Variant>>> InputStore => inputStore;

        public ENetConnection DebugEnet { get; private set; }
        public enum DebugDataType
        {
            TICK,
            PAYLOADS,
            EXPORT,
            LOGS,
            PEERS,
            CALLS
        }

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

        Callable _OnPeerDisconnected;

        public override void _Ready()
        {
            base._Ready();
            Name = "WorldRunner";

            if (NetRunner.Instance.IsServer)
            {
                DebugEnet = new ENetConnection();
                Error err;
                int port = GetAvailablePort();
                int attempts = 0;
                const int MAX_ATTEMPTS = 1000;
                do
                {
                    err = DebugEnet.CreateHostBound(NetRunner.Instance.ServerAddress, port, NetRunner.Instance.MaxPeers);
                    if (err == Error.Ok) break;
                    port = GetAvailablePort();
                    attempts++;
                } while (attempts < MAX_ATTEMPTS);
                if (err != Error.Ok)
                {
                    Log($"Error starting debug server after {attempts} attempts: {err}", Debugger.DebugLevel.ERROR);
                    return;
                }
                DebugEnet.Compress(ENetConnection.CompressionMode.RangeCoder);
                _OnPeerDisconnected = Callable.From((NetPeer peer) =>
                {
                    if (AutoPlayerCleanup)
                    {
                        CleanupPlayer(peer);
                        return;
                    }
                    var newPeerState = PeerStates[peer];
                    newPeerState.Tick = CurrentTick;
                    newPeerState.Status = PeerSyncStatus.DISCONNECTED;
                    SetPeerState(peer, newPeerState);
                });
                NetRunner.Instance.Connect("OnPeerDisconnected", _OnPeerDisconnected);

                Log($"World {WorldId} debug server started on port {DebugEnet.GetLocalPort()}", Debugger.DebugLevel.VERBOSE);
            }
        }

        public override void _ExitTree()
        {
            base._ExitTree();
            if (NetRunner.Instance.IsServer)
            {
                foreach (var peer in DebugEnet.GetPeers())
                {
                    peer.PeerDisconnect(0);
                }
                DebugEnet.Destroy();
                NetRunner.Instance.Disconnect("OnPeerDisconnected", _OnPeerDisconnected);
            }
        }

        /// <summary>
        /// The current network tick. On the client side, this does not represent the server's current tick, which will always be slightly ahead.
        /// </summary>
        public int CurrentTick { get; internal set; } = 0;

        public NetNodeWrapper GetNodeFromNetId(NetId networkId)
        {
            if (networkId == null)
                return new NetNodeWrapper(null);
            if (!NetScenes.ContainsKey(networkId))
                return new NetNodeWrapper(null);
            return NetScenes[networkId];
        }

        public NetNodeWrapper GetNodeFromNetId(long networkId)
        {
            if (networkId == NetId.NONE)
                return new NetNodeWrapper(null);
            if (!networkIds.ContainsKey(networkId))
                return new NetNodeWrapper(null);
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
                return null;
            return networkIds[id];
        }

        public NetId GetNetIdFromPeerId(NetPeer peer, byte id)
        {
            if (!PeerStates[peer].PeerToWorldNodeMap.ContainsKey(id))
                return null;
            return PeerStates[peer].PeerToWorldNodeMap[id];
        }

        [Signal]
        public delegate void OnAfterNetworkTickEventHandler(Tick tick);

        [Signal]
        public delegate void OnPlayerJoinedEventHandler(UUID peerId);


        /// <summary>
        /// When a player disconnects, we automatically dispose of their data in the World. If you wish to manually handle this,
        /// (e.g. you wish to save their data first), then set this to false, and call <see cref="CleanupPlayer"/> when you are ready to dispose of their data yourself.
        /// <see cref="CleanupPlayer"/> is all that is needed to fully dispose of their data on the server, including freeing their owned nodes (when <see cref="NetworkController.DespawnOnUnowned"/> is true).
        /// </summary>
        public bool AutoPlayerCleanup = true;

        /// <summary>
        /// Immediately disconnects the player from the world and frees all of their data from the server, including freeing their owned nodes (when <see cref="NetworkController.DespawnOnUnowned"/> is true).
        /// </summary>
        /// <param name="peer"></param>
        public void CleanupPlayer(NetPeer peer)
        {
            if (!NetRunner.Instance.IsServer) return;

            if (peer.IsActive())
            {
                peer.PeerDisconnect(0);
            }

            var peerState = PeerStates[peer];
            foreach (var node in peerState.OwnedNodes)
            {
                if (node.Network.DespawnOnUnowned)
                {
                    node.Node.QueueFree();
                }
                else
                {
                    node.Network.SetInputAuthority(null);
                }
            }
            PeerStates.Remove(peer);
            NetRunner.Instance.Peers.Remove(NetRunner.Instance.GetPeerId(peer));
            NetRunner.Instance.WorldPeerMap.Remove(NetRunner.Instance.GetPeerId(peer));
            NetRunner.Instance.PeerWorldMap.Remove(peer);
            NetRunner.Instance.PeerIds.Remove(peer);
        }

        private int _frameCounter = 0;
        /// <summary>
        /// This method is executed every tick on the Server side, and kicks off all logic which processes and sends data to every client.
        /// </summary>
        public async void ServerProcessTick()
        {

            foreach (var net_id in NetScenes.Keys)
            {
                var netNode = NetScenes[net_id];
                if (netNode == null)
                    continue;

                if (!IsInstanceValid(netNode.Node) || netNode.Node.IsQueuedForDeletion())
                {
                    NetScenes.Remove(net_id);
                    continue;
                }
                if (netNode.Node.ProcessMode == ProcessModeEnum.Disabled)
                {
                    continue;
                }
                foreach (var networkChild in netNode.StaticNetworkChildren)
                {
                    if (networkChild.Node == null)
                    {
                        Log($"Network child node is unexpectedly null: {netNode.Node.SceneFilePath}", Debugger.DebugLevel.ERROR);
                    }
                    if (networkChild.Node.ProcessMode == ProcessModeEnum.Disabled)
                    {
                        continue;
                    }
                    networkChild._NetworkProcess(CurrentTick);
                }
                netNode._NetworkProcess(CurrentTick);
            }

            if (DebugEnet != null)
            {
                // Notify the Debugger of the incoming tick
                var debugBuffer = new HLBuffer();
                HLBytes.Pack(debugBuffer, (byte)DebugDataType.TICK);
                HLBytes.Pack(debugBuffer, DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond);
                HLBytes.Pack(debugBuffer, CurrentTick);
                foreach (var debugPeer in DebugEnet.GetPeers())
                {
                    debugPeer.Send(0, debugBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
                }
            }

            foreach (var queuedFunction in queuedNetFunctions)
            {
                var args = queuedFunction.Args;
                if (queuedFunction.FunctionInfo.WithPeer)
                {
                    args = new List<Variant>() { queuedFunction.Sender }.Concat(args).ToArray();
                }
                var functionNode = queuedFunction.Node.GetNode(queuedFunction.FunctionInfo.NodePath) as INetNode;
                functionNode.Network.IsInboundCall = true;
                functionNode.Node.Call(queuedFunction.FunctionInfo.Name, args);
                functionNode.Network.IsInboundCall = false;

                if (DebugEnet != null)
                {
                    // Notify the Debugger of the incoming tick
                    var debugBuffer = new HLBuffer();
                    HLBytes.Pack(debugBuffer, (byte)DebugDataType.CALLS);
                    HLBytes.Pack(debugBuffer, queuedFunction.FunctionInfo.Name);
                    HLBytes.Pack(debugBuffer, (byte)args.Length);
                    foreach (var arg in args)
                    {
                        HLBytes.PackVariant(debugBuffer, arg, packType: true);
                    }
                    foreach (var debugPeer in DebugEnet.GetPeers())
                    {
                        debugPeer.Send(0, debugBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
                    }
                }
            }
            queuedNetFunctions.Clear();

            foreach (var log in tickLogBuffer)
            {
                var logBuffer = new HLBuffer();
                HLBytes.Pack(logBuffer, (byte)DebugDataType.LOGS);
                HLBytes.Pack(logBuffer, (byte)log.Level);
                HLBytes.Pack(logBuffer, log.Message);
                foreach (var debugPeer in DebugEnet.GetPeers())
                {
                    debugPeer.Send(0, logBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
                }
            }
            tickLogBuffer.Clear();

            var fullGameState = BsonTransformer.Instance.ToBSONDocument(RootScene.Node as INetNode, recurse: true);
            var exportBuffer = new HLBuffer();
            HLBytes.Pack(exportBuffer, (byte)DebugDataType.EXPORT);
            HLBytes.Pack(exportBuffer, fullGameState.ToBson());
            foreach (var debugPeer in DebugEnet.GetPeers())
            {
                debugPeer.Send(0, exportBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
            }

            var peers = PeerStates.Keys.ToList();
            var exportedState = ExportState(peers);
            foreach (var peer in peers)
            {
                if (PeerStates[peer].Status == PeerSyncStatus.DISCONNECTED)
                {
                    continue;
                }
                var buffer = new HLBuffer();
                HLBytes.Pack(buffer, CurrentTick);
                HLBytes.Pack(buffer, exportedState[peer].bytes);
                var size = buffer.bytes.Length;
                if (size > NetRunner.MTU)
                {
                    Log($"Data size {size} exceeds MTU {NetRunner.MTU}", Debugger.DebugLevel.WARN);
                }

                peer.Send(1, buffer.bytes, (int)ENetPacketPeer.FlagUnsequenced);
                if (DebugEnet != null)
                {
                    var debugBuffer = new HLBuffer();
                    HLBytes.Pack(debugBuffer, (byte)DebugDataType.PAYLOADS);
                    HLBytes.Pack(debugBuffer, PeerStates[peer].Id.ToByteArray());
                    HLBytes.Pack(debugBuffer, exportedState[peer].bytes);
                    foreach (var debugPeer in DebugEnet.GetPeers())
                    {
                        debugPeer.Send(0, debugBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
                    }
                }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            base._PhysicsProcess(delta);

            if (NetRunner.Instance.IsServer)
            {
                if (DebugEnet != null)
                {
                    DebugEnet.Service();
                }

                _frameCounter += 1;
                if (_frameCounter < NetRunner.PhysicsTicksPerNetworkTick)
                    return;
                _frameCounter = 0;
                CurrentTick += 1;
                ServerProcessTick();
                EmitSignal("OnAfterNetworkTick", CurrentTick);
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

        public void ChangeScene(NetNodeWrapper node)
        {
            if (NetRunner.Instance.IsServer) return;

            if (RootScene != null)
            {
                RootScene.Node.QueueFree();
            }
            Log("Changing scene to " + node.Node.Name);
            // TODO: Support this more generally
            GetTree().CurrentScene.AddChild(node.Node);
            RootScene = node;
            node._NetworkPrepare(this);
            node._WorldReady();
        }

        public PeerState? GetPeerWorldState(UUID peerId)
        {
            var peer = NetRunner.Instance.GetPeer(peerId);
            if (peer == null || !PeerStates.ContainsKey(peer))
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
        public void SetPeerState(UUID peerId, PeerState state)
        {
            var peer = NetRunner.Instance.GetPeer(peerId);
            SetPeerState(peer, state);
        }
        public void SetPeerState(NetPeer peer, PeerState state)
        {
            if (PeerStates[peer].Status != state.Status)
            {
                // TODO: Should this have side-effects?
                EmitSignal("OnPeerSyncStatusChange", NetRunner.Instance.GetPeerId(peer), (int)state.Status);
                if (state.Status == PeerSyncStatus.IN_WORLD)
                {
                    EmitSignal("OnPlayerJoined", NetRunner.Instance.GetPeerId(peer));
                }
            }
            PeerStates[peer] = state;
        }

        public byte GetPeerNodeId(NetPeer peer, NetNodeWrapper node)
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
        public NetNodeWrapper GetPeerNode(NetPeer peer, byte networkId)
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

        internal void DeregisterPeerNode(NetNodeWrapper node, NetPeer peer = null)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (peer == null)
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
        internal byte TryRegisterPeerNode(NetNodeWrapper node, NetPeer peer = null)
        {
            if (NetRunner.Instance.IsServer)
            {
                if (peer == null)
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

            NetScenes[node.NetId] = node;
            return 1;
        }

        public T Spawn<T>(T node, NetNodeWrapper parent = null, NetPeer inputAuthority = null, string nodePath = ".") where T : Node, INetNode
        {
            if (NetRunner.Instance.IsClient) return null;

            node.Network.IsClientSpawn = true;
            node.Network.CurrentWorld = this;
            node.Network.SetInputAuthority(inputAuthority);
            if (parent == null)
            {
                node.Network.NetParent = RootScene;
                node.Network.NetParent.Node.GetNode(nodePath).AddChild(node);
            }
            else
            {
                node.Network.NetParent = parent;
                parent.Node.GetNode(nodePath).AddChild(node);
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

        internal Dictionary<ENetPacketPeer, HLBuffer> ExportState(List<ENetPacketPeer> peers)
        {
            Dictionary<NetPeer, HLBuffer> peerBuffers = [];
            foreach (var node in NetScenes.Values)
            {
                // Initialize serializers
                foreach (var serializer in node.Serializers)
                {
                    serializer.Begin();
                }
            }

            foreach (ENetPacketPeer peer in peers)
            {
                long updatedNodes = 0;
                peerBuffers[peer] = new HLBuffer();
                var peerNodesBuffers = new Dictionary<long, HLBuffer>();
                var peerNodesSerializersList = new Dictionary<long, byte>();
                foreach (var node in NetScenes.Values)
                {
                    var serializersBuffer = new HLBuffer();
                    byte serializersRun = 0;
                    for (var serializerIdx = 0; serializerIdx < node.Serializers.Length; serializerIdx++)
                    {
                        var serializer = node.Serializers[serializerIdx];
                        var serializerResult = serializer.Export(this, peer);
                        if (serializerResult.bytes.Length == 0)
                        {
                            continue;
                        }
                        serializersRun |= (byte)(1 << serializerIdx);
                        HLBytes.Pack(serializersBuffer, serializerResult.bytes);
                    }
                    if (serializersRun == 0)
                    {
                        continue;
                    }
                    byte localNodeId = PeerStates[peer].WorldToPeerNodeMap[node.NetId];
                    updatedNodes |= (long)1 << localNodeId;
                    peerNodesSerializersList[localNodeId] = serializersRun;
                    peerNodesBuffers[localNodeId] = new HLBuffer();
                    HLBytes.Pack(peerNodesBuffers[localNodeId], serializersBuffer.bytes);
                }

                // 1. Pack a bit list of all nodes which have serialized data
                HLBytes.Pack(peerBuffers[peer], updatedNodes);

                // 2. Pack what serializers are run for every node 
                var orderedNodeKeys = peerNodesBuffers.OrderBy(x => x.Key).Select(x => x.Key).ToList();
                foreach (var nodeKey in orderedNodeKeys)
                {
                    HLBytes.Pack(peerBuffers[peer], peerNodesSerializersList[nodeKey]);
                }

                // 3. Pack the serialized data for every node
                foreach (var nodeKey in orderedNodeKeys)
                {
                    HLBytes.Pack(peerBuffers[peer], peerNodesBuffers[nodeKey].bytes);
                }
            }

            foreach (var node in NetScenes.Values)
            {
                // Finally, cleanup serializers
                foreach (var serializer in node.Serializers)
                {
                    serializer.Cleanup();
                }
            }

            return peerBuffers;
        }

        internal void ImportState(HLBuffer stateBytes)
        {
            var affectedNodes = HLBytes.UnpackInt64(stateBytes);
            var nodeIdToSerializerList = new Dictionary<byte, byte>();
            for (byte i = 0; i < MAX_NETWORK_NODES; i++)
            {
                if ((affectedNodes & ((long)1 << i)) == 0)
                {
                    continue;
                }
                var serializersRun = HLBytes.UnpackInt8(stateBytes);
                nodeIdToSerializerList[i] = serializersRun;
            }

            foreach (var nodeIdSerializerList in nodeIdToSerializerList)
            {
                var localNodeId = nodeIdSerializerList.Key;
                var node = GetNodeFromNetId(localNodeId);
                if (node == null)
                {
                    var blankScene = new NetNode3D();
                    blankScene.Network.NetId = AllocateNetId(localNodeId);
                    blankScene.SetupSerializers();
                    NetRunner.Instance.AddChild(blankScene);
                    node = new NetNodeWrapper(blankScene);
                }
                for (var serializerIdx = 0; serializerIdx < node.Serializers.Length; serializerIdx++)
                {
                    if ((nodeIdSerializerList.Value & ((long)1 << serializerIdx)) == 0)
                    {
                        continue;
                    }
                    var serializerInstance = node.Serializers[serializerIdx];
                    serializerInstance.Import(this, stateBytes, out NetNodeWrapper nodeOut);
                    if (node != nodeOut)
                    {
                        node = nodeOut;
                        serializerIdx = 0;
                    }
                }
            }
        }

        public void PeerAcknowledge(NetPeer peer, Tick tick)
        {
            if (PeerStates[peer].Tick >= tick)
            {
                return;
            }
            if (PeerStates[peer].Status == PeerSyncStatus.INITIAL)
            {
                var newPeerState = PeerStates[peer];
                newPeerState.Tick = tick;
                newPeerState.Status = PeerSyncStatus.IN_WORLD;
                // The first time a peer acknowledges a tick, we know they are in the World
                SetPeerState(peer, newPeerState);
            }

            foreach (var node in NetScenes.Values)
            {
                for (var serializerIdx = 0; serializerIdx < node.Serializers.Length; serializerIdx++)
                {
                    var serializer = node.Serializers[serializerIdx];
                    serializer.Acknowledge(this, peer, tick);
                }
            }
        }
        public void ClientHandleTick(int incomingTick, byte[] stateBytes)
        {
            if (incomingTick <= CurrentTick)
            {
                return;
            }
            // GD.Print("INCOMING DATA: " + BitConverter.ToString(stateBytes));
            CurrentTick = incomingTick;
            ImportState(new HLBuffer(stateBytes));
            foreach (var net_id in NetScenes.Keys)
            {
                var node = NetScenes[net_id];
                if (node == null)
                    continue;
                if (node.Node.IsQueuedForDeletion())
                {
                    NetScenes.Remove(net_id);
                    continue;
                }
                node._NetworkProcess(CurrentTick);
                SendInput(node);

                foreach (var staticChild in node.StaticNetworkChildren)
                {
                    if (staticChild == null || staticChild.Node.IsQueuedForDeletion())
                    {
                        continue;
                    }
                    staticChild._NetworkProcess(CurrentTick);
                    SendInput(staticChild);
                }
            }

            foreach (var queuedFunction in queuedNetFunctions)
            {
                var args = queuedFunction.Args;
                if (queuedFunction.FunctionInfo.WithPeer)
                {
                    args = new List<Variant>() { queuedFunction.Sender }.Concat(args).ToArray();
                }
                var functionNode = queuedFunction.Node.GetNode(queuedFunction.FunctionInfo.NodePath) as INetNode;
                functionNode.Network.IsInboundCall = true;
                functionNode.Node.Call(queuedFunction.FunctionInfo.Name, args);
                functionNode.Network.IsInboundCall = false;
            }
            queuedNetFunctions.Clear();

            // Acknowledge tick
            HLBuffer buffer = new HLBuffer();
            HLBytes.Pack(buffer, incomingTick);
            NetRunner.Instance.ENetHost.Send(1, buffer.bytes, (int)ENetPacketPeer.FlagUnsequenced);
        }

        /// <summary>
        /// This is called for nodes that are initialized in a scene by default.
        /// Clients automatically dequeue all network nodes on initialization.
        /// All network nodes on the client side must come from the server by gaining Interest in the node.
        /// </summary>
        /// <param name="wrapper"></param>
        /// <returns></returns>
        public bool CheckStaticInitialization(NetNodeWrapper wrapper)
        {
            if (NetRunner.Instance.IsServer)
            {
                wrapper.NetId = AllocateNetId();
                NetScenes[wrapper.NetId] = wrapper;
            }
            else
            {
                if (!wrapper.IsClientSpawn)
                {
                    wrapper.Node.QueueFree();
                    return false;
                }
            }

            return true;
        }

        internal void SendInput(NetNodeWrapper netNode)
        {
            if (NetRunner.Instance.IsServer) return;
            var setInputs = netNode.InputBuffer.Keys.Aggregate((long)0, (acc, key) =>
            {
                if (netNode.PreviousInputBuffer.ContainsKey(key) && netNode.PreviousInputBuffer[key].Equals(netNode.InputBuffer[key]))
                {
                    return acc;
                }
                acc |= (long)1 << key;
                return acc;
            });
            if (setInputs == 0)
            {
                return;
            }

            var inputBuffer = NetId.NetworkSerialize(this, NetRunner.Instance.ENetHost, netNode.NetId);
            HLBytes.Pack(inputBuffer, setInputs);
            foreach (var key in netNode.InputBuffer.Keys)
            {
                if ((setInputs & ((long)1 << key)) == 0)
                {
                    continue;
                }
                netNode.PreviousInputBuffer[key] = netNode.InputBuffer[key];
                HLBytes.Pack(inputBuffer, key);
                HLBytes.PackVariant(inputBuffer, netNode.InputBuffer[key], true, true);
            }

            NetRunner.Instance.ENetHost.Send((int)NetRunner.ENetChannelId.Input, inputBuffer.bytes, (int)ENetPacketPeer.FlagReliable);
            netNode.InputBuffer = [];
        }

        internal void ReceiveInput(NetPeer peer, HLBuffer buffer)
        {
            if (NetRunner.Instance.IsClient) return;
            var networkId = HLBytes.UnpackByte(buffer);
            var worldNetId = GetNetIdFromPeerId(peer, networkId);
            var node = GetNodeFromNetId(worldNetId);
            if (node == null)
            {
                Log($"Received input for unknown node {worldNetId}", Debugger.DebugLevel.ERROR);
                return;
            }

            if (node.InputAuthority != peer)
            {
                Log($"Received input for node {worldNetId} from unauthorized peer {peer}", Debugger.DebugLevel.ERROR);
                return;
            }

            var setInputs = HLBytes.UnpackInt64(buffer);
            while (setInputs > 0)
            {
                var key = HLBytes.UnpackInt8(buffer);
                var value = HLBytes.UnpackVariant(buffer);
                if (value.HasValue)
                {
                    node.InputBuffer[key] = value.Value;
                }
                setInputs &= ~((long)1 << key);
            }
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
                    var buffer = NetId.NetworkSerialize(this, NetRunner.Instance.Peers[peer], netId);
                    HLBytes.Pack(buffer, GetPeerNodeId(NetRunner.Instance.Peers[peer], node));
                    HLBytes.Pack(buffer, functionId);
                    foreach (var arg in args)
                    {
                        HLBytes.PackVariant(buffer, arg);
                    }
                    NetRunner.Instance.Peers[peer].Send((int)NetRunner.ENetChannelId.Function, buffer.bytes, (int)ENetPacketPeer.FlagReliable);
                }
            }
            else
            {
                var buffer = NetId.NetworkSerialize(this, NetRunner.Instance.ENetHost, netId);
                HLBytes.Pack(buffer, functionId);
                foreach (var arg in args)
                {
                    HLBytes.PackVariant(buffer, arg);
                }
                NetRunner.Instance.ENetHost.Send((int)NetRunner.ENetChannelId.Function, buffer.bytes, (int)ENetPacketPeer.FlagReliable);
            }
        }

        internal void ReceiveNetFunction(NetPeer peer, HLBuffer buffer)
        {
            var netId = HLBytes.UnpackByte(buffer);
            var functionId = HLBytes.UnpackByte(buffer);
            var node = NetRunner.Instance.IsServer ? GetPeerNode(peer, netId) : GetNodeFromNetId(netId);
            List<Variant> args = [];
            var functionInfo = ProtocolRegistry.Instance.UnpackFunction(node.Node.SceneFilePath, functionId);
            foreach (var arg in functionInfo.Arguments)
            {
                var result = HLBytes.UnpackVariant(buffer, knownType: arg.VariantType);
                if (!result.HasValue)
                {
                    Log($"Failed to unpack argument of type {arg} for function {functionInfo.Name}", Debugger.DebugLevel.ERROR);
                    return;
                }
                args.Add(result.Value);
            }
            if (NetRunner.Instance.IsServer && (functionInfo.Sources & NetFunction.NetworkSources.Client) == 0)
            {
                return;
            }
            if (NetRunner.Instance.IsClient && (functionInfo.Sources & NetFunction.NetworkSources.Server) == 0)
            {
                return;
            }
            queuedNetFunctions.Add(new QueuedFunction
            {
                Node = node.Node,
                FunctionInfo = functionInfo,
                Args = args.ToArray(),
                Sender = peer
            });
        }
    }
}