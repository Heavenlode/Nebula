using System;

namespace Nebula
{
    /// <summary>
    /// Defines class-level interest requirements for a network scene.
    /// Controls whether a scene spawns for a peer based on their interest layers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class NetInterest : Attribute
    {
        /// <summary>
        /// Peer must have ANY of these interest layers set (OR logic).
        /// If 0, no "any" check is performed.
        /// </summary>
        public long Any = 0;

        /// <summary>
        /// Peer must have ALL of these interest layers set (AND logic).
        /// If 0, no "required" check is performed.
        /// </summary>
        public long Required = 0;
    }
}
