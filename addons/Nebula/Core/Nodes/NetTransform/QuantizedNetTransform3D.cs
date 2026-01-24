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

        [NetProperty(Interpolate = true, InterpolateSpeed = 15f)]
        public NetPose3D NetPose { get; set; } = new NetPose3D();

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            TargetNode ??= GetParent3D();
            SourceNode ??= GetParent3D();
            NetPose.Owner = Network.NetParent.InputAuthority;

            NetPose.OnChange += () =>
            {
                // Mark the NetPose property as dirty - propagates to parent net scene automatically
                Network.MarkDirtyRef(this, "NetPose", NetPose);
            };

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
            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"NetTransform parent is not a Node3D");
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

        /// <summary>
        /// Updates TargetNode position/rotation from NetPose each frame on the client.
        /// The network serializer calls ProcessInterpolation which lerps NetPose toward the target.
        /// </summary>
        public override void _Process(double delta)
        {
            base._Process(delta);
            
            if (!Network.IsWorldReady || !NetRunner.Instance.IsClient || TargetNode == null)
                return;

            var targetPos = NetPose.Position;
            var targetQuat = NetPose.RotationQuat;
            
            var currentQuat = TargetNode.Quaternion;
            // Shortest path for quaternion
            if (currentQuat.Dot(targetQuat) < 0)
                targetQuat = -targetQuat;
            
            float t = 1f - Mathf.Exp(-15f * (float)delta);
            
            TargetNode.Position = TargetNode.Position.Lerp(targetPos, t);
            TargetNode.Quaternion = currentQuat.Slerp(targetQuat, t);
        }

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
    }
}