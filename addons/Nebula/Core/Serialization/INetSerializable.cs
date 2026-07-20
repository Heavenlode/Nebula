using System;

namespace Nebula.Serialization
{
    /// <summary>
    /// Interface for types that need to notify their parent NetworkController when internal state mutates.
    /// Used by NetArray to signal that element-level changes need to trigger serialization.
    /// </summary>
    public interface INetPropertyBindable
    {
        /// <summary>
        /// Bind a callback to be invoked when internal state changes.
        /// The callback should mark the property dirty in the NetworkController.
        /// </summary>
        /// <param name="onMutated">Callback to invoke on internal mutation</param>
        void BindToNetProperty(Action onMutated);
    }

    /// <summary>
    /// Optional interface for INetSerializable reference types that keep a global
    /// (cross-peer) dirty set. OnExportComplete is invoked once per node per server tick,
    /// after Export has run for every peer, so the type can fold global dirty state into
    /// per-peer pending state and clear it. Without this, clearing global dirty flags on a
    /// single peer's ack would drop resends for the remaining peers.
    /// </summary>
    public interface INetExportAware
    {
        void OnExportComplete();
    }

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
    /// Objects implementing this interface own their per-peer state and self-filter during serialization.
    /// </summary>
    /// <typeparam name="T">The type being serialized</typeparam>
    public interface INetSerializable<T> where T : class
    {
        /// <summary>
        /// Serialize the object to the network buffer.
        /// Returns true if data was written, false if nothing to send for this peer.
        /// Simple types (NetNode3D) ignore maxBytes and always return true.
        /// Complex types (NetArray) use maxBytes for chunking and return false when nothing to send.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The target peer</param>
        /// <param name="obj">The object to serialize</param>
        /// <param name="buffer">The buffer to write to</param>
        /// <param name="maxBytes">Maximum bytes to write (for chunked serialization). Simple types may ignore this.</param>
        /// <returns>True if data was written, false if nothing to send</returns>
        public static abstract bool NetworkSerialize(WorldRunner currentWorld, NetPeer peer, T obj, NetBuffer buffer, int maxBytes);

        /// <summary>
        /// Deserialize an object from the network buffer.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The source peer</param>
        /// <param name="buffer">The buffer to read from</param>
        /// <param name="existing">Optional existing instance to update (for delta encoding support)</param>
        /// <returns>The deserialized object (may be the updated existing instance or a new one)</returns>
        public static abstract T NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, T existing = null);

        /// <summary>
        /// Called when peer acknowledges receipt of data.
        /// Complex types should commit pending state to confirmed state, but only
        /// state that was sent at or before <paramref name="tick"/> — an ack only proves
        /// the peer received the packet exported at that tick, not anything sent later.
        /// Simple types should no-op.
        /// </summary>
        /// <param name="obj">The object instance</param>
        /// <param name="peerId">The peer that acknowledged</param>
        /// <param name="tick">The tick the peer acknowledged</param>
        public static abstract void OnPeerAcknowledge(T obj, UUID peerId, Tick tick);

        /// <summary>
        /// Called when a peer disconnects. Clean up any per-peer state.
        /// Simple types should no-op.
        /// </summary>
        /// <param name="obj">The object instance</param>
        /// <param name="peerId">The peer that disconnected</param>
        public static abstract void OnPeerDisconnected(T obj, UUID peerId);
    }
}
