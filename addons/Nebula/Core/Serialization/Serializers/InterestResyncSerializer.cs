using System.Collections.Generic;
using Godot;

namespace Nebula.Serialization.Serializers
{
    /// <summary>
    /// Serializer that syncs interest state to clients at a staggered interval.
    /// Writes 1 byte (0 or 1) to indicate interest state.
    /// Staggered by node ID to spread network load across ticks.
    /// </summary>
    public partial class InterestResyncSerializer : RefCounted, IStateSerializer
    {
        private const int SYNC_INTERVAL = 3; // ~10hz at 30 TPS
        
        private NetworkController network;
        
        // Client-side: tracks current interest state
        private bool clientHasInterest = false;

        public InterestResyncSerializer(NetworkController controller)
        {
            network = controller;
        }

        public void Begin() { }
        public void Cleanup() { }
        public void CleanupPeer(UUID peerId) { }
        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck) { }

        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            // Only sync after the node has been spawned for this peer
            if (!currentWorld.HasSpawnedForClient(network.NetId, peer))
            {
                return;
            }
            
            // Stagger by node ID so not all nodes sync on same tick
            int tickOffset = (int)(network.NetId.Value % SYNC_INTERVAL);
            if ((currentWorld.CurrentTick + tickOffset) % SYNC_INTERVAL != 0)
            {
                return;
            }
            
            bool isInterested = network.IsPeerInterested(peer);
            NetWriter.WriteByte(buffer, isInterested ? (byte)1 : (byte)0);
        }

        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;
            if (network == null) return;
            
            byte interestByte = NetReader.ReadByte(buffer);
            bool hasInterest = interestByte == 1;
            
            // Only fire event if state actually changed
            if (hasInterest != clientHasInterest)
            {
                clientHasInterest = hasInterest;
                network.FireInterestChanged(hasInterest);
            }
        }
    }
}
