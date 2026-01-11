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
        private bool initialized = false;
        private Dictionary<string, string> env = new Dictionary<string, string>();

        public Dictionary<string, string> StartArgs = [];

        public enum DevelopmentModeType {
            Local,
            Unknown,
        }

        public DevelopmentModeType DevelopmentMode => GetValue("DEVELOPMENT_MODE") switch {
            "local" => DevelopmentModeType.Local,
            _ => DevelopmentModeType.Unknown
        };

        public enum ProjectSettingId
        {
            WORLD_DEFAULT_SCENE
        }

        public static Dictionary<ProjectSettingId, string> ProjectSettingKeys = new Dictionary<ProjectSettingId, string> {
            { ProjectSettingId.WORLD_DEFAULT_SCENE, "Nebula/world/default_scene" }
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

            InitialWorldScene = StartArgs.GetValueOrDefault("initialWorldScene", ProjectSettings.GetSetting(ProjectSettingKeys[ProjectSettingId.WORLD_DEFAULT_SCENE]).AsString());

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

        public string GetValue(string valuename)
        {
            if (OS.HasEnvironment(valuename))
            {
                return OS.GetEnvironment(valuename);
            }

            Dictionary<string, string> parsedEnv;

            if (HasServerFeatures)
            {
                parsedEnv = Parse("res://.env.server");
            }
            else
            {
                parsedEnv = Parse("res://.env.client");
            }

            if (parsedEnv.ContainsKey(valuename))
            {
                return parsedEnv[valuename];
            }

            return "";
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
            if (initialized) return env;

            if (!FileAccess.FileExists(filename))
            {
                return new Dictionary<string, string>();
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

            initialized = true;
            return env;
        }
    }
}