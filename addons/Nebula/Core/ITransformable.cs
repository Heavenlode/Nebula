using Godot;

namespace Nebula
{
    /// <summary>
    /// Interface for networked nodes that need to expose their authoritative transform.
    /// Useful when the node's actual position differs from its Node3D.GlobalTransform
    /// (e.g., Player uses a physics body for movement).
    /// </summary>
    public interface ITransformable
    {
        /// <summary>
        /// Returns the authoritative global transform for this networked object.
        /// </summary>
        Transform3D GetNetworkTransform();
    }
}
