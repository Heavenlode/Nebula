using System.Collections.Generic;

namespace Nebula.Serialization
{
    /// <summary>
    /// Splits a tick's node list into the ones a given peer has INPUT AUTHORITY over and
    /// everything else, so <c>WorldRunner.ExportState</c> can serve a peer's own nodes before
    /// it serves the crowd.
    ///
    /// <para>The point is latency the player can feel. A remote character stuttering is
    /// cosmetic; the player's OWN character stuttering is the game feeling broken, because it
    /// is the one node whose delay they perceive through their own input. Without this the
    /// budget is handed out in world order for spawns and round-robin for props, so walking
    /// into a crowd makes your own character compete on equal terms with twenty strangers.</para>
    ///
    /// <para>Selection order only, exactly like <see cref="ExportRotation"/>: wire order stays
    /// ascending peer-local node id, because sections are accumulated into per-node buffers and
    /// the packet is assembled from the node bitmask afterwards.</para>
    /// </summary>
    internal static class ExportPartition
    {
        /// <summary>
        /// Whether <paramref name="peer"/> holds input authority over <paramref name="node"/>.
        ///
        /// <para>Compares <c>ID</c> rather than using <c>Equals</c>, matching the authorization
        /// check in <c>WorldRunner.HandleInput</c> and <c>SpawnSerializer</c> — <c>ENet.Peer</c>
        /// is a struct wrapping a native pointer plus a cached id, and the id is the identity
        /// the whole codebase keys on.</para>
        ///
        /// <para>The <c>IsSet</c> guard is load-bearing, not defensive: an unowned node has
        /// <c>default(NetPeer)</c>, whose <c>ID</c> is 0. Drop the guard and every unowned node
        /// in the world reads as owned by whichever peer happens to hold id 0.</para>
        ///
        /// <para>Deliberately NOT <c>PeerState.OwnedNodes</c>, which looks like the ready-made
        /// answer and is not: it both over-includes (static children are added to it but are
        /// never separate export units) and under-includes (dynamic network children inherit
        /// authority through a raw field write in <c>_NetworkPrepare</c> that bypasses
        /// <c>SetInputAuthority</c>, so an owned nested net scene never lands in the set).
        /// Reading the authority off the node cannot drift from the authority the input path
        /// authorizes against.</para>
        /// </summary>
        public static bool IsOwnedBy(NetworkController node, NetPeer peer)
            => node.InputAuthority.IsSet && node.InputAuthority.ID == peer.ID;

        /// <summary>
        /// Fills <paramref name="owned"/> and <paramref name="shared"/> from
        /// <paramref name="source"/>, preserving source (world) order within each side.
        ///
        /// <para><b>Disjoint and exhaustive, and both properties are load-bearing.</b> A node
        /// that lands in NEITHER list never has its props serializer exported for this peer this
        /// tick — and <c>NetPropertiesSerializer.Begin()</c> has already consumed and cleared the
        /// node's dirty mask for the tick, so those changes die with no bank into
        /// <c>PendingDirtyMask</c>. That is silent, permanent data loss, not a dropped frame. A
        /// node in BOTH lists would have its spawn serializer exported twice, which breaks the
        /// one-Export-per-node-per-peer-per-tick invariant the props phase relies on.</para>
        /// </summary>
        public static void Partition(
            List<NetworkController> source,
            NetPeer peer,
            List<NetworkController> owned,
            List<NetworkController> shared)
        {
            owned.Clear();
            shared.Clear();
            for (var i = 0; i < source.Count; i++)
            {
                var node = source[i];
                if (IsOwnedBy(node, peer))
                {
                    owned.Add(node);
                }
                else
                {
                    shared.Add(node);
                }
            }
        }
    }
}
