namespace Nebula.Tools;

#if TOOLS

using System.Collections.Generic;
using Godot;

/// <summary>
/// "Performance" tab of the Nebula main screen: one live row per server world,
/// fed by the ServerMetrics JSON lines that ride the debug channel (~1/s per
/// world). Always present; it has data only when the server is collecting, which
/// <see cref="Nebula.Diagnostics.ServerMetrics.EnableEnvVar"/> gates server-side
/// (process environment or .env.server) so production instances spend nothing.
///
/// <para>This view owns no sockets: <see cref="Internal.Editor.NebulaDebugView"/>
/// keeps the debug-channel connections and forwards METRICS frames here, so the
/// tab adds no traffic to the hub.</para>
/// </summary>
[Tool]
public partial class NebulaPerformanceView : Control
{
    private Tree table;
    private Label placeholder;

    /// <summary>Row per (process port, world id); updated in place each metrics line.</summary>
    private readonly Dictionary<string, TreeItem> rows = new();

    private enum Column
    {
        World,
        Peers,
        TickP50,
        TickP95,
        TickMax,
        BytesPerPeer,
        PayloadP50,
        PayloadP95,
        PayloadP99,
        PayloadMax,
        Deferred,
        SpawnBacklog,
        AckTimeouts,
        MtuExceeded,
        Gc,
    }

    /// <summary>
    /// Column definitions. MinWidth is explicit and no column expands: leaving one
    /// column expanding handed it every spare pixel (World was enormous) while the
    /// rest were autosized down until their contents clipped (GC).
    /// </summary>
    private static readonly (Column Col, string Title, string Tooltip, int MinWidth)[] Columns =
    {
        (Column.World, "World", "World id (first 8 chars) @ process debug port", 420),
        (Column.Peers, "Peers", "Connected peers", 60),
        (Column.TickP50, "Tick p50", "Server tick time, median (ms)", 72),
        (Column.TickP95, "Tick p95", "Server tick time, 95th percentile (ms)", 72),
        (Column.TickMax, "Tick max", "Server tick time, worst in window (ms)", 72),
        (Column.BytesPerPeer, "B/peer/s", "Outgoing bytes per peer per second (post-compression, on the wire)", 84),
        (Column.PayloadP50, "Size p50", "Per-peer tick payload, median (bytes, pre-compression)", 72),
        (Column.PayloadP95, "Size p95", "Per-peer tick payload, 95th percentile (bytes, pre-compression)", 72),
        (Column.PayloadP99, "Size p99", "Per-peer tick payload, 99th percentile (bytes, pre-compression)", 72),
        (Column.PayloadMax, "Size max", "Largest per-peer tick payload / the budget cap. Splitting keeps this at or under the cap.", 220),
        (Column.Deferred, "Deferred s/p", "Spawn / props sections deferred for budget this window", 96),
        (Column.SpawnBacklog, "Backlog", "Worst per-peer count of in-flight (Spawning) spawn records", 70),
        (Column.AckTimeouts, "Ack TO", "Peers force-disconnected for ack timeout this window", 64),
        (Column.MtuExceeded, "MTU", "Packets exceeding the MTU this window (must be 0)", 56),
        (Column.Gc, "GC", "Gen0/1/2 collections this window (netcode target: 0/0/0)", 138),
    };

    private const string TableName = "MetricsTable";
    private const string PlaceholderName = "MetricsPlaceholder";

    public override void _Ready()
    {
        Name = "Performance";
        SetAnchorsPreset(LayoutPreset.FullRect);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        EnsureUi();
    }

    /// <summary>
    /// Binds <see cref="table"/> and <see cref="placeholder"/>, building them on first
    /// use. Also the recovery path after a .NET assembly reload: that recreates this
    /// node's managed side — dropping both references and the <see cref="rows"/> map —
    /// WITHOUT re-running _Ready, while the engine-side children survive. Silently
    /// dropping every metric in that state is exactly the kind of failure that looks
    /// like "the feature is broken", so re-bind instead of assuming _Ready ran.
    /// </summary>
    private bool EnsureUi()
    {
        if (table is not null && GodotObject.IsInstanceValid(table)
            && placeholder is not null && GodotObject.IsInstanceValid(placeholder))
            return true;

        table = FindChild(TableName, recursive: true, owned: false) as Tree;
        placeholder = FindChild(PlaceholderName, recursive: true, owned: false) as Label;

        if (table is not null && placeholder is not null)
        {
            // Re-bound after a reload: the row map is gone but the engine-side items
            // remain, so start the table over rather than appending duplicates. Rows
            // repopulate on the next report, a second later.
            rows.Clear();
            table.Clear();
            table.CreateItem();
            UpdatePlaceholder();
            return true;
        }

        BuildUi();
        return table is not null;
    }

