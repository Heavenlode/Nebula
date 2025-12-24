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

        [NetProperty]
        public NetPose3D NetPose { get; set; } = new NetPose3D();

        /// <inheritdoc/>
        public override void _WorldReady()
        {
            base._WorldReady();
            TargetNode ??= GetParent3D();
            SourceNode ??= GetParent3D();
            NetPose.Owner = Network.NetParent.Network.InputAuthority;

            // TODO: Is there a better way to do this automatically with INotifyPropertyChanged?
            NetPose.Connect("OnChange", Callable.From(() =>
            {
                Network.NetParent.Network.EmitSignal("NetPropertyChanged", Network.NetParent.Node.GetPathTo(this), "NetPose");
            }));

            // NetPose.Multiplier = new Fixed64(2f);

            if (GetMeta("import_from_external", false).AsBool())
            {
                SourceNode.Position = NetPose.Position;
                SourceNode.Rotation = NetPose.Rotation;
                TargetNode.Position = NetPose.Position;
                TargetNode.Rotation = NetPose.Rotation;
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

        public double NetworkLerpNetPose(Variant from, Variant to, double weight)
        {
            // Convert Euler angles to quaternions
            Quaternion startQuat = Quaternion.FromEuler(from.As<NetPose3D>().Rotation);
            Quaternion endQuat = Quaternion.FromEuler(to.As<NetPose3D>().Rotation);

            // Use spherical linear interpolation (SLERP) for rotation
            TargetNode.Quaternion = startQuat.Slerp(endQuat, (float)weight);

            // Position interpolation remains the same
            Vector3 startPosition = from.As<NetPose3D>().Position;
            Vector3 endPosition = to.As<NetPose3D>().Position;
            TargetNode.Position = startPosition.Lerp(endPosition, (float)weight);

            return weight;
        }
        // public double NetworkLerpNetRotation(Variant from, Variant to, double weight)
        // {
        // 	Vector3 start = from.AsVector3();
        // 	Vector3 end = to.AsVector3();
        // 	NetRotation = new Vector3((float)Mathf.LerpAngle(start.X, end.X, weight), (float)Mathf.LerpAngle(start.Y, end.Y, weight), (float)Mathf.LerpAngle(start.Z, end.Z, weight));
        // 	return weight;
        // }

        // public void Teleport(Vector3 incoming_position)
        // {
        // 	TargetNode.Position = incoming_position;
        // 	IsTeleporting = true;
        // }
    }
}
