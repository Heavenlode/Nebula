namespace Nebula.Tools;

using Godot;

/// <summary>
/// Root of the dummy scene the Nebula plugin plays via PlayCustomScene to force
/// the editor's debug server to start listening, so self-spawned play instances
/// can attach as debugger sessions. Does nothing but minimize its window and
/// idle — it must stay alive as long as the attached sessions are wanted,
/// because the editor closes the debug server when this session ends.
/// </summary>
public partial class NebulaDebugSession : Node
{
    public override void _Ready()
    {
        GD.Print("NEBULA_DEBUG_SESSION: holding the editor debug server open.");
        var window = GetWindow();
        if (window is not null)
        {
            window.Title = "Nebula Debug Session";
            window.Mode = Window.ModeEnum.Minimized;
        }
    }
}
