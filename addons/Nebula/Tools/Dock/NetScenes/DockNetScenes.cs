#if TOOLS
using Godot;
using Nebula.Serialization;

namespace Nebula.Internal.Editor
{
    /// <summary>
    /// "Network Scenes" dock: every registered NetScene and the node paths
    /// within it that carry network state. Selecting an entry opens the scene
    /// and selects the node.
    ///
    /// <para>Contents come from the generated protocol tables, i.e. the last
    /// successful C# build — a new NetScene appears after a rebuild.</para>
    /// </summary>
    [Tool]
    public partial class DockNetScenes : Control
    {
        [Export]
        public Tree ScenesTree;

        private enum ItemType
        {
            Scene,
            Node,
        }

        public void _OnVisibilityChanged()
        {
            if (!IsNodeReady() || IsQueuedForDeletion())
            {
                return;
            }
            if (!Visible)
            {
                return;
            }

            ScenesTree.Clear();
            var scenesRoot = ScenesTree.CreateItem();
            scenesRoot.SetText(0, "Scenes");

            foreach (var scenePath in Protocol.ListScenes())
            {
                var sceneItem = scenesRoot.CreateChild();
                sceneItem.SetText(0, scenePath.GetFile());
                sceneItem.SetTooltipText(0, scenePath);
                sceneItem.SetMeta("nodeType", (int)ItemType.Scene);
                sceneItem.SetMeta("sceneName", scenePath);
                sceneItem.SetMeta("nodePath", ".");

                foreach (var nodePath in Protocol.ListStaticNodes(scenePath))
                {
                    if (nodePath == ".") continue;

                    var nodeItem = sceneItem.CreateChild();
                    nodeItem.SetText(0, nodePath);
                    nodeItem.SetMeta("sceneName", scenePath);
                    nodeItem.SetMeta("nodePath", nodePath);
                    nodeItem.SetMeta("nodeType", (int)ItemType.Node);
                }
            }
        }

        [Signal]
        public delegate void InspectNodeEventHandler(Node node);

        /// <summary>
        /// Opens the selected entry's scene and inspects the node.
        ///
        /// <para>The previous implementation reached into the editor's internal
        /// SceneTreeDock/SceneTreeEditor controls by class name to find and
        /// select the item; EditorInterface exposes everything needed.</para>
        /// </summary>
        public async void _OnItemSelected()
        {
            var item = ScenesTree.GetSelected();
            if (item == null || !item.HasMeta("nodeType")) return;

            var scenePath = item.GetMeta("sceneName").AsString();
            EditorInterface.Singleton.OpenSceneFromPath(scenePath);

            // Wait until the requested scene is the edited one.
            while (true)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var openedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (openedRoot != null && openedRoot.SceneFilePath == scenePath)
                    break;
            }

            var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            var nodePath = item.GetMeta("nodePath").AsString();
            var targetNode = nodePath == "." ? sceneRoot : sceneRoot.GetNodeOrNull(nodePath);
            if (targetNode == null)
                return;

            if (!targetNode.IsNodeReady())
            {
                await ToSignal(targetNode, Node.SignalName.Ready);
            }

            EditorInterface.Singleton.InspectObject(targetNode);
            EmitSignal(SignalName.InspectNode, targetNode);
        }
    }
}
#endif
