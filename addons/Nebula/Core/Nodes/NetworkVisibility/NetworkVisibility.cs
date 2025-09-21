using Godot;
using Godot.Collections;

namespace Nebula.Utility.Nodes
{
    public partial class NetworkVisibility : Node3D
    {
        [Export]
        public Array<Node> nodes = [];

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            var netParent = GetParentOrNull<INetNodeBase>();
            if (netParent.Network.IsCurrentOwner && NetRunner.Instance.IsClient)
            {
                return;
            }

            while (nodes.Count > 0)
            {
                var node = nodes[0];
                nodes.RemoveAt(0);
                node.ProcessMode = ProcessModeEnum.Disabled;
                foreach (var child in node.GetChildren())
                {
                    nodes.Add(child);
                }
                node.QueueFree();
            }
        }
    }
}