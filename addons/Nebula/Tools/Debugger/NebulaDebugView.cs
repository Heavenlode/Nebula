#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Godot;
using LiteDB;
using Nebula.Serialization;
using Nebula.Tools;

namespace Nebula.Internal.Editor
{
    /// <summary>
    /// Body of the Nebula main-screen tab: connects to the debug channel of
    /// every process in the current play session and shows the selected world.
    /// Only server worlds are listed; a client's world is a mirror of one the
    /// server already exposes.
    ///
    /// <para>This replaces the old floating "Server Debug Client" window, which
    /// spoke ENet and had no reachable entry point. Because the editor's Play
    /// button spawns the instances itself and assigns each a
    /// <c>--debugPort</c>, there is no discovery protocol: the ports come
    /// straight from the play orchestrator.</para>
    ///
    /// <para>Connection state is deliberately cheap to rebuild — a .NET
    /// assembly reload frees and recreates this node, and the ports are
    /// re-read from the reload-surviving orchestrator.</para>
    /// </summary>
    [Tool]
    public partial class NebulaDebugView : Control
    {
        private const string ORCHESTRATOR_NODE_NAME = "NebulaPlayOrchestrator";
        private const string DEBUG_DB_DIR = "user://nebula_debug";
        private const int MAX_RETAINED_DATABASES = 5;

        /// <summary>Seconds between connection attempts to ports that aren't up yet.</summary>
        private const double CONNECT_RETRY_INTERVAL = 1.0;

        private static readonly PackedScene WorldDebugScene =
            GD.Load<PackedScene>("res://addons/Nebula/Tools/Debugger/world_debug.tscn");

        /// <summary>One attached process. Client processes are connected to
        /// (their announcements are how we know they're clients) but never get a
        /// world entry — only server worlds are listed.</summary>
        private sealed class ProcessConnection
        {
            public int Port;
            public TcpClient Client;
            public NetworkStream Stream;
            public Task ConnectTask;
            public readonly MemoryStream Inbox = new();
            public readonly Dictionary<string, WorldDebug> Panels = new();

            public bool IsConnected => Stream is not null && Client is { Connected: true };

            public void Close()
            {
                try { Stream?.Dispose(); } catch { /* already gone */ }
                try { Client?.Close(); } catch { /* already gone */ }
                Stream = null;
                Client = null;
                ConnectTask = null;
            }
        }

        private OptionButton worldSelector;
        private Control panelHost;
        private Label placeholder;
        private LiteDatabase db;

        /// <summary>Selector index -> (process port, world id), parallel to the dropdown.</summary>
        private readonly List<(int Port, string WorldKey)> selectorEntries = new();

        private readonly Dictionary<int, ProcessConnection> connections = new();
        private readonly List<int> targetPorts = new();
        private readonly byte[] readBuffer = new byte[8192];
        private double sinceConnectAttempt;
        private bool loggedFrameError;
        private bool loggedMetricsError;

        private NebulaPerformanceView performanceView;

