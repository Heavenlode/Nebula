using Godot;
using Nebula.Serialization;
using System;
using System.Linq;

namespace Nebula.Internal.Editor
{
    [Tool]
    public partial class NetSceneInspector : EditorInspectorPlugin
    {
        PackedScene inspectorScene;
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
        public override bool _CanHandle(GodotObject obj)
        {
            base._CanHandle(obj);
            return false;
            // if (Protocol.EditorInstance == null)
            // {
            //     return false;
            // }
            // var sceneRootItem = GetEditorSceneTree().GetRoot();
            // var selectedNodeItem = GetEditorSceneTree().GetSelected();
            // if (sceneRootItem == null || selectedNodeItem == null)
            // {
            //     return false;
            // }
            // var sceneRootNode = GetEditorSceneTree().GetNodeOrNull(sceneRootItem.GetMetadata(0).AsString());
            // var sceneSelectedNode = GetEditorSceneTree().GetNodeOrNull(selectedNodeItem.GetMetadata(0).AsString());
            // if (sceneRootNode == null || sceneSelectedNode == null)
            // {
            //     return false;
            // }
            // var relativeNodePath = sceneRootNode.GetPathTo(sceneSelectedNode);
            // return Protocol.EditorInstance.PackNode(sceneRootNode.SceneFilePath, relativeNodePath, out _) ||
            //     Protocol.EditorInstance.IsNetScene(sceneSelectedNode.SceneFilePath);
        }

        public override void _ParseBegin(GodotObject obj)
        {
            // base._ParseBegin(obj);
            // try
            // {
            //     if (inspectorScene == null)
            //     {
            //         inspectorScene = GD.Load<PackedScene>("res://addons/Nebula/Tools/Inspector/inspect_network_scene.tscn");
            //     }
            //     var sceneRootItem = GetEditorSceneTree().GetRoot();
            //     var selectedNodeItem = GetEditorSceneTree().GetSelected();
            //     var sceneRootNode = GetEditorSceneTree().GetNodeOrNull(sceneRootItem.GetMetadata(0).AsString());
            //     var sceneSelectedNode = GetEditorSceneTree().GetNodeOrNull(selectedNodeItem.GetMetadata(0).AsString());
            //     if (sceneRootNode == null || sceneSelectedNode == null)
            //     {
            //         return;
            //     }
            //     var inspector = inspectorScene.Instantiate<Control>();
            //     AddCustomControl(inspector);

            //     var relativeNodePath = sceneRootNode.GetPathTo(sceneSelectedNode);
            //     var properties = Protocol.EditorInstance.ListProperties(sceneRootNode.SceneFilePath, relativeNodePath);
            //     var functions = Protocol.EditorInstance.ListFunctions(sceneRootNode.SceneFilePath, relativeNodePath);
            //     inspector.Call("set_title", "Network " + (Protocol.EditorInstance.IsNetScene(sceneSelectedNode.SceneFilePath) ? "Scene" : "Node"));
            //     foreach (var property in properties)
            //     {
            //         inspector.Call("add_property", property.Name, property.VariantType.ToString());
            //     }
            //     foreach (var function in functions)
            //     {
            //         inspector.Call("add_function", function.Name, $"({string.Join(", ", function.Arguments.Select(a => a.VariantType.ToString()))})");
            //     }
            //     // if (Protocol.EditorInstance.IsNetScene(sceneSelectedNode.SceneFilePath))
            //     // {
            //     //     var staticNodes = Protocol.EditorInstance.ListStaticNodes(sceneSelectedNode.SceneFilePath);
            //     //     foreach (var node in staticNodes)
            //     //     {
            //     //         var id = inspector.Call("add_child_detail", node);
            //     //         inspector.Call("set_path", node, id);
            //     //         var staticNodeProperties = Protocol.EditorInstance.ListProperties(sceneSelectedNode.SceneFilePath, node);
            //     //         foreach (var property in staticNodeProperties)
            //     //         {
            //     //             inspector.Call("add_property", property.Name, property.VariantType.ToString(), id);
            //     //         }
            //     //         var staticNodeFunctions = Protocol.EditorInstance.ListFunctions(sceneSelectedNode.SceneFilePath, node);
            //     //         foreach (var function in staticNodeFunctions)
            //     //         {
            //     //             inspector.Call("add_function", function.Name, $"({string.Join(", ", function.Arguments.Select(a => a.VariantType.ToString()))})", id);
            //     //         }
            //     //     }
            //     // }
            // }
            // catch (Exception _)
            // {
            //     return;
            // }
        }
    }
}