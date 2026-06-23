using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Utility.Nodes
{
    /// <summary>
    /// Visual interpolation mode for owned entities.
    /// </summary>
    public enum VisualInterpolationMode
    {
        /// <summary>
        /// Exponential smoothing (legacy). Fast response but can show micro-jitter at high speeds.
        /// </summary>
        Exponential,

        /// <summary>
        /// Hermite extrapolation. Zero-latency smooth motion using velocity data.
        /// Requires SourceNode to implement IInterpolationVelocitySource.
        /// </summary>
        Hermite
    }

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
        /// Only used when InterpolationMode is Exponential.
        /// </summary>
        [Export]
        public float VisualInterpolateSpeed { get; set; } = 20f;

        /// <summary>
        /// The interpolation mode for owned entities.
        /// Exponential: classic smooth chase (can show micro-jitter at high speeds).
        /// Hermite: zero-latency cubic extrapolation using velocity (requires IInterpolationVelocitySource).
        /// </summary>
        [Export]
        public VisualInterpolationMode InterpolationMode { get; set; } = VisualInterpolationMode.Exponential;

        /// <summary>
        /// When true, smoothly interpolates visual toward physics.
        /// When false, snaps visual to physics instantly.
        /// Set to false when source position is already smooth (e.g., computed from visual planet position).
        /// </summary>
        public bool VisualSmoothing { get; set; } = true;

        /// <summary>
        /// When true, skips _NetworkProcess entirely and disables reconciliation hooks.
        /// Not exported - controlled programmatically at runtime only.
        /// </summary>
        public bool SyncPaused { get; set; } = false;

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
            if (!Network.IsWorldReady && Network.IsClient)
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
            // For owned predicted entities post-spawn, don't modify NetRotation here.
            // Reconciliation will handle applying confirmed state if needed.
            // Only normalize and apply for non-owned or during spawn.
            if (Network.IsCurrentOwner && Network.IsWorldReady && NetRunner.Instance.IsClient)
            {
                // Owned + world ready + client = prediction is active, don't interfere
                return;
            }
            
            // Ensure the rotation is normalized for interpolation
            NetRotation = SafeNormalize(newVal);

            // During spawn (before world ready), sync imported rotation to SourceNode
            if (!Network.IsWorldReady && Network.IsClient)
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

        // Hermite interpolation state
        private const int HERMITE_BUFFER_SIZE = 32;
        private Vector3[] _hermitePositions;
        private Quaternion[] _hermiteRotations;
        private Vector3[] _hermiteVelocities;
        private int _hermiteLatestTick = -1;
        private int _hermiteLastProcessedTick = -1;
        private double _hermiteTimeSincePhysicsUpdate;
        private IInterpolationVelocitySource _velocitySource;
        private bool _velocitySourceChecked;
        private Vector3 _hermiteVisualVelocity;
        private bool _hermiteInitialized;

        protected virtual void OnNetChangeIsTeleporting(int tick, bool oldVal, bool newVal)
        {
            _isTeleporting = true;
            // Clear snapshot buffer on teleport to prevent interpolating from old position
            if (newVal && Network.IsClient)
            {
                Network.ClearSnapshotBuffer();
            }
        }

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            SourceNode ??= GetParent3D();

            if (Network.IsServer && SourceNode != null)
            {
                // Server: initialize NetPosition from SourceNode so first state export is correct
                NetPosition = SourceNode.Position;
                NetRotation = SafeNormalize(SourceNode.Quaternion);
            }
            if (Network.IsClient && SourceNode != null)
            {
                SourceNode.Position = NetPosition;
                SourceNode.Quaternion = SafeNormalize(NetRotation);
            }
            // Ensure TargetNode has a valid initial quaternion
            if (Network.IsClient && TargetNode != null)
            {
                TargetNode.Quaternion = SafeNormalize(TargetNode.Quaternion);
            }
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
            if (Network.IsClient)
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
            if (SyncPaused) return;

            if (SourceNode != null)
            {
                var confirmedRot = SafeNormalize(NetRotation);
                var currentRot = SafeNormalize(SourceNode.Quaternion);

                SourceNode.Position = NetPosition;
                SourceNode.Quaternion = EnsureSameHemisphere(confirmedRot, currentRot);
            }
        }

        /// <summary>
        /// Called after predicted properties are restored from prediction buffer.
        /// Syncs restored NetPosition/NetRotation to SourceNode so physics can continue.
        /// </summary>
        partial void OnPredictedStateRestored()
        {
            if (SyncPaused) return;

            NetRotation = SafeNormalize(NetRotation);

            if (SourceNode != null)
            {
                var currentRot = SafeNormalize(SourceNode.Quaternion);

                SourceNode.Position = NetPosition;
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

            if (SyncPaused)
            {
                // Server: skip entirely — no need to serialize global transform during matched state.
                // Owned client: still read from SourceNode to keep the prediction buffer current,
                // so RestoreToPredictedState always has valid values.
                if (Network.IsClient && Network.IsCurrentOwner && SourceNode != null)
                {
                    NetPosition = SourceNode.Position;
                    NetRotation = SafeNormalize(SourceNode.Quaternion);

                    // Keep Hermite buffer updated so visuals stay smooth during velocity-matched state
                    if (!Network.IsResimulating && InterpolationMode == VisualInterpolationMode.Hermite)
                    {
                        BufferHermiteState(tick);
                    }
                }
                return;
            }

            // Non-owned clients don't run simulation - interpolation handles them
            if (Network.IsClient && !Network.IsCurrentOwner) return;

            // Server AND owned client: read from SourceNode (physics simulation node)
            if (SourceNode != null)
            {
                NetPosition = SourceNode.Position;
                NetRotation = SafeNormalize(SourceNode.Quaternion);
            }

            // Buffer position/velocity for Hermite interpolation (owned client, forward simulation only)
            if (Network.IsClient && Network.IsCurrentOwner && !Network.IsResimulating
                && InterpolationMode == VisualInterpolationMode.Hermite && SourceNode != null)
            {
                BufferHermiteState(tick);
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

        /// <summary>
        /// Buffers the current position, rotation, and velocity for Hermite extrapolation.
        /// Called each tick during forward simulation (not during resimulation).
        /// </summary>
        private void BufferHermiteState(int tick)
        {
            if (_hermitePositions == null)
            {
                _hermitePositions = new Vector3[HERMITE_BUFFER_SIZE];
                _hermiteRotations = new Quaternion[HERMITE_BUFFER_SIZE];
                _hermiteVelocities = new Vector3[HERMITE_BUFFER_SIZE];
                for (int i = 0; i < HERMITE_BUFFER_SIZE; i++)
                    _hermiteRotations[i] = Quaternion.Identity;
            }

            if (!_velocitySourceChecked)
            {
                _velocitySource = SourceNode as IInterpolationVelocitySource;
                _velocitySourceChecked = true;
            }

            int slot = tick & (HERMITE_BUFFER_SIZE - 1);
            _hermitePositions[slot] = SourceNode.Position;
            _hermiteRotations[slot] = SafeNormalize(SourceNode.Quaternion);
            _hermiteVelocities[slot] = _velocitySource?.InterpolationLinearVelocity ?? Vector3.Zero;
            _hermiteLatestTick = tick;
        }

        /// <summary>
        /// Resets the Hermite interpolation state after a teleport or initialization.
        /// </summary>
        private void ResetHermiteState(Vector3 position, Quaternion rotation, Vector3 velocity = default)
        {
            if (_hermitePositions == null) return;

            for (int i = 0; i < HERMITE_BUFFER_SIZE; i++)
            {
                _hermitePositions[i] = position;
                _hermiteRotations[i] = rotation;
                _hermiteVelocities[i] = velocity;
            }
            _hermiteTimeSincePhysicsUpdate = 0;
            _hermiteLastProcessedTick = _hermiteLatestTick;
            _hermiteVisualVelocity = velocity;
            _hermiteInitialized = false;
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            base._Process(delta);
            if (!Network.IsWorldReady) return;
            if (!Network.IsClient) return;
            
            // Skip visual interpolation during resimulation - physics is replaying history
            if (Network.IsResimulating) return;

            // Determine the target node to interpolate (TargetNode if set, otherwise SourceNode)
            var target = TargetNode ?? SourceNode;
            if (target == null) return;

            // For owned entities: update visual from physics
            if (Network.IsCurrentOwner && SourceNode != null)
            {
                if (!VisualSmoothing)
                {
                    // Snap directly - source position is already smooth
                    target.Position = SourceNode.Position;
                    target.Quaternion = SafeNormalize(SourceNode.Quaternion);
                }
                else if (InterpolationMode == VisualInterpolationMode.Hermite && _hermiteLatestTick >= 0)
                {
                    int slot = _hermiteLatestTick & (HERMITE_BUFFER_SIZE - 1);
                    Vector3 physicsPos = _hermitePositions[slot];
                    Vector3 physicsVel = _hermiteVelocities[slot];
                    Quaternion physicsRot = _hermiteRotations[slot];

                    // Carry over excess time when ticks advance.
                    // Hard-resetting to 0 creates a discontinuity in expectedPos
                    // (the visual jumps backward every tick). Instead, subtract
                    // the elapsed tick time so expectedPos stays continuous.
                    if (_hermiteLatestTick != _hermiteLastProcessedTick)
                    {
                        if (_hermiteLastProcessedTick < 0)
                        {
                            _hermiteTimeSincePhysicsUpdate = 0;
                        }
                        else
                        {
                            int ticksAdvanced = _hermiteLatestTick - _hermiteLastProcessedTick;
                            double tickDelta = 1.0 / NetRunner.TPS;
                            _hermiteTimeSincePhysicsUpdate -= ticksAdvanced * tickDelta;
                            if (_hermiteTimeSincePhysicsUpdate < 0)
                                _hermiteTimeSincePhysicsUpdate = 0;
                        }
                        _hermiteLastProcessedTick = _hermiteLatestTick;
                    }

                    if (!_hermiteInitialized)
                    {
                        target.Position = physicsPos;
                        _hermiteVisualVelocity = physicsVel;
                        _hermiteInitialized = true;
                    }

                    // Extrapolate physics to current frame time.
                    // Because we carry over excess time, this line is continuous
                    // across tick boundaries -- no staircase, no backward jumps.
                    Vector3 expectedPos = physicsPos + physicsVel * (float)_hermiteTimeSincePhysicsUpdate;

                    Vector3 error = expectedPos - target.Position;

                    // Smoothly track physics velocity
                    float smoothFactor = 1f - Mathf.Exp(-VisualInterpolateSpeed * (float)delta);
                    _hermiteVisualVelocity = _hermiteVisualVelocity.Lerp(physicsVel, smoothFactor);

                    // Position = velocity integration + error correction
                    float errorCorrectionRate = 10f;
                    target.Position += _hermiteVisualVelocity * (float)delta + error * errorCorrectionRate * (float)delta;

                    _hermiteTimeSincePhysicsUpdate += delta;

                    // Rotation: smooth toward physics rotation
                    var sourceRot = SafeNormalize(physicsRot);
                    var visualRot = SafeNormalize(target.Quaternion);
                    visualRot = EnsureSameHemisphere(visualRot, sourceRot);

                    float angleDiff = visualRot.AngleTo(sourceRot);
                    if (angleDiff > RotationSnapThreshold)
                    {
                        target.Quaternion = sourceRot;
                    }
                    else
                    {
                        target.Quaternion = visualRot.Slerp(sourceRot, smoothFactor);
                    }
                }
                else
                {
                    // Exponential smoothing (legacy): smoothly lerp visual toward physics
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
                }
                return;
            }

            // Non-owned client: use NetPosition/NetRotation directly (network layer already interpolates)
            // Unless SyncPaused, in which case use SourceNode (physics is being driven externally,
            // e.g., velocity-matched state where position is computed from relative position + planet)
            if (SyncPaused && SourceNode != null)
            {
                target.Position = SourceNode.Position;
                target.Quaternion = SafeNormalize(SourceNode.Quaternion);
            }
            else
            {
                target.Position = NetPosition;
                target.Quaternion = SafeNormalize(NetRotation);
            }
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
            ResetHermiteState(incoming_position, NetRotation);
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
            ResetHermiteState(incoming_position, normalizedRotation);
        }
    }
}
