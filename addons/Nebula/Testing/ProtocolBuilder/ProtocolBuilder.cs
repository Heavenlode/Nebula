using Godot;
using Nebula.Serialization;

namespace Nebula.Testing;

/// <summary>
/// A headless scene that builds the protocol registry and exits.
/// Used by the test fixture to ensure the protocol is built before tests run.
/// </summary>
public partial class ProtocolBuilder : Node
{
    public override void _Ready()
    {
        GD.Print("[ProtocolBuilder] Starting protocol build...");
        
        var builder = new ProtocolRegistryBuilder();
        AddChild(builder);
        
        var success = builder.Build();
        
        if (success)
        {
            GD.Print("[PROTOCOL_BUILD_SUCCESS]");
        }
        else
        {
            GD.PrintErr("[PROTOCOL_BUILD_FAILED]");
        }
        
        GetTree().Quit(success ? 0 : 1);
    }
}




