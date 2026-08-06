namespace Nebula.Tools;

#if TOOLS

using Godot;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using Internal.Editor;
using Nebula.Serialization;

/// <summary>
/// Main Nebula editor plugin for Godot. Handles autoloads, project settings,
/// the main-screen debugger tab, the toolbar play controls, the Network Scenes
/// dock, the inspector plugin, and the addon manager.
/// </summary>
[Tool]
public partial class Main : EditorPlugin
{
    public const string DISABLE_TOOLING_SETTING = "Nebula/config/editor/disable_tooling";

    /// <summary>
    /// Master switch (project setting): when true, none of the Nebula editor
    /// tooling is set up — no tab, no play controls, no run-bar hiding,
    /// no headless run-instances config. Editor restart required to apply.
    /// </summary>
    private static bool ToolingDisabled => ProjectSettings.GetSetting(DISABLE_TOOLING_SETTING, false).AsBool();

    private const string AUTOLOAD_RUNNER = "NetRunner";
    private const string AUTOLOAD_ENV = "Env";
    private const string AUTOLOAD_DEBUGGER = "Debugger";

    private const string DEBUG_SESSION_SCENE = "res://addons/Nebula/Tools/MainScreen/nebula_debug_session.tscn";
    private const string ORCHESTRATOR_SCRIPT_PATH = "res://addons/Nebula/Tools/MainScreen/nebula_play_orchestrator.gd";
    private const string ORCHESTRATOR_NODE_NAME = "NebulaPlayOrchestrator";

    // Preloaded resources
    private static readonly PackedScene DockNetScenes = GD.Load<PackedScene>("res://addons/Nebula/Tools/Dock/NetScenes/dock_net_scenes.tscn");
    private static readonly PackedScene AddonManager = GD.Load<PackedScene>("res://addons/Nebula/Tools/AddonManager/addon_manager.tscn");

    // Instances
    private NebulaMainScreen mainScreenInstance;
    private HBoxContainer playBarInstance;
    private Button playButton;
    private MenuButton configDropdown;
    private NebulaConfigDialog configDialogInstance;
    private Control editorRunBar;

    /// <summary>How often the play session's running state is re-checked, in seconds.</summary>
    private const double SESSION_POLL_INTERVAL = 0.25;
    private double sinceSessionPoll;
    private bool lastKnownSessionRunning;

    private Control dockNetScenesInstance;
    private NetSceneInspector netSceneInspectorInstance;
    private Node addonManagerInstance;
    private ProjectSettingsController projectSettingsController;

    /// <summary>
    /// Gets the plugin name for the Godot editor.
    /// </summary>
    public override string _GetPluginName() => "Nebula";

    /// <summary>
    /// Nebula owns a main-screen tab (the live network debugger).
    /// </summary>
    public override bool _HasMainScreen() => !ToolingDisabled;

    /// <summary>
    /// Shows or hides the Nebula main-screen tab.
    /// </summary>
    public override void _MakeVisible(bool visible)
    {
        if (mainScreenInstance is not null)
            mainScreenInstance.Visible = visible;
    }

    /// <summary>
    /// Icon shown on the main-screen tab button.
    /// </summary>
    public override Texture2D _GetPluginIcon()
    {
        // Fetch lazily: the editor theme may not be ready during _EnterTree.
        var theme = EditorInterface.Singleton.GetEditorTheme();
        if (theme is null)
            return null;
        if (theme.HasIcon("MultiplayerSynchronizer", "EditorIcons"))
            return theme.GetIcon("MultiplayerSynchronizer", "EditorIcons");
        return theme.GetIcon("Node", "EditorIcons");
    }

