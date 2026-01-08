namespace Nebula.Tools;

#if TOOLS

using Godot;
using System;

using Internal.Editor;
using Nebula.Serialization;

/// <summary>
/// Main Nebula editor plugin for Godot. Handles autoloads, project settings,
/// docked tools, debugger, inspector, and addon manager.
/// </summary>
[Tool]
public partial class Main : EditorPlugin
{
    private const string AUTOLOAD_RUNNER = "NetRunner";
    private const string AUTOLOAD_ENV = "Env";
    private const string AUTOLOAD_DEBUGGER = "Debugger";
    private const string AUTOLOAD_DATA_TRANSFORMER = "BsonTransformer";

    // Preloaded resources
    private static readonly PackedScene DockNetScenes = GD.Load<PackedScene>("res://addons/Nebula/Tools/Dock/NetScenes/dock_net_scenes.tscn");
    private static readonly PackedScene ServerDebugClient = GD.Load<PackedScene>("res://addons/Nebula/Tools/Debugger/server_debug_client.tscn");
    private static readonly PackedScene AddonManager = GD.Load<PackedScene>("res://addons/Nebula/Tools/AddonManager/addon_manager.tscn");
    private static readonly PackedScene ToolMenu = GD.Load<PackedScene>("res://addons/Nebula/Tools/tool_menu.tscn");

    // Instances
    private Control dockNetScenesInstance;
    private Window serverDebugClientInstance;
    private NetSceneInspector netSceneInspectorInstance;
    private Node addonManagerInstance;
    private PopupMenu toolMenuInstance;
    private ProjectSettingsController projectSettingsController;

    private int menuItemIds = 0;

    /// <summary>
    /// Gets the plugin name for the Godot editor.
    /// </summary>
    public override string _GetPluginName() => "Nebula";

    /// <summary>
    /// Called when the plugin is enabled in the editor.
    /// Registers autoloads, docks, project settings, and editor extensions.
    /// </summary>
    public override void _EnterTree()
    {
        // Instantiate and add submenu
        // toolMenuInstance = ToolMenu.Instantiate<PopupMenu>();
        // AddToolSubmenuItem("Nebula", toolMenuInstance);

        // Register autoload singletons
        AddAutoloadSingleton(AUTOLOAD_DEBUGGER, "res://addons/Nebula/Utils/Debugger/Debugger.cs");
        AddAutoloadSingleton(AUTOLOAD_ENV, "res://addons/Nebula/Utils/Env/Env.cs");
        AddAutoloadSingleton(AUTOLOAD_DATA_TRANSFORMER, "res://addons/Nebula/Core/Serialization/BsonTransformer.cs");
        AddAutoloadSingleton(AUTOLOAD_RUNNER, "res://addons/Nebula/Core/NetRunner.cs");

        // Project settings controller
        projectSettingsController = new ProjectSettingsController();
        AddChild(projectSettingsController);

        // Dock: Network Scenes
        dockNetScenesInstance = DockNetScenes.Instantiate<Control>();
        dockNetScenesInstance.Name = "Network Scenes";
        AddControlToDock(DockSlot.LeftUr, dockNetScenesInstance);

        // Debugger client window
        serverDebugClientInstance = ServerDebugClient.Instantiate<Window>();
        AddChild(serverDebugClientInstance);
        // RegisterMenuItem("Debugger", () => serverDebugClientInstance.Show());

        // Inspector plugin
        netSceneInspectorInstance = new NetSceneInspector();
        AddInspectorPlugin(netSceneInspectorInstance);

        // Addon manager
        addonManagerInstance = AddonManager.Instantiate<Node>();
        AddChild(addonManagerInstance);
        addonManagerInstance.Call("SetPluginRoot", this);
    }

    /// <summary>
    /// Called when the plugin is disabled in the editor.
    /// Cleans up docks, autoloads, controllers, and inspector plugins.
    /// </summary>
    public override void _ExitTree()
    {
        // Clean up
        if (addonManagerInstance is not null)
            addonManagerInstance.QueueFree();

        if (netSceneInspectorInstance is not null)
            RemoveInspectorPlugin(netSceneInspectorInstance);

        if (dockNetScenesInstance is not null)
        {
            RemoveControlFromDocks(dockNetScenesInstance);
            dockNetScenesInstance.QueueFree();
        }

        if (projectSettingsController is not null)
            projectSettingsController.QueueFree();

        // Remove autoloads
        RemoveAutoloadSingleton(AUTOLOAD_RUNNER);
        RemoveAutoloadSingleton(AUTOLOAD_DATA_TRANSFORMER);
        RemoveAutoloadSingleton(AUTOLOAD_ENV);
        RemoveAutoloadSingleton(AUTOLOAD_DEBUGGER);

        RemoveToolMenuItem("Nebula");
    }

    /// <summary>
    /// Registers a custom menu item inside the Nebula submenu.
    /// </summary>
    /// <param name="label">Menu item label</param>
    /// <param name="onClick">Callback when clicked</param>
    public void RegisterMenuItem(string label, Action onClick)
    {
        int newId = menuItemIds;
        toolMenuInstance.AddItem(label, newId);
        toolMenuInstance.IdPressed += id =>
        {
            if (id == newId)
                onClick.Invoke();
        };
        menuItemIds++;
    }
}

#endif // Tools