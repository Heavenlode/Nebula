namespace Nebula.Serialization.Serializers
{
    /// <summary>
    /// Defines an object which the server utilizes to serialize and send data to the client, 
    /// and the client can then receive and deserialize from the server.
    /// </summary>
    public interface IStateSerializer
    {
        public void Begin();

        /// <summary>
        /// Client-side only. Receive and deserialize binary received from the server.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="data">The network buffer containing serialized data</param>
        /// <param name="nodeOut">Output network controller</param>
        public void Import(WorldRunner currentWorld, NetBuffer data, out NetworkController nodeOut);

        /// <summary>
        /// Server-side only. Serialize and write data to the provided buffer.
        /// Writes nothing if there's no data to export.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The target peer</param>
        /// <param name="buffer">Buffer to write serialized data into</param>
        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer);

        /// <summary>
        /// Server-side only. Called when a peer acknowledges the packet exported at
        /// <paramref name="tick"/>. Implementations must only commit state that was sent
        /// at or before that tick.
        /// </summary>
        /// <returns>
        /// True if this serializer still has unacknowledged data for the peer; false when
        /// fully acked. When every serializer of a node returns false, the node is removed
        /// from the per-peer pending-ack set (it re-enters on its next export).
        /// </returns>
        public bool Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick);

        public void Cleanup();
        
        /// <summary>
        /// Server-side only. Called when a peer disconnects to clean up any per-peer cached data.
        /// This prevents memory leaks from accumulating peer-specific state.
        /// </summary>
        /// <param name="peerId">The UUID of the disconnecting peer</param>
        public void CleanupPeer(UUID peerId) { }
    }
}