    /// <summary>
    /// Called when the plugin is enabled in the editor.
    /// Registers autoloads, docks, project settings, and editor extensions.
    /// </summary>
    public override void _EnterTree()
    {
        // Register autoload singletons. AddAutoloadSingleton on an entry that already
        // exists in project.godot logs "Singleton already registered" every editor
        // start, so only add the ones that are actually missing.
        RegisterAutoloadIfMissing(AUTOLOAD_DEBUGGER, "res://addons/Nebula/Utils/Debugger/Debugger.cs");
        RegisterAutoloadIfMissing(AUTOLOAD_ENV, "res://addons/Nebula/Utils/Env/Env.cs");
        RegisterAutoloadIfMissing(AUTOLOAD_RUNNER, "res://addons/Nebula/Core/NetRunner.cs");

        // Project settings controller
        projectSettingsController = new ProjectSettingsController();
        AddChild(projectSettingsController);

        if (!ToolingDisabled)
        {
            // Main-screen tab (live network debugger)
            CreateMainScreen(visible: false);

            // Top-bar play controls (Play/Stop + configuration dropdown)
            CreatePlayBar();

            // Re-attach to a session that outlived a plugin reload.
            GetOrchestrator(createIfMissing: false);
        }

        // Editor-managed plays are only used to hold the debug server open for
        // Nebula play sessions, so force a single headless run instance (or
        // restore vanilla behavior when the tooling is disabled).
        ApplyRunInstancesConfig();

        // Hide the built-in run bar (restore it when tooling is disabled);
        // deferred so the editor UI is fully constructed, live-updated via
        // settings_changed.
        // IMPORTANT: name-bound Callable, NOT Callable.From. A delegate-backed
        // callable parked on the immortal ProjectSettings singleton holds a
        // GCHandle that pins the assembly on .NET reload (godot#78513): reloads
        // skip _ExitTree, so such a connection is never released. Name-bound
        // callables hold no managed state and re-resolve after reloads.
        CallDeferred(MethodName.ApplyRunBarVisibility);
        ProjectSettings.Singleton.Connect("settings_changed", new Callable(this, MethodName.ApplyRunBarVisibility));

        // Dock: Network Scenes
        dockNetScenesInstance = DockNetScenes.Instantiate<Control>();
        dockNetScenesInstance.Name = "Network Scenes";
        AddControlToDock(DockSlot.LeftUr, dockNetScenesInstance);

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
        // Clean up (name-bound callables compare by object+method, so a freshly
        // constructed one matches the stored connection even across reloads)
        var settingsCallable = new Callable(this, MethodName.ApplyRunBarVisibility);
        if (ProjectSettings.Singleton.IsConnected("settings_changed", settingsCallable))
            ProjectSettings.Singleton.Disconnect("settings_changed", settingsCallable);
        if (editorRunBar is not null && GodotObject.IsInstanceValid(editorRunBar))
            editorRunBar.Visible = true;
        editorRunBar = null;

        DisconnectOrchestrator();

        if (playBarInstance is not null)
        {
            RemoveControlFromContainer(CustomControlContainer.Toolbar, playBarInstance);
            playBarInstance.QueueFree();
            playBarInstance = null;
            playButton = null;
            configDropdown = null;
        }

        if (configDialogInstance is not null && GodotObject.IsInstanceValid(configDialogInstance))
        {
            configDialogInstance.QueueFree();
            configDialogInstance = null;
        }

        if (mainScreenInstance is not null)
        {
            mainScreenInstance.QueueFree();
            mainScreenInstance = null;
        }

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
        RemoveAutoloadSingleton(AUTOLOAD_ENV);
        RemoveAutoloadSingleton(AUTOLOAD_DEBUGGER);
    }

    private void CreateMainScreen(bool visible)
    {
        mainScreenInstance = new NebulaMainScreen();
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(mainScreenInstance);
        mainScreenInstance.Visible = visible;
    }

    // ─── Toolbar play controls ───────────────────────────────────────────────

