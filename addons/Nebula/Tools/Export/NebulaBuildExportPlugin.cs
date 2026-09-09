namespace Nebula.Tools;

#if TOOLS

using Godot;

/// <summary>
/// Decides, per export, the two things Nebula compiles differently per build: whether MongoDB.Bson and
/// the BSON persistence API are in (<c>nebula/bson_support</c>), and which network role the build is
/// (<c>nebula/role</c>: NetRunner.IsServer / IsClient become constants, so the other role's code folds
/// away).
///
/// <para>The .NET publish that Godot runs during an export sees only the environment, never the
/// preset, so this plugin turns the preset's options into the <c>NEBULA_BSON_SUPPORT</c> and
/// <c>NEBULA_ROLE</c> variables that Nebula.props reads. It has to do that BEFORE the .NET export
/// plugin publishes, and that plugin registers at editor start, ahead of every addon, and publishes
/// inside its own <c>_export_begin</c> -- every begin/feature hook of an addon runs after it. The one
/// place that runs earlier with the preset attached is export-option validation: Godot's
/// <c>can_export</c> hands each plugin the preset and asks it about its own options, and that happens
/// right before a command-line export and whenever the export dialog's current preset changes. So the
/// variables are written from <see cref="_GetExportOptionWarning"/>.</para>
///
/// <para>"Export All" is the exception: it exports every preset without re-validating each one, so
/// the variables still reflect the preset the dialog was showing. <see cref="_ExportBegin"/> sees
/// the real preset (too late to build with, early enough to tell) and reports the mismatch as an
/// export error.</para>
///
/// <para>Defaults: BSON support off; role Auto, which follows the preset's dedicated_server flag. An
/// export made without this plugin keeps the runtime role, never a wrong constant.</para>
/// </summary>
[Tool]
public partial class NebulaBuildExportPlugin : EditorExportPlugin
{
    /// <summary>Per-preset export option: whether this export compiles BSON support.</summary>
    public const string OptionName = "nebula/bson_support";
    /// <summary>Project setting: whether the EDITOR build compiles BSON support (read by Nebula.props).</summary>
    public const string ProjectSettingName = "Nebula/config/build/bson_support";
    /// <summary>What Nebula.props reads. "1" on, "0" off; anything else falls back to its own rule.</summary>
    public const string EnvironmentVariable = "NEBULA_BSON_SUPPORT";
    private const string EnvironmentOn = "1";
    private const string EnvironmentOff = "0";

    /// <summary>Per-preset export option: the build's network role. Values are <see cref="Role"/>.</summary>
    public const string RoleOptionName = "nebula/role";
    /// <summary>What Nebula.props reads for the role: "server", "client" or "runtime".</summary>
    public const string RoleEnvironmentVariable = "NEBULA_ROLE";
    private const string RoleServer = "server";
    private const string RoleClient = "client";
    private const string RoleRuntime = "runtime";

    /// <summary>The <c>nebula/role</c> option, in the order the enum hint lists them.</summary>
    public enum Role
    {
        /// <summary>Server when the preset has the dedicated_server flag, client otherwise.</summary>
        Auto = 0,
        Client = 1,
        Server = 2,
        /// <summary>No constant: the role is decided when the network starts, as in the editor.</summary>
        Runtime = 3,
    }
    private const string RoleEnumHint = "Auto,Client,Server,Runtime";
    private const string DedicatedServerFeature = "dedicated_server";
    /// <summary>
    /// How a preset is asked whether it is a dedicated server: the preset API exposes no such query
    /// to plugins, but GetProjectSetting applies the preset's feature tags, so a setting whose
    /// dedicated_server override is true answers the question. Registered by ProjectSettingsController.
    /// </summary>
    public const string DedicatedServerProbeSetting = "Nebula/config/build/dedicated_server";
    public const string DedicatedServerProbeOverride = DedicatedServerProbeSetting + "." + DedicatedServerFeature;
    private const string MessageCategory = "Nebula";

