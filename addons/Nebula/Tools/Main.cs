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
    public const string DISABLE_PLAY_BUTTON_SETTING = "Nebula/config/editor/disable_play_button";

    /// <summary>
    /// Project setting: when true, the Nebula Play button and its configuration
    /// dropdown are not created. Nothing else is affected — the debugger tab,
    /// the dock and the inspector plugin still load, and Godot's own run bar is
    /// always left exactly as the user has it. Editor restart required to apply.
    /// </summary>
    private static bool PlayButtonDisabled => ProjectSettings.GetSetting(DISABLE_PLAY_BUTTON_SETTING, false).AsBool();

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

    /// <summary>
    /// True when the play bar had to fall back to the documented toolbar
    /// container because the editor run bar could not be located — teardown has
    /// to use the matching removal call.
    /// </summary>
    private bool playBarUsesToolbarContainer;

    /// <summary>How often the play session's running state is re-checked, in seconds.</summary>
    private const double SESSION_POLL_INTERVAL = 0.25;
    private double sinceSessionPoll;
    private bool lastKnownSessionRunning;
    private bool lastKnownNebulaActive;
    private bool lastKnownEditorPlaying;

    /// <summary>
    /// Godot's built-in run bar, cached between visibility updates. Nebula hides
    /// it only for the duration of a Nebula session — never on the strength of a
    /// setting — and always puts it back in <see cref="_ExitTree"/>.
    /// </summary>
    private Control editorRunBar;

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
    public override bool _HasMainScreen() => true;

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

        // Main-screen tab (live network debugger)
        CreateMainScreen(visible: false);

        // Top-bar play controls (Play/Stop + configuration dropdown)
        if (!PlayButtonDisabled)
            CreatePlayBar();

        // Re-attach to a session that outlived a plugin reload.
        GetOrchestrator(createIfMissing: false);

        // Deferred: the run bar is only findable once the editor UI is built.
        CallDeferred(MethodName.ApplyPlayControlVisibility);

        // NOTE: Nebula never touches Godot's run bar or its Run Instances
        // settings. An earlier version hid the run bar and rewrote the
        // debug_options metadata on every editor start, which silently wiped
        // whatever the project had configured under Debug -> Customize Run
        // Instances. Nebula's own server/client/bot processes are spawned with
        // OS.create_process and owe nothing to that machinery; the editor-managed
        // play is only the debug-server holder, and NebulaDebugSession keeps
        // itself to one visible-free instance on its own.

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
        // Unconditional: the plugin can be disabled mid-session, and a run bar
        // left hidden would be indistinguishable from a broken editor.
        if (editorRunBar is not null && GodotObject.IsInstanceValid(editorRunBar))
            editorRunBar.Visible = true;
        editorRunBar = null;

        DisconnectOrchestrator();

        if (playBarInstance is not null)
        {
            DetachPlayBar();
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

        AttachPlayBar();
        UpdatePlayButton();
    }

    /// <summary>
    /// Parents the Nebula play bar immediately to the LEFT of Godot's built-in
    /// run bar, so the two sets of play controls sit together with Nebula's
    /// first. <c>AddControlToContainer(Toolbar, …)</c> appends to the end of the
    /// title bar — past the renderer selector — which is why the placement is
    /// done by hand here; it stays the fallback if the run bar can't be found.
    /// </summary>
    private void AttachPlayBar()
    {
        var anchor = FindPlayBarAnchor();
        if (anchor?.GetParent() is not BoxContainer titleBar)
        {
            playBarUsesToolbarContainer = true;
            AddControlToContainer(CustomControlContainer.Toolbar, playBarInstance);
            return;
        }

        playBarUsesToolbarContainer = false;
        titleBar.AddChild(playBarInstance);
        // The bar was appended after the anchor, so the anchor's index is still
        // the slot immediately before it.
        titleBar.MoveChild(playBarInstance, anchor.GetIndex());
    }

    private void DetachPlayBar()
    {
        if (playBarUsesToolbarContainer)
        {
            RemoveControlFromContainer(CustomControlContainer.Toolbar, playBarInstance);
            return;
        }
        playBarInstance.GetParent()?.RemoveChild(playBarInstance);
    }

    /// <summary>
    /// The node to insert the play bar in front of: the run bar itself, or
    /// whatever wrapper Godot has put around it, as long as that wrapper still
    /// sits directly in a horizontal box. Climbing to the box is what keeps this
    /// from becoming an unexpected second child of a single-child panel — the
    /// run bar's immediate parent has changed shape between Godot releases.
    /// </summary>
    private static Control FindPlayBarAnchor()
    {
        const int MAX_WRAPPER_DEPTH = 3;
        Node anchor = FindEditorRunBar(EditorInterface.Singleton.GetBaseControl());
        for (int depth = 0; anchor is not null && depth <= MAX_WRAPPER_DEPTH; depth++)
        {
            if (anchor.GetParent() is BoxContainer)
                return anchor as Control;
            anchor = anchor.GetParent() as Control;
        }
        return null;
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

        // List order is spawn order (the orchestrator staggers spawns): bots first, the
        // player client(s) last. A bot flood saturates the host exactly when a joining
        // client is doing its heaviest main-thread work (world + player scene builds), and
        // a client stalled past the server's ack timeout gets force-disconnected - so the
        // player client only launches once every bot process already exists.
        var clientArgsList = new Godot.Collections.Array();

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

        for (int i = 0; i < config.ClientCount; i++)
        {
            int clientDebugPort = ReserveLoopbackPort();
            debugPorts.Add(clientDebugPort);

            var clientArgs = new System.Collections.Generic.List<string>
            {
                "--path", projectPath,
                "--remote-debug", debugUri,
                $"--debugPort={clientDebugPort}",
                $"--clientId={i}",
            };

            // Impairment rides as per-instance args because that is the only level at which it can
            // differ between clients -- a project setting would degrade all of them identically, and
            // the case worth testing is one bad peer watched by a healthy one.
            if (config.HasImpairment && (!config.ImpairFirstClientOnly || i == 0))
            {
                clientArgs.Add($"--simLatencyMs={config.SimLatencyMs}");
                clientArgs.Add($"--simJitterMs={config.SimJitterMs}");
                clientArgs.Add($"--simLossPct={config.SimLossPct}");
                clientArgs.Add($"--simBurstLossPct={config.SimBurstLossPct}");
                clientArgs.Add($"--simBurstEverySec={config.SimBurstEverySec}");
                clientArgs.Add($"--simBurstMs={config.SimBurstMs}");
            }

            clientArgsList.Add(clientArgs.ToArray());
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
        lastKnownNebulaActive = IsNebulaSessionActive();
        lastKnownEditorPlaying = EditorInterface.Singleton.IsPlayingScene();
        UpdatePlayButton();
        ApplyPlayControlVisibility();
        // The debugger tab attaches to / releases the session's debug ports.
        if (mainScreenInstance is not null && GodotObject.IsInstanceValid(mainScreenInstance))
            mainScreenInstance.OnSessionStateChanged();
    }

    /// <summary>
    /// Only one set of play controls is live at a time: a Nebula session hides
    /// Godot's run bar until Nebula Stop, and a plain editor play hides Nebula's
    /// bar until it ends. Neither is ever hidden while nothing is running — the
    /// buttons belong to the user, and this is a transient mode, not a policy.
    ///
    /// <para>"The user pressed Godot's Play" has to be read as
    /// <c>IsPlayingScene() &amp;&amp; !nebulaActive</c>, because a Nebula session
    /// opens an editor play session of its own to hold the debug server; taking
    /// IsPlayingScene at face value would hide Nebula's own Stop button the
    /// instant it launched.</para>
    ///
    /// <para>The Nebula side keys off the orchestrator's <c>is_active</c> rather
    /// than <c>is_running</c>: PlayCustomScene runs the C# build first, so there
    /// is a multi-second window on a cold build where the session is unmistakably
    /// a Nebula one but no instance process exists yet.</para>
    /// </summary>
    private void ApplyPlayControlVisibility()
    {
        bool nebulaActive = IsNebulaSessionActive();
        bool editorPlaying = EditorInterface.Singleton.IsPlayingScene() && !nebulaActive;

        if (playBarInstance is not null && GodotObject.IsInstanceValid(playBarInstance))
            playBarInstance.Visible = !editorPlaying;

        if (editorRunBar is null || !GodotObject.IsInstanceValid(editorRunBar))
            editorRunBar = FindEditorRunBar(EditorInterface.Singleton.GetBaseControl());
        if (editorRunBar is not null)
            editorRunBar.Visible = !nebulaActive;
    }

    /// <summary>
    /// Whether a Nebula play session is live in the broad sense — launching,
    /// running, or staggering its instances — as opposed to
    /// <see cref="IsSessionRunning"/>, which only knows about spawned pids.
    /// </summary>
    private bool IsNebulaSessionActive()
    {
        var orchestrator = GetOrchestrator(createIfMissing: false);
        return orchestrator is not null && orchestrator.Call("is_active").AsBool();
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

        // Godot's own Play is pressed behind Nebula's back — there is no editor
        // signal for it — so the editor's play state is polled alongside the
        // session's rather than waited on.
        bool running = IsSessionRunning();
        bool active = IsNebulaSessionActive();
        bool editorPlaying = EditorInterface.Singleton.IsPlayingScene();
        if (running == lastKnownSessionRunning
            && active == lastKnownNebulaActive
            && editorPlaying == lastKnownEditorPlaying)
            return;

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
        if (!PlayButtonDisabled)
            CreatePlayBar();
        GetOrchestrator(createIfMissing: false);
        // The reload happens INSIDE PlayCustomScene, i.e. exactly while the run
        // bar is hidden for a launching session, so the rule has to be re-applied
        // against the surviving orchestrator rather than assumed.
        ApplyPlayControlVisibility();
    }

    private static void CollectNodesByName(Node root, string namePart, List<Node> results)
    {
        if (((string)root.Name).Contains(namePart))
            results.Add(root);
        foreach (var child in root.GetChildren())
            CollectNodesByName(child, namePart, results);
    }

    /// <summary>
    /// Locates Godot's built-in run bar (Run … Movie Maker Mode), which Nebula
    /// parks its own play controls in front of and hides for the duration of a
    /// Nebula session. It is never hidden on the strength of a project setting —
    /// an earlier version did that, and the built-in buttons are not Nebula's to
    /// take away. The run bar is an internal editor node, so this reaches into
    /// the editor UI tree; the class name check is the only unofficial
    /// dependency, and a miss costs the play bar its preferred position and
    /// leaves both sets of controls visible.
    /// </summary>
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
