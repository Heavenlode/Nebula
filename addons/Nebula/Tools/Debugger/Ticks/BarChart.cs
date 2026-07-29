using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Internal.Editor.DTO;
namespace Nebula.Internal.Editor
{
    [Tool]
    public partial class BarChart : VBoxContainer
    {
        public enum BarChartType
        {
            Call,
            Log,
            Egress,
        }
        [Export(PropertyHint.Enum)]
        public BarChartType type;

        private Label label;
        private Label maxLabel;
        private Label minLabel;
        private Label medLabel;

        private string _title = "Bar Chart";
        [Export]
        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                if (label != null)
                {
                    label.Text = _title;
                }
            }
        }

        private int _maxValue = 100;
        [Export]
        public int MaxValue
        {
            get => _maxValue;
            set
            {
                _maxValue = value;
                _dynamicMaxValue = value;
                UpdateMtuValues();
            }
        }

        private int _minValue = 0;
        [Export]
        public int MinValue
        {
            get => _minValue;
            set
            {
                _minValue = value;
                _dynamicMinValue = value;
                UpdateMtuValues();
            }
        }

        private bool _dynamicValues = false;

        private int _dynamicMinValue = int.MaxValue;
        private int _dynamicMaxValue = int.MinValue;

        private void UpdateMtuValues()
        {
            var maxValue = _maxValue;
            var minValue = _minValue;
            if (_dynamicValues)
            {
                maxValue = _dynamicMaxValue;
                minValue = _dynamicMinValue;
            }
            if (maxValue == minValue)
            {
                if (maxLabel != null)
                {
                    maxLabel.Text = "";
                }
                if (minLabel != null)
                {
                    minLabel.Text = "";
                }
                if (medLabel != null)
                {
                    medLabel.Text = "";
                }
            }
            else
            {
                // maxValue/minValue, not the _-prefixed fields: on a dynamic
                // chart those are the configured bounds, not the ones the bars
                // are actually scaled against.
                if (maxLabel != null)
                {
                    maxLabel.Text = maxValue.ToString();
                }
                if (minLabel != null)
                {
                    minLabel.Text = minValue.ToString();
                }
                if (medLabel != null)
                {
                    var medText = ((maxValue + minValue) / 2).ToString();
                    medLabel.Text = medText == "0" ? "" : medText;
                }
            }

            foreach (var tickFrame in tickFrames.Values)
            {
                UpdateFrameSize(tickFrame, tickFrame.Get("frame_size").AsInt32());
            }
        }

        [Export]
        public WorldDebug debugPanel;

        [Export]
        public CheckBox liveCheckbox;

        private bool IsLive()
        {
            return liveCheckbox.ButtonPressed;
        }

        protected PackedScene tickFrameScene = GD.Load<PackedScene>("res://addons/Nebula/Tools/Debugger/Ticks/tick_frame.tscn");
        protected Control selectedTickFrame;
        protected Control previousTickFrame;
        protected ScrollContainer scrollContainer;
        protected Dictionary<int, Control> tickFrames = [];
        public override void _Ready()
        {
            scrollContainer = GetNode<ScrollContainer>("%ScrollContainer");
            scrollContainer.Connect("on_previous_page", Callable.From((int startId, int numChildren) => OnPreviousPage(startId, numChildren)));
            scrollContainer.Connect("on_next_page", Callable.From((int startId, int numChildren) => OnNextPage(startId, numChildren)));
            label = GetNode<Label>("Label");
            label.Text = _title;

            // These were declared but never assigned, so the axis kept the
            // placeholder 100/50/0 baked into the scene no matter what the chart
            // was actually measuring.
            maxLabel = GetNodeOrNull<Label>("HBoxContainer/VBoxContainer/MTUMax");
            medLabel = GetNodeOrNull<Label>("HBoxContainer/VBoxContainer/MTUMed");
            minLabel = GetNodeOrNull<Label>("HBoxContainer/VBoxContainer/MTUMin");

            // Egress is measured in bytes-on-the-wire, so the MTU is the
            // meaningful ceiling: a bar at the top of the chart is a packet at
            // the limit. The other chart types count calls/logs and have no
            // fixed scale, so they stay dynamic.
            if (type == BarChartType.Egress)
            {
                _maxValue = NetRunner.MTU;
                _minValue = 0;
            }

            if (_minValue == _maxValue)
            {
                _dynamicValues = true;
            }
            UpdateMtuValues();
            Connect(CanvasItem.SignalName.VisibilityChanged,
                new Callable(this, MethodName.OnChartVisibilityChanged));
            if (liveCheckbox != null)
            {
                liveCheckbox.Connect("toggled", Callable.From((bool toggled_on) => {
                    scrollContainer.Set("is_live", toggled_on);
                }));
            }
        }

        private void OnPreviousPage(int startId, int numChildren)
        {
            var ids = Enumerable.Range(startId - numChildren, numChildren).ToArray();
            var frames = debugPanel.GetFrames(ids);
            var frameUIs = new Godot.Collections.Array();
            foreach (var frame in frames)
            {
                var frameUI = CreateTickFrameUI(frame.AsGodotDictionary()["details"].AsGodotDictionary()["Tick"].AsGodotDictionary()["ID"].AsInt32());
                UpdateFrameSize(frameUI, frame.AsGodotDictionary());
                frameUIs.Add(frameUI);
            }
            scrollContainer.Call("load_previous_page", frameUIs, frames.Count < numChildren);
        }

        private void OnNextPage(int startId, int numChildren)
        {
            var ids = Enumerable.Range(startId, numChildren).ToArray();
            var frames = debugPanel.GetFrames(ids, false);
            var frameUIs = new Godot.Collections.Array();
            foreach (var frame in frames)
            {
                var frameUI = CreateTickFrameUI(frame.AsGodotDictionary()["details"].AsGodotDictionary()["Tick"].AsGodotDictionary()["ID"].AsInt32());
                UpdateFrameSize(frameUI, frame.AsGodotDictionary());
                frameUIs.Add(frameUI);
            }
            scrollContainer.Call("load_next_page", frameUIs, frames.Count < numChildren);
        }

        /// <summary>Newest tick seen, tracked even while hidden so the chart can backfill.</summary>
        private int lastFrameId = -1;

        public void _OnReceiveFrame(int id)
        {
            lastFrameId = id;
            if (!IsLive()) return;
            // Three of the four charts in a world panel live inside the hidden
            // "View All Frames" window, and every chart in a non-selected world
            // is hidden too. They were all instantiating a tick-frame scene node
            // per tick regardless; they now backfill from the database when they
            // actually become visible.
            if (!IsVisibleInTree()) return;
            var frameUI = CreateTickFrameUI(id);
            scrollContainer.Call("paginate_child", frameUI);
        }

        private void OnChartVisibilityChanged()
        {
            if (!IsVisibleInTree() || lastFrameId < 0 || tickFrames.Count > 0)
                return;
            BackfillRecentFrames();
        }

        /// <summary>Loads the most recent page of frames from the database.</summary>
        private void BackfillRecentFrames()
        {
            const int Count = 60;
            var ids = Enumerable.Range(System.Math.Max(0, lastFrameId - Count + 1), Count).ToArray();
            var frames = debugPanel.GetFrames(ids, false);
            var frameUIs = new Godot.Collections.Array();
            foreach (var frame in frames)
            {
                var dict = frame.AsGodotDictionary();
                var frameUI = CreateTickFrameUI(dict["details"].AsGodotDictionary()["Tick"].AsGodotDictionary()["ID"].AsInt32());
                UpdateFrameSize(frameUI, dict);
                frameUIs.Add(frameUI);
            }
            if (frameUIs.Count > 0)
                scrollContainer.Call("load_next_page", frameUIs, true);
        }

        private Control CreateTickFrameUI(int id) {
            var activeTickFrame = tickFrameScene.Instantiate<Control>();
            activeTickFrame.Call("set_frame_size", 0, 0);
            activeTickFrame.Set("tick_frame_id", id);
            if (previousTickFrame != null)
            {
                activeTickFrame.Set("previous_tick_frame", previousTickFrame);
                previousTickFrame.Set("next_tick_frame", activeTickFrame);
            }
            previousTickFrame = activeTickFrame;
            activeTickFrame.Connect("gui_input", Callable.From((InputEvent @event) =>
            {
                if (activeTickFrame.IsQueuedForDeletion()) return;
                _GuiInput(activeTickFrame, @event);
            }));
            activeTickFrame.Connect("tree_exiting", Callable.From(() => {
                tickFrames.Remove(id);
                if (selectedTickFrame == activeTickFrame)
                {
                    selectedTickFrame = null;
                }
            }));
            if (IsLive())
            {
                OnTickFrameSelected(activeTickFrame, false);
            }
            tickFrames[id] = activeTickFrame;
            return activeTickFrame;
        }

        private void UpdateFrameSize(Control tickFrame, Godot.Collections.Dictionary frameData) {
            int frameSize = 0;
            if (type == BarChartType.Call)
            {
                frameSize = frameData["network_function_calls"].AsGodotArray().Count;
            }
            else if (type == BarChartType.Egress)
            {
                frameSize = frameData["details"].AsGodotDictionary()["Tick"].AsGodotDictionary()["Greatest Size"].AsInt32();
            }
            else if (type == BarChartType.Log)
            {
                frameSize = frameData["logs"].AsGodotArray().Count;
            }
            if (frameSize > _dynamicMaxValue)
            {
                _dynamicMaxValue = frameSize;
                UpdateMtuValues();
            }
            if (frameSize < _dynamicMinValue)
            {
                _dynamicMinValue = frameSize;
                UpdateMtuValues();
            }
            UpdateFrameSize(tickFrame, frameSize);
        }

        public virtual void _OnFrameUpdated(int id)
        {
            if (!IsVisibleInTree()) return;
            if (!tickFrames.ContainsKey(id)) return;
            var tickFrame = tickFrames[id];
            var frameData = debugPanel.GetFrameSummary(id);
            UpdateFrameSize(tickFrame, frameData);
        }

        private void UpdateFrameSize(Control tickFrame, int frameSize)
        {
            if (_dynamicValues)
            {
                if (_dynamicMaxValue == 0)
                {
                    tickFrame.Call("set_frame_size", frameSize, 0);
                }
                else
                {
                    tickFrame.Call("set_frame_size", frameSize, (float)frameSize / _dynamicMaxValue);
                }
            }
            else
            {
                // Clamped: on the egress chart the ceiling is the MTU, and a tick
                // that exceeds it would otherwise draw a bar taller than the chart.
                // It still renders full-height and red, which is the point.
                tickFrame.Call("set_frame_size", frameSize, Mathf.Min(1f, (float)frameSize / _maxValue));
            }
        }


        private async void ScrollTo(Control tickFrame)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var framePosition = tickFrame.GetRect().Position.X;
            if (framePosition + tickFrame.GetRect().Size.X > scrollContainer.GetHScrollBar().Value + scrollContainer.GetRect().Size.X)
            {
                scrollContainer.GetHScrollBar().Value = framePosition + tickFrame.GetRect().Size.X - scrollContainer.GetRect().Size.X;
            }
            else if (framePosition < scrollContainer.GetHScrollBar().Value)
            {
                scrollContainer.GetHScrollBar().Value = framePosition;
            }
        }

        private void OnTickFrameSelected(Control tickFrame, bool pause = false)
        {
            if (selectedTickFrame != null)
            {
                selectedTickFrame.Call("deselect");
            }
            selectedTickFrame = tickFrame;
            selectedTickFrame.Call("select");
            ScrollTo(selectedTickFrame);
            debugPanel.EmitSignal(WorldDebug.SignalName.TickFrameSelected, tickFrame);
            liveCheckbox.ButtonPressed = !pause;
        }
        public override void _UnhandledInput(InputEvent @event)
        {
            base._UnhandledInput(@event);
            if (@event is InputEventKey eventKey && eventKey.Keycode == Key.Left && eventKey.Pressed)
            {
                if (selectedTickFrame.Get("previous_tick_frame").AsGodotObject() != null)
                {
                    OnTickFrameSelected(selectedTickFrame.Get("previous_tick_frame").AsGodotObject() as Control, true);
                    AcceptEvent();
                }
            }
            else if (@event is InputEventKey eventKey2 && eventKey2.Keycode == Key.Right && eventKey2.Pressed)
            {
                if (selectedTickFrame.Get("next_tick_frame").AsGodotObject() != null)
                {
                    OnTickFrameSelected(selectedTickFrame.Get("next_tick_frame").AsGodotObject() as Control, true);
                    AcceptEvent();
                }
            }

        }

        public void _GuiInput(Control frameNode, InputEvent @event)
        {
            if (@event is InputEventMouseButton mouseEvent && mouseEvent.ButtonIndex == MouseButton.Left && mouseEvent.Pressed)
            {
                OnTickFrameSelected(frameNode, true);
            }
        }

    }
}