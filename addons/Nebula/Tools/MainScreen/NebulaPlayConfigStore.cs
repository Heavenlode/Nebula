namespace Nebula.Tools;

#if TOOLS

using Godot;
using System.Collections.Generic;

/// <summary>
/// A locally-stored play target: a headless server plus some number of clients
/// on this machine.
///
/// <para>Deliberately minimal for now — remote/deploy targets carried address,
/// port and export-preset fields that nothing consumed.</para>
/// </summary>
public sealed class NebulaPlayConfiguration
{
    public string Name = "";
    public int ClientCount = 1;

    public NebulaPlayConfiguration Clone() => new()
    {
        Name = Name,
        ClientCount = ClientCount,
    };

    /// <summary>The configuration is just a client count, so name it after one.</summary>
    public static string DisplayName(int clientCount) =>
        clientCount == 1 ? "1 client" : $"{clientCount} clients";
}

/// <summary>
/// Single owner of <c>user://nebula_config.cfg</c>: the list of play
/// configurations plus which one is currently selected in the toolbar.
///
/// Previously the tab and the toolbar each parsed this file with their own
/// ConfigFile instance and could drift apart; everything goes through here now.
/// </summary>
public static class NebulaPlayConfigStore
{
    public const string LOCAL_CONFIG_PATH = "user://nebula_config.cfg";
    public const string CONFIG_SECTION_PREFIX = "play_configuration_";
    private const string META_SECTION = "nebula";
    private const string SELECTED_KEY = "selected";
    private const int DEFAULT_CLIENT_COUNT = 1;

    /// <summary>
    /// Loads every stored configuration. When the file is missing or holds no
    /// configurations a default local target is seeded <em>and persisted</em>,
    /// so the toolbar and the manage dialog always agree on what exists.
    /// </summary>
    public static List<NebulaPlayConfiguration> Load()
    {
        var configurations = new List<NebulaPlayConfiguration>();

        var file = new ConfigFile();
        if (file.Load(LOCAL_CONFIG_PATH) == Error.Ok)
        {
            foreach (string section in file.GetSections())
            {
                if (!section.StartsWith(CONFIG_SECTION_PREFIX))
                    continue;
                int clientCount = file.GetValue(section, "client_count", DEFAULT_CLIENT_COUNT).AsInt32();
                configurations.Add(new NebulaPlayConfiguration
                {
                    // Older files predate client_count and carried a free-text name.
                    Name = file.GetValue(section, "name", NebulaPlayConfiguration.DisplayName(clientCount)).AsString(),
                    ClientCount = clientCount,
                });
            }
        }

        if (configurations.Count == 0)
        {
            configurations.Add(new NebulaPlayConfiguration
            {
                Name = NebulaPlayConfiguration.DisplayName(DEFAULT_CLIENT_COUNT),
                ClientCount = DEFAULT_CLIENT_COUNT,
            });
            Save(configurations);
        }

        return configurations;
    }

    /// <summary>
    /// Rewrites the file from scratch (so removed entries actually disappear),
    /// preserving the current selection.
    /// </summary>
    public static void Save(IReadOnlyList<NebulaPlayConfiguration> configurations)
    {
        string selected = GetSelectedName();

        var file = new ConfigFile();
        for (int i = 0; i < configurations.Count; i++)
        {
            var config = configurations[i];
            string section = CONFIG_SECTION_PREFIX + i;
            file.SetValue(section, "name", config.Name);
            file.SetValue(section, "client_count", config.ClientCount);
        }
        if (selected.Length > 0)
            file.SetValue(META_SECTION, SELECTED_KEY, selected);

        var err = file.Save(LOCAL_CONFIG_PATH);
        if (err != Error.Ok)
            GD.PushError($"Nebula: failed to save {LOCAL_CONFIG_PATH} ({err})");
    }

    /// <summary>
    /// Name of the selected configuration, or an empty string when unset.
    /// Stored by name rather than index so reordering doesn't silently switch
    /// which target the Play button launches.
    /// </summary>
    public static string GetSelectedName()
    {
        var file = new ConfigFile();
        if (file.Load(LOCAL_CONFIG_PATH) != Error.Ok)
            return "";
        return file.GetValue(META_SECTION, SELECTED_KEY, "").AsString();
    }

    public static void SetSelectedName(string name)
    {
        var file = new ConfigFile();
        file.Load(LOCAL_CONFIG_PATH); // missing file is fine: we're creating it
        file.SetValue(META_SECTION, SELECTED_KEY, name);
        var err = file.Save(LOCAL_CONFIG_PATH);
        if (err != Error.Ok)
            GD.PushError($"Nebula: failed to save {LOCAL_CONFIG_PATH} ({err})");
    }

    /// <summary>
    /// The configuration the Play button acts on: the one named by the stored
    /// selection, falling back to the first entry when nothing is selected yet
    /// or the selection was since renamed or deleted.
    ///
    /// <para>The fallback is <em>persisted</em>, so the dropdown shows a checked
    /// entry from the very first run and the choice carries into later
    /// sessions instead of silently re-defaulting each time.</para>
    /// </summary>
    public static NebulaPlayConfiguration ResolveSelected()
    {
        var configurations = Load();
        string selected = GetSelectedName();
        foreach (var config in configurations)
        {
            if (config.Name == selected)
                return config;
        }

        SetSelectedName(configurations[0].Name);
        return configurations[0];
    }
}

#endif // TOOLS
