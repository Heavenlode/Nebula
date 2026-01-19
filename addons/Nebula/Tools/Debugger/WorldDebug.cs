using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using LiteDB;

namespace Nebula.Internal.Editor
{
    [Tool]
    public partial class WorldDebug : Panel
    {
        [Export]
        public RichTextLabel worldIdLabel;
        private LiteDatabase db;
        private ENetConnection debugConnection;
        private int port;
        private TickFrame incomingTickFrame;
        public int SelectedTickFrameId = -1;
        private int greatestStateSize = 0;
        private UUID worldId;
        public bool disconnected = false;
        public bool IsLive => GetNode<CheckBox>("%LiveCheckbox").ButtonPressed;

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

        public void _OnTickFrameSelected(Control tickFrame)
        {
            SelectedTickFrameId = tickFrame.Get("tick_frame_id").AsInt32();
        }

        public void Setup(UUID worldId, int port, LiteDatabase db)
        {
            this.db = db;
            this.worldId = worldId;
            var uuidSegments = worldId.ToString().Split("-");
            Name = uuidSegments[^1] ?? "World";
            worldIdLabel.Text = worldId.ToString();
            debugConnection = new ENetConnection();
            debugConnection.CreateHost();
            debugConnection.Compress(ENetConnection.CompressionMode.RangeCoder);
            debugConnection.ConnectToHost("127.0.0.1", port);
            this.port = port;
        }

        public override void _ExitTree()
        {
            if (incomingTickFrame != null)
            {
                try
                {
                    db.GetCollection<TickFrame>("tick_frames").Insert(incomingTickFrame);
                }
                catch (LiteDB.LiteException e)
                {
                    if (e.ErrorCode == LiteDB.LiteException.ENGINE_DISPOSED)
                    {
                        // The database is already disposed
                    }
                    else
                    {
                        throw e;
                    }
                }
                incomingTickFrame = null;
            }
        }

        public override void _Process(double delta)
        {
            if (debugConnection == null || disconnected)
            {
                return;
            }
            while (true)
            {
                var enetEvent = debugConnection.Service();
                var eventType = enetEvent[0].As<ENetConnection.EventType>();
                if (eventType == ENetConnection.EventType.None || eventType == (ENetConnection.EventType)(-1))
                {
                    break;
                }

                var packetPeer = enetEvent[1].As<ENetPacketPeer>();
                switch (eventType)
                {
                    case ENetConnection.EventType.Receive:
                    {
                        using var data = new NetBuffer(packetPeer.GetPacket());
                        var debugDataType = (WorldRunner.DebugDataType)NetReader.ReadByte(data);
                        switch (debugDataType)
                        {
                            case WorldRunner.DebugDataType.TICK:
                                {
                                    if (incomingTickFrame != null)
                                    {
                                        incomingTickFrame = null;
                                    }
                                    greatestStateSize = 0;
                                    var milliseconds = NetReader.ReadInt64(data);
                                    var tickId = NetReader.ReadInt32(data);
                                    var datetime = new DateTime(milliseconds * TimeSpan.TicksPerMillisecond);
                                    incomingTickFrame = new TickFrame { Id = tickId, Timestamp = datetime, WorldId = worldId, Logs = [], NetFunctionCalls = [] };
                                    db.GetCollection<TickFrame>("tick_frames").Insert(incomingTickFrame);
                                    EmitSignal(SignalName.TickFrameReceived, incomingTickFrame.Id);
                                }
                                break;
                            case WorldRunner.DebugDataType.CALLS:
                                {
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
                                    db.GetCollection<TickFrame>("tick_frames").Update(incomingTickFrame);
                                    EmitSignal(SignalName.NetFunctionCalled, incomingTickFrame.Id, functionName);
                                }
                                break;
                            case WorldRunner.DebugDataType.PAYLOADS:
                                {
                                    if (incomingTickFrame == null)
                                    {
                                        break;
                                    }
                                    var peerId = new UUID(NetReader.ReadBytes(data, 16));
                                    var payload = NetReader.ReadRemainingBytes(data);
                                    greatestStateSize = Math.Max(greatestStateSize, payload.Length);
                                    incomingTickFrame.PeerPayloads[peerId.ToString()] = payload;
                                    incomingTickFrame.GreatestSize = greatestStateSize;
                                    db.GetCollection<TickFrame>("tick_frames").Update(incomingTickFrame);
                                    EmitSignal(SignalName.TickFrameUpdated, incomingTickFrame.Id);
                                }
                                break;
                            case WorldRunner.DebugDataType.LOGS:
                                {
                                    var level = (Debugger.DebugLevel)NetReader.ReadByte(data);
                                    var message = NetReader.ReadString(data);
                                    incomingTickFrame.Logs.Add(new BsonDocument
                                    {
                                        ["level"] = (int)level,
                                        ["message"] = message,
                                    });
                                    db.GetCollection<TickFrame>("tick_frames").Update(incomingTickFrame);
                                    EmitSignal(SignalName.Log, incomingTickFrame.Id, incomingTickFrame.Timestamp.ToString(), level.ToString(), message);
                                    EmitSignal(SignalName.TickFrameUpdated, incomingTickFrame.Id);
                                }
                                break;
                            case WorldRunner.DebugDataType.EXPORT:
                                {
                                    var fullGameState = NetReader.ReadRemainingBytes(data);
                                    incomingTickFrame.WorldState = BsonSerializer.Deserialize(fullGameState);
                                    db.GetCollection<TickFrame>("tick_frames").Update(incomingTickFrame);
                                    EmitSignal(SignalName.TickFrameUpdated, incomingTickFrame.Id);
                                }
                                break;
                        }
                        break;
                    }
                }
            }
        }

        public Godot.Collections.Dictionary GetFrame(int id)
        {
            TickFrame tickFrameData;
            tickFrameData = db.GetCollection<TickFrame>("tick_frames").FindById(id);
            if (tickFrameData == null)
            {
                return [];
            }

            return MarshallTickFrame(tickFrameData);
        }

        public Godot.Collections.Array GetFrames(int[] ids, bool descending = true)
        {
            var result = new Godot.Collections.Array();
            var bsonIds = new BsonArray(ids.Select(id => new BsonValue(id)));
            var data = db.GetCollection<TickFrame>("tick_frames").Find(Query.In("_id", bsonIds));
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
                result.Add(MarshallTickFrame(tickFrame));
            }
            return result;
        }

        public Godot.Collections.Array GetLogs()
        {
            var result = new Godot.Collections.Array();
            var data = db.GetCollection<TickFrame>("tick_frames").FindAll();
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

        private Godot.Collections.Dictionary MarshallTickFrame(TickFrame tickFrameData)
        {
            var callsList = new Godot.Collections.Array();
            foreach (var call in tickFrameData.NetFunctionCalls)
            {
                var callDict = new Godot.Collections.Dictionary();
                // callDict["id"] = call["id"].AsInt32;
                callDict["name"] = call["name"].AsString;
                // callDict["args"] = call["args"].Asdi
                callsList.Add(callDict);
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
                ["world_state"] = Json.ParseString(tickFrameData.WorldState.ToString()).AsGodotDictionary(),
            };

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db = null;
            }
            base.Dispose(disposing);
        }
    }
}
