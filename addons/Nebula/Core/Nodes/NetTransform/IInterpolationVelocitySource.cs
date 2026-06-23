using Godot;

namespace Nebula.Utility.Nodes
{
    /// <summary>
    /// Interface for nodes that can provide velocity data for Hermite interpolation.
    /// Implement this on any SourceNode that has velocity to enable smooth
    /// cubic extrapolation in NetTransform3D.
    /// </summary>
    public interface IInterpolationVelocitySource
    {
        /// <summary>
        /// The current linear velocity used for Hermite extrapolation.
        /// </summary>
        Vector3 InterpolationLinearVelocity { get; }
    }
}
