using Godot;
using System;
using Nebula.Utility.Tools;

namespace Nebula.Utility.Nodes
{
    /// <summary>
    /// Synchronizes a Node3D's transform over the network with support for:
    /// - Server authoritative state
    /// - Client-side prediction for owned entities
    /// - Smooth visual interpolation for ALL clients (owned and non-owned)
    /// </summary>
    [GlobalClass]
    public partial class NetTransform3D : NetNode3D
    {
        /// <summary>
        /// The physics/simulation node to read authoritative transform from.
        /// This node runs at tick rate. Defaults to parent if not set.
        /// </summary>
        [Export]
        public Node3D SourceNode { get; set; }

        /// <summary>
        /// The visual node to write interpolated transform to.
        /// If null, defaults to SourceNode (legacy behavior).
        /// For owned clients, this interpolates toward SourceNode at frame rate.
        /// For non-owned clients, this interpolates toward NetPosition/NetRotation.
        /// </summary>
        [Export]
        public Node3D TargetNode { get; set; }

        /// <summary>
        /// How fast the TargetNode interpolates toward the source transform.
        /// Higher values = faster/tighter follow, lower = smoother but more lag.
        /// </summary>
        [Export]
        public float VisualInterpolateSpeed { get; set; } = 20f;

        [NetProperty]
        public bool IsTeleporting { get; set; }

        /// <summary>
        /// Networked position with interpolation for non-owned and prediction for owned entities.
        /// </summary>
        [NetProperty(Interpolate = true, InterpolateSpeed = 10f, Predicted = true, PredictionTolerance = 20f, NotifyOnChange = true)]
        public Vector3 NetPosition { get; set; }

        /// <summary>
        /// Networked rotation with interpolation for non-owned and prediction for owned entities.
        /// </summary>
        [NetProperty(Interpolate = true, InterpolateSpeed = 15f, Predicted = true, PredictionTolerance = 0.1f, NotifyOnChange = true)]
        public Quaternion NetRotation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// Called when NetPosition changes during network import.
        /// During initial spawn, sync to SourceNode so physics starts at correct position.
        /// </summary>
        partial void OnNetChangeNetPosition(int tick, Vector3 oldVal, Vector3 newVal)
        {
            // During spawn (before world ready), sync imported position to SourceNode
            if (!Network.IsWorldReady && NetRunner.Instance.IsClient)
            {
                SourceNode ??= GetParent3D();
                if (SourceNode != null)
                {
                    SourceNode.Position = newVal;
                }
            }
        }

        /// <summary>
        /// Called when NetRotation changes during network import.
        /// During initial spawn, sync to SourceNode so physics starts at correct rotation.
        /// </summary>
        partial void OnNetChangeNetRotation(int tick, Quaternion oldVal, Quaternion newVal)
        {
            // Ensure the rotation is normalized for interpolation
            NetRotation = SafeNormalize(newVal);
            
            // During spawn (before world ready), sync imported rotation to SourceNode
            if (!Network.IsWorldReady && NetRunner.Instance.IsClient)
            {
                SourceNode ??= GetParent3D();
                if (SourceNode != null)
                {
                    SourceNode.Quaternion = NetRotation;
                }
            }
        }

        private bool _isTeleporting = false;
        private bool _visualInitialized = false;
        private bool teleportExported = false;

        public void OnNetworkChangeIsTeleporting(Tick tick, bool from, bool to)
        {
            _isTeleporting = true;
            _visualInitialized = false; // Reset so next position snaps
        }

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            SourceNode ??= GetParent3D();

            if (NetRunner.Instance.IsServer && SourceNode != null)
            {
                // Server: initialize NetPosition from SourceNode so first state export is correct
                NetPosition = SourceNode.Position;
                NetRotation = SafeNormalize(SourceNode.Quaternion);
            }
            
            // Ensure TargetNode has a valid initial quaternion
            if (NetRunner.Instance.IsClient && TargetNode != null)
            {
                TargetNode.Quaternion = SafeNormalize(TargetNode.Quaternion);
            }
            
            // Ensure NetRotation is normalized (for interpolation)
            NetRotation = SafeNormalize(NetRotation);
        }

