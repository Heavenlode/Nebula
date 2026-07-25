namespace Nebula.Serialization.Serializers
{
    public static class BitConstants
    {
        public const int BitsInByte = 8;
        public const int BitsInShort = 16;
        public const int BitsInInt = 32;
        public const int BitsInLong = 64;

        /// <summary>
        /// Maximum networked properties per NetScene (including rolled-up static children).
        /// Bound by the 64-bit dirty mask in NetworkController and the fixed size of
        /// CachedProperties. Enforced at build time by the protocol generator (NEBULA004)
        /// and at runtime by NetPropertiesSerializer's constructor.
        /// </summary>
        public const int MaxSceneProperties = BitsInLong;
    }
}