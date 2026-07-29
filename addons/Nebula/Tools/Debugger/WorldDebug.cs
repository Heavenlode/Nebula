using System;
using System.Linq;
using Godot;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using LiteDB;

namespace Nebula.Internal.Editor
{
    /// <summary>
    /// One world's debugger panel: tick bar chart, per-peer payload sizes,
    /// logs, net-function calls, and the networked-node tree + inspector.
    ///
    /// <para>This used to own an ENet connection to a per-world debug port.
    /// The debug channel is now one TCP socket per process, owned by
    /// <see cref="NebulaDebugView"/>, which demultiplexes by world and hands
    /// frames here via <see cref="HandleFrame"/>.</para>
    /// </summary>
    [Tool]
    public partial class WorldDebug : Panel
    {
        [Export]
        public RichTextLabel worldIdLabel;
        private LiteDatabase db;
        private ILiteCollection<TickFrame> frames;
        private TickFrame incomingTickFrame;
        public int SelectedTickFrameId = -1;
        private int greatestStateSize = 0;
        private UUID worldId;
        public bool disconnected = false;
        /// <summary>
        /// Read from GDScript via get("IsLive"), including while the panel is
        /// being torn down — hence GetNodeOrNull: the checkbox is already gone
        /// by then and a hard GetNode throws across the C#/GDScript boundary.
        /// </summary>
        public bool IsLive => GetNodeOrNull<CheckBox>("%LiveCheckbox")?.ButtonPressed ?? false;

        /// <summary>
        /// Last world state received. Carried into each new tick frame so the
        /// node tree's frame-N/N-1 diff stays meaningful when exports are
        /// throttled — otherwise the previous frame's state is empty and every
        /// node flashes as "changed" on every export.
        /// </summary>
        private string lastWorldStateJson = "";

        /// <summary>
        /// Set by any handler that mutates <see cref="incomingTickFrame"/>; the
        /// row is written once when the next tick arrives rather than on every
        /// log line, call and payload (which was hundreds of synchronous disk
        /// writes per second).
        /// </summary>
        private bool tickFrameDirty;

        [Signal]
        public delegate void TickFrameReceivedEventHandler(int id);

        [Signal]
        public delegate void TickFrameUpdatedEventHandler(int id);

        [Signal]
        public delegate void TickFrameSelectedEventHandler(Control tickFrame);

        [Signal]
        public delegate void LogEventHandler(int frameId, string timestamp, string level, string message);

        [Signal]
        public delegate void NetFunctionCalledEventHandler(int frameId, string functionIndex);

        [Signal]
        public delegate void PeersUpdatedEventHandler(Godot.Collections.Array peers);

        public void _OnTickFrameSelected(Control tickFrame)
        {
            SelectedTickFrameId = tickFrame.Get("tick_frame_id").AsInt32();
        }

        /// <param name="instanceKey">
        /// Identifies the owning process. Only used to keep this panel's LiteDB
        /// collection distinct from another process hosting the same world id;
        /// it is never displayed.
        /// </param>
        public void Setup(UUID worldId, LiteDatabase db, string instanceKey)
        {
            this.db = db;
            this.worldId = worldId;
            if (worldIdLabel is not null)
                worldIdLabel.Text = worldId.ToString();

            if (db is null)
            {
                GD.PushError("Nebula: debugger has no database; this world's panel will stay empty.");
                return;
            }

            // Each panel gets its OWN collection. Tick frames are keyed by tick
            // number, so a shared collection makes the server and client panels
            // collide on _id the moment their tick counters overlap — the insert
            // throws and the panel silently stops updating. It also kept
            // GetLogs() returning every world's logs at once.
            frames = db.GetCollection<TickFrame>($"frames_{Sanitize(instanceKey)}_{Sanitize(worldId.ToString())}");
            frames.EnsureIndex(x => x.Id);
        }