    private void BuildUi()
    {
        var rootVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rootVBox.SetAnchorsPreset(LayoutPreset.FullRect);
        rootVBox.AddThemeConstantOverride("separation", 6);
        AddChild(rootVBox);

        table = new Tree
        {
            Name = TableName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            Columns = Columns.Length,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        for (int i = 0; i < Columns.Length; i++)
        {
            table.SetColumnTitle(i, Columns[i].Title);
            table.SetColumnTitleTooltipText(i, Columns[i].Tooltip);
            table.SetColumnCustomMinimumWidth(i, Columns[i].MinWidth);
            table.SetColumnExpand(i, false);
        }
        table.CreateItem(); // hidden root
        rootVBox.AddChild(table);

        placeholder = new Label
        {
            Name = PlaceholderName,
            Text = "Enable NEBULA_PERFORMANCE=true in your .env and run your game.\n",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        placeholder.SetAnchorsPreset(LayoutPreset.FullRect);
        placeholder.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.4f));
        AddChild(placeholder);
        UpdatePlaceholder();
    }

    /// <summary>
    /// Called by <see cref="Internal.Editor.NebulaDebugView"/> for each METRICS frame,
    /// which resolves this node from the tree per frame. One call per world per second;
    /// parsing at that rate is free.
    /// </summary>
    public void OnMetricsReceived(int port, string json)
    {
        if (!EnsureUi())
            return;

        var parsed = Json.ParseString(json);
        if (parsed.VariantType != Variant.Type.Dictionary)
            return;
        var data = parsed.AsGodotDictionary();

        string worldId = data.TryGetValue("world", out var worldVar) ? worldVar.AsString() : "?";
        string rowKey = $"{port}:{worldId}";

        if (!rows.TryGetValue(rowKey, out var row) || !GodotObject.IsInstanceValid(row))
        {
            row = table.CreateItem(table.GetRoot());
            string shortWorld = worldId.Length > 8 ? worldId[..8] : worldId;
            row.SetText((int)Column.World, $"{shortWorld} @ :{port}");
            row.SetTooltipText((int)Column.World, worldId);
            rows[rowKey] = row;
            UpdatePlaceholder();
        }

        var tickMs = GetDict(data, "tick_ms");
        var payload = GetDict(data, "payload");
        var budget = GetDict(data, "budget");

        row.SetText((int)Column.Peers, GetInt(data, "peers").ToString());
        row.SetText((int)Column.TickP50, $"{GetFloat(tickMs, "p50"):F1}");
        row.SetText((int)Column.TickP95, $"{GetFloat(tickMs, "p95"):F1}");
        row.SetText((int)Column.TickMax, $"{GetFloat(tickMs, "max"):F1}");
        row.SetText((int)Column.BytesPerPeer, GetInt(data, "bytes_per_peer_s").ToString());
        row.SetText((int)Column.PayloadP50, GetInt(payload, "p50").ToString());
        row.SetText((int)Column.PayloadP95, GetInt(payload, "p95").ToString());
        row.SetText((int)Column.PayloadP99, GetInt(payload, "p99").ToString());

        // Shown against the cap so "is this near the limit?" needs no second lookup.
        int payloadMax = GetInt(payload, "max");
        int cap = GetInt(payload, "cap");
        row.SetText((int)Column.PayloadMax, cap > 0 ? $"{payloadMax} / {cap}" : payloadMax.ToString());

        row.SetText((int)Column.Deferred, $"{GetInt(budget, "spawn_deferred")}/{GetInt(budget, "props_deferred")}");
        row.SetText((int)Column.SpawnBacklog, GetInt(budget, "spawn_backlog_max").ToString());
        SetCountCell(row, Column.AckTimeouts, GetInt(data, "ack_timeouts"));
        SetCountCell(row, Column.MtuExceeded, GetInt(data, "mtu_exceeded"));

        string gc = "?";
        if (data.TryGetValue("gc", out var gcVar) && gcVar.VariantType == Variant.Type.Array)
        {
            var gcArray = gcVar.AsGodotArray();
            if (gcArray.Count == 3)
                gc = $"{gcArray[0].AsInt32()}/{gcArray[1].AsInt32()}/{gcArray[2].AsInt32()}";
        }
        row.SetText((int)Column.Gc, gc);
    }

    /// <summary>Zero renders plain; any failure count renders in the editor's error red.</summary>
    private void SetCountCell(TreeItem row, Column column, int count)
    {
        row.SetText((int)column, count.ToString());
        if (count > 0)
        {
            row.SetCustomColor((int)column, new Color(1.0f, 0.47f, 0.42f));
        }
        else
        {
            row.ClearCustomColor((int)column);
        }
    }

    private static Godot.Collections.Dictionary GetDict(Godot.Collections.Dictionary data, string key)
        => data.TryGetValue(key, out var value) && value.VariantType == Variant.Type.Dictionary
            ? value.AsGodotDictionary()
            : new Godot.Collections.Dictionary();

    private static int GetInt(Godot.Collections.Dictionary data, string key)
        => data.TryGetValue(key, out var value) ? value.AsInt32() : 0;

    private static float GetFloat(Godot.Collections.Dictionary data, string key)
        => data.TryGetValue(key, out var value) ? (float)value.AsDouble() : 0f;

    private void UpdatePlaceholder()
    {
        if (placeholder is null)
            return;
        placeholder.Visible = rows.Count == 0;
    }
}

#endif // TOOLS