        /// <summary>
        /// Finds the sibling Performance tab, which receives every METRICS frame. This
        /// view stays the single owner of the debug-channel connections so a second tab
        /// doesn't double the hub's traffic.
        ///
        /// <para>Resolved from the live scene tree on demand rather than wired once as a
        /// C# event or an injected reference. A .NET assembly reload — which every Play
        /// press triggers via its build — recreates the managed side of each node and
        /// DROPS managed state (event subscriptions, plain fields) WITHOUT re-running
        /// _Ready. A subscription made in NebulaMainScreen._Ready is therefore silently
        /// dead for the rest of the session: frames arrived, `subscribers=0`, and the
        /// Performance tab never updated. Re-resolving is reload-proof.</para>
        /// </summary>
        private NebulaPerformanceView ResolvePerformanceView()
        {
            if (performanceView is not null && GodotObject.IsInstanceValid(performanceView))
                return performanceView;

            performanceView = null;
            foreach (var sibling in GetParent()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            {
                if (sibling is NebulaPerformanceView found)
                {
                    performanceView = found;
                    break;
                }
            }
            return performanceView;
        }

        public override void _Ready()
        {
            Name = "NebulaDebugView";
            SetAnchorsPreset(LayoutPreset.FullRect);

            var rootVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            rootVBox.SetAnchorsPreset(LayoutPreset.FullRect);
            rootVBox.AddThemeConstantOverride("separation", 6);
            AddChild(rootVBox);

            var selectorRow = new HBoxContainer();
            selectorRow.AddThemeConstantOverride("separation", 8);
            rootVBox.AddChild(selectorRow);

            selectorRow.AddChild(new Label { Text = "World" });

            worldSelector = new OptionButton
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Server worlds currently reachable on the debug channel.",
            };
            worldSelector.Connect(OptionButton.SignalName.ItemSelected,
                new Callable(this, MethodName.OnWorldSelected));
            selectorRow.AddChild(worldSelector);

            panelHost = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            rootVBox.AddChild(panelHost);

            placeholder = new Label
            {
                Text = "No worlds — press Play in the toolbar.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeholder.SetAnchorsPreset(LayoutPreset.FullRect);
            placeholder.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.4f));
            AddChild(placeholder);

            RefreshSessionTargets();
            UpdatePlaceholder();
        }

        public override void _ExitTree()
        {
            // Assembly reloads recreate this node, so sockets must not be left
            // to the finalizer.
            foreach (var connection in connections.Values)
                connection.Close();
            connections.Clear();

            db?.Dispose();
            db = null;
        }

        /// <summary>
        /// Re-reads which debug ports to attach to. Called on _Ready and
        /// whenever the play orchestrator's state changes, so a new session is
        /// picked up without polling for it.
        /// </summary>
        public void RefreshSessionTargets()
        {
            targetPorts.Clear();

            var orchestrator = EditorInterface.Singleton.GetBaseControl().GetNodeOrNull(ORCHESTRATOR_NODE_NAME);
            // Read live rather than through NetRunner.DebugServerEnabled, which caches
            // for the lifetime of a run: in the editor the setting can be toggled at
            // any time and the tab should reflect it immediately.
            if (IsDebugServerEnabled
                && orchestrator is not null && orchestrator.Call("is_running").AsBool())
            {
                foreach (int port in orchestrator.Call("get_debug_ports").AsInt32Array())
                {
                    if (port > 0)
                        targetPorts.Add(port);
                }
            }

            // Drop connections to ports that are no longer part of the session.
            var stale = new List<int>();
            foreach (var port in connections.Keys)
            {
                if (!targetPorts.Contains(port))
                    stale.Add(port);
            }
            foreach (var port in stale)
                DropConnection(port, "session ended");

            UpdatePlaceholder();
        }

        /// <summary>
        /// Whether the debug channel is switched on for this project. Uncached so a
        /// toggle in Project Settings shows up without an editor restart.
        /// </summary>
        private static bool IsDebugServerEnabled =>
            ProjectSettings.GetSetting(NetRunner.DEBUG_SERVER_SETTING, true).AsBool();

        public override void _Process(double delta)
        {
            // Drains regardless of visibility. An attached-but-undrained socket fills
            // until the server's DebugHub write times out and drops the editor client,
            // which is unrecoverable for the session — so "the user switched tabs" must
            // never stop the reader. (This also can't be delegated to a flag set from
            // outside: assembly reloads reset plain fields without re-running _Ready.)

            if (!IsDebugServerEnabled)
            {
                // Nothing is listening, so don't sit in a connect-retry loop. Any
                // connections from before the toggle are dropped by RefreshSessionTargets.
                RefreshSessionTargets();
                return;
            }

            DrainConnections();

            sinceConnectAttempt += delta;
            if (sinceConnectAttempt < CONNECT_RETRY_INTERVAL)
                return;
            sinceConnectAttempt = 0;

            // Re-read the session's ports every tick rather than relying solely
            // on RefreshSessionTargets being called for us: launching triggers a
            // C# build and .NET assembly reload that recreates this node, so the
            // one notification we'd otherwise depend on can be delivered to an
            // instance that no longer exists (or before the instances have been
            // spawned and their ports recorded).
            RefreshSessionTargets();
            ServiceConnectAttempts();
        }

