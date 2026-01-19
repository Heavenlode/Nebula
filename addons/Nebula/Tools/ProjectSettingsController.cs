namespace Nebula.Tools;

#if TOOLS

using Godot;

/// <summary>
/// Controller class to manage Nebula-specific project settings in the Godot editor.
/// Sets up configuration, networking, and world-related properties for runtime usage.
/// </summary>
[Tool]
public partial class ProjectSettingsController : Node
{

    /// <summary>
    /// Called when the node enters the scene tree.
    /// Initializes Nebula project settings and registers them with Godot's ProjectSettings.
    /// </summary>
    public override void _EnterTree()
    {

        // Log level setting
        ProjectSettings.SetSetting("Nebula/config/log_level", ProjectSettings.GetSetting("Nebula/config/log_level", 0));
        ProjectSettings.SetInitialValue("Nebula/config/log_level", 0);
        ProjectSettings.SetAsBasic("Nebula/config/log_level", true);
        ProjectSettings.AddPropertyInfo(new(){
            {"name", "Nebula/config/log_level"},
            {"type", (int)Variant.Type.Int},
            { "hint", (int)PropertyHint.Enum},
            { "hint_string", "Error:1,Warn:2,Info:4,Verbose:8"}
        });

        // Network Settings - IP
        ProjectSettings.SetSetting("Nebula/network/IP", ProjectSettings.GetSetting("Nebula/network/IP", "127.0.0.1"));
        ProjectSettings.SetInitialValue("Nebula/network/IP", "127.0.0.1");
        ProjectSettings.SetAsBasic("Nebula/network/IP", true);
        ProjectSettings.AddPropertyInfo(new(){
            {"name", "Nebula/network/IP"},
            {"type", (int)Variant.Type.String},
        });

        // Network Settings - Default Port
        ProjectSettings.SetSetting("Nebula/network/default_port", ProjectSettings.GetSetting("Nebula/network/default_port", 8888));
        ProjectSettings.SetInitialValue("Nebula/network/default_port", 8888);
        ProjectSettings.SetAsBasic("Nebula/network/default_port", true);
        ProjectSettings.AddPropertyInfo(new(){
            {"name", "Nebula/network/default_port"},
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "1000,65535,1"},
        });

        // Network Settings - MTU
        ProjectSettings.SetSetting("Nebula/network/MTU", ProjectSettings.GetSetting("Nebula/network/MTU", 1400));
        ProjectSettings.SetInitialValue("Nebula/network/MTU", 1400);
        ProjectSettings.SetAsBasic("Nebula/network/MTU", true);
        ProjectSettings.AddPropertyInfo(new(){
            {"name", "Nebula/network/MTU"},
            {"type", (int)Variant.Type.Int},
            {"hint", (int)PropertyHint.Range},
            {"hint_string", "100,65535,1"},
        });

        // World Settings - Default Scene
        ProjectSettings.SetSetting("Nebula/world/default_scene",
            ProjectSettings.GetSetting("Nebula/world/default_scene", ProjectSettings.GetSetting("application/run/main_scene", "")));
        ProjectSettings.SetInitialValue("Nebula/world/default_scene", ProjectSettings.GetSetting("application/run/main_scene", ""));
        ProjectSettings.SetAsBasic("Nebula/world/default_scene", true);
        ProjectSettings.AddPropertyInfo(new(){
            {"name", "Nebula/world/default_scene"},
            {"type", (int)Variant.Type.String},
            {"hint", (int)PropertyHint.File},
            {"hint_string", "*.tscn"},
        });

        // World Settings - Managed Entrypoint
        // ProjectSettings.SetSetting("Nebula/world/managed_entrypoint", ProjectSettings.GetSetting("Nebula/world/managed_entrypoint", false));
        // ProjectSettings.SetInitialValue("Nebula/world/managed_entrypoint", false);
        // ProjectSettings.SetAsBasic("Nebula/world/managed_entrypoint", true);
        // ProjectSettings.AddPropertyInfo(new(){
        //     {"name", "Nebula/world/managed_entrypoint"},
        //     {"type", (int)Variant.Type.Bool},
        // });

        // // Override main_scene if managed_entrypoint is enabled
        // if (ProjectSettings.GetSetting("Nebula/world/managed_entrypoint", true).AsBool())
        //     ProjectSettings.SetSetting("application/run/main_scene", "res://addons/Nebula/Utils/ServerClientConnector/default_server_client_connector.tscn");

        // Save project settings after modification
        ProjectSettings.Save();
    }

    /// <summary>
    /// Called when the node exits the scene tree.
    /// Restores original project settings and clears Nebula-specific overrides.
    /// </summary>
    public override void _ExitTree()
    {

        // Restore main_scene to user-defined default
        // ProjectSettings.SetSetting("application/run/main_scene", ProjectSettings.GetSetting("Nebula/world/default_scene"));

        // Clear Nebula-specific settings from ProjectSettings
        // ProjectSettings.Clear("Nebula/config/log_level");
        // ProjectSettings.Clear("Nebula/network/IP");
        // ProjectSettings.Clear("Nebula/network/default_port");
        // ProjectSettings.Clear("Nebula/network/MTU");
        // ProjectSettings.Clear("Nebula/world/default_scene");
        // ProjectSettings.Clear("Nebula/world/managed_entrypoint");

        // Save after cleanup
        ProjectSettings.Save();
    }

    /// <summary>
    /// Configures the networking runner instance based on Nebula project settings.
    /// </summary>
    /// <returns>True if configuration was applied successfully.</returns>
    public bool Build()
    {
        // Override the port for the networking runner
        NetRunner.Instance.OverridePort(ProjectSettings.GetSetting("Nebula/network/default_port").AsInt32());

        // Apply the server IP address (sets the default, can be overridden by SERVER_ADDRESS env var)
        NetRunner.Instance.DefaultServerAddress = ProjectSettings.GetSetting("Nebula/network/IP").AsString();

        return true;
    }
}

#endif // TOOLS