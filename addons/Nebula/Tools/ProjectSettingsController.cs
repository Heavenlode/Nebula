namespace Nebula.Tools;

#if TOOLS

using Godot;

/// <summary>
/// Controller class to manage Nebula-specific project settings in the Godot editor.
/// Sets up configuration, networking, and world-related properties for runtime usage.
/// All settings live under a single "Nebula/config" group so they show in one tab.
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
    /// Called when the node enters the scene tree.
    /// Initializes Nebula project settings and registers them with Godot's ProjectSettings.
    /// </summary>
    public override void _EnterTree()
    {
        // Server IP address
        Register("Nebula/config/ip", "127.0.0.1", new(){
            {"type", (int)Variant.Type.String},
        });

        // Default port
        Register("Nebula/config/default_port", 8888, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1000,65535,1"},
        });

        // MTU
        Register("Nebula/config/mtu", 1400, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "100,65535,1"},
        });

        // Default world scene
        var defaultScene = ProjectSettings.GetSetting("application/run/main_scene", "");
        Register("Nebula/config/default_scene", defaultScene, new(){
            {"type", (int)Variant.Type.String},
            {"hint", (int)PropertyHint.File},
            {"hint_string", "*.tscn"},
        });

        // Log level
        Register("Nebula/config/log_level", 0, new(){
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Enum},
            {"hint_string", "Error:1,Warn:2,Info:4,Verbose:8"},
        });

        // NOTE: Nebula/config/enable_tcp is deliberately NOT registered here. The debug TCP
        // channel is pending rework, so we keep it out of the editor UI to avoid it being
        // flipped on and shipped. It's still readable at runtime (defaults to false), so
        // --debugPort=XXXX and a manual project.godot entry both still work.

        // Debug: log the full hex of every server tick payload on the client
        Register("Nebula/config/log_tick_payloads", false, new(){
            {"type", (int)Variant.Type.Bool},
        });

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
        NetRunner.Instance.OverridePort(ProjectSettings.GetSetting("Nebula/config/default_port").AsInt32());

        // Apply the server IP address (sets the default, can be overridden by SERVER_ADDRESS env var)
        NetRunner.Instance.DefaultServerAddress = ProjectSettings.GetSetting("Nebula/config/ip").AsString();

        return true;
    }
}

#endif // TOOLS
