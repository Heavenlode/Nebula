using System;
using Nebula.Serialization;
using MongoDB.Bson;

namespace Nebula
{
    /// <summary>
    /// A UUID implementation for Nebula. Serializes into 16 bytes.
    /// This is a value type (struct) to avoid allocations.
    /// </summary>
    public readonly struct UUID : INetValue<UUID>, IBsonValue<UUID>, IEquatable<UUID>
    {
        /// <summary>
        /// The underlying GUID value.
        /// </summary>
        public readonly Guid Guid;

        /// <summary>
        /// Returns true if this UUID is empty (all zeros).
        /// Use this instead of null checks since UUID is a struct.
        /// </summary>
        public bool IsEmpty => Guid == Guid.Empty;

        /// <summary>
        /// Creates a new random UUID.
        /// </summary>
        public UUID()
        {
            Guid = Guid.NewGuid();
        }

        /// <summary>
        /// Creates a UUID from a string representation.
        /// </summary>
        public UUID(string value)
        {
            Guid = Guid.Parse(value);
        }

        /// <summary>
        /// Creates a UUID from a byte array (must be 16 bytes).
        /// </summary>
        public UUID(byte[] value)
        {
            Guid = new Guid(value);
        }

        /// <summary>
        /// Creates a UUID from an existing Guid.
        /// </summary>
        public UUID(Guid guid)
        {
            Guid = guid;
        }

        /// <summary>
        /// Creates a new random UUID. Equivalent to new UUID().
        /// </summary>
        public static UUID NewUUID() => new UUID();

        /// <summary>
        /// Returns an empty UUID (all zeros).
        /// </summary>
        public static UUID Empty => default;

        public override string ToString() => Guid.ToString();

        public override bool Equals(object obj) => obj is UUID other && Equals(other);

        public bool Equals(UUID other) => Guid.Equals(other.Guid);

        public override int GetHashCode() => Guid.GetHashCode();

        public static bool operator ==(UUID left, UUID right) => left.Equals(right);

        public static bool operator !=(UUID left, UUID right) => !left.Equals(right);

        /// <summary>
        /// Returns the UUID as a 16-byte array. Note: This allocates a new array.
        /// Prefer using Guid directly when possible.
        /// </summary>
        public byte[] ToByteArray() => Guid.ToByteArray();

        #region Network Serialization

        public static void NetworkSerialize(WorldRunner currentWorld, NetPeer peer, in UUID value, NetBuffer buffer)
        {
            if (value.IsEmpty)
            {
                NetWriter.WriteByte(buffer, 0);
                return;
            }
            NetWriter.WriteByte(buffer, 1);
            NetWriter.WriteBytes(buffer, value.Guid.ToByteArray());
        }

        public static UUID NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            var nullFlag = NetReader.ReadByte(buffer);
            if (nullFlag == 0)
            {
                return default;
            }
            return new UUID(NetReader.ReadBytes(buffer, 16));
        }

        #endregion

        #region BSON Serialization

        public static BsonValue BsonSerialize(in UUID value)
        {
            if (value.IsEmpty)
            {
                return BsonNull.Value;
            }
            return new BsonBinaryData(value.Guid, GuidRepresentation.Standard);
        }

        public static UUID BsonDeserialize(BsonValue bson)
        {
            if (bson == null || bson.IsBsonNull)
            {
                return default;
            }
            
            var binaryData = bson.AsBsonBinaryData;
            var guid = GuidConverter.FromBytes(binaryData.Bytes, GuidRepresentation.Standard);
            return new UUID(guid);
        }

        #endregion
    }
}
