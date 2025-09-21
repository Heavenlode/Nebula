using Godot;
using System.Collections.Generic;
using System;
using MongoDB.Bson;

namespace Nebula.Serialization
{
    public partial class NetNodeCommonBsonDeserializeContext : RefCounted
    {
        public BsonDocument bsonDocument;
    }

}