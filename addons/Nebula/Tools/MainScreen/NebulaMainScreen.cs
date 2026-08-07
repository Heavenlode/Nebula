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
///
/// <para>The body is a Debugger/Performance tab pair. The Performance tab is
/// always present; whether it has data depends on the servers, which only
/// collect and report metrics when <see cref="Diagnostics.ServerMetrics.EnableEnvVar"/>
/// is set for them (process environment or .env.server) — a production instance
/// spends nothing on metrics unless asked.</para>
/// </summary>
[Tool]
public partial class NebulaMainScreen : Control
{
    private NebulaDebugView debugView;
    private NebulaPerformanceView performanceView;

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

        var tabs = new TabContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        margin.AddChild(tabs);

        // Child node names become tab titles.
        debugView.Name = "Debugger";
        tabs.AddChild(debugView);

        performanceView = new NebulaPerformanceView();
        tabs.AddChild(performanceView);

        // Deliberately NO wiring between the two here. Anything established in _Ready
        // (an event subscription, an injected reference) is lost on the next .NET
        // assembly reload, which recreates the managed instances without re-running
        // _Ready — the tabs then look correct while quietly delivering nothing. The
        // debug view resolves this Performance tab from the tree per frame instead;
        // see NebulaDebugView.ResolvePerformanceView.
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
