using System.Threading.Tasks;
using MongoDB.Bson;

namespace Nebula.Serialization
{
    /// <summary>
    /// Context for BSON serialization of complex reference types.
    /// Replaces the Godot.Variant context to avoid Godot bridge crossings.
    /// Named NetBsonContext to avoid collision with MongoDB.Bson.Serialization.BsonSerializationContext.
    /// </summary>
    public struct NetBsonContext
    {
        /// <summary>
        /// Whether to recursively serialize child nodes.
        /// </summary>
        public bool Recurse;

        /// <summary>
        /// Optional filter function for nodes during serialization.
        /// </summary>
        public System.Func<object, bool> NodeFilter;

        /// <summary>
        /// Optional game-specific context data (e.g., player ID for per-peer serialization).
        /// </summary>
        public object CustomContext;

        public static NetBsonContext Default => new NetBsonContext { Recurse = true };
    }

    /// <summary>
    /// Interface for value types (structs) that can be serialized to/from BSON.
    /// Uses static-only methods to avoid boxing. No async to avoid Task allocation.
    /// </summary>
    /// <typeparam name="T">The struct type being serialized</typeparam>
    public interface IBsonValue<T> where T : struct
    {
        /// <summary>
        /// Serialize the value to a BSON value.
        /// </summary>
        static abstract BsonValue BsonSerialize(in T value);

        /// <summary>
        /// Deserialize a value from BSON.
        /// </summary>
        static abstract T BsonDeserialize(BsonValue bson);
    }

    /// <summary>
    /// Non-generic base interface for reference type BSON serialization.
    /// </summary>
    public interface IBsonSerializableBase
    {
        BsonValue BsonSerialize(NetBsonContext context);

        /// <summary>
        /// Virtual method called during BSON deserialization to allow custom deserialization logic.
        /// Override this in derived classes to handle type-specific deserialization.
        /// </summary>
        /// <param name="context">The deserialization context</param>
        /// <param name="doc">The BSON document being deserialized</param>
        Task OnBsonDeserialize(NetBsonContext context, BsonDocument doc);
    }

    /// <summary>
    /// Generic interface for reference types that can be serialized to/from BSON.
    /// Inherits from base for polymorphic access.
    /// </summary>
    public interface IBsonSerializable<T> : IBsonSerializableBase where T : class
    {
        /// <summary>
        /// Static method for BSON deserialization. This handles the base deserialization
        /// and then calls the virtual OnBsonDeserialize method for custom logic.
        /// </summary>
        static abstract Task<T> BsonDeserialize(NetBsonContext context, byte[] bson, T initialObject);

        /// <summary>
        /// Instance method for BSON deserialization convenience.
        /// </summary>
        Task<T> BsonDeserialize(NetBsonContext context, byte[] bson);
    }
}
