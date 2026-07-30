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

        /// <summary>
        /// Maximum static network nodes per NetScene, excluding the scene root (which is
        /// implicitly static child id 0). Bound by the byte-wide static child id the
        /// protocol generator assigns from 1 upward and that NetNode serializes on the
        /// wire, so ids 1..255 are available.
        /// </summary>
        public const int MaxStaticNetNodes = 255;
    }
}