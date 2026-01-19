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

        [NetProperty(Interpolate = true, InterpolateSpeed = 15f)]
        public Vector3 NetPosition { get; set; }

        [NetProperty(Interpolate = true, InterpolateSpeed = 15f)]
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
            
            // Register SourceNode for zero-alloc property access on server
            if (NetRunner.Instance.IsServer && SourceNode != null)
            {
                NativeBridge.Register(SourceNode);
            }

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

        bool teleportExported = false;

        /// <inheritdoc/>
        public override void _NetworkProcess(int tick)
        {
            base._NetworkProcess(tick);
            if (NetRunner.Instance.IsClient)
            {
                return;
            }

            // Use NativeBridge for zero-alloc property access (synced in NetRunner._PhysicsProcess)
            NetPosition = NativeBridge.GetPosition(SourceNode);
            NetRotation = Quaternion.FromEuler(NativeBridge.GetRotation(SourceNode));

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