        // ─── Connections ─────────────────────────────────────────────────────

        private void ServiceConnectAttempts()
        {
            foreach (int port in targetPorts)
            {
                if (!connections.TryGetValue(port, out var connection))
                {
                    connection = new ProcessConnection { Port = port };
                    connections[port] = connection;
                }

                if (connection.IsConnected)
                    continue;

                if (connection.ConnectTask is null)
                {
                    // Async so a port with nothing listening can't stall the
                    // editor: a synchronous connect per port per frame is
                    // visible as a stutter even on loopback.
                    connection.Client = new TcpClient();
                    try
                    {
                        connection.ConnectTask = connection.Client.ConnectAsync(IPAddress.Loopback, port);
                    }
                    catch
                    {
                        connection.Close();
                    }
                    continue;
                }

                if (!connection.ConnectTask.IsCompleted)
                    continue;

                if (connection.ConnectTask.IsCompletedSuccessfully && connection.Client is { Connected: true })
                {
                    connection.Client.NoDelay = true;
                    connection.Stream = connection.Client.GetStream();
                    connection.ConnectTask = null;
                    EnsureDatabase();
                }
                else
                {
                    // Not up yet (or refused) — retry on the next tick.
                    connection.Close();
                }
            }
        }

        private void DrainConnections()
        {
            foreach (var connection in connections.Values)
            {
                if (!connection.IsConnected)
                    continue;

                try
                {
                    while (connection.Stream.DataAvailable)
                    {
                        int read = connection.Stream.Read(readBuffer, 0, readBuffer.Length);
                        if (read <= 0)
                        {
                            DropConnection(connection.Port, "closed by peer");
                            break;
                        }
                        connection.Inbox.Write(readBuffer, 0, read);
                    }
                }
                catch (Exception ex)
                {
                    DropConnection(connection.Port, ex.Message);
                    continue;
                }

                if (connection.Inbox.Length > 0)
                    ConsumeFrames(connection);
            }
        }

        private void ConsumeFrames(ProcessConnection connection)
        {
            var data = connection.Inbox.GetBuffer().AsSpan(0, (int)connection.Inbox.Length);
            int offset = 0;

            while (DebugFrame.TryRead(data, offset, out int consumed, out byte type,
                       out var worldId, out var payload))
            {
                HandleFrame(connection, type, worldId, payload);
                offset += consumed;
            }

            // Retain the incomplete tail by sliding it down inside the Inbox's own
            // buffer, rather than copying it out to a fresh array first — that ran on
            // every drain that ended mid-frame, which is most of them.
            int remaining = data.Length - offset;
            if (offset > 0)
            {
                if (remaining > 0)
                {
                    // Array.Copy, whose overlapping-range behavior is documented.
                    var raw = connection.Inbox.GetBuffer();
                    Array.Copy(raw, offset, raw, 0, remaining);
                }
                connection.Inbox.SetLength(remaining);
                connection.Inbox.Position = remaining;
            }
        }

