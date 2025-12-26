using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Godot;

namespace Nebula.Testing.Integration;

/// <summary>
/// A composable component for handling stdin commands in integration tests.
/// Add as a child node and connect to the CommandReceived signal.
/// 
/// Built-in commands:
/// - dump_tree: Outputs the entire scene tree to stdout
/// </summary>
public partial class StdinCommandHandler : Node
{
    /// <summary>
    /// Emitted when a command is received from stdin (excluding built-in commands).
    /// The command string is the raw line received.
    /// </summary>
    [Signal]
    public delegate void CommandReceivedEventHandler(string command);

    private readonly ConcurrentQueue<string> _commandQueue = new();
    private Thread _readerThread;
    private volatile bool _running = true;

    public override void _Ready()
    {
        _readerThread = new Thread(ReadStdinLoop)
        {
            IsBackground = true,
            Name = "StdinReader"
        };
        _readerThread.Start();
    }

    public override void _Process(double delta)
    {
        // Process any queued commands on the main thread
        while (_commandQueue.TryDequeue(out var command))
        {   
            // Handle built-in commands
            if (HandleBuiltInCommand(command))
            {
                continue;
            }
            
            EmitSignal(SignalName.CommandReceived, command);
        }
    }

    public override void _ExitTree()
    {
        _running = false;
        // Don't wait on the thread - it's blocking on ReadLine
    }

    private bool HandleBuiltInCommand(string command)
    {
        if (command == "dump_tree")
        {
            DumpSceneTree();
            return true;
        }
        return false;
    }

    private void DumpSceneTree()
    {
        var root = GetTree()?.Root;
        if (root == null)
        {
            GD.Print("[SCENE_TREE_DUMP_START]");
            GD.Print("(no scene tree available)");
            GD.Print("[SCENE_TREE_DUMP_END]");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[SCENE_TREE_DUMP_START]");
        DumpNode(root, sb, 0);
        sb.AppendLine("[SCENE_TREE_DUMP_END]");
        
        GD.Print(sb.ToString());
    }

    private void DumpNode(Node node, StringBuilder sb, int indent)
    {
        var indentStr = new string(' ', indent * 2);
        var typeName = node.GetClass();
        var scriptPath = "";
        
        // Try to get the script name if it has one
        var script = node.GetScript();
        if (script.VariantType != Variant.Type.Nil && script.AsGodotObject() is Script s)
        {
            scriptPath = $" ({s.ResourcePath})";
        }

        sb.AppendLine($"{indentStr}{node.Name} [{typeName}]{scriptPath}");

        foreach (var child in node.GetChildren())
        {
            DumpNode(child, sb, indent + 1);
        }
    }

    private void ReadStdinLoop()
    {
        try
        {
            while (_running)
            {
                var line = Console.In.ReadLine();
                if (line == null)
                {
                    // stdin closed
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _commandQueue.Enqueue(line.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[StdinCommandHandler] Error reading stdin: {ex.Message}");
        }
    }
}
