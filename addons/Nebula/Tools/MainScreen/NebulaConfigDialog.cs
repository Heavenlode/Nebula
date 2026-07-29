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
            list.AddItem($"{config.Name}\n    headless server + {config.ClientCount} client instance(s)");
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

        if (index >= 0 && index < configurations.Count)
        {
            formDialog.Title = "Edit Configuration";
            clientCountSpin.Value = configurations[index].ClientCount;
        }
        else
        {
            formDialog.Title = "Add Configuration";
            clientCountSpin.Value = 1;
        }

        formDialog.PopupCentered(new Vector2I(360, 0));
        clientCountSpin.GrabFocus();
    }

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

        formDialog.Connect(ConfirmationDialog.SignalName.Confirmed, new Callable(this, MethodName.OnFormConfirmed));
        AddChild(formDialog);
    }

    private void OnFormConfirmed()
    {
        int clientCount = (int)clientCountSpin.Value;
        var edited = new NebulaPlayConfiguration
        {
            // The client count is the whole configuration, so it is also the name.
            Name = NebulaPlayConfiguration.DisplayName(clientCount),
            ClientCount = clientCount,
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
