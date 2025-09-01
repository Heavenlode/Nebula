using System;
using Godot;
using Nebula.Internal.Editor.DTO;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using LiteDB;

namespace Nebula.Internal.Editor
{
    [Tool]
    public partial class ServerDebugClient : Window
    {
        private ENetConnection debugConnection;
        private PackedScene debugPanelScene = GD.Load<PackedScene>("res://addons/Nebula/Tools/Debugger/world_debug.tscn");
        private LiteDatabase db;

        public void _OnCloseRequested()
        {
            Hide();
        }

        public override void _Ready()
        {
            GetTree().MultiplayerPoll = false;
        }

        public override void _ExitTree()
        {
            debugConnection.Destroy();
            if (db != null)
            {
                db.Dispose();
            }
        }

        private void OnDebugConnect()
        {
            Title = "Server Debug Client (Online)";
            db?.Dispose();
            Debugger.EditorInstance.Log("Connected to debug server", Debugger.DebugLevel.VERBOSE);
            foreach (var child in GetNode("Container/TabContainer").GetChildren())
            {
                child.QueueFree();
            }
            if (!Visible)
            {
                Show();
            }
            try
            {
                string dbFilePath = $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.db";
                db = new LiteDatabase(dbFilePath);
                var tickFrames = db.GetCollection<TickFrame>("tick_frames");
                tickFrames.EnsureIndex(x => x.Id);
            }
            catch (Exception e)
            {
                GD.PrintErr($"Error creating database: {e}");
                return;
            }
        }

        public override void _Process(double delta)
        {
            return;
            while (true)
            {
                Godot.Collections.Array enetEvent;
                try
                {
                    enetEvent = debugConnection.Service();
                }
                catch
                {
                    try
                    {
                        if (debugConnection != null)
                        {
                            debugConnection.Destroy();
                            debugConnection = null;
                        }
                        debugConnection = new ENetConnection();
                        debugConnection.CreateHost();
                        debugConnection.Compress(ENetConnection.CompressionMode.RangeCoder);
                        debugConnection.ConnectToHost("127.0.0.1", NetRunner.DebugPort);
                    }
                    catch (Exception err)
                    {
                        Debugger.EditorInstance.Log($"Error creating debug connection: {err}", Debugger.DebugLevel.VERBOSE);
                        return;
                    }
                    return;
                }
                var eventType = enetEvent[0].As<ENetConnection.EventType>();
                if (eventType == ENetConnection.EventType.None || eventType == (ENetConnection.EventType)(-1))
                {
                    break;
                }
                var packetPeer = enetEvent[1].As<ENetPacketPeer>();
                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                        packetPeer.SetTimeout(1, 1000, 1000);
                        OnDebugConnect();
                        break;

                    case ENetConnection.EventType.Disconnect:
                        Title = "Server Debug Client (Offline)";
                        foreach (var child in GetNode("Container/TabContainer").GetChildren())
                        {
                            child.Set("disconnected", true);
                        }
                        debugConnection.Destroy();
                        debugConnection = null;
                        return;

                    case ENetConnection.EventType.Receive:
                        var data = packetPeer.GetPacket();
                        var packet = new HLBuffer(data);
                        var worldId = new UUID(HLBytes.UnpackByteArray(packet, 16));
                        var port = HLBytes.UnpackInt32(packet);
                        var debugPanel = debugPanelScene.Instantiate<WorldDebug>();
                        GetNode("Container/TabContainer").AddChild(debugPanel);
                        debugPanel.Setup(worldId, port, db);
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (db != null)
                {
                    db.Dispose();
                    db = null;
                }
                if (debugConnection != null)
                {
                    debugConnection.Destroy();
                    debugConnection = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
