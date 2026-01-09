namespace Nebula.Serialization
{
    /// <summary>
    /// Interface for value types (structs) that can be serialized/deserialized over the network.
    /// Uses static-only methods to avoid boxing. Pass structs with 'in' to avoid copies.
    /// </summary>
    /// <typeparam name="T">The struct type being serialized</typeparam>
    public interface INetValue<T> where T : struct
    {
        /// <summary>
        /// Serialize the value to the network buffer.
        /// </summary>
        static abstract void NetworkSerialize(WorldRunner currentWorld, NetPeer peer, in T value, NetBuffer buffer);

        /// <summary>
        /// Deserialize a value from the network buffer.
        /// </summary>
        static abstract T NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer);
    }

    /// <summary>
    /// Interface for reference types (classes) that can be serialized/deserialized over the network.
    /// Implementations should use NetWriter/NetReader for strongly-typed, zero-allocation serialization.
    /// </summary>
    /// <typeparam name="T">The type being serialized</typeparam>
    public interface INetSerializable<T> where T : class
    {
        /// <summary>
        /// Serialize the object to the network buffer.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The target peer</param>
        /// <param name="obj">The object to serialize</param>
        /// <param name="buffer">The buffer to write to</param>
        public static abstract void NetworkSerialize(WorldRunner currentWorld, NetPeer peer, T obj, NetBuffer buffer);

        /// <summary>
        /// Deserialize an object from the network buffer.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The source peer</param>
        /// <param name="buffer">The buffer to read from</param>
        /// <returns>The deserialized object</returns>
        public static abstract T NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer);
    }
}
