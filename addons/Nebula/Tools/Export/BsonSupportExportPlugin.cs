namespace Nebula.Tools;

#if TOOLS

using Godot;

/// <summary>
/// Decides, per export, whether the build compiles MongoDB.Bson and the BSON persistence API.
///
/// <para>The .NET publish that Godot runs during an export sees only the environment, never the
/// preset, so this plugin turns the preset's <c>nebula/bson_support</c> option into the
/// <c>NEBULA_BSON_SUPPORT</c> variable that Nebula.props reads. It has to do that BEFORE the .NET
/// export plugin publishes, and that plugin registers at editor start, ahead of every addon, and
/// publishes inside its own <c>_export_begin</c> -- every begin/feature hook of an addon runs after
/// it. The one place that runs earlier with the preset attached is export-option validation:
/// Godot's <c>can_export</c> hands each plugin the preset and asks it about its own options, and
/// that happens right before a command-line export and whenever the export dialog's current preset
/// changes. So the variable is written from <see cref="_GetExportOptionWarning"/>.</para>
///
/// <para>"Export All" is the exception: it exports every preset without re-validating each one, so
/// the variable still reflects the preset the dialog was showing. <see cref="_ExportBegin"/> sees
/// the real preset (too late to build with, early enough to tell) and reports the mismatch as an
/// export error.</para>
///
/// <para>Off by default. A dedicated_server preset exported with it off gets a warning here and,
/// on the game side, whatever refusal the persistence layer chooses at startup.</para>
/// </summary>
[Tool]
public partial class BsonSupportExportPlugin : EditorExportPlugin
{
    /// <summary>Per-preset export option: whether this export compiles BSON support.</summary>
    public const string OptionName = "nebula/bson_support";
    /// <summary>Project setting: whether the EDITOR build compiles BSON support (read by Nebula.props).</summary>
    public const string ProjectSettingName = "Nebula/config/build/bson_support";
    /// <summary>What Nebula.props reads. "1" on, "0" off; anything else falls back to its own rule.</summary>
    public const string EnvironmentVariable = "NEBULA_BSON_SUPPORT";
    private const string EnvironmentOn = "1";
    private const string EnvironmentOff = "0";
    private const string DedicatedServerFeature = "dedicated_server";
    private const string MessageCategory = "Nebula";

    public override string _GetName() => "NebulaBsonSupport";

    /// <summary>
    /// Every platform. Not optional: Godot's default is FALSE, and <c>can_export</c> skips an
    /// unsupported plugin's option validation entirely, which is the hook this whole plugin lives in.
    /// </summary>
    public override bool _SupportsPlatform(EditorExportPlatform platform) => true;

    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetExportOptions(EditorExportPlatform platform)
    {
        return new Godot.Collections.Array<Godot.Collections.Dictionary>
        {
            new Godot.Collections.Dictionary
            {
                ["option"] = new Godot.Collections.Dictionary
                {
                    ["name"] = OptionName,
                    ["type"] = (int)Variant.Type.Bool,
                },
                ["default_value"] = false,
            },
        };
    }

    /// <summary>
    /// Validation-time hook: the preset is attached and the .NET publish has not run yet. Writes the
    /// variable the build reads. Also the dialog's per-option warning text, so a server preset with
    /// BSON support off says so next to the checkbox.
    /// </summary>
    public override string _GetExportOptionWarning(EditorExportPlatform platform, string option)
    {
        if (option != OptionName)
            return "";

        var preset = GetExportPreset();
        if (preset == null)
            return "";

        bool enabled = preset.Get(OptionName).AsBool();
        string value = enabled ? EnvironmentOn : EnvironmentOff;
        if (System.Environment.GetEnvironmentVariable(EnvironmentVariable) != value)
        {
            SetVariable(value);
            GD.Print($"[Nebula] BSON support {(enabled ? "ON" : "OFF")} for the next export ({OptionName}).");
        }

        if (!enabled && IsDedicatedServer(preset))
            return "This preset is a dedicated server but BSON support is off: nothing it persists through BSON will be saved.";
        return "";
    }

    /// <summary>
    /// The preset here is the one really being exported. By now the .NET publish has used whatever
    /// the variable held, so a disagreement cannot be fixed, only reported.
    /// </summary>
    public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
    {
        var preset = GetExportPreset();
        bool wanted = preset != null && preset.Get(OptionName).AsBool();
        bool built = System.Environment.GetEnvironmentVariable(EnvironmentVariable) == EnvironmentOn;

        if (wanted != built)
        {
            GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Error, MessageCategory,
                $"\"{OptionName}\" is {(wanted ? "on" : "off")} for this preset but the .NET build was made " +
                $"with it {(built ? "on" : "off")}. This happens with Export All when presets differ, or with " +
                "a pack/zip-only export; export this preset on its own.");
            return;
        }

        if (!built && System.Array.IndexOf(features, DedicatedServerFeature) >= 0)
        {
            GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Warning, MessageCategory,
                $"dedicated_server preset exported without BSON support: nothing this server persists " +
                $"through BSON will be saved. Enable \"{OptionName}\" on the preset if it should.");
        }
    }

    private static bool IsDedicatedServer(EditorExportPreset preset)
    {
        if (preset.Get("dedicated_server").AsBool())
            return true;
        string custom = preset.Get("custom_features").AsString();
        return custom.Contains(DedicatedServerFeature);
    }

    /// <summary>
    /// Clears the variable so a later export starts from validation again rather than inheriting
    /// this one's answer. The editor process owns this variable; anything it held on launch is not
    /// preserved, on purpose: it would otherwise silently decide every export.
    /// </summary>
    public override void _ExportEnd()
    {
        SetVariable(null);
    }

    /// <summary>
    /// Both copies of the environment. The .NET runtime snapshots the process environment at startup
    /// and keeps its own copy on Unix: a native setenv (Godot's OS.set_environment) is invisible to it,
    /// and the dotnet publish is started from the managed side, so it inherits the MANAGED copy. That
    /// is the one that matters; the native one is kept in step so GDScript sees the same answer.
    /// </summary>
    private static void SetVariable(string value)
    {
        System.Environment.SetEnvironmentVariable(EnvironmentVariable, value);
        if (value == null)
            OS.UnsetEnvironment(EnvironmentVariable);
        else
            OS.SetEnvironment(EnvironmentVariable, value);
    }
}

#endif