        private static string Sanitize(string value)
        {
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        /// <summary>Marks the panel dead when its world (or process) goes away.</summary>
        public void MarkDisconnected()
        {
            disconnected = true;
            FlushTickFrame();
        }

        public override void _ExitTree()
        {
            // Persist the in-flight frame, but do NOT notify: the views are
            // being torn down with us, and driving them here had them querying
            // nodes that no longer exist.
            FlushTickFrame(notify: false);
            incomingTickFrame = null;
        }

        /// <summary>
        /// Handles one decoded frame for this world. The payload buffer is
        /// positioned at the start of the frame body (the type byte and worldId
        /// have already been consumed by the demultiplexer).
        /// </summary>
        public void HandleFrame(WorldRunner.DebugDataType dataType, NetBuffer data)
        {
            if (disconnected || frames is null)
                return;

            switch (dataType)
            {
                case WorldRunner.DebugDataType.TICK:
                    {
                        // The previous frame's accumulated state is written once, here.
                        FlushTickFrame();

                        greatestStateSize = 0;
                        var milliseconds = NetReader.ReadInt64(data);
                        var tickId = NetReader.ReadInt32(data);
                        var datetime = new DateTime(milliseconds * TimeSpan.TicksPerMillisecond);
                        incomingTickFrame = new TickFrame
                        {
                            Id = tickId,
                            Timestamp = datetime,
                            WorldId = worldId,
                            Logs = [],
                            NetFunctionCalls = [],
                            WorldStateJson = lastWorldStateJson,
                        };
                        // Upsert, not Insert: tick ids repeat if a panel is
                        // recreated for the same world (reconnect, or the editor
                        // rebuilding its UI after an assembly reload), and a
                        // duplicate-key throw here would stop the panel dead.
                        frames.Upsert(incomingTickFrame);
                        EmitSignal(SignalName.TickFrameReceived, incomingTickFrame.Id);
                    }
                    break;

                case WorldRunner.DebugDataType.CALLS:
                    {
                        // Guarded like every other case below: a view that
                        // attaches mid-tick receives these before its first
                        // TICK, and dereferencing the frame then throws.
                        if (incomingTickFrame == null)
                            break;
                        var functionName = NetReader.ReadString(data);
                        var args = new BsonArray();
                        var argsLength = NetReader.ReadByte(data);
                        for (int i = 0; i < argsLength; i++)
                        {
                            var val = NetReader.ReadWithType(data, out var serialType);
                            if (val != null)
                            {
                                args.Add(new BsonValue(val));
                            }
                        }
                        incomingTickFrame.NetFunctionCalls.Add(new BsonDocument
                        {
                            ["name"] = functionName,
                            ["args"] = args,
                        });
                        tickFrameDirty = true;
                        EmitSignal(SignalName.NetFunctionCalled, incomingTickFrame.Id, functionName);
                    }
                    break;

                case WorldRunner.DebugDataType.PAYLOADS:
                    {
                        if (incomingTickFrame == null)
                            break;
                        var peerId = new UUID(NetReader.ReadBytes(data, 16));
                        // Size actually transmitted to this peer, as reported by the
                        // server. The state bytes that follow are the pre-pack payload
                        // kept for inspection and are a different (larger) length.
                        var transmittedSize = NetReader.ReadInt32(data);
                        var payload = NetReader.ReadRemainingBytes(data);
                        greatestStateSize = Math.Max(greatestStateSize, transmittedSize);
                        incomingTickFrame.PeerPayloads[peerId.ToString()] = payload;
                        incomingTickFrame.GreatestSize = greatestStateSize;
                        tickFrameDirty = true;
                    }
                    break;

                case WorldRunner.DebugDataType.LOGS:
                    {
                        if (incomingTickFrame == null)
                            break;
                        var level = (Debugger.DebugLevel)NetReader.ReadByte(data);
                        var message = NetReader.ReadString(data);
                        incomingTickFrame.Logs.Add(new BsonDocument
                        {
                            ["level"] = (int)level,
                            ["message"] = message,
                        });
                        tickFrameDirty = true;
                        EmitSignal(SignalName.Log, incomingTickFrame.Id, incomingTickFrame.Timestamp.ToString(), level.ToString(), message);
                    }
                    break;

                case WorldRunner.DebugDataType.EXPORT:
                    {
                        if (incomingTickFrame == null)
                            break;
                        lastWorldStateJson = NetReader.ReadString(data);
                        incomingTickFrame.WorldStateJson = lastWorldStateJson;
                        tickFrameDirty = true;
                    }
                    break;

                case WorldRunner.DebugDataType.PEERS:
                    {
                        // Live-only: a "what is happening right now" view, not
                        // part of the per-tick history.
                        var peers = new Godot.Collections.Array();
                        var count = NetReader.ReadByte(data);
                        for (int i = 0; i < count; i++)
                        {
                            var peerId = new UUID(NetReader.ReadBytes(data, 16));
                            var tick = NetReader.ReadInt32(data);
                            var status = NetReader.ReadByte(data);
                            var ownedNodes = NetReader.ReadUInt16(data);
                            peers.Add(new Godot.Collections.Dictionary
                            {
                                ["id"] = peerId.ToString(),
                                ["tick"] = tick,
                                ["status"] = ((WorldRunner.PeerSyncStatus)status).ToString(),
                                ["owned_nodes"] = ownedNodes,
                            });
                        }
                        EmitSignal(SignalName.PeersUpdated, peers);
                    }
                    break;

                case WorldRunner.DebugDataType.DEBUG_EVENT:
                    {
                        var category = NetReader.ReadString(data);
                        var message = NetReader.ReadString(data);
                        if (incomingTickFrame == null)
                            break;
                        incomingTickFrame.Logs.Add(new BsonDocument
                        {
                            ["level"] = (int)Debugger.DebugLevel.INFO,
                            ["message"] = $"[{category}] {message}",
                        });
                        tickFrameDirty = true;
                        EmitSignal(SignalName.Log, incomingTickFrame.Id, incomingTickFrame.Timestamp.ToString(),
                            Debugger.DebugLevel.INFO.ToString(), $"[{category}] {message}");
                    }
                    break;
            }
        }

        private void FlushTickFrame(bool notify = true)
        {
            if (incomingTickFrame == null || !tickFrameDirty || frames is null)
                return;
            try
            {
                frames.Update(incomingTickFrame);
            }
            catch (LiteDB.LiteException e) when (e.ErrorCode == LiteDB.LiteException.ENGINE_DISPOSED)
            {
                // The database is already disposed
            }
            tickFrameDirty = false;

            // Coalesced to once per tick. This used to fire on every payload,
            // log line and export — 4+ times per tick with 2 peers — and each
            // one drove a full re-read, JSON re-parse and node-tree reconcile.
            if (notify && IsInsideTree() && !IsQueuedForDeletion())
                EmitSignal(SignalName.TickFrameUpdated, incomingTickFrame.Id);
        }

        public Godot.Collections.Dictionary GetFrame(int id)
        {
            if (frames is null)
                return [];

            TickFrame tickFrameData;
            tickFrameData = frames.FindById(id);
            if (tickFrameData == null)
            {
                return [];
            }

            return MarshallTickFrame(tickFrameData);
        }

        /// <summary>
        /// Same as <see cref="GetFrame"/> minus the world state. The bar charts
        /// only need sizes and counts, and parsing the full world-state JSON for
        /// them was the single most expensive thing the panel did.
        /// </summary>
        public Godot.Collections.Dictionary GetFrameSummary(int id)
        {
            if (frames is null)
                return [];

            var tickFrameData = frames.FindById(id);
            if (tickFrameData == null)
            {
                return [];
            }

            return MarshallTickFrame(tickFrameData, includeWorldState: false);
        }

        public Godot.Collections.Array GetFrames(int[] ids, bool descending = true)
        {
            var result = new Godot.Collections.Array();
            if (frames is null)
                return result;

            var bsonIds = new BsonArray(ids.Select(id => new BsonValue(id)));
            var data = frames.Find(Query.In("_id", bsonIds));
            if (descending)
            {
                data = data.OrderByDescending(t => t.Id);
            }
            else
            {
                data = data.OrderBy(t => t.Id);
            }
            foreach (var tickFrame in data)
            {
                // Charts only: world state omitted deliberately (see GetFrameSummary).
                result.Add(MarshallTickFrame(tickFrame, includeWorldState: false));
            }
            return result;
        }

        public Godot.Collections.Array GetLogs()
        {
            var result = new Godot.Collections.Array();
            if (frames is null)
                return result;

            var data = frames.FindAll();
            foreach (var tickFrame in data)
            {
                result.AddRange(MarshallLogs(tickFrame));
            }
            return result;
        }

        private Godot.Collections.Array MarshallLogs(TickFrame tickFrameData)
        {
            var logsList = new Godot.Collections.Array();
            foreach (var log in tickFrameData.Logs)
            {
                var logDict = new Godot.Collections.Dictionary();
                logDict["id"] = tickFrameData.Id;
                logDict["level"] = ((Debugger.DebugLevel)log["level"].AsInt32).ToString();
                logDict["message"] = log["message"].AsString;
                logDict["timestamp"] = tickFrameData.Timestamp.ToString();
                logsList.Add(logDict);
            }
            return logsList;
        }

        private Godot.Collections.Dictionary MarshallTickFrame(TickFrame tickFrameData, bool includeWorldState = true)
        {
            var callsList = new Godot.Collections.Array();
            foreach (var call in tickFrameData.NetFunctionCalls)
            {
                var callDict = new Godot.Collections.Dictionary();
                callDict["name"] = call["name"].AsString;
                callsList.Add(callDict);
            }

            var worldState = new Godot.Collections.Dictionary();
            if (includeWorldState && !string.IsNullOrEmpty(tickFrameData.WorldStateJson))
            {
                var parsed = Json.ParseString(tickFrameData.WorldStateJson);
                if (parsed.VariantType == Variant.Type.Dictionary)
                    worldState = parsed.AsGodotDictionary();
            }

            var result = new Godot.Collections.Dictionary
            {
                ["details"] = new Godot.Collections.Dictionary
                {
                    ["Tick"] = new Godot.Collections.Dictionary
                    {
                        ["ID"] = tickFrameData.Id,
                        ["Timestamp"] = tickFrameData.Timestamp.ToString(),
                        ["Greatest Size"] = tickFrameData.GreatestSize,
                    },
                },
                ["logs"] = MarshallLogs(tickFrameData),
                ["network_function_calls"] = callsList,
                ["world_state"] = worldState,
            };

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Owned by NebulaDebugView; just drop the references.
                frames = null;
                db = null;
            }
            base.Dispose(disposing);
        }
    }
}
