namespace Nebula.Tools;

#if TOOLS

using Godot;
using System.Collections.Generic;

/// <summary>
/// "Manage Configurations…" modal: the list of Nebula play targets with add /
/// edit / delete. Replaces the main-screen tab's left-hand config list, which
/// was removed when the tab became the debugger.
///
/// Parented to the editor base control rather than the tab, so config
/// management works whether or not the Nebula tab has ever been opened.
///
/// All signal connections here are name-bound Callables — never `+=` /
/// Callable.From. Delegate-backed connections have repeatedly produced dead
/// 'ManagedCallableMiddleman' handles (inert buttons) and assembly-reload pins
/// in this plugin; name-bound callables have no managed state to die.
/// </summary>
[Tool]
public partial class NebulaConfigDialog : AcceptDialog
{
    /// <summary>Emitted after any add/edit/delete is persisted.</summary>
    [Signal]
    public delegate void ConfigurationsChangedEventHandler();

    private readonly List<NebulaPlayConfiguration> configurations = new();

    private ItemList list;
    private Button editButton;
    private Button deleteButton;

    // Add/Edit form
    private ConfirmationDialog formDialog;
    private SpinBox clientCountSpin;
    private SpinBox botCountSpin;
    private OptionButton botBehaviorPicker;
    private CheckBox botsHeadlessCheck;
    private SpinBox simLatencySpin;
    private SpinBox simJitterSpin;
    private SpinBox simLossSpin;
    private CheckBox impairFirstOnlyCheck;
    private SpinBox simBurstLossSpin;
    private SpinBox simBurstEverySpin;
    private SpinBox simBurstMsSpin;
    /// <summary>
    /// Behavior type names in <see cref="botBehaviorPicker"/> order, index 0 being "(none)".
    /// Kept alongside the control because OptionButton only carries the display text.
    /// </summary>
    private readonly List<string> botBehaviorNames = new();
    /// <summary>Index being edited, or -1 when the form is adding a new entry.</summary>
    private int editingIndex = -1;

    public override void _Ready()
    {
        Name = "NebulaConfigDialog";
        Title = "Nebula Play Configurations";
        OkButtonText = "Close";
        MinSize = new Vector2I(480, 320);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        AddChild(vbox);

        list = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
            AllowReselect = true,
        };
        list.Connect(ItemList.SignalName.ItemSelected, new Callable(this, MethodName.OnListSelectionChanged));
        list.Connect(ItemList.SignalName.ItemActivated, new Callable(this, MethodName.OnListItemActivated));
        vbox.AddChild(list);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(buttons);

