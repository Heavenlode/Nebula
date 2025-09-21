using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using MongoDB.Bson;
using System.Threading.Tasks;

namespace Nebula
{
    /**
    <summary>
    A unique identifier for a networked object. The NetId for a node is different between the server and client. On the client side, a NetId is only a byte, whereas on the server side it is an int64. The server's <see cref="WorldRunner"/> keeps a map of all NetIds to their corresponding value on each client for serialization.
    </summary>
    */
    [SerialTypeIdentifier("NetId")]
    public partial class NetId : RefCounted, INetSerializable<NetId>, IBsonSerializable<NetId>
    {
        public static int NONE = -1;
        public NetNodeWrapper Node { get; private set; }
        public long Value { get; private set; }
        internal NetId(long value)
        {
            Value = value;
        }
        public async Task OnBsonDeserialize(Variant context, BsonDocument doc)
        {
            // NetId deserialization is handled entirely in the static method
            await Task.CompletedTask;
        }

        public static async Task<NetId> BsonDeserialize(Variant context, byte[] bson, NetId initialObject)
        {
            var bsonValue = BsonTransformer.Instance.DeserializeBsonValue<BsonInt64>(bson);
            var result = new NetId(bsonValue.Value);
            
            // Call the virtual method for custom deserialization logic
            var doc = new BsonDocument { ["value"] = bsonValue };
            await result.OnBsonDeserialize(context, doc);
            
            return result;
        }
        
        public async Task<NetId> BsonDeserialize(Variant context, byte[] bson)
        {
            return await BsonDeserialize(context, bson, this);
        }

        public BsonValue BsonSerialize(Variant context)
        {
            return new BsonInt64(Value);
        }
        public static HLBuffer NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetId obj)
        {
            var buffer = new HLBuffer();
            if (NetRunner.Instance.IsServer) {
                HLBytes.Pack(buffer, currentWorld.GetPeerWorldState(peer).Value.WorldToPeerNodeMap[obj]);
            } else {
                HLBytes.Pack(buffer, (byte)obj.Value);
            }
            return buffer;
        }

        public static Variant GetDeserializeContext(NetId obj)
        {
            return new Variant();
        }
        public static NetId NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, Variant ctx)
        {
            if (NetRunner.Instance.IsServer) {
                var id = HLBytes.UnpackInt8(buffer);
                return currentWorld.GetNetIdFromPeerId(peer, id);
            } else {
                var id = HLBytes.UnpackByte(buffer);
                return currentWorld.GetNetId(id);
            }
        }
    }
}