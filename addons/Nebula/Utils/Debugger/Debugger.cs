using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Godot;

namespace Nebula.Utility.Tools
{
    /// <summary>
    /// Interpolated string handler for Nebula logging. Avoids string formatting/allocation when the log level is disabled.
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct NebulaLogInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public bool Enabled => _enabled;

        public NebulaLogInterpolatedStringHandler(int literalLength, int formattedCount, Debugger.DebugLevel level, out bool shouldAppend)
        {
            _enabled = Debugger.IsEnabled(level);
            shouldAppend = _enabled;

            _inner = _enabled
                ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture)
                : default;
        }

        public void AppendLiteral(string value)
        {
            if (_enabled) _inner.AppendLiteral(value);
        }

        public void AppendFormatted<T>(T value)
        {
            if (_enabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted<T>(T value, string format)
        {
            if (_enabled) _inner.AppendFormatted(value, format);
        }

        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment);
        }

        public void AppendFormatted<T>(T value, int alignment, string format)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment, format);
        }

        public void AppendFormatted(string value)
        {
            if (_enabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted(string value, int alignment = 0, string format = null)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment, format);
        }

        public string ToStringAndClear()
        {
            return _enabled ? _inner.ToStringAndClear() : string.Empty;
        }
    }

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

        public static bool IsEnabled(DebugLevel level)
        {
            return level <= (DebugLevel)ProjectSettings.GetSetting("Nebula/config/log_level", 0).AsInt16();
        }

        public void Log(string msg, DebugLevel level = DebugLevel.INFO)
        {
            if (!IsEnabled(level))
            {
                return;
            }
            var platform = Env.Instance == null ? "Editor" : (Env.Instance.HasServerFeatures ? "Server" : "Client");
            var clientId = Env.Instance?.StartArgs.GetValueOrDefault("clientId", null);
            var clientPrefix = clientId != null ? $" [{clientId}]" : "";
            var messageString = $"({level}) Nebula.{platform}{clientPrefix}: {msg}";
            if (level == DebugLevel.ERROR)
            {
                GD.PushError(messageString);
            }
            else if (level == DebugLevel.WARN)
            {
                GD.PushWarning(messageString);
            }
            else {
                GD.Print(messageString);
            }
        }

        public void Log(DebugLevel level, [InterpolatedStringHandlerArgument("level")] ref NebulaLogInterpolatedStringHandler handler)
        {
            if (!handler.Enabled) return;
            Log(handler.ToStringAndClear(), level);
        }
    }
}