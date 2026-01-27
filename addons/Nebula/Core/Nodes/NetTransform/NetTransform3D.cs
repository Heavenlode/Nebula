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

        [NetProperty(NotifyOnChange = true)]
        public bool IsTeleporting { get; set; }

        /// <summary>
        /// Networked position with interpolation for non-owned and prediction for owned entities.
        /// </summary>
        [NetProperty(Interpolate = true, InterpolateSpeed = 1f, Predicted = true, NotifyOnChange = true)]
        public Vector3 NetPosition { get; set; }

        /// <summary>
        /// Tolerance for position misprediction detection.
        /// Set this from parent nodes that use NetTransform3D via composition.
        /// </summary>
        [Export]
        public float NetPositionPredictionTolerance { get; set; } = 2f;

        /// <summary>
        /// Networked rotation with interpolation for non-owned and prediction for owned entities.
        /// </summary>
        [NetProperty(Interpolate = true, InterpolateSpeed = 15f, Predicted = true, NotifyOnChange = true)]
        public Quaternion NetRotation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// Tolerance for rotation misprediction detection.
        /// Set this from parent nodes that use NetTransform3D via composition.
        /// </summary>
        [Export]
        public float NetRotationPredictionTolerance { get; set; } = 0.05f;

        /// <summary>
        /// Called when NetPosition changes during network import.
        /// During initial spawn, sync to SourceNode so physics starts at correct position.
        /// </summary>
        protected virtual void OnNetChangeNetPosition(int tick, Vector3 oldVal, Vector3 newVal)
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
        protected virtual void OnNetChangeNetRotation(int tick, Quaternion oldVal, Quaternion newVal)
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
        private bool teleportExported = false;

        protected virtual void OnNetChangeIsTeleporting(int tick, bool oldVal, bool newVal)
        {
            _isTeleporting = true;
            // Clear snapshot buffer on teleport to prevent interpolating from old position
            if (newVal && NetRunner.Instance.IsClient)
            {
                Network.ClearSnapshotBuffer();
            }
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
        /// Syncs the restored properties to SourceNode so physics can continue from confirmed state.
        /// </summary>
        partial void OnConfirmedStateRestored()
        {
            if (SourceNode != null)
            {
                // Sync position from the (just restored) property
                SourceNode.Position = NetPosition;
                
                // Sync rotation with normalization and hemisphere check
                var confirmedRot = SafeNormalize(NetRotation);
                var currentRot = SafeNormalize(SourceNode.Quaternion);
                SourceNode.Quaternion = EnsureSameHemisphere(confirmedRot, currentRot);
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
                // Ensure same hemisphere as current SourceNode to avoid "long way around" rotation
                var currentRot = SafeNormalize(SourceNode.Quaternion);
                SourceNode.Quaternion = EnsureSameHemisphere(NetRotation, currentRot);
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

        /// <summary>
        /// Angle threshold (in radians) above which we snap rotation instead of interpolating.
        /// This prevents the "long way around" rotation when there's a large discrepancy.
        /// </summary>
        private const float RotationSnapThreshold = Mathf.Pi / 2f; // 90 degrees

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
                
                // Check for large rotation error - snap instead of slerp to avoid visual artifacts
                float angleDiff = visualRot.AngleTo(sourceRot);
                if (angleDiff > RotationSnapThreshold)
                {
                    target.Quaternion = sourceRot;
                }
                else
                {
                    target.Quaternion = visualRot.Slerp(sourceRot, t);
                }
                return;
            }

            // Non-owned client: use NetPosition/NetRotation directly (network layer already interpolates)
            target.Position = NetPosition;
            target.Quaternion = SafeNormalize(NetRotation);
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
        }
    }
}
