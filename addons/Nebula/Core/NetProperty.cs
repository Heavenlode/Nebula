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

        /// <summary>
        /// When true, this property participates in client-side prediction.
        /// The generator will emit:
        /// - Snapshot/restore methods for rollback
        /// - Visual smoothing (render value vs simulation value)
        /// - Comparison tolerance for misprediction detection
        /// Only meaningful on client for owned entities.
        /// </summary>
        public bool Predicted = false;

        /// <summary>
        /// Smoothing rate for prediction corrections. Higher = faster snap to correct value.
        /// Only used when Predicted = true. Called every frame in _Process.
        /// Typical values: 0.1-0.3 for smooth feel, 0.5+ for snappy feel.
        /// </summary>
        public float PredictionSmoothRate = 0.2f;

        /// <summary>
        /// Threshold for snapping instead of smoothing. If correction exceeds this, teleport immediately.
        /// Only used when Predicted = true.
        /// </summary>
        public float PredictionSnapThreshold = 2.0f;

        /// <summary>
        /// Tolerance for comparing predicted vs confirmed state.
        /// Misprediction only triggers if difference exceeds this value.
        /// For Vector3/Vector2, this is the distance threshold.
        /// For float/double, this is the absolute difference threshold.
        /// </summary>
        public float PredictionTolerance = 0.001f;
    }
}