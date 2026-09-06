namespace Nebula.Tools;

#if TOOLS

using Godot;

/// <summary>
/// Controller class to manage Nebula-specific project settings in the Godot editor.
///
/// <para>Everything lives under <c>Nebula/config/</c> so the Project Settings
/// dialog shows a single Nebula section, sub-grouped by concern
/// (network / world / pack / debug / editor). Settings that predate that layout
/// are migrated on load, so existing projects keep their values.</para>
/// </summary>
[Tool]
public partial class ProjectSettingsController : Node
{
    /// <summary>
    /// Registers a single Nebula project setting: seeds its current/initial value, marks it as
    /// basic (visible without Advanced Settings), and attaches editor property info. The
    /// property info dict's "name" is filled in automatically.
    /// </summary>
    private static void Register(string name, Variant defaultValue, Godot.Collections.Dictionary propertyInfo)
    {
        ProjectSettings.SetSetting(name, ProjectSettings.GetSetting(name, defaultValue));
        ProjectSettings.SetInitialValue(name, defaultValue);
        ProjectSettings.SetAsBasic(name, true);
        propertyInfo["name"] = name;
        ProjectSettings.AddPropertyInfo(propertyInfo);
    }

    /// <summary>
    /// Settings renamed when everything was consolidated under Nebula/config.
    /// Values are carried across and the old keys erased, so upgrading a project
    /// doesn't silently reset (for instance) its log level.
    /// </summary>
    private static readonly (string Old, string New)[] RenamedSettings =
    {
        ("Nebula/config/ip",                    "Nebula/config/network/ip"),
        ("Nebula/config/default_port",          "Nebula/config/network/default_port"),
        ("Nebula/config/mtu",                   "Nebula/config/network/mtu"),
        ("Nebula/config/default_scene",         "Nebula/config/world/default_scene"),
        ("Nebula/config/log_level",             "Nebula/config/debug/log_level"),
        ("Nebula/config/log_tick_payloads",     "Nebula/config/debug/log_tick_payloads"),
        ("Nebula/config/debug_export_interval", "Nebula/config/debug/export_interval"),
        ("Nebula/editor/disable_editor_tooling", "Nebula/config/editor/disable_tooling"),
        // Narrowed in scope, not just renamed: the switch used to suppress the
        // whole editor tooling and hide Godot's run bar. It now only suppresses
        // Nebula's own Play button. Carried across so a project that opted out
        // still gets no Nebula Play button; the run bar and the debugger tab
        // come back either way. NOTE: order matters here — this pair consumes
        // the result of the migration above it.
        ("Nebula/config/editor/disable_tooling", "Nebula/config/editor/disable_play_button"),
    };

    /// <summary>
    /// Keys that no longer exist and are not migrated anywhere. Erased so they
    /// stop showing up as stray groups under Nebula in Project Settings.
    /// </summary>
    private static readonly string[] ObsoleteSettings =
    {
        // Superseded by the editor/disable_play_button switch.
        "Nebula/editor/hide_embedded_play_buttons",
        // Never read by anything; the live key is config/world/default_scene,
        // which falls back to application/run/main_scene.
        "Nebula/world/default_scene",
        // The debug channel is exposed via --debugPort= only; it was never a
        // project-level concern (both names, pre- and post-regrouping).
        "Nebula/config/enable_tcp",
        "Nebula/config/debug/enable_tcp",
    };

    private static void RemoveObsoleteSettings()
    {
        foreach (var name in ObsoleteSettings)
        {
            if (ProjectSettings.HasSetting(name))
                ProjectSettings.SetSetting(name, default);
        }
    }

    /// <summary>Moves a setting's value to its new key and erases the old one.</summary>
    private static void MigrateRenamed()
    {
        foreach (var (oldName, newName) in RenamedSettings)
        {
            if (!ProjectSettings.HasSetting(oldName))
                continue;
            if (!ProjectSettings.HasSetting(newName))
                ProjectSettings.SetSetting(newName, ProjectSettings.GetSetting(oldName));
            // Assigning a null Variant removes the entry entirely.
            ProjectSettings.SetSetting(oldName, default);
        }
    }

    /// <summary>
    /// Called when the node enters the scene tree.
    /// Initializes Nebula project settings and registers them with Godot's ProjectSettings.
    /// </summary>
    public override void _EnterTree()
    {
        // Before Register: it seeds each key from its current value, which must
        // already be the migrated one.
        MigrateRenamed();

        // ── Network ──────────────────────────────────────────────────────
        // Server IP address
        Register("Nebula/config/network/ip", "127.0.0.1", new(){
            {"type", (int)Variant.Type.String},
        });

        // Default port
        Register("Nebula/config/network/default_port", 8888, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1000,65535,1"},
        });

