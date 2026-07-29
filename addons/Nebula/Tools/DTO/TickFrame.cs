using System;
using System.Collections.Generic;
using LiteDB;

namespace Nebula.Internal.Editor.DTO
{
    public class TickFrame
    {
        public UUID WorldId { get; set; }
        public int Id { get; set; }
        public int GreatestSize { get; set; }
        public DateTime Timestamp { get; set; }
        public BsonDocument PeerPayloads { get; set; } = new BsonDocument();
        public List<BsonDocument> Logs { get; set; } = new List<BsonDocument>();
        public List<BsonDocument> NetFunctionCalls { get; set; } = new List<BsonDocument>();

        /// <summary>
        /// Full world state as RelaxedExtendedJson. Stored as a string rather
        /// than a BsonDocument: the server produces it with MongoDB's
        /// serializer and this record is persisted by LiteDB, whose BSON type
        /// set does not match — and the editor converts it straight to JSON to
        /// hand to GDScript anyway.
        /// </summary>
        public string WorldStateJson { get; set; } = "";
    }
}
