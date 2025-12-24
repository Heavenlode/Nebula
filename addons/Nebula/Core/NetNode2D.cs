using System;
using System.ComponentModel;
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using MongoDB.Bson;
using System.Threading.Tasks;

namespace Nebula
{
    /**
		<summary>
		<see cref="Node2D">Node2D</see>, extended with Nebula networking capabilities. This is the most basic networked 2D object.
		See <see cref="NetNode"/> for more information.
		</summary>
	*/
    [SerialTypeIdentifier("NetNode"), Icon("res://addons/Nebula/Core/NetNode2D.png")]
    public partial class NetNode2D : Node2D, INetNode<NetNode2D>, INotifyPropertyChanged
    {
        public Node Node => this;
        public NetworkController Network { get; internal set; }
        public NetNode2D()
        {
            Network = new NetworkController(this);
        }
        // Cannot have more than 8 serializers
        public IStateSerializer[] Serializers { get; private set; } = [];

        public void SetupSerializers()
        {
            var spawnSerializer = new SpawnSerializer();
            AddChild(spawnSerializer);
            var propertySerializer = new NetPropertiesSerializer();
            AddChild(propertySerializer);
            Serializers = [spawnSerializer, propertySerializer];
        }

        public virtual void _WorldReady() { }
        public virtual void _NetworkProcess(int _tick) { }

        /// <inheritdoc/>
        public override void _PhysicsProcess(double delta) { }
        public static HLBuffer NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetNode2D obj)
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
                    throw new Exception($"Failed to pack node: {obj.GetPath()}");
                }
            }
            var peerNodeId = currentWorld.GetPeerWorldState(peer).Value.WorldToPeerNodeMap[targetNetId];
            HLBytes.Pack(buffer, peerNodeId);
            HLBytes.Pack(buffer, staticChildId);
            return buffer;
        }

        public static Variant GetDeserializeContext(NetNode2D obj)
        {
            return new Variant();
        }
        public static NetNode2D NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, Variant ctx)
        {
            var networkID = HLBytes.UnpackByte(buffer);
            if (networkID == 0)
            {
                return null;
            }
            var staticChildId = HLBytes.UnpackByte(buffer);
            var node = currentWorld.GetNodeFromNetId(networkID).Node as NetNode2D;
            if (staticChildId > 0)
            {
                node = node.GetNodeOrNull(ProtocolRegistry.Instance.UnpackNode(node.SceneFilePath, staticChildId)) as NetNode2D;
            }
            return node;
        }

        public BsonValue BsonSerialize(Variant context)
        {
            var doc = new BsonDocument();
            if (Network.IsNetScene())
            {
                doc["NetId"] = Network.NetId.BsonSerialize(context);
            }
            else
            {
                doc["NetId"] = Network.NetParent.NetId.BsonSerialize(context);
                doc["StaticChildPath"] = Network.NetParent.Node.GetPathTo(this).ToString();
            }
            return doc;
        }

        public virtual async Task OnBsonDeserialize(Variant context, BsonDocument doc)
        {
            // Base implementation - no custom logic needed for NetNode2D
            // Derived classes should override this method
            await Task.CompletedTask;
        }

        public async Task<NetNode2D> BsonDeserialize(Variant context, byte[] bson)
        {
            return await BsonDeserialize(context, bson, this);
        }

        public static async Task<NetNode2D> BsonDeserialize(Variant context, byte[] bson, NetNode2D obj)
        {
            var data = BsonTransformer.Instance.DeserializeBsonValue<BsonDocument>(bson);
            if (data.IsBsonNull) return null;
            var doc = data.AsBsonDocument;
            var node = obj ?? new NetNode2D();
            
            // NetNode2D-specific deserialization logic
            node.Network._prepareNetId = await NetId.BsonDeserialize(context, BsonTransformer.Instance.SerializeBsonValue(doc["NetId"]), node.Network.NetId);
            if (doc.Contains("StaticChildPath"))
            {
                node.Network._prepareStaticChildPath = doc["StaticChildPath"].AsString;
            }
            
            // Call the virtual method for custom deserialization logic
            await node.OnBsonDeserialize(context, doc);
            
            return node;
        }


        public string NodePathFromNetScene()
        {
            if (Network.IsNetScene())
            {
                return GetPathTo(this);
            }

            return Network.NetParent.Node.GetPathTo(this);
        }
    }
}
