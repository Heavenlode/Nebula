using System;
using System.ComponentModel;
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using MongoDB.Bson;
using System.Threading.Tasks;
using Nebula.Utility;
using MongoDB.Bson.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
    /**
		<summary>
		<see cref="Node3D">Node3D</see>, extended with Nebula networking capabilities. This is the most basic networked 3D object.
		See <see cref="NetNode"/> for more information.
		</summary>
	*/
    [SerialTypeIdentifier("NetNode"), Icon("res://addons/Nebula/Core/NetNode3D.png")]
    public partial class NetNode3D : Node3D, INetNode<NetNode3D>, INotifyPropertyChanged
    {
        public Node Node => this;
        public NetworkController Network { get; internal set; }
        public NetNode3D()
        {
            Network = new NetworkController(this);
        }
        // Cannot have more than 8 serializers
        public IStateSerializer[] Serializers { get; private set; } = [];

        public override void _Notification(int what)
        {
            if (what == NotificationSceneInstantiated)
            {
                Network.Setup();
            }
        }

        public virtual long InitializeInterest(NetPeer peer)
        {
            // By default, the peer has full interest in the node.
            return long.MaxValue;
        }

        public void SetupSerializers()
        {
            var spawnSerializer = new SpawnSerializer();
            AddChild(spawnSerializer);
            spawnSerializer.Setup();
            var propertySerializer = new NetPropertiesSerializer();
            AddChild(propertySerializer);
            propertySerializer.Setup();
            Serializers = [spawnSerializer, propertySerializer];
        }

        public virtual void _WorldReady() { }
        public virtual void _NetworkProcess(int _tick) { }

        /// <inheritdoc/>
        public override void _PhysicsProcess(double delta) { }
        public static HLBuffer NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetNode3D obj)
        {
            var buffer = new HLBuffer();
            if (obj == null)
            {
                HLBytes.Pack(buffer, (byte)0);
                return buffer;
            }
            NetId targetNetId;
            byte staticChildId = 0;
            if (obj.Network.IsNetScene())
            {
                targetNetId = obj.Network.NetId;
            }
            else
            {
                if (ProtocolRegistry.Instance.PackNode(obj.Network.NetParent.Node.SceneFilePath, obj.Network.NetParent.Node.GetPathTo(obj), out staticChildId))
                {
                    targetNetId = obj.Network.NetParent.NetId;
                }
                else
                {
                    throw new Exception($"Failed to pack node: {obj.Network.NetParent.Node.SceneFilePath} cannot find static child {obj.Network.NetParent.Node.GetPathTo(obj)}: {obj.GetPath()}");
                }
            }
            var peerNodeId = currentWorld.GetPeerWorldState(peer).Value.WorldToPeerNodeMap[targetNetId];
            HLBytes.Pack(buffer, peerNodeId);
            HLBytes.Pack(buffer, staticChildId);
            return buffer;
        }

        public static Variant GetDeserializeContext(NetNode3D obj)
        {
            return new Variant();
        }
        public static NetNode3D NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, Variant ctx)
        {
            var networkID = HLBytes.UnpackByte(buffer);
            if (networkID == 0)
            {
                return null;
            }
            var staticChildId = HLBytes.UnpackByte(buffer);
            var node = currentWorld.GetNodeFromNetId(networkID).Node as NetNode3D;
            if (staticChildId > 0)
            {
                node = node.GetNodeOrNull(ProtocolRegistry.Instance.UnpackNode(node.SceneFilePath, staticChildId)) as NetNode3D;
            }
            return node;
        }

        public virtual BsonValue BsonSerialize(Variant context)
        {
            return NetNodeCommon.ToBSONDocument(this, context);
        }

        /// <summary>
        /// Virtual method called during BSON deserialization. Override in derived classes
        /// to add custom deserialization logic. Always call base.OnBsonDeserialize() first.
        /// </summary>
        public virtual async Task OnBsonDeserialize(Variant context, BsonDocument doc)
        {
            // Base implementation - no custom logic needed for NetNode3D
            // Derived classes should override this method
            await Task.CompletedTask;
        }

        public async Task<NetNode3D> BsonDeserialize(Variant context, byte[] bson)
        {
            return await BsonDeserialize(context, bson, this);
        }

        public static async Task<NetNode3D> BsonDeserialize(Variant context, byte[] bson, NetNode3D obj)
        {
            var doc = BsonSerializer.Deserialize<BsonDocument>(bson);
            if (context.VariantType != Variant.Type.Nil)
            {
                try
                {
                    context.As<NetNodeCommonBsonDeserializeContext>().bsonDocument = doc;
                }
                catch (InvalidCastException)
                {
                    Debugger.Instance.Log("Context is not a NetNodeCommonBsonDeserializeContext", Debugger.DebugLevel.ERROR);
                }
            }

            if (doc == NetNodeCommon.NullBsonDocument)
            {
                return null;
            }

            // Perform base BSON deserialization (NetNodeCommon handles natural instantiation)
            var newNode = await NetNodeCommon.FromBSON(ProtocolRegistry.Instance, context, doc, obj);

            // Call the virtual method for custom deserialization logic
            await newNode.OnBsonDeserialize(context, doc);

            return newNode;
        }

        public string NodePathFromNetScene()
        {
            if (Network.IsNetScene())
            {
                return GetPathTo(this);
            }

            return Network.NetParent.Node.GetPathTo(this);
        }

        // public static Task<NetNode3D> BsonDeserialize(Variant context, byte[] bson, NetNode3D initialObject)
        // {
        //     throw new NotImplementedException();
        // }

        // public Task<R> BsonDeserialize<R>(Variant context, byte[] bson) where R : NetNode3D
        // {
        //     throw new NotImplementedException();
        // }
    }
}
