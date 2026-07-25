using System;

namespace Nebula
{
    /// <summary>
    /// Thrown on the client when the server rejects the connection because the two builds
    /// have different protocol hashes (different scenes, properties, functions, or wire
    /// format version). Subscribe to <see cref="NetRunner.OnProtocolMismatch"/> to handle
    /// this without an exception (e.g. to show an "update required" screen); if no handler
    /// is subscribed, NetRunner throws this from its event pump.
    /// </summary>
    public class ProtocolMismatchException : Exception
    {
        /// <summary>The full 64-bit protocol hash of this (local) build.</summary>
        public ulong LocalProtocolHash { get; }

        /// <summary>The 32-bit fold of the local hash that was sent in the connect handshake.</summary>
        public uint LocalHandshakeHash { get; }

        public ProtocolMismatchException(ulong localProtocolHash, uint localHandshakeHash)
            : base($"Server rejected connection: protocol hash mismatch. This build's protocol hash is 0x{localProtocolHash:X16} (handshake 0x{localHandshakeHash:X8}). The client and server were built from different protocol versions - scenes, [NetProperty]/[NetFunction] definitions, or the Nebula wire format differ. Rebuild both from the same source, or update the client.")
        {
            LocalProtocolHash = localProtocolHash;
            LocalHandshakeHash = localHandshakeHash;
        }
    }
}