    // NOTE: all Nebula editor-UI signal connections are name-bound Callables —
    // never `+=` / Callable.From. Delegate-backed connections have repeatedly
    // produced dead 'ManagedCallableMiddleman' handles (inert buttons) and
    // assembly-reload pins in this plugin; name-bound callables have no managed
    // state to die.
    private void CreatePlayBar()
    {
        playBarInstance = new HBoxContainer { Name = "NebulaPlayBar" };
        playBarInstance.AddThemeConstantOverride("separation", 0);

        playButton = new Button { Name = "NebulaPlayButton", Flat = true };
        playButton.Connect(Node.SignalName.Ready, new Callable(this, MethodName.OnPlayBarReady));
        playButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPlayButtonPressed));
        playBarInstance.AddChild(playButton);

        configDropdown = new MenuButton
        {
            Name = "NebulaPlayConfigDropdown",
            Flat = true,
            TooltipText = "Choose which Nebula play configuration the Play button launches.",
        };
        var popup = configDropdown.GetPopup();
        popup.Connect(Window.SignalName.AboutToPopup, new Callable(this, MethodName.PopulateConfigPopup));
        popup.Connect(PopupMenu.SignalName.IndexPressed, new Callable(this, MethodName.OnConfigPopupIndexPressed));
        playBarInstance.AddChild(configDropdown);

        AddControlToContainer(CustomControlContainer.Toolbar, playBarInstance);
        UpdatePlayButton();
    }

    private void OnPlayBarReady()
    {
        // Icons fetched once the controls are in the tree, so the editor theme
        // is resolvable.
        if (configDropdown is not null && GodotObject.IsInstanceValid(configDropdown))
        {
            if (configDropdown.HasThemeIcon("GuiOptionArrow", "EditorIcons"))
                configDropdown.Icon = configDropdown.GetThemeIcon("GuiOptionArrow", "EditorIcons");
            else
                configDropdown.Text = "▾";
        }
        UpdatePlayButton();
    }

    /// <summary>
    /// Reflects the selected configuration and whether a session is live. The
    /// button doubles as Stop while running, which is also the only running
    /// indicator now that the tab is the debugger.
    /// </summary>
    private void UpdatePlayButton()
    {
        if (playButton is null || !GodotObject.IsInstanceValid(playButton))
            return;

        bool running = IsSessionRunning();
        var config = NebulaPlayConfigStore.ResolveSelected();

        // The label carries the state, not just the icon. There is no "MainStop"
        // in the editor theme — only MainPlay/Play/Stop — so keying the running
        // state purely off an icon swap left the button looking identical
        // whether or not a session was live.
        playButton.Text = running ? $"Stop {config.Name}" : config.Name;
        playButton.TooltipText = running
            ? $"Stop the running Nebula session ({config.Name})."
            : $"Play {config.Name}.\nLaunches a headless server + a client, both attached to the editor debugger and to the Nebula debug channel.";

        string iconName = running ? "Stop" : "MainPlay";
        if (playButton.HasThemeIcon(iconName, "EditorIcons"))
            playButton.Icon = playButton.GetThemeIcon(iconName, "EditorIcons");
    }

    /// <summary>
    /// Fills the play configuration dropdown from the store (rebuilt on every
    /// open so edits made in the manage dialog show up immediately).
    /// </summary>
    private void PopulateConfigPopup()
    {
        var popup = configDropdown.GetPopup();
        popup.Clear();

        var configurations = NebulaPlayConfigStore.Load();
        // ResolveSelected (not GetSelectedName): it defaults to — and persists —
        // the first entry when nothing is selected, so an entry is always checked.
        string selected = NebulaPlayConfigStore.ResolveSelected().Name;
        for (int i = 0; i < configurations.Count; i++)
        {
            popup.AddRadioCheckItem(configurations[i].Name);
            popup.SetItemChecked(i, configurations[i].Name == selected);
        }

        popup.AddSeparator();
        popup.AddItem("Manage Configurations…");
    }

    private void OnConfigPopupIndexPressed(long index)
    {
        var popup = configDropdown.GetPopup();
        // Layout is: [config]* , separator, "Manage Configurations…"
        int configCount = popup.ItemCount - 2;
        if (index >= configCount)
        {
            if (index > configCount) // the separator itself is not selectable
                EnsureConfigDialog().Open();
            return;
        }

        NebulaPlayConfigStore.SetSelectedName(popup.GetItemText((int)index));
        UpdatePlayButton();
    }

    private NebulaConfigDialog EnsureConfigDialog()
    {
        if (configDialogInstance is not null && GodotObject.IsInstanceValid(configDialogInstance))
            return configDialogInstance;

        configDialogInstance = new NebulaConfigDialog();
        EditorInterface.Singleton.GetBaseControl().AddChild(configDialogInstance);
        configDialogInstance.Connect(NebulaConfigDialog.SignalName.ConfigurationsChanged,
            new Callable(this, MethodName.OnConfigurationsChanged));
        return configDialogInstance;
    }

    private void OnConfigurationsChanged()
    {
        UpdatePlayButton();
    }

    // ─── Play / stop ─────────────────────────────────────────────────────────

    private void OnPlayButtonPressed()
    {
        if (IsSessionRunning())
        {
            GetOrchestrator(createIfMissing: false)?.Call("stop");
            return;
        }
        LaunchSelected();
    }

    private bool IsSessionRunning()
    {
        var orchestrator = GetOrchestrator(createIfMissing: false);
        return orchestrator is not null && orchestrator.Call("is_running").AsBool();
    }

    /// <summary>
    /// Requests a local play: instance 1 = headless server, instance 2 = client,
    /// both with --remote-debug pointed at the editor's debug server (held open
    /// by the dummy session) and each with its own Nebula debug-channel port so
    /// the debugger tab can attach without any discovery protocol.
    ///
    /// The actual launch — including PlayCustomScene, whose C# build can trigger
    /// an assembly reload — is delegated to the GDScript orchestrator so no C#
    /// frames are on the stack when the reload runs (see
    /// nebula_play_orchestrator.gd).
    /// </summary>
    private void LaunchSelected()
    {
        var config = NebulaPlayConfigStore.ResolveSelected();

        var editorSettings = EditorInterface.Singleton.GetEditorSettings();
        string host = editorSettings.GetSetting("network/debug/remote_host").AsString();
        int remoteDebugPort = editorSettings.GetSetting("network/debug/remote_port").AsInt32();
        if (host.Length == 0)
            host = "127.0.0.1";
        if (remoteDebugPort == 0)
            remoteDebugPort = 6007;
        string debugUri = $"tcp://{host}:{remoteDebugPort}";

        string exe = OS.GetExecutablePath();
        string projectPath = ProjectSettings.GlobalizePath("res://");

        // One debug port per instance: the server's, then one per client.
        int serverDebugPort = ReserveLoopbackPort();
        var debugPorts = new List<int> { serverDebugPort };

        var serverArgs = new[]
        {
            "--path", projectPath,
            "--remote-debug", debugUri,
            "--server", "--headless",
            $"--debugPort={serverDebugPort}",
        };

        var clientArgsList = new Godot.Collections.Array();
        for (int i = 0; i < config.ClientCount; i++)
        {
            int clientDebugPort = ReserveLoopbackPort();
            debugPorts.Add(clientDebugPort);
            clientArgsList.Add(new[]
            {
                "--path", projectPath,
                "--remote-debug", debugUri,
                $"--debugPort={clientDebugPort}",
                $"--clientId={i}",
            });
        }

        // Bots are ordinary client instances with a behavior attached, so they ride the same
        // orchestrator list rather than needing a launch path of their own. Their clientId
        // continues the client numbering, keeping debug labels unique across the whole session.
        for (int i = 0; i < config.BotCount; i++)
        {
            int botDebugPort = ReserveLoopbackPort();
            debugPorts.Add(botDebugPort);

            var botArgs = new List<string>
            {
                "--path", projectPath,
                "--remote-debug", debugUri,
                $"--debugPort={botDebugPort}",
                $"--clientId={config.ClientCount + i}",
                Nebula.Bots.BotRunner.BotArg,
                $"{Nebula.Bots.BotRunner.BotIdArg}{i}",
                $"{Nebula.Bots.BotRunner.BotBehaviorArg}{config.BotBehavior}",
            };
            if (config.BotsHeadless)
                botArgs.Add("--headless");

            clientArgsList.Add(botArgs.ToArray());
        }

        GetOrchestrator(createIfMissing: true).Call("launch",
            DEBUG_SESSION_SCENE, exe, serverArgs, clientArgsList, config.Name,
            debugPorts.ToArray());
    }

    /// <summary>
    /// Picks a free loopback port by binding port 0 and reading what the OS
    /// assigned. There is a theoretical window between releasing the probe and
    /// the child process binding it; ephemeral ports are not immediately reused
    /// in practice, and a collision only costs that instance its debug channel.
    /// </summary>
    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// The orchestrator is a GDScript node parented to the editor base control,
    /// so it survives .NET assembly reloads (which recreate this plugin) and
    /// owns all running-instance state — pids, log buffer, and the debug ports
    /// the debugger tab reconnects to. See nebula_play_orchestrator.gd for why
    /// it must not be C#.
    /// </summary>
    private Node GetOrchestrator(bool createIfMissing)
    {
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        var orchestrator = baseControl.GetNodeOrNull(ORCHESTRATOR_NODE_NAME);
        if (orchestrator is null && createIfMissing)
        {
            var script = GD.Load<GDScript>(ORCHESTRATOR_SCRIPT_PATH);
            orchestrator = (Node)script.New();
            orchestrator.Name = ORCHESTRATOR_NODE_NAME;
            baseControl.AddChild(orchestrator);
        }
        if (orchestrator is not null)
            ConnectOrchestrator(orchestrator);
        return orchestrator;
    }

    private void ConnectOrchestrator(Node orchestrator)
    {
        // Name-bound, NOT Callable.From: the orchestrator is a GDScript node
        // that outlives assembly reloads, and a delegate-backed callable parked
        // on it pins the unloading assembly — the same godot#78513 mechanism as
        // the ProjectSettings connection above.
        var logCallable = new Callable(this, MethodName.OnOrchestratorLogLine);
        if (!orchestrator.IsConnected("log_line", logCallable))
            orchestrator.Connect("log_line", logCallable);
        var stateCallable = new Callable(this, MethodName.OnOrchestratorStateChanged);
        if (!orchestrator.IsConnected("state_changed", stateCallable))
            orchestrator.Connect("state_changed", stateCallable);
    }

    private void DisconnectOrchestrator()
    {
        var orchestrator = EditorInterface.Singleton.GetBaseControl().GetNodeOrNull(ORCHESTRATOR_NODE_NAME);
        if (orchestrator is null)
            return;
        var logCallable = new Callable(this, MethodName.OnOrchestratorLogLine);
        if (orchestrator.IsConnected("log_line", logCallable))
            orchestrator.Disconnect("log_line", logCallable);
        var stateCallable = new Callable(this, MethodName.OnOrchestratorStateChanged);
        if (orchestrator.IsConnected("state_changed", stateCallable))
            orchestrator.Disconnect("state_changed", stateCallable);
    }

    private void OnOrchestratorLogLine(string line)
    {
        GD.Print("[Nebula] " + line);
    }

    private void OnOrchestratorStateChanged()
    {
        lastKnownSessionRunning = IsSessionRunning();
        UpdatePlayButton();
        // The debugger tab attaches to / releases the session's debug ports.
        if (mainScreenInstance is not null && GodotObject.IsInstanceValid(mainScreenInstance))
            mainScreenInstance.OnSessionStateChanged();
    }

    /// <summary>
    /// Forces editor-managed play to a single headless instance — its only
    /// remaining job is the Nebula debug-server dummy session — or restores
    /// vanilla run-instances behavior when the tooling is disabled. Note the
    /// Run Instances dialog caches this metadata, so a change converges on the
    /// next editor start.
    /// </summary>
    private void ApplyRunInstancesConfig()
    {
        var editorSettings = EditorInterface.Singleton.GetEditorSettings();
        bool disabled = ToolingDisabled;
        var instance = new Godot.Collections.Dictionary
        {
            { "arguments", disabled ? "" : "--headless" },
            { "features", "" },
            { "override_args", !disabled },
            { "override_features", false },
        };
        editorSettings.SetProjectMetadata("debug_options", "run_instances_config",
            new Godot.Collections.Array { instance });
        editorSettings.SetProjectMetadata("debug_options", "run_instance_count", 1);
        editorSettings.SetProjectMetadata("debug_options", "multiple_instances_enabled", !disabled);
    }

    /// <summary>
    /// A .NET assembly reload recreates this plugin instance in place WITHOUT
    /// re-running _EnterTree: plain fields come back null and the UI's
    /// capturing-lambda connections die, leaving zombie nodes in the tree (dead
    /// Play buttons). Processing resumes on the recreated instance, so a null
    /// mainScreenInstance here is the post-reload signature — sweep the stale
    /// nodes and rebuild. Safe now that unloads are clean (see the name-bound
    /// Callable notes); the fresh tab resyncs from the reload-surviving
    /// orchestrator in its _Ready.
    /// </summary>
    public override void _Process(double delta)
    {
        if (ToolingDisabled)
            return;

        if (mainScreenInstance is null)
        {
            RebuildUiAfterAssemblyReload();
            return;
        }

        PollSessionState(delta);
    }

    /// <summary>
    /// Keeps the Play/Stop button and the debugger tab in step with the play
    /// orchestrator.
    ///
    /// <para>Polled rather than driven purely by the orchestrator's
    /// <c>state_changed</c> signal, because launching is exactly the moment the
    /// signal can't be relied on: <c>play_custom_scene</c> runs the C# build, and
    /// the .NET assembly reload that follows nulls this plugin's fields and
    /// recreates its controls mid-flight. A state_changed emitted in that window
    /// arrives at a plugin whose playButton no longer exists, and nothing
    /// re-checks afterwards — which left the button showing Play during a live
    /// session and the tab stuck on "No session".</para>
    ///
    /// <para>The signal is still connected: it makes the common case update
    /// immediately instead of up to <see cref="SESSION_POLL_INTERVAL"/> late.</para>
    /// </summary>
    private void PollSessionState(double delta)
    {
        sinceSessionPoll += delta;
        if (sinceSessionPoll < SESSION_POLL_INTERVAL)
            return;
        sinceSessionPoll = 0;

        bool running = IsSessionRunning();
        if (running == lastKnownSessionRunning)
            return;
        lastKnownSessionRunning = running;
        OnOrchestratorStateChanged();
    }

    private void RebuildUiAfterAssemblyReload()
    {
        GD.Print("Nebula: rebuilding editor UI after .NET assembly reload");

        var baseControl = EditorInterface.Singleton.GetBaseControl();
        var stale = new List<Node>();
        CollectNodesByName(baseControl, "NebulaMainScreen", stale);
        CollectNodesByName(baseControl, "NebulaPlayBar", stale);
        CollectNodesByName(baseControl, "NebulaConfigDialog", stale);
        bool wasVisible = false;
        foreach (var node in stale)
        {
            if (((string)node.Name).Contains("NebulaMainScreen") && node is Control { Visible: true })
                wasVisible = true;
            node.Name = (string)node.Name + "_stale";
            node.QueueFree();
        }
        configDialogInstance = null;

        CreateMainScreen(wasVisible);
        CreatePlayBar();
        GetOrchestrator(createIfMissing: false);
        CallDeferred(MethodName.ApplyRunBarVisibility);
    }

    private static void CollectNodesByName(Node root, string namePart, List<Node> results)
    {
        if (((string)root.Name).Contains(namePart))
            results.Add(root);
        foreach (var child in root.GetChildren())
            CollectNodesByName(child, namePart, results);
    }

    /// <summary>
    /// Shows/hides Godot's built-in run bar (Run … Movie Maker Mode) based on the
    /// Nebula/config/editor/disable_tooling project setting. The run bar is an
    /// internal editor node, so this reaches into the editor UI tree — the class
    /// name check is the only unofficial dependency.
    /// </summary>
    private void ApplyRunBarVisibility()
    {
        if (editorRunBar is null || !GodotObject.IsInstanceValid(editorRunBar))
            editorRunBar = FindEditorRunBar(EditorInterface.Singleton.GetBaseControl());
        if (editorRunBar is null)
            return;
        editorRunBar.Visible = ToolingDisabled;
    }

    private static Control FindEditorRunBar(Node node)
    {
        if (node.GetClass() == "EditorRunBar")
            return node as Control;
        foreach (var child in node.GetChildren())
        {
            var found = FindEditorRunBar(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Adds an autoload only when project.godot doesn't already carry it, so a normal
    /// editor restart doesn't re-register (and error on) the existing entries.
    /// </summary>
    private void RegisterAutoloadIfMissing(string name, string path)
    {
        if (ProjectSettings.HasSetting($"autoload/{name}"))
            return;
        AddAutoloadSingleton(name, path);
    }
}

#endif // Tools