        private void HandleFrame(ProcessConnection connection, byte type,
            ReadOnlySpan<byte> worldIdBytes, ReadOnlySpan<byte> payload)
        {
            // Span ctor, not ToArray(): this runs for every frame on every
            // connection, several times per tick.
            var worldId = new UUID(worldIdBytes);
            string key = worldId.ToString();
            var dataType = (WorldRunner.DebugDataType)type;

            switch (dataType)
            {
                case WorldRunner.DebugDataType.WORLD_ANNOUNCE:
                {
                    // Payload: [isServer:bool][scenePath:string][currentTick:int32].
                    // Only server worlds are listed — a client's world mirrors what
                    // the server already shows, has no tick/payload traffic of its
                    // own, and never even learns its own world id.
                    using var announce = new NetBuffer(payload, usePool: false);
                    if (!NetReader.ReadBool(announce))
                        return;
                    // Idempotent by design: a second client attaching to the
                    // same process re-broadcasts announcements to everyone.
                    EnsurePanel(connection, worldId, key);
                    return;
                }

                case WorldRunner.DebugDataType.WORLD_REMOVED:
                    if (connection.Panels.TryGetValue(key, out var removed))
                        removed.MarkDisconnected();
                    return;

                case WorldRunner.DebugDataType.METRICS:
                {
                    // Handled before the panel lookup: metrics are process-level data
                    // for the Performance tab, not per-panel state. Guarded because a
                    // throwing handler would otherwise escape ConsumeFrames before the
                    // inbox tail-slide, re-parsing (and re-throwing on) the same frame
                    // every editor frame.
                    //
                    // Its own one-shot flag, NOT the shared loggedFrameError: one
                    // unrelated panel-frame error would otherwise permanently silence
                    // every metrics failure after it.
                    try
                    {
                        using var metricsBuffer = new NetBuffer(payload, usePool: false);
                        ResolvePerformanceView()?.OnMetricsReceived(
                            connection.Port, NetReader.ReadString(metricsBuffer));
                    }
                    catch (Exception ex)
                    {
                        if (!loggedMetricsError)
                        {
                            loggedMetricsError = true;
                            GD.PushError($"Nebula: metrics frame failed: {ex}");
                        }
                    }
                    return;
                }
            }

            if (!connection.Panels.TryGetValue(key, out var panel))
                return;

            try
            {
                using var buffer = new NetBuffer(payload, usePool: true);
                panel.HandleFrame(dataType, buffer);
            }
            catch (Exception ex)
            {
                // One malformed or unexpected frame must not tear down the whole
                // read loop and leave every panel frozen.
                if (!loggedFrameError)
                {
                    loggedFrameError = true;
                    GD.PushError($"Nebula: debug frame ({dataType}) failed to decode: {ex}");
                }
            }
        }

        private void EnsurePanel(ProcessConnection connection, UUID worldId, string key)
        {
            if (connection.Panels.ContainsKey(key))
                return;

            EnsureDatabase();

            var panel = WorldDebugScene.Instantiate<WorldDebug>();
            panel.Name = $"world_{connection.Port}_{key}";
            panel.SetAnchorsPreset(LayoutPreset.FullRect);
            panelHost.AddChild(panel);

            // The process port keeps the LiteDB collection unique if two
            // processes ever host the same world id; it isn't shown anywhere.
            panel.Setup(worldId, db, $"p{connection.Port}");
            connection.Panels[key] = panel;

            RebuildWorldSelector(preferredKey: key);
        }

        /// <summary>
        /// Repopulates the world dropdown from every attached process, keeping
        /// the current selection where possible.
        /// </summary>
        private void RebuildWorldSelector(string preferredKey = null)
        {
            if (worldSelector is null)
                return;

            string previous = preferredKey;
            if (previous is null && worldSelector.Selected >= 0 && worldSelector.Selected < selectorEntries.Count)
                previous = selectorEntries[worldSelector.Selected].WorldKey;

            selectorEntries.Clear();
            worldSelector.Clear();

            foreach (var connection in connections.Values)
            {
                foreach (var key in connection.Panels.Keys)
                {
                    selectorEntries.Add((connection.Port, key));
                    worldSelector.AddItem(key);
                }
            }

            int index = 0;
            for (int i = 0; i < selectorEntries.Count; i++)
            {
                if (selectorEntries[i].WorldKey == previous)
                {
                    index = i;
                    break;
                }
            }
            if (selectorEntries.Count > 0)
                worldSelector.Selected = index;

            ShowSelectedWorld();
            UpdatePlaceholder();
        }

