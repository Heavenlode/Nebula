using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Utility.Nodes
{
    [GlobalClass]
    public partial class QuantizedNetTransform3D : NetNode3D
    {
        [Export]
        public Node3D SourceNode { get; set; }

        [Export]
        public Node3D TargetNode { get; set; }

        [NetProperty(LerpMode = NetLerpMode.Buffered, LerpParam = 2)]
        public NetPose3D NetPose { get; set; } = new NetPose3D();

        // Lerp state
        private Quaternion _startQuat;
        private Vector3 _startPos;
        private Quaternion _endQuat;
        private Vector3 _endPos;
        private Quaternion _lastTargetQuat;
        private Vector3 _lastTargetPos;
        private bool _initialized = false;

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            TargetNode ??= GetParent3D();
            SourceNode ??= GetParent3D();
            NetPose.Owner = Network.NetParent.InputAuthority;

            NetPose.Connect("OnChange", Callable.From(() =>
            {
                Network.NetParent.EmitSignal("NetPropertyChanged", Network.NetParent.RawNode.GetPathTo(this), "NetPose");
            }));

            if (GetMeta("import_from_external", false).AsBool())
            {
                SourceNode.Position = NetPose.Position;
                SourceNode.Quaternion = NetPose.RotationQuat;
                TargetNode.Position = NetPose.Position;
                TargetNode.Quaternion = NetPose.RotationQuat;
            }
        }

        public Node3D GetParent3D()
        {
            var parent = GetParent();
            if (parent is Node3D)
            {
                return (Node3D)parent;
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
            NetPose.ApplyDelta(SourceNode.Position, SourceNode.Rotation);
            NetPose.NetworkProcess(Network.CurrentWorld);
        }

        // // Custom smooth handler for NetPose3D (complex object)
        // public void NetworkSmoothNetPose(Variant target, float t)
        // {
        //     var targetPose = target.As<NetPose3D>();

        //     var currentPos = TargetNode.Position;
        //     var currentQuat = TargetNode.Quaternion;

        //     var targetPos = targetPose.Position;
        //     var targetQuat = targetPose.RotationQuat;

        //     if (currentQuat.Dot(targetQuat) < 0)
        //         targetQuat = -targetQuat;

        //     TargetNode.Position = currentPos.Lerp(targetPos, t);
        //     TargetNode.Quaternion = currentQuat.Slerp(targetQuat, t);
        // }

        public virtual void NetworkBufferedLerpNetPose(Variant before, Variant after, float t)
        {
            var beforePose = before.As<NetPose3D>();
            var afterPose = after.As<NetPose3D>();

            var beforePos = beforePose.Position;
            var afterPos = afterPose.Position;

            var beforeQuat = beforePose.RotationQuat;
            var afterQuat = afterPose.RotationQuat;

            // Shortest path for quaternion
            if (beforeQuat.Dot(afterQuat) < 0)
                afterQuat = -afterQuat;

            // Simple linear interpolation between two known states
            // This produces constant velocity - no jitter
            TargetNode.Position = beforePos.Lerp(afterPos, t);
            TargetNode.Quaternion = beforeQuat.Slerp(afterQuat, t);
        }
    }
}