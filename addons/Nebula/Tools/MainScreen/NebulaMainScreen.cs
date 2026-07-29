namespace Nebula.Tools;

#if TOOLS

using Godot;
using Nebula.Internal.Editor;

/// <summary>
/// Main-screen "Nebula" tab: the live network debugger.
///
/// The tab used to host a mock Test &amp; Deploy workbench (play configuration
/// list, build/deploy buttons, server status grid, console). Play configuration
/// moved to the toolbar Play button + dropdown in <see cref="Main"/>, and the
/// tab body is now the debugger that attaches to whatever the Play button
/// launched. This class is just the shell; <see cref="NebulaDebugView"/> owns
/// the connections and the per-world panels.
/// </summary>
[Tool]
public partial class NebulaMainScreen : Control
{
    private NebulaDebugView debugView;

    public override void _Ready()
    {
        Name = "NebulaMainScreen";
        // The editor main screen is a VBoxContainer: children are laid out by
        // size flags, so anchors alone leave the tab at its minimum height.
        SetAnchorsPreset(LayoutPreset.FullRect);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        AddChild(margin);

        debugView = new NebulaDebugView
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        margin.AddChild(debugView);
    }

    /// <summary>
    /// Called by <see cref="Main"/> when the play orchestrator's state changes,
    /// so the debugger picks up (or releases) the session's debug ports without
    /// polling for them.
    /// </summary>
    public void OnSessionStateChanged()
    {
        if (debugView is not null && GodotObject.IsInstanceValid(debugView))
            debugView.RefreshSessionTargets();
    }
}

#endif // TOOLS