        var addButton = new Button { Text = "Add" };
        addButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddPressed));
        buttons.AddChild(addButton);

        editButton = new Button { Text = "Edit", Disabled = true };
        editButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnEditPressed));
        buttons.AddChild(editButton);

        deleteButton = new Button { Text = "Delete", Disabled = true };
        deleteButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnDeletePressed));
        buttons.AddChild(deleteButton);

    }

    /// <summary>Reloads from disk and shows the dialog.</summary>
    public void Open()
    {
        Refresh();
        PopupCentered(new Vector2I(520, 380));
    }

    private void Refresh()
    {
        configurations.Clear();
        configurations.AddRange(NebulaPlayConfigStore.Load());

        int previous = list.IsAnythingSelected() ? list.GetSelectedItems()[0] : -1;

        list.Clear();
        foreach (var config in configurations)
        {
            string detail = $"    headless server + {config.ClientCount} client instance(s)";
            if (config.BotCount > 0)
                detail += $" + {config.BotCount} bot(s) running {BotBehaviorLabel(config.BotBehavior)}";
            list.AddItem($"{config.Name}\n{detail}");
        }

        if (previous >= 0 && previous < configurations.Count)
            list.Select(previous);
        OnListSelectionChanged(0);
    }

    private void OnListSelectionChanged(long _index)
    {
        bool hasSelection = list.IsAnythingSelected();
        editButton.Disabled = !hasSelection;
        // Refuse to delete the last entry: Load() would just seed the default
        // back in, which reads as the delete having silently failed.
        deleteButton.Disabled = !hasSelection || configurations.Count <= 1;
    }

    private void OnListItemActivated(long index)
    {
        OpenForm((int)index);
    }

    private void OnAddPressed()
    {
        OpenForm(-1);
    }

    private void OnEditPressed()
    {
        if (!list.IsAnythingSelected())
            return;
        OpenForm(list.GetSelectedItems()[0]);
    }

    private void OnDeletePressed()
    {
        if (!list.IsAnythingSelected())
            return;
        int index = list.GetSelectedItems()[0];
        if (index < 0 || index >= configurations.Count)
            return;

        bool wasSelectedTarget = configurations[index].Name == NebulaPlayConfigStore.GetSelectedName();
        configurations.RemoveAt(index);
        NebulaPlayConfigStore.Save(configurations);
        // Deleting the active play target would leave the toolbar pointing at
        // nothing; fall the selection back to the first remaining entry.
        if (wasSelectedTarget && configurations.Count > 0)
            NebulaPlayConfigStore.SetSelectedName(configurations[0].Name);

        Refresh();
        EmitSignal(SignalName.ConfigurationsChanged);
    }

    // ─── Add / Edit form ─────────────────────────────────────────────────────

    private void OpenForm(int index)
    {
        EnsureFormDialog();
        editingIndex = index;

        // Rebuilt on every open: the game assembly reloads while the editor is running, so a
        // behavior added since the dialog was last shown has to appear without an editor restart.
        RefreshBotBehaviorPicker();

        if (index >= 0 && index < configurations.Count)
        {
            var config = configurations[index];
            formDialog.Title = "Edit Configuration";
            clientCountSpin.Value = config.ClientCount;
            botCountSpin.Value = config.BotCount;
            botsHeadlessCheck.ButtonPressed = config.BotsHeadless;
            simLatencySpin.Value = config.SimLatencyMs;
            simJitterSpin.Value = config.SimJitterMs;
            simLossSpin.Value = config.SimLossPct;
            simBurstLossSpin.Value = config.SimBurstLossPct;
            simBurstEverySpin.Value = config.SimBurstEverySec;
            simBurstMsSpin.Value = config.SimBurstMs;
            impairFirstOnlyCheck.ButtonPressed = config.ImpairFirstClientOnly;
            SelectBotBehavior(config.BotBehavior);
        }
        else
        {
            formDialog.Title = "Add Configuration";
            clientCountSpin.Value = 1;
            botCountSpin.Value = 0;
            botsHeadlessCheck.ButtonPressed = true;
            botBehaviorPicker.Selected = 0;

            // Reset these too. The form's controls are reused across opens, so anything not
            // explicitly cleared here is inherited from whatever was last EDITED -- which for
            // impairment means a new configuration silently arrives pre-degraded.
            simLatencySpin.Value = 0;
            simJitterSpin.Value = 0;
            simLossSpin.Value = 0;
            simBurstLossSpin.Value = 0;
            simBurstEverySpin.Value = 10;
            simBurstMsSpin.Value = 0;
            impairFirstOnlyCheck.ButtonPressed = true;
        }

        formDialog.PopupCentered(new Vector2I(420, 0));
        clientCountSpin.GrabFocus();
    }

    /// <summary>
    /// Fills the picker from the BotBehavior subclasses that actually exist, so a configuration
    /// cannot name one that was never written. A configured behavior that has since been removed
    /// or renamed is re-added as a "(missing)" entry rather than silently reset to none.
    /// </summary>
    private void RefreshBotBehaviorPicker()
    {
        botBehaviorPicker.Clear();
        botBehaviorNames.Clear();

        botBehaviorPicker.AddItem("(none)");
        botBehaviorNames.Add("");

        foreach (var type in Nebula.Bots.BotRunner.DiscoverBehaviorTypes())
        {
            botBehaviorPicker.AddItem(type.Name);
            botBehaviorNames.Add(type.Name);
        }
    }

    private void SelectBotBehavior(string behaviorName)
    {
        if (string.IsNullOrEmpty(behaviorName))
        {
            botBehaviorPicker.Selected = 0;
            return;
        }

        int index = botBehaviorNames.IndexOf(behaviorName);
        if (index < 0)
        {
            botBehaviorPicker.AddItem($"{behaviorName}  (missing)");
            botBehaviorNames.Add(behaviorName);
            index = botBehaviorNames.Count - 1;
        }
        botBehaviorPicker.Selected = index;
    }

    private static string BotBehaviorLabel(string behaviorName) =>
        string.IsNullOrEmpty(behaviorName) ? "no behavior" : behaviorName;

    private void EnsureFormDialog()
    {
        if (formDialog is not null)
            return;

        formDialog = new ConfirmationDialog
        {
            Title = "Add Configuration",
            OkButtonText = "Save",
        };

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 8);
        formDialog.AddChild(grid);

        grid.AddChild(new Label { Text = "Client count" });
        clientCountSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 8,
            Value = 1,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Client instances launched alongside the headless server. 0 runs the server on its own.",
        };
        grid.AddChild(clientCountSpin);

        grid.AddChild(new Label { Text = "Bot count" });
        botCountSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 64,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Scripted bot instances launched alongside the clients. Each is a full "
                + "client process driven by a BotBehavior instead of a keyboard.",
        };
        grid.AddChild(botCountSpin);

        grid.AddChild(new Label { Text = "Bot behavior" });
        botBehaviorPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(220, 0),
            TooltipText = "The BotBehavior subclass the bots run. Write one in your game project; "
                + "it is discovered automatically.",
        };
        grid.AddChild(botBehaviorPicker);

        grid.AddChild(new Label { Text = "Bots headless" });
        botsHeadlessCheck = new CheckBox
        {
            ButtonPressed = true,
            TooltipText = "Run bot instances without a window. Turn off to watch a bot directly.",
        };
        grid.AddChild(botsHeadlessCheck);

        // ── Synthetic network impairment ─────────────────────────────────
        // Everything about interpolation was developed on localhost, where jitter is small and loss
        // is zero. These make a bad link something you can produce on purpose.
        grid.AddChild(new Label { Text = "Sim latency (ms)" });
        simLatencySpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 1000,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "One-way delay added to inbound packets. 0 disables impairment entirely.",
        };
        grid.AddChild(simLatencySpin);

        grid.AddChild(new Label { Text = "Sim jitter (+/- ms)" });
        simJitterSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 500,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Random spread around the latency. This is what drives the adaptive "
                + "interpolation buffer, and what reorders packets.",
        };
        grid.AddChild(simJitterSpin);

        grid.AddChild(new Label { Text = "Sim loss (%)" });
        simLossSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Percentage of snapshot packets discarded. Reliable channels are never "
                + "dropped -- ENet has already delivered those, so dropping one loses it for good.",
        };
        grid.AddChild(simLossSpin);

        grid.AddChild(new Label { Text = "Burst loss (%)" });
        simBurstLossSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Loss during a burst. Real links drop RUNS of packets, not scattered ones, "
                + "and a run is what actually empties the interpolation buffer. 100 = full dropout. "
                + "Needs a burst length to do anything.",
        };
        grid.AddChild(simBurstLossSpin);

        grid.AddChild(new Label { Text = "Burst every (s)" });
        simBurstEverySpin = new SpinBox
        {
            MinValue = 1,
            MaxValue = 120,
            Value = 10,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "Average seconds between bursts. Actual spacing is random around this, "
                + "because real faults do not arrive on a metronome.",
        };
        grid.AddChild(simBurstEverySpin);

        grid.AddChild(new Label { Text = "Burst length (ms)" });
        simBurstMsSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 5000,
            Value = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "How long each burst lasts. 0 disables bursts. Anything longer than the "
                + "interpolation buffer (~66ms by default) will starve it, which is the point.",
        };
        grid.AddChild(simBurstMsSpin);

        grid.AddChild(new Label { Text = "Impair first client only" });
        impairFirstOnlyCheck = new CheckBox
        {
            ButtonPressed = true,
            TooltipText = "Degrade only client 0, leaving the rest healthy. This is usually what you "
                + "want: an impaired peer is only interesting when a healthy one is watching it.",
        };
        grid.AddChild(impairFirstOnlyCheck);

        formDialog.Connect(ConfirmationDialog.SignalName.Confirmed, new Callable(this, MethodName.OnFormConfirmed));
        AddChild(formDialog);
    }

    private void OnFormConfirmed()
    {
        int clientCount = (int)clientCountSpin.Value;
        int botCount = (int)botCountSpin.Value;

        int behaviorIndex = botBehaviorPicker.Selected;
        string botBehavior = behaviorIndex >= 0 && behaviorIndex < botBehaviorNames.Count
            ? botBehaviorNames[behaviorIndex]
            : "";
        // Bots with no behavior would connect and stand there, which reads as a broken session
        // rather than a misconfiguration. Treat it as "no bots" instead.
        if (string.IsNullOrEmpty(botBehavior))
            botCount = 0;

        var edited = new NebulaPlayConfiguration
        {
            // The instance counts are the whole configuration, so they are also the name.
            Name = NebulaPlayConfiguration.DisplayName(clientCount, botCount),
            ClientCount = clientCount,
            BotCount = botCount,
            BotBehavior = botBehavior,
            BotsHeadless = botsHeadlessCheck.ButtonPressed,
            SimLatencyMs = (int)simLatencySpin.Value,
            SimJitterMs = (int)simJitterSpin.Value,
            SimLossPct = (int)simLossSpin.Value,
            SimBurstLossPct = (int)simBurstLossSpin.Value,
            SimBurstEverySec = (int)simBurstEverySpin.Value,
            SimBurstMs = (int)simBurstMsSpin.Value,
            ImpairFirstClientOnly = impairFirstOnlyCheck.ButtonPressed,
        };

        if (editingIndex >= 0 && editingIndex < configurations.Count)
        {
            // Renaming the active play target must carry the selection across,
            // since the selection is stored by name.
            string previousName = configurations[editingIndex].Name;
            configurations[editingIndex] = edited;
            NebulaPlayConfigStore.Save(configurations);
            if (previousName == NebulaPlayConfigStore.GetSelectedName() && previousName != edited.Name)
                NebulaPlayConfigStore.SetSelectedName(edited.Name);
        }
        else
        {
            configurations.Add(edited);
            NebulaPlayConfigStore.Save(configurations);
        }

        Refresh();
        EmitSignal(SignalName.ConfigurationsChanged);
    }
}

#endif // TOOLS
