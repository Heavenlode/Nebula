using Godot;

namespace Nebula.Utility.Tools
{
    [Tool]
    public partial class Debugger : Node
    {
        public static Debugger Instance { get; private set; }
        public static Debugger EditorInstance => Engine.GetSingleton("Debugger") as Debugger;

        public override void _EnterTree()
        {
            if (Engine.IsEditorHint())
            {
                Engine.RegisterSingleton("Debugger", this);
                return;
            }
            if (Instance != null)
            {
                QueueFree();
                return;
            }
            Instance = this;
        }

        public enum DebugLevel
        {
            ERROR,
            WARN,
            INFO,
            VERBOSE,
        }

        public void Log(string msg, DebugLevel level = DebugLevel.INFO)
        {
            if (level > (DebugLevel)ProjectSettings.GetSetting("Nebula/config/log_level", 0).AsInt16())
            {
                return;
            }
            var platform = Env.Instance == null ? "Editor" : (Env.Instance.HasServerFeatures ? "Server" : "Client");
            var messageString = $"({level}) Nebula.{platform}: {msg}";
            if (level == DebugLevel.ERROR)
            {
                GD.PushError(messageString, new System.Exception().StackTrace);
            }
            else if (level == DebugLevel.WARN)
            {
                GD.PushWarning(messageString);
            }
            else {
                GD.Print(messageString);
            }
        }
    }
}