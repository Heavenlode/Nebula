using System;

namespace Nebula
{
    public enum NetLerpMode
    {
        /// <summary>No interpolation - snap to new values</summary>
        None = 0,
        /// <summary>Chase target exponentially each frame - responsive, minimal latency</summary>
        Smooth = 1,
        /// <summary>Interpolate between buffered past states - perfectly smooth, adds latency</summary>
        Buffered = 2,
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class NetProperty : Attribute
    {
        public enum SyncFlags
        {
            LinearInterpolation = 1 << 0,
            LossyConsistency = 1 << 1,
        }

        public SyncFlags Flags;
        public long InterestMask = long.MaxValue;
        
        /// <summary>
        /// How to interpolate this property on clients.
        /// </summary>
        public NetLerpMode LerpMode = NetLerpMode.None;
        
        /// <summary>
        /// For Smooth mode: higher = faster catch-up (10-20 typical)
        /// For Buffered mode: number of ticks to render behind (2-3 typical)
        /// </summary>
        public float LerpParam = 15f;
    }
}