    public override string _GetName() => "NebulaBuild";

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
            new Godot.Collections.Dictionary
            {
                ["option"] = new Godot.Collections.Dictionary
                {
                    ["name"] = RoleOptionName,
                    ["type"] = (int)Variant.Type.Int,
                    ["hint"] = (int)PropertyHint.Enum,
                    ["hint_string"] = RoleEnumHint,
                },
                ["default_value"] = (int)Role.Auto,
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
        if (option != OptionName && option != RoleOptionName)
            return "";

        var preset = GetExportPreset();
        if (preset == null)
            return "";

        // Both variables on either call: Godot asks about the options one at a time, and the
        // publish must see the whole answer whichever question came last.
        bool bson = preset.Get(OptionName).AsBool();
        string role = EffectiveRole(preset);
        ApplyVariable(EnvironmentVariable, bson ? EnvironmentOn : EnvironmentOff,
            $"[Nebula] BSON support {(bson ? "ON" : "OFF")} for the next export ({OptionName}).");
        ApplyVariable(RoleEnvironmentVariable, role,
            $"[Nebula] role {role.ToUpperInvariant()} for the next export ({RoleOptionName}).");

        if (option == OptionName && !bson && IsDedicatedServer(preset))
            return "This preset is a dedicated server but BSON support is off: nothing it persists through BSON will be saved.";
        if (option == RoleOptionName && role == RoleClient && bson)
            return "This preset is a client but BSON support is on: the client would carry the server's persistence code.";
        return "";
    }

    /// <summary>The role a preset's option resolves to, as the value Nebula.props reads.</summary>
    private static string EffectiveRole(EditorExportPreset preset)
    {
        var role = (Role)preset.Get(RoleOptionName).AsInt32();
        return role switch
        {
            Role.Client => RoleClient,
            Role.Server => RoleServer,
            Role.Runtime => RoleRuntime,
            _ => IsDedicatedServer(preset) ? RoleServer : RoleClient,
        };
    }

    private static void ApplyVariable(string variable, string value, string announcement)
    {
        if (System.Environment.GetEnvironmentVariable(variable) == value)
            return;
        SetVariable(variable, value);
        GD.Print(announcement);
    }

    /// <summary>
    /// The preset here is the one really being exported. By now the .NET publish has used whatever
    /// the variable held, so a disagreement cannot be fixed, only reported.
    /// </summary>
    public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
    {
        var preset = GetExportPreset();
        bool wantedBson = preset != null && preset.Get(OptionName).AsBool();
        bool builtBson = System.Environment.GetEnvironmentVariable(EnvironmentVariable) == EnvironmentOn;
        string wantedRole = preset != null ? EffectiveRole(preset) : RoleRuntime;
        string builtRole = System.Environment.GetEnvironmentVariable(RoleEnvironmentVariable) ?? RoleRuntime;

        if (wantedBson != builtBson || wantedRole != builtRole)
        {
            GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Error, MessageCategory,
                $"this preset wants {RoleOptionName}={wantedRole}, {OptionName}={(wantedBson ? "on" : "off")} " +
                $"but the .NET build was made with role={builtRole}, BSON {(builtBson ? "on" : "off")}. This happens " +
                "with Export All when presets differ, or with a pack/zip-only export; export this preset on its own.");
            return;
        }

        if (!builtBson && System.Array.IndexOf(features, DedicatedServerFeature) >= 0)
        {
            GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Warning, MessageCategory,
                $"dedicated_server preset exported without BSON support: nothing this server persists " +
                $"through BSON will be saved. Enable \"{OptionName}\" on the preset if it should.");
        }
    }

    private static bool IsDedicatedServer(EditorExportPreset preset)
        => preset.GetProjectSetting(DedicatedServerProbeSetting).AsBool();

    /// <summary>
    /// Clears the variable so a later export starts from validation again rather than inheriting
    /// this one's answer. The editor process owns this variable; anything it held on launch is not
    /// preserved, on purpose: it would otherwise silently decide every export.
    /// </summary>
    public override void _ExportEnd()
    {
        SetVariable(EnvironmentVariable, null);
        SetVariable(RoleEnvironmentVariable, null);
    }

    /// <summary>
    /// Both copies of the environment. The .NET runtime snapshots the process environment at startup
    /// and keeps its own copy on Unix: a native setenv (Godot's OS.set_environment) is invisible to it,
    /// and the dotnet publish is started from the managed side, so it inherits the MANAGED copy. That
    /// is the one that matters; the native one is kept in step so GDScript sees the same answer.
    /// </summary>
    private static void SetVariable(string variable, string value)
    {
        System.Environment.SetEnvironmentVariable(variable, value);
        if (value == null)
            OS.UnsetEnvironment(variable);
        else
            OS.SetEnvironment(variable, value);
    }
}

#endif