        // MTU
        Register("Nebula/config/network/mtu", 1400, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "100,65535,1"},
        });

        // Liveness cutoff for in-world peers: seconds without a tick ack before the
        // server force-disconnects.
        Register(NetRunner.ACK_TIMEOUT_SETTING, NetRunner.DefaultAckTimeoutSeconds, new(){
            {"type", (int)Variant.Type.Float},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1,300,0.5"},
        });

        // Same cutoff for a JOINING peer (never acked yet): its first ack only follows
        // boot + world-scene load + a successfully imported tick, so it needs far more
        // headroom than the in-world cutoff.
        Register(NetRunner.JOIN_ACK_TIMEOUT_SETTING, NetRunner.DefaultJoinAckTimeoutSeconds, new(){
            {"type", (int)Variant.Type.Float},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1,300,0.5"},
        });

        // Network tick rate in ticks per second. The network tick fires on whole physics
        // frames, so this should divide physics/common/physics_ticks_per_second evenly
        // (with 60 physics: 60, 30, 20, 15, 12, 10, ...); anything else snaps to the
        // nearest achievable rate with a startup warning naming it. Read once at startup,
        // so changes take effect on the next run.
        Register("Nebula/config/network/ticks_per_second", 30, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1,120,1"},
        });

        // ── World ────────────────────────────────────────────────────────
        // Default world scene
        var defaultScene = ProjectSettings.GetSetting("application/run/main_scene", "");
        Register("Nebula/config/world/default_scene", defaultScene, new(){
            {"type", (int)Variant.Type.String},
            {"hint", (int)PropertyHint.File},
            {"hint_string", "*.tscn"},
        });

        // ── Debug ────────────────────────────────────────────────────────
        // Master switch for the debug channel. OFF by default: a diagnostic channel
        // has to be asked for, either here or with NEBULA_DEBUG=1 in the process's
        // .env. Even on it never opens a port by itself - it is ANDed with
        // --debugPort=N, which the editor's Play button supplies. Off makes
        // NetRunner/WorldRunner skip the broadcast path entirely rather than merely
        // muting it.
        Register(NetRunner.DEBUG_SERVER_SETTING, false, new(){
            {"type", (int)Variant.Type.Bool},
        });

        // Log level
        Register("Nebula/config/debug/log_level", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Enum},
            {"hint_string", "Error:1,Warn:2,Info:4,Verbose:8"},
        });

        // Network ticks between full world-state exports on the debug channel. The debugger
        // carries the last known state forward between exports, so raising this costs very
        // little fidelity on a busy world.
        Register("Nebula/config/debug/export_interval", 1, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1,60,1"},
        });

        // Debug: log the full hex of every server tick payload on the client
        Register("Nebula/config/debug/log_tick_payloads", false, new(){
            {"type", (int)Variant.Type.Bool},
        });

        // Debug: percentage of received tick packets the client drops before processing.
        // Simulates an unreliable link on a lossless LAN, to exercise loss-recovery paths
        // (spawn resend-until-acked, delta baseline fallback). 0 = off.
        Register("Nebula/config/debug/simulate_incoming_tick_loss", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,100,1"},
        });

        // Debug: synthetic network impairment applied to INBOUND packets. These are the editor
        // defaults; the per-instance switches are the --simLatencyMs / --simJitterMs / --simLossPct
        // command-line args (see Diagnostics/NetworkImpairment.cs), because a project setting is
        // process-global and would impair every client the Play tab spawns identically. The point of
        // the feature is one bad client beside a healthy one.
        Register("Nebula/config/debug/sim_latency_ms", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,1000,1"},
        });
        Register("Nebula/config/debug/sim_jitter_ms", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,500,1"},
        });
        Register("Nebula/config/debug/sim_loss_pct", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,100,1"},
        });

        // Debug: BURSTY loss. Independent per-packet loss is the friendly case -- real links drop
        // RUNS of consecutive packets (handover, congestion, interference), and a run is what actually
        // empties an interpolation buffer. Set burst_loss_pct to 100 for a full dropout.
        Register("Nebula/config/debug/sim_burst_loss_pct", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,100,1"},
        });
        Register("Nebula/config/debug/sim_burst_every_sec", 10, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1,120,1"},
        });
        Register("Nebula/config/debug/sim_burst_ms", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "0,5000,10"},
        });

        // ── Threading ────────────────────────────────────────────────────
        // Give every server world's SubViewport its own ProcessThreadGroup, so worlds run their
        // ticks concurrently instead of being walked one after another on the main thread.
        //
        // Note this parallelizes _process/_physics_process callbacks only. It does NOT parallelize
        // physics simulation: PhysicsServer3D steps every active space sequentially, so per-world
        // World3Ds still simulate serially either way. The gain is ServerProcessTick (dominated by
        // state serialization) and gameplay scripts.
        //
        // Off by default. Everything it depends on is written to be correct in both modes, so this
        // changes timing rather than behavior -- but it does move all gameplay code in a world onto
        // a worker thread, so anything reaching across worlds or into a mutable autoload needs to
        // have been audited first. Read once at startup.
        Register("Nebula/config/threading/per_world_thread_group", false, new(){
            {"type", (int)Variant.Type.Bool},
        });

        // ── Editor ───────────────────────────────────────────────────────
        // Editor: suppress Nebula's toolbar Play button and its configuration
        // dropdown. Godot's own run bar is always left alone, and the debugger
        // tab, dock and inspector plugin load regardless. Requires an editor
        // restart to take effect.
        Register(Main.DISABLE_PLAY_BUTTON_SETTING, false, new(){
            {"type", (int)Variant.Type.Bool},
        });

        RemoveObsoleteSettings();

        // Save project settings after modification
        ProjectSettings.Save();
    }

    /// <summary>
    /// Called when the node exits the scene tree.
    /// </summary>
    public override void _ExitTree()
    {
        ProjectSettings.Save();
    }

    /// <summary>
    /// Configures the networking runner instance based on Nebula project settings.
    /// </summary>
    /// <returns>True if configuration was applied successfully.</returns>
    public bool Build()
    {
        // Override the port for the networking runner
        NetRunner.Instance.OverridePort(ProjectSettings.GetSetting("Nebula/config/network/default_port").AsInt32());

        // Apply the server IP address (sets the default, can be overridden by SERVER_ADDRESS env var)
        NetRunner.Instance.DefaultServerAddress = ProjectSettings.GetSetting("Nebula/config/network/ip").AsString();

        return true;
    }
}

#endif // TOOLS
