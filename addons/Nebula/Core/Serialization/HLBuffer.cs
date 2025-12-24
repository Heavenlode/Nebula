using Godot;

namespace Nebula.Serialization
{
    /// <summary>
    /// Standard object used to package data that will be transferred across the network.
    /// Used extensively by <see cref="HLBytes"/>.
    /// </summary>
    public partial class HLBuffer: RefCounted
    {
        public HLBuffer()
        {
            bytes = [];
        }

        public HLBuffer(byte[] bytes)
        {
            this.bytes = bytes;
        }

        public byte[] bytes;
        public int Pointer { get; internal set; } = 0;
        public bool IsPointerEnd => Pointer >= bytes.Length;
        public const int CONSISTENCY_BUFFER_SIZE_LIMIT = 256;
        public byte[] RemainingBytes => bytes[Pointer..];

        public int Length => bytes.Length;

        /// <summary>
        /// Resets the pointer to 0, allowing the buffer to be reused for reading from the beginning.
        /// </summary>
        public void ResetPointer()
        {
            Pointer = 0;
        }
    }
}