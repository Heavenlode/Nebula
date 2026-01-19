using System;

namespace Nebula
{
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
        public long InterestRequired = 0;

        /// <summary>
        /// When true, the source generator will emit a virtual OnNetworkChange{PropertyName} method
        /// that you can override to handle property changes. This provides compile-time type safety
        /// and zero-allocation change notifications.
        /// </summary>
        public bool NotifyOnChange = false;

        /// <summary>
        /// When true, the source generator will emit a virtual Interpolate{PropertyName} method
        /// that smoothly interpolates this property toward network values each frame.
        /// The property value is not set immediately on network receive; instead it lerps toward the target.
        /// </summary>
        public bool Interpolate = false;

        /// <summary>
        /// Speed of interpolation when Interpolate = true. Higher = faster catch-up.
        /// Typical values: 10-20 for responsive feel, 5-10 for smooth feel.
        /// </summary>
        public float InterpolateSpeed = 15f;
    }
}