        public Node3D GetParent3D()
        {
            var parent = GetParent();
            if (parent is Node3D node3D)
            {
                return node3D;
            }
            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"NetTransform parent is not a Node3D");
            return null;
        }

        public void Face(Vector3 direction)
        {
            if (NetRunner.Instance.IsClient)
            {
                return;
            }
            if (SourceNode == null)
            {
                return;
            }
            SourceNode.LookAt(direction, Vector3.Up, true);
        }

        /// <summary>
        /// Called after mispredicted properties are restored during rollback.
        /// Syncs to SourceNode ONLY the properties that actually mispredicted.
        /// After RestoreMispredictedToConfirmed runs, mispredicted properties equal their _confirmed values.
        /// </summary>
        partial void OnConfirmedStateRestored()
        {
            if (SourceNode != null)
            {
                // Only sync properties that were actually restored (mispredicted)
                // After RestoreMispredictedToConfirmed, restored props equal _confirmed
                if (NetPosition == _confirmed_NetPosition)
                {
                    SourceNode.Position = _confirmed_NetPosition;
                }
                if (NetRotation == _confirmed_NetRotation)
                {
                    var normalizedRot = SafeNormalize(_confirmed_NetRotation);
                    SourceNode.Quaternion = normalizedRot;
                }
            }
        }

        /// <summary>
        /// Called after predicted properties are restored from prediction buffer.
        /// Syncs restored NetPosition/NetRotation to SourceNode so physics can continue.
        /// </summary>
        partial void OnPredictedStateRestored()
        {
            // Ensure restored rotation is normalized
            NetRotation = SafeNormalize(NetRotation);
            
            if (SourceNode != null)
            {
                SourceNode.Position = NetPosition;
                SourceNode.Quaternion = NetRotation;
            }
        }

        private static Quaternion SafeNormalize(Quaternion value)
        {
            return value.LengthSquared() < 0.0001f ? Quaternion.Identity : value.Normalized();
        }

        /// <summary>
        /// Ensures quaternions are on the same hemisphere for proper Slerp interpolation.
        /// If quaternions are on opposite hemispheres, Slerp takes the "long way" around.
        /// </summary>
        private static Quaternion EnsureSameHemisphere(Quaternion from, Quaternion to)
        {
            if (from.Dot(to) < 0)
                return new Quaternion(-from.X, -from.Y, -from.Z, -from.W);
            return from;
        }

        /// <inheritdoc/>
        public override void _NetworkProcess(int tick)
        {
            base._NetworkProcess(tick);
            
            // Non-owned clients don't run simulation - interpolation handles them
            if (NetRunner.Instance.IsClient && !Network.IsCurrentOwner) return;

            // Server AND owned client: read from SourceNode (physics simulation node)
            if (SourceNode != null)
            {
                NetPosition = SourceNode.Position;
                NetRotation = SafeNormalize(SourceNode.Quaternion);
            }

            if (IsTeleporting)
            {
                if (teleportExported)
                {
                    IsTeleporting = false;
                    teleportExported = false;
                }
                else
                {
                    teleportExported = true;
                }
            }
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            base._Process(delta);
            if (!Network.IsWorldReady) return;
            if (!NetRunner.Instance.IsClient) return;

            // Determine the target node to interpolate (TargetNode if set, otherwise SourceNode)
            var target = TargetNode ?? SourceNode;
            if (target == null) return;

            // For owned entities: smoothly lerp visual toward physics using exponential smoothing
            if (Network.IsCurrentOwner && SourceNode != null)
            {
                // Frame-rate independent smoothing factor
                float t = 1f - Mathf.Exp(-VisualInterpolateSpeed * (float)delta);
                
                // Smooth position
                target.Position = target.Position.Lerp(SourceNode.Position, t);
                
                // Smooth rotation with hemisphere check for shortest path
                var sourceRot = SafeNormalize(SourceNode.Quaternion);
                var visualRot = SafeNormalize(target.Quaternion);
                visualRot = EnsureSameHemisphere(visualRot, sourceRot);
                target.Quaternion = visualRot.Slerp(sourceRot, t);
                return;
            }

            // Non-owned client: interpolate toward NetPosition/NetRotation
            Vector3 targetPos = NetPosition;
            Quaternion targetRot = NetRotation;

            // Initialize on first frame or after teleport
            if (!_visualInitialized)
            {
                target.Position = targetPos;
                target.Quaternion = targetRot;
                _visualInitialized = true;
                return;
            }

            // Smooth interpolation using exponential decay
            float t2 = (float)(1.0 - Math.Exp(-VisualInterpolateSpeed * delta));
            target.Position = target.Position.Lerp(targetPos, t2);
            
            // Ensure quaternions are normalized before Slerp
            var currentRot = SafeNormalize(target.Quaternion);
            target.Quaternion = currentRot.Slerp(targetRot, t2);
        }

        /// <summary>
        /// Teleports to a position, skipping interpolation.
        /// </summary>
        public void Teleport(Vector3 incoming_position)
        {
            if (SourceNode != null)
            {
                SourceNode.Position = incoming_position;
            }
            if (TargetNode != null)
            {
                TargetNode.Position = incoming_position;
            }
            NetPosition = incoming_position;
            IsTeleporting = true;
            _visualInitialized = false;
        }

        /// <summary>
        /// Teleports to a position and rotation, skipping interpolation.
        /// </summary>
        public void Teleport(Vector3 incoming_position, Quaternion incoming_rotation)
        {
            var normalizedRotation = SafeNormalize(incoming_rotation);
            
            if (SourceNode != null)
            {
                SourceNode.Position = incoming_position;
                SourceNode.Quaternion = normalizedRotation;
            }
            if (TargetNode != null)
            {
                TargetNode.Position = incoming_position;
                TargetNode.Quaternion = normalizedRotation;
            }
            NetPosition = incoming_position;
            NetRotation = normalizedRotation;
            IsTeleporting = true;
            _visualInitialized = false;
        }
    }
}
