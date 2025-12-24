using Godot;
using MongoDB.Bson;

namespace Nebula.Serialization
{
    public partial class NetNodeCommonBsonDeserializeContext : RefCounted
    {
        public BsonDocument bsonDocument;
    }

}