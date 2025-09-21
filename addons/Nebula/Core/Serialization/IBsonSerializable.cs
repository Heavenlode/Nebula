using System.Threading.Tasks;
using Godot;
using MongoDB.Bson;

namespace Nebula.Serialization {

    // Non-generic base interface
    public interface IBsonSerializableBase {
        BsonValue BsonSerialize(Variant context);
        
        /// <summary>
        /// Virtual method called during BSON deserialization to allow custom deserialization logic.
        /// Override this in derived classes to handle type-specific deserialization.
        /// </summary>
        /// <param name="context">The deserialization context</param>
        /// <param name="doc">The BSON document being deserialized</param>
        Task OnBsonDeserialize(Variant context, BsonDocument doc);
    }

    // Generic interface inherits from base
    public interface IBsonSerializable<T> : IBsonSerializableBase where T : GodotObject {
        // BsonSerialize is inherited from IBsonSerializableBase
        // OnBsonDeserialize is inherited from IBsonSerializableBase
        
        /// <summary>
        /// Static method for BSON deserialization. This handles the base deserialization
        /// and then calls the virtual OnBsonDeserialize method for custom logic.
        /// </summary>
        static abstract Task<T> BsonDeserialize(Variant context, byte[] bson, T initialObject);

        /// <summary>
        /// Instance method for BSON deserialization convenience.
        /// </summary>
        Task<T> BsonDeserialize(Variant context, byte[] bson);
    }
}