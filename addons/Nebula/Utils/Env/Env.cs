using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Nebula.Utility.Tools
{
    [Tool]
    public partial class Env : Node
    {
        public static Env Instance { get; private set; }
        private string initializedFilename = null;
        private Dictionary<string, string> env = new Dictionary<string, string>();

        /// <summary>Guards <see cref="env"/> and <see cref="initializedFilename"/>. See GetValue.</summary>
        private readonly object _parseLock = new();

        public Dictionary<string, string> StartArgs = [];

        public enum DevelopmentModeType {
            Local,
            Unknown,
        }
        public enum CdnModeType {
            Local,
            Live,
        }

        public DevelopmentModeType DevelopmentMode => GetValue("DEVELOPMENT_MODE") switch {
            "local" => DevelopmentModeType.Local,
            _ => DevelopmentModeType.Unknown
        };

        public CdnModeType CdnMode => GetValue("CDN_MODE") switch {
            "local" => CdnModeType.Local,
            "live" => CdnModeType.Live,
            _ => CdnModeType.Local
        };

        public enum ProjectSettingId
        {
            WORLD_DEFAULT_SCENE
        }

        public static Dictionary<ProjectSettingId, string> ProjectSettingKeys = new Dictionary<ProjectSettingId, string> {
            { ProjectSettingId.WORLD_DEFAULT_SCENE, "Nebula/config/world/default_scene" }
        };

        public override void _Ready()
        {
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument.Contains('='))
                {
                    var keyValuePair = argument.Split("=");
                    StartArgs[keyValuePair[0].TrimStart('-')] = keyValuePair[1];
                }
                else
                {
                    // Options without an argument will be present in the dictionary,
                    // with the value set to an empty string.
                    StartArgs[argument.TrimStart('-')] = "";
                }
            }

            // Priority: cmdline --initialWorldScene → .env INITIAL_WORLD_SCENE → project default.
            if (StartArgs.TryGetValue("initialWorldScene", out var argScene) && !string.IsNullOrEmpty(argScene))
            {
                InitialWorldScene = argScene;
            }
            else
            {
                var fromEnv = GetValue("INITIAL_WORLD_SCENE");
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    InitialWorldScene = fromEnv;
                }
                else
                {
                    InitialWorldScene = ProjectSettings.GetSetting(ProjectSettingKeys[ProjectSettingId.WORLD_DEFAULT_SCENE]).AsString();
                }
            }

            // Check for worldId with case-insensitive key lookup
            var worldIdKey = StartArgs.Keys.FirstOrDefault(k => k.Equals("worldId", StringComparison.OrdinalIgnoreCase));
            if (worldIdKey != null)
            {
                InitialWorldId = new UUID(StartArgs[worldIdKey]);
            }
            else
            {
                InitialWorldId = UUID.Empty;
            }
        }

        public override void _EnterTree()
        {
            if (Instance != null)
            {
                QueueFree();
            }
            Instance = this;
        }

        /// <summary>
        /// Reads a boolean switch from the process environment, falling back to this
        /// process's .env file (<c>.env.server</c> or <c>.env.client</c>). Only <c>"1"</c>
        /// and <c>"true"</c> mean on. Every other value means OFF — including ones that
        /// read as affirmative to a human (<c>"yes"</c>, <c>"on"</c>, <c>"enabled"</c>).
        /// These switches all gate diagnostics, so an unrecognised value resolving to off
        /// costs a run its telemetry, whereas the opposite silently leaves instrumentation
        /// on in a build that asked for none.
        /// </summary>
        /// <returns>
        /// False when the variable is absent everywhere, leaving <paramref name="value"/>
        /// untouched — callers use that to fall back to a project setting rather than
        /// treating "unset" as "off". A variable that IS present reports true whatever its
        /// value: writing it at all is a decision to configure the switch, so falling back
        /// to a project setting that says the opposite would be worse than reading an
        /// unrecognised value as off.
        /// </returns>
        public static bool TryGetFlag(string name, out bool value)
        {
            value = false;

            // Instance is null in contexts that run no autoloads (the editor, bare unit
            // tests); the process environment is still readable there.
            string raw = Instance is not null
                ? Instance.GetValue(name)
                : (OS.HasEnvironment(name) ? OS.GetEnvironment(name) : "");

            if (string.IsNullOrEmpty(raw))
                return false;

            value = raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        public string GetValue(string valuename)
        {
            if (OS.HasEnvironment(valuename))
            {
                return OS.GetEnvironment(valuename);
            }

            // The lock spans the lookup, not just the parse: Parse hands back the shared `env`
            // dictionary, so reading it outside the lock could observe another thread's Clear()
            // partway through a reparse. Callers reach this from worker threads (world generation,
            // and any world tick once per-world thread groups are enabled), and it is contended
            // only on the first access per file -- afterwards Parse returns on its first line.
            lock (_parseLock)
            {
                var parsedEnv = Parse(HasServerFeatures ? "res://.env.server" : "res://.env.client");
                return parsedEnv.TryGetValue(valuename, out var value) ? value : "";
            }
        }

        public string InitialWorldScene { get; private set; }

        public UUID InitialWorldId { get; private set; }

        /// <inheritdoc/>

        public bool HasServerFeatures
        {
            get
            {
                if (OS.HasFeature("dedicated_server")) return true;
                if (StartArgs.ContainsKey("server")) return true;
                return false;
            }
        }

        private Dictionary<string, string> Parse(string filename)
        {
            if (initializedFilename == filename) return env;

            if (!FileAccess.FileExists(filename))
            {
                return new Dictionary<string, string>();
            }

            // Clear any previously cached env if switching files
            if (initializedFilename != null)
            {
                env.Clear();
            }

            var file = FileAccess.Open(filename, FileAccess.ModeFlags.Read);
            while (!file.EofReached())
            {
                string line = file.GetLine();
                var o = line.Split("=");

                if (o.Length == 2)
                {
                    env[o[0]] = o[1].Trim('"');
                }
            }

            initializedFilename = filename;
            return env;
        }
    }
}