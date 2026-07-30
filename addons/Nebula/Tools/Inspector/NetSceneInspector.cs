#if TOOLS
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using System.Linq;

namespace Nebula.Internal.Editor
{
    /// <summary>
    /// Adds a "Network Scene" / "Network Node" section to the inspector for
    /// nodes that carry Nebula network state, listing their [NetProperty]s and
    /// [NetFunction]s.
    ///
    /// <para>Reads the generated protocol tables, which reflect the last
    /// successful C# build — a [NetProperty] added since then won't appear, and
    /// a brand-new NetScene won't be handled at all, until a rebuild.</para>
    /// </summary>
    [Tool]
    public partial class NetSceneInspector : EditorInspectorPlugin
    {
        private PackedScene inspectorScene;

        public override bool _CanHandle(GodotObject obj)
        {
            return ResolveTarget(obj, out _, out _, out _);
        }

        public override void _ParseBegin(GodotObject obj)
        {
            if (!ResolveTarget(obj, out Node node, out string scenePath, out string nodePath))
                return;

            inspectorScene ??= GD.Load<PackedScene>("res://addons/Nebula/Tools/Inspector/inspect_network_scene.tscn");
            var inspector = inspectorScene.Instantiate<Control>();
            AddCustomControl(inspector);

            bool isNetScene = nodePath == ".";
            inspector.Call("set_title", isNetScene ? "NetScene" : "NetNode");
            inspector.Call("set_path", nodePath);

            if (isNetScene)
                PopulateSceneOverview(inspector, node, scenePath);
            else
                PopulateNetSceneLink(inspector, scenePath);

            foreach (var property in Protocol.ListProperties(scenePath, nodePath))
                inspector.Call("add_property", property.Name, property.VariantType.ToString());

            foreach (var function in Protocol.ListFunctions(scenePath, nodePath))
            {
                inspector.Call("add_function", function.Name,
                    $"({string.Join(", ", function.Arguments.Select(a => a.VariantType.ToString()))})");
            }
        }

        /// <summary>
        /// NetScene-only: the two per-scene protocol budgets plus the list of static
        /// NetNodes rolled up into this scene's network state.
        ///
        /// <para>The property count is scene-wide (static children and nested
        /// non-NetScene instances roll up into the root's serializer), which is the
        /// number the 64-property limit applies to — not the root's own count shown in
        /// the Properties row.</para>
        /// </summary>
        private static void PopulateSceneOverview(Control inspector, Node node, string scenePath)
        {
            var staticNodes = Protocol.ListStaticNodes(scenePath);
            inspector.Call("set_scene_stats",
                Protocol.GetPropertyCount(scenePath), BitConstants.MaxSceneProperties,
                staticNodes.Count, BitConstants.MaxStaticNetNodes);

            // Only the edited scene's own root has its static children in the scene
            // tree dock: the children of a NetScene *instanced* into the edited scene
            // are not editable there, so there is nothing to select.
            bool childrenSelectable = node == EditorInterface.Singleton.GetEditedSceneRoot();

            foreach (var staticNodePath in staticNodes)
            {
                int propertyCount = Protocol.ListProperties(scenePath, staticNodePath).Count;
                bool selectable = childrenSelectable && node.GetNodeOrNull(staticNodePath) is not null;
                inspector.Call("add_static_node", staticNodePath, propertyCount, selectable);
            }
        }

        /// <summary>
        /// NetNode-only: a link up to the NetScene that owns this node's network state.
        /// <see cref="ResolveTarget"/> only resolves a static child against the edited
        /// scene root, so that root is always the owning NetScene.
        /// </summary>
        private static void PopulateNetSceneLink(Control inspector, string scenePath)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root is null)
                return;
            inspector.Call("set_net_scene_link", root.Name.ToString(), scenePath);
        }

        /// <summary>
        /// Decides whether a node carries network state, and if so which
        /// (scenePath, nodePath) pair describes it in the protocol tables.
        ///
        /// <para>This used to locate the selected node by walking the editor's
        /// internal SceneTreeDock/SceneTreeEditor controls by class name and
        /// picking out a Tree child — exactly the kind of thing that breaks
        /// silently on a Godot upgrade, and unnecessary: the inspected object
        /// is handed to us.</para>
        /// </summary>
        private static bool ResolveTarget(GodotObject obj, out Node node, out string scenePath, out string nodePath)
        {
            node = obj as Node;
            scenePath = "";
            nodePath = "";
            if (node is null)
                return false;

            // An instanced NetScene (including the edited scene's own root)
            // describes itself under ".".
            if (Protocol.IsNetScene(node.SceneFilePath))
            {
                scenePath = node.SceneFilePath;
                nodePath = ".";
                return true;
            }

            // Otherwise it may be a static child inside the edited NetScene.
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root is null || string.IsNullOrEmpty(root.SceneFilePath))
                return false;
            if (!Protocol.IsNetScene(root.SceneFilePath))
                return false;

            string relative = root.GetPathTo(node);
            if (!Protocol.PackNode(root.SceneFilePath, relative, out _))
                return false;

            scenePath = root.SceneFilePath;
            nodePath = relative;
            return true;
        }
    }
}
#endif
