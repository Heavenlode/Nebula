#if TOOLS
using Godot;
using Nebula.Serialization;

namespace Nebula.Internal.Editor
{
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

        private Tree editorSceneTree;
        private Tree GetEditorSceneTree()
        {
            if (editorSceneTree != null)
            {
                return editorSceneTree;
            }
            var baseControl = EditorInterface.Singleton.GetBaseControl();
            var sceneTreeDock = baseControl.FindChildren("Scene", "SceneTreeDock", true, false)[0];
            Control sceneTreeEditor = null;
            foreach (var child in sceneTreeDock.GetChildren())
            {
                if (child.Name.ToString().Contains("SceneTreeEditor"))
                {
                    sceneTreeEditor = child as Control;
                    break;
                }
            }
            if (sceneTreeEditor == null)
            {
                GD.PrintErr("Nebula: No scene tree found");
                return null;
            }
            Tree sceneTree = null;
            foreach (var child in sceneTreeEditor.GetChildren())
            {
                if (child.GetType() == typeof(Tree))
                {
                    sceneTree = child as Tree;
                    break;
                }
            }
            if (sceneTree == null)
            {
                GD.PrintErr("Nebula: No scene tree found");
                return null;
            }
            editorSceneTree = sceneTree;
            return editorSceneTree;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public async void _OnVisibilityChanged()
        {
            if (!IsNodeReady() || IsQueuedForDeletion())
            {
                return;
            }
            if (!Visible)
            {
                return;
            }

            // ScenesTree.Clear();
            // var ScenesRoot = ScenesTree.CreateItem();
            // ScenesRoot.SetText(0, "Scenes");
            // foreach (var scene in Protocol.EditorInstance.ListScenes())
            // {
            //     var sceneName = scene.Key;
            //     var nodePaths = scene.Value;

            //     var sceneItem = ScenesRoot.CreateChild();
            //     sceneItem.SetText(0, sceneName);
            //     sceneItem.SetMeta("nodeType", (int)ItemType.Scene);
            //     sceneItem.SetMeta("sceneName", sceneName);
            //     sceneItem.SetMeta("nodePath", ".");

            //     foreach (var nodePath in nodePaths)
            //     {
            //         if (nodePath == ".") continue;

            //         var nodeItem = sceneItem.CreateChild();
            //         nodeItem.SetText(0, nodePath);
            //         nodeItem.SetMeta("sceneName", sceneName);
            //         nodeItem.SetMeta("nodePath", nodePath);
            //         nodeItem.SetMeta("nodeType", (int)ItemType.Node);
            //     }
            // }
        }

        [Signal]
        public delegate void InspectNodeEventHandler(Node node);

        public void OnInspectNode(TreeItem treeItem, Node node)
        {
            treeItem.Select(0);
            EditorInterface.Singleton.InspectObject(node);
        }

        public async void _OnItemSelected()
        {
            var item = ScenesTree.GetSelected();
            if (item == null || !item.HasMeta("nodeType")) return;
            EditorInterface.Singleton.OpenSceneFromPath(item.GetMeta("sceneName").AsString());
            // Wait until the scene is opened
            while (true)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (GetEditorSceneTree().GetRoot() == null) continue;
                var checkSceneRootPath = GetEditorSceneTree().GetRoot().GetMetadata(0).AsString();
                var sceneRootNode = GetEditorSceneTree().GetNode(checkSceneRootPath);
                if (sceneRootNode.SceneFilePath == item.GetMeta("sceneName").AsString()) break;
            }

            // Select the node in the scene tree
            GetEditorSceneTree().DeselectAll();
            var treeNode = GetEditorSceneTree().GetRoot();
            if (item.GetMeta("nodePath").AsString() != ".")
            {
                var nodePathParts = item.GetMeta("nodePath").AsString().Split('/');
                foreach (var part in nodePathParts)
                {
                    foreach (var child in treeNode.GetChildren())
                    {
                        if (child.GetText(0) == part)
                        {
                            treeNode = child;
                            break;
                        }
                    }
                }
            }
            var targetNode = GetEditorSceneTree().GetNode(treeNode.GetMetadata(0).AsString());
            if (!targetNode.IsNodeReady())
            {
                await ToSignal(targetNode, Node.SignalName.Ready);
            }
            EmitSignal(SignalName.InspectNode, treeNode, targetNode);
        }
    }
}
#endif