using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Utility.Nodes
{
    [GlobalClass]
    public partial class NetTransform3D : NetNode3D
    {
        [Export]
        public Node3D SourceNode { get; set; }

        [Export]
        public Node3D TargetNode { get; set; }

        [NetProperty]
        public bool IsTeleporting { get; set; }

        [NetProperty(LerpMode = NetLerpMode.Smooth, LerpParam = 15f)]
        public Vector3 NetPosition { get; set; }

        [NetProperty(LerpMode = NetLerpMode.Smooth, LerpParam = 15f)]
        public Quaternion NetRotation { get; set; } = Quaternion.Identity;

        private bool _isTeleporting = false;
        private bool _smoothInitialized = false;

        public void OnNetworkChangeIsTeleporting(Tick tick, bool from, bool to)
        {
            _isTeleporting = true;
            _smoothInitialized = false; // Reset so next position snaps
        }

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            TargetNode ??= GetParent3D();
            SourceNode ??= GetParent3D();

            if (GetMeta("import_from_external", false).AsBool())
            {
                SourceNode.Position = NetPosition;
                SourceNode.Quaternion = NetRotation;
                TargetNode.Position = NetPosition;
                TargetNode.Quaternion = NetRotation;
            }
        }

        public Node3D GetParent3D()
        {
            var parent = GetParent();
            if (parent is Node3D node3D)
            {
                return node3D;
            }
            Debugger.Instance.Log("NetTransform parent is not a Node3D", Debugger.DebugLevel.ERROR);
            return null;
        }

        public void Face(Vector3 direction)
        {
            if (NetRunner.Instance.IsClient)
            {
                return;
            }
            var parent = GetParent3D();
            if (parent == null)
            {
                return;
            }
            parent.LookAt(direction, Vector3.Up, true);
        }

        bool teleportExported = false;

        /// <inheritdoc/>
        public override void _NetworkProcess(int tick)
        {
            base._NetworkProcess(tick);
            if (NetRunner.Instance.IsClient)
            {
                return;
            }

            NetPosition = SourceNode.Position;
            NetRotation = SourceNode.Quaternion;

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

        #region Smooth Mode (exponential chase)

        /// <summary>
        /// Called by NetPropertiesSerializer for Smooth lerp mode.
        /// Exponentially chases target position each frame.
        /// </summary>
        public void NetworkSmoothNetPosition(Variant target, float t)
        {
            var targetPos = target.AsVector3();

            if (!_smoothInitialized || _isTeleporting)
            {
                // First update or teleport - snap to position
                TargetNode.Position = targetPos;
                NetPosition = targetPos;
                _isTeleporting = false;
                _smoothInitialized = true;
                return;
            }

            TargetNode.Position = TargetNode.Position.Lerp(targetPos, t);
            NetPosition = TargetNode.Position;
        }

        /// <summary>
        /// Called by NetPropertiesSerializer for Smooth lerp mode.
        /// Exponentially chases target rotation each frame.
        /// </summary>
        public void NetworkSmoothNetRotation(Variant target, float t)
        {
            var targetQuat = target.AsQuaternion().Normalized();

            if (!_smoothInitialized || _isTeleporting)
            {
                // First update or teleport - snap to rotation
                TargetNode.Quaternion = targetQuat;
                NetRotation = targetQuat;
                return;
            }

            var currentQuat = TargetNode.Quaternion.Normalized();

            // Shortest path
            if (currentQuat.Dot(targetQuat) < 0)
            {
                targetQuat = new Quaternion(-targetQuat.X, -targetQuat.Y, -targetQuat.Z, -targetQuat.W);
            }

            TargetNode.Quaternion = currentQuat.Slerp(targetQuat, t);
            NetRotation = TargetNode.Quaternion;
        }

        #endregion

        #region Buffered Mode (interpolate between past states)

        /// <summary>
        /// Called by NetPropertiesSerializer for Buffered lerp mode.
        /// Interpolates between two known past positions.
        /// </summary>
        public void NetworkBufferedLerpNetPosition(Variant before, Variant after, float t)
        {
            if (_isTeleporting)
            {
                TargetNode.Position = after.AsVector3();
                NetPosition = TargetNode.Position;
                _isTeleporting = false;
                return;
            }

            TargetNode.Position = before.AsVector3().Lerp(after.AsVector3(), t);
            NetPosition = TargetNode.Position;
        }

        /// <summary>
        /// Called by NetPropertiesSerializer for Buffered lerp mode.
        /// Interpolates between two known past rotations.
        /// </summary>
        public void NetworkBufferedLerpNetRotation(Variant before, Variant after, float t)
        {
            var fromQuat = before.AsQuaternion().Normalized();
            var toQuat = after.AsQuaternion().Normalized();

            // Shortest path
            if (fromQuat.Dot(toQuat) < 0)
            {
                toQuat = new Quaternion(-toQuat.X, -toQuat.Y, -toQuat.Z, -toQuat.W);
            }

            TargetNode.Quaternion = fromQuat.Slerp(toQuat, t);
            NetRotation = TargetNode.Quaternion;
        }

        #endregion

        /// <inheritdoc/>
        public override void _PhysicsProcess(double delta)
        {
            if (Engine.IsEditorHint())
            {
                return;
            }
            base._PhysicsProcess(delta);

            // Smoothing/buffering is now handled by NetPropertiesSerializer._Process
            // No need to apply here anymore for clients
        }

        public void Teleport(Vector3 incoming_position)
        {
            TargetNode.Position = incoming_position;
            IsTeleporting = true;
            _smoothInitialized = false;
        }

        public void Teleport(Vector3 incoming_position, Quaternion incoming_rotation)
        {
            TargetNode.Position = incoming_position;
            TargetNode.Quaternion = incoming_rotation;
            IsTeleporting = true;
            _smoothInitialized = false;
        }
    }
}