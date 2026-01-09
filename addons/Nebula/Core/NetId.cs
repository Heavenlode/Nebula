using System;
using Nebula.Serialization;
using MongoDB.Bson;

namespace Nebula
{
    /// <summary>
    /// A unique identifier for a networked object. The NetId for a node is different between 
    /// the server and client. On the client side, a NetId is only a byte, whereas on the server 
    /// side it is an int64. The server's WorldRunner keeps a map of all NetIds to their 
    /// corresponding value on each client for serialization.
    /// This is a value type (struct) to avoid allocations.
    /// </summary>
    public readonly struct NetId : INetValue<NetId>, IBsonValue<NetId>, IEquatable<NetId>
    {
        /// <summary>
        /// Represents an invalid/unassigned NetId.
        /// </summary>
        public const long NONE = -1;

        /// <summary>
        /// The underlying value.
        /// </summary>
        public readonly long Value;

        /// <summary>
        /// Returns true if this NetId is invalid (NONE).
        /// Use this instead of null checks since NetId is a struct.
        /// </summary>
        public bool IsNone => Value == NONE;

        /// <summary>
        /// Returns true if this NetId is valid (not NONE and not default).
        /// </summary>
        public bool IsValid => Value != NONE && Value != 0;

        /// <summary>
        /// Creates a NetId with the specified value.
        /// </summary>
        public NetId(long value)
        {
            Value = value;
        }

        /// <summary>
        /// Returns an invalid NetId.
        /// </summary>
        public static NetId None => new NetId(NONE);

        public override string ToString() => Value.ToString();

        public override bool Equals(object obj) => obj is NetId other && Equals(other);

        public bool Equals(NetId other) => Value == other.Value;

        public override int GetHashCode() => Value.GetHashCode();

        public static bool operator ==(NetId left, NetId right) => left.Equals(right);

        public static bool operator !=(NetId left, NetId right) => !left.Equals(right);

        /// <summary>
        /// Explicit conversion from long to NetId.
        /// </summary>
        public static explicit operator NetId(long value) => new NetId(value);

        /// <summary>
        /// Explicit conversion from NetId to long.
        /// </summary>
        public static explicit operator long(NetId netId) => netId.Value;

        #region Network Serialization

        public static void NetworkSerialize(WorldRunner currentWorld, NetPeer peer, in NetId value, NetBuffer buffer)
        {
            if (NetRunner.Instance.IsServer)
            {
                NetWriter.WriteByte(buffer, currentWorld.GetPeerWorldState(peer).Value.WorldToPeerNodeMap[value]);
            }
            else
            {
                NetWriter.WriteByte(buffer, (byte)value.Value);
            }
        }

        public static NetId NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            if (NetRunner.Instance.IsServer)
            {
                var id = NetReader.ReadByte(buffer);
                return currentWorld.GetNetIdFromPeerId(peer, id);
            }
            else
            {
                var id = NetReader.ReadByte(buffer);
                return currentWorld.GetNetId(id);
            }
        }

        #endregion

        #region BSON Serialization

        public static BsonValue BsonSerialize(in NetId value)
        {
            return new BsonInt64(value.Value);
        }

        public static NetId BsonDeserialize(BsonValue bson)
        {
            if (bson == null || bson.IsBsonNull)
            {
                return None;
            }
            return new NetId(bson.AsInt64);
        }

        #endregion
    }
}
