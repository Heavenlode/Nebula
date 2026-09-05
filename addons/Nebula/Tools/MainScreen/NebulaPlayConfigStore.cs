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

    /// <summary>
    /// Scripted bot instances launched alongside the real clients. Each is a full client process
    /// driven by <see cref="Nebula.Bots.BotBehavior"/> rather than a keyboard.
    /// </summary>
    public int BotCount = 0;

    /// <summary>
    /// Name of the <see cref="Nebula.Bots.BotBehavior"/> subclass the bots run. Stored as a type
    /// name rather than a script path — see BotRunner for why. Empty means "no bots", regardless
    /// of <see cref="BotCount"/>.
    /// </summary>
    public string BotBehavior = "";

    /// <summary>
    /// Whether bot instances get <c>--headless</c>. On by default: bots are usually there to
    /// populate a world rather than to be watched, and rendering ten of them is pure cost.
    /// </summary>
    public bool BotsHeadless = true;

    /// <summary>
    /// Synthetic network impairment applied to the launched clients (see
    /// <see cref="Nebula.Diagnostics.NetworkImpairment"/>). Passed as per-instance command-line args,
    /// which is the only way to vary it: a project setting would impair every spawned client
    /// identically.
    /// </summary>
    public int SimLatencyMs = 0;
    public int SimJitterMs = 0;
    public int SimLossPct = 0;

    /// <summary>
    /// Bursty loss: a run of consecutive drops rather than scattered ones. This is the case that
    /// actually empties an interpolation buffer -- uniform loss almost never leaves a hole.
    /// <see cref="SimBurstLossPct"/> of 100 is a full dropout.
    /// </summary>
    public int SimBurstLossPct = 0;
    public int SimBurstEverySec = 10;
    public int SimBurstMs = 0;

    /// <summary>
    /// Impair only the FIRST client instead of all of them. This is the configuration that matters:
    /// an impaired peer is only interesting when a healthy one is watching it, and a session where
    /// everyone is equally degraded hides exactly the asymmetries worth seeing.
    /// </summary>
    public bool ImpairFirstClientOnly = true;

    public NebulaPlayConfiguration Clone() => new()
    {
        Name = Name,
        ClientCount = ClientCount,
        BotCount = BotCount,
        BotBehavior = BotBehavior,
        BotsHeadless = BotsHeadless,
        SimLatencyMs = SimLatencyMs,
        SimJitterMs = SimJitterMs,
        SimLossPct = SimLossPct,
        SimBurstLossPct = SimBurstLossPct,
        SimBurstEverySec = SimBurstEverySec,
        SimBurstMs = SimBurstMs,
        ImpairFirstClientOnly = ImpairFirstClientOnly,
    };

    /// <summary>Whether this configuration impairs anything at all.</summary>
    public bool HasImpairment =>
        SimLatencyMs > 0 || SimJitterMs > 0 || SimLossPct > 0
        || (SimBurstLossPct > 0 && SimBurstMs > 0);

    /// <summary>
    /// The configuration is just its instance counts, so name it after them. Bots have to appear
    /// here: the selected target is stored by name, so two configurations differing only in bot
    /// count would otherwise be indistinguishable.
    /// </summary>
    public static string DisplayName(int clientCount, int botCount)
    {
        string clients = clientCount == 1 ? "1 client" : $"{clientCount} clients";
        if (botCount <= 0)
            return clients;
        return botCount == 1 ? $"{clients} + 1 bot" : $"{clients} + {botCount} bots";
    }
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
                // Absent bot keys default to "no bots", so files written before bots existed load
                // unchanged and keep behaving exactly as they did.
                int botCount = file.GetValue(section, "bot_count", 0).AsInt32();
                configurations.Add(new NebulaPlayConfiguration
                {
                    // Older files predate client_count and carried a free-text name.
                    Name = file.GetValue(section, "name", NebulaPlayConfiguration.DisplayName(clientCount, botCount)).AsString(),
                    ClientCount = clientCount,
                    BotCount = botCount,
                    BotBehavior = file.GetValue(section, "bot_behavior", "").AsString(),
                    BotsHeadless = file.GetValue(section, "bots_headless", true).AsBool(),
                    // Absent in older files, which simply means "no impairment".
                    SimLatencyMs = file.GetValue(section, "sim_latency_ms", 0).AsInt32(),
                    SimJitterMs = file.GetValue(section, "sim_jitter_ms", 0).AsInt32(),
                    SimLossPct = file.GetValue(section, "sim_loss_pct", 0).AsInt32(),
                    SimBurstLossPct = file.GetValue(section, "sim_burst_loss_pct", 0).AsInt32(),
                    SimBurstEverySec = file.GetValue(section, "sim_burst_every_sec", 10).AsInt32(),
                    SimBurstMs = file.GetValue(section, "sim_burst_ms", 0).AsInt32(),
                    ImpairFirstClientOnly = file.GetValue(section, "impair_first_client_only", true).AsBool(),
                });
            }
        }

        if (configurations.Count == 0)
        {
            configurations.Add(new NebulaPlayConfiguration
            {
                Name = NebulaPlayConfiguration.DisplayName(DEFAULT_CLIENT_COUNT, 0),
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
            file.SetValue(section, "bot_count", config.BotCount);
            file.SetValue(section, "bot_behavior", config.BotBehavior);
            file.SetValue(section, "bots_headless", config.BotsHeadless);
            file.SetValue(section, "sim_latency_ms", config.SimLatencyMs);
            file.SetValue(section, "sim_jitter_ms", config.SimJitterMs);
            file.SetValue(section, "sim_loss_pct", config.SimLossPct);
            file.SetValue(section, "sim_burst_loss_pct", config.SimBurstLossPct);
            file.SetValue(section, "sim_burst_every_sec", config.SimBurstEverySec);
            file.SetValue(section, "sim_burst_ms", config.SimBurstMs);
            file.SetValue(section, "impair_first_client_only", config.ImpairFirstClientOnly);
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
