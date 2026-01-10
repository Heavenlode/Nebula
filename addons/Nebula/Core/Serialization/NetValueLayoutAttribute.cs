using System;

namespace Nebula.Serialization
{
    /// <summary>
    /// Specifies the size in bytes of a value type implementing INetValue&lt;T&gt;.
    /// Required for the PropertyCache union struct generator to calculate safe field offsets.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public class NetValueLayoutAttribute : Attribute
    {
        /// <summary>
        /// The size of the struct in bytes.
        /// </summary>
        public int SizeInBytes { get; }

        /// <summary>
        /// Creates a new NetValueLayoutAttribute with the specified size.
        /// </summary>
        /// <param name="sizeInBytes">The size of the struct in bytes (e.g., sizeof(long) = 8)</param>
        public NetValueLayoutAttribute(int sizeInBytes)
        {
            if (sizeInBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Size must be positive");
            SizeInBytes = sizeInBytes;
        }
    }
}
