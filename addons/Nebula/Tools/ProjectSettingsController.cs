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
        ("Nebula/config/pack_enabled",          "Nebula/config/pack/enabled"),
        ("Nebula/config/pack_validate",         "Nebula/config/pack/validate"),
        ("Nebula/config/log_level",             "Nebula/config/debug/log_level"),
        ("Nebula/config/log_tick_payloads",     "Nebula/config/debug/log_tick_payloads"),
        ("Nebula/config/debug_export_interval", "Nebula/config/debug/export_interval"),
        ("Nebula/editor/disable_editor_tooling", "Nebula/config/editor/disable_tooling"),
    };

    /// <summary>
    /// Keys that no longer exist and are not migrated anywhere. Erased so they
    /// stop showing up as stray groups under Nebula in Project Settings.
    /// </summary>
    private static readonly string[] ObsoleteSettings =
    {
        // Superseded by the editor/disable_tooling master switch.
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
        // Master switch for the debug channel. On by default, but it never opens a
        // port on its own: it is ANDed with --debugPort=N, which the editor's Play
        // button supplies. Turning it off makes NetRunner/WorldRunner skip the
        // broadcast path entirely rather than merely muting it.
        Register(NetRunner.DEBUG_SERVER_SETTING, true, new(){
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

        // ── Pack ─────────────────────────────────────────────────────────
        // NebulaPack: delta-compress tick payloads against a baseline the peer has acknowledged.
        // Server-side and per-packet - every packet says whether it is a delta or raw - so clients
        // decode both regardless and no handshake is involved.
        Register("Nebula/config/pack/enabled", true, new(){
            {"type", (int)Variant.Type.Bool},
        });

        // NebulaPack: append a checksum of the raw payload and verify it after decoding. Costs 2
        // bytes per packet and turns any window divergence into an immediate, loud failure rather
        // than silently corrupted state. Worth leaving on until the feature has real mileage.
        Register("Nebula/config/pack/validate", true, new(){
            {"type", (int)Variant.Type.Bool},
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
        // Editor: master switch for the Nebula editor tooling (main-screen tab,
        // play target button, run-bar hiding, headless run-instances config).
        // Requires an editor restart to take full effect.
        Register(Main.DISABLE_TOOLING_SETTING, false, new(){
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
