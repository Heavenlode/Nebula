using Godot;
using Godot.Collections;

namespace Nebula.Internal.Editor
{

    [Tool]
    public partial class WorldInspector : Control
    {
        [Export]
        public WorldDebug debugPanel;

        [Export]
        public VBoxContainer inspectorContainer;

        [Export]
        public VBoxContainer tickDataContainer;
        private PackedScene inspectorTitleScene = GD.Load<PackedScene>("res://addons/Nebula/Tools/Inspector/inspector_title.tscn");
        private PackedScene inspectorFieldScene = GD.Load<PackedScene>("res://addons/Nebula/Tools/Inspector/inspector_field.tscn");

        private PackedScene networkSceneInspector = GD.Load<PackedScene>("res://addons/Nebula/Tools/Inspector/inspect_network_scene.tscn");
        private Control networkSceneInpsectorInstance;

        private Dictionary currentNodeData;
        private Dictionary inspectorProperties;

        public void _OnNetNodeInspected(Dictionary nodeData)
        {
            var isNew = false;
            if (currentNodeData == null || !currentNodeData.TryGetValue("scene", out var scene) || scene.AsString() != nodeData["scene"].AsString())
            {
                isNew = true;
                if (networkSceneInpsectorInstance != null)
                {
                    networkSceneInpsectorInstance.QueueFree();
                }
                networkSceneInpsectorInstance = networkSceneInspector.Instantiate<Control>();
                inspectorContainer.AddChild(networkSceneInpsectorInstance);
            }
            networkSceneInpsectorInstance.Call("set_title", nodeData["scene"]);
            var staticNetNodes = nodeData["data"].AsGodotDictionary();
            foreach (var node in staticNetNodes.Keys)
            {
                var networkProperties = staticNetNodes[node].AsGodotDictionary();
                foreach (var property in networkProperties.Keys)
                {
                    if (isNew)
                    {
                        networkSceneInpsectorInstance.Call("add_property", property, networkProperties[property].ToString());
                    }
                    else
                    {
                        networkSceneInpsectorInstance.Call("set_property", property, networkProperties[property].ToString());
                    }
                }
            }
            currentNodeData = nodeData;
        }

        public void _OnTickFrameSelected(Control tickFrame)
        {

            foreach (var child in tickDataContainer.GetChildren())
            {
                child.QueueFree();
            }

            var frame_id = tickFrame.Get("tick_frame_id").AsInt32();
            var frame_data = debugPanel.GetFrame(frame_id);

            foreach (var category in frame_data["details"].AsGodotDictionary().Keys)
            {
                var title = inspectorTitleScene.Instantiate<Control>();
                title.GetNode<Label>("%Label").Text = category.AsString();
                tickDataContainer.AddChild(title);

                foreach (var kvp in frame_data["details"].AsGodotDictionary()[category].AsGodotDictionary())
                {
                    var fieldName = kvp.Key.AsString();
                    var fieldValue = kvp.Value.AsString();
                    var field = inspectorFieldScene.Instantiate<Control>();
                    field.GetNode<Label>("%Label").Text = fieldName;
                    field.GetNode<RichTextLabel>("%Value").Text = fieldValue;
                    tickDataContainer.AddChild(field);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (currentNodeData != null)
                {
                    currentNodeData.Clear();
                }
                if (inspectorProperties != null)
                {
                    inspectorProperties.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }
}