        private void OnWorldSelected(long index)
        {
            ShowSelectedWorld();
        }

        /// <summary>
        /// Only the selected world's panel is visible; the rest stay in the tree
        /// so they keep recording their own tick frames in the background.
        /// </summary>
        private void ShowSelectedWorld()
        {
            int selected = worldSelector?.Selected ?? -1;
            (int Port, string WorldKey)? active =
                selected >= 0 && selected < selectorEntries.Count ? selectorEntries[selected] : null;

            foreach (var connection in connections.Values)
            {
                foreach (var (key, panel) in connection.Panels)
                {
                    if (!GodotObject.IsInstanceValid(panel))
                        continue;
                    panel.Visible = active.HasValue
                        && active.Value.Port == connection.Port
                        && active.Value.WorldKey == key;
                }
            }
        }

        private void DropConnection(int port, string reason)
        {
            if (!connections.TryGetValue(port, out var connection))
                return;

            foreach (var panel in connection.Panels.Values)
            {
                if (GodotObject.IsInstanceValid(panel))
                    panel.QueueFree();
            }
            connection.Panels.Clear();
            connection.Close();
            connections.Remove(port);

            GD.Print($"[Nebula] debug channel :{port} detached ({reason})");
            RebuildWorldSelector();
        }

        private void UpdatePlaceholder()
        {
            if (placeholder is null || panelHost is null)
                return;
            bool empty = selectorEntries.Count == 0;
            placeholder.Visible = empty;
            panelHost.Visible = !empty;
            if (worldSelector is not null)
                worldSelector.GetParent<Control>().Visible = !empty;

            // Distinguish "switched off" from "nothing running" — otherwise the tab
            // tells you to press Play, which would never produce anything.
            if (empty)
            {
                placeholder.Text = IsDebugServerEnabled
                    ? "No worlds — press Play in the toolbar."
                    : "Debug Server Disabled";
            }
        }

        // ─── Tick-frame database ─────────────────────────────────────────────

        /// <summary>
        /// Tick frames are persisted so the bar chart can page back through a
        /// session. Written under <c>user://nebula_debug/</c> — this used to
        /// drop a .db file into whatever directory the editor was launched
        /// from.
        /// </summary>
        private void EnsureDatabase()
        {
            if (db is not null)
                return;

            try
            {
                string dirPath = ProjectSettings.GlobalizePath(DEBUG_DB_DIR);
                DirAccess.MakeDirRecursiveAbsolute(dirPath);
                PruneOldDatabases(dirPath);

                string dbFilePath = Path.Combine(dirPath, $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.db");
                // Explicit ConnectionString rather than the string overload: that
                // overload parses its argument as a `key=value;` connection string,
                // and the user data directory contains spaces on macOS
                // ("Application Support"). Each world gets its own collection, so
                // there's no shared index to create here.
                db = new LiteDatabase(new ConnectionString
                {
                    Filename = dbFilePath,
                    Connection = ConnectionType.Direct,
                });
            }
            catch (Exception ex)
            {
                GD.PushError($"Nebula: failed to open debug database: {ex.Message}");
                db = null;
            }
        }

        private static void PruneOldDatabases(string dirPath)
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(dirPath, "debug_*.db"));
                if (files.Count < MAX_RETAINED_DATABASES)
                    return;
                files.Sort(StringComparer.Ordinal); // timestamped names sort chronologically
                for (int i = 0; i <= files.Count - MAX_RETAINED_DATABASES; i++)
                {
                    try { File.Delete(files[i]); } catch { /* in use by another editor */ }
                }
            }
            catch
            {
                // Pruning is best-effort; never block opening a session on it.
            }
        }
    }
}
#endif
