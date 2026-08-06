using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Bots
{
    /// <summary>
    /// Process-wide owner of this instance's <see cref="BotBehavior"/>. Created by
    /// <see cref="NetRunner"/> only when the process was launched with <c>--bot</c>, so a normal
    /// client or server never carries any of this.
    ///
    /// <para>Command line: <c>--bot --botId=N --botBehavior=TypeName</c>. The behavior is resolved
    /// by type name rather than by script path — a C# script resource cannot be instantiated
    /// through the Godot script API the way a GDScript one can, and reflection over the loaded
    /// assemblies is both reliable and what lets the editor offer a dropdown of the behaviors that
    /// actually exist.</para>
    /// </summary>
    public partial class BotRunner : Node
    {
        public const string BotArg = "--bot";
        public const string BotIdArg = "--botId=";
        public const string BotBehaviorArg = "--botBehavior=";

        public static BotRunner Instance { get; private set; }

        /// <summary>True in a process launched with <c>--bot</c>.</summary>
        public static bool IsBot => Instance != null;

        public int BotId { get; private set; }
        public BotBehavior Behavior { get; private set; }

        /// <summary>
        /// Completes once <see cref="BotBehavior.BotStartup"/> has finished. A game's entry flow
        /// should await this before deciding how to proceed, so the bot has established its
        /// identity first. Always non-null; a bot with no resolvable behavior completes
        /// immediately rather than hanging the launch.
        /// </summary>
        public Task StartupTask { get; private set; } = Task.CompletedTask;

        private WorldRunner _subscribedWorld;
        private bool _botReadyFired;
        private ulong _lastTickUsec;

        /// <summary>
        /// Called from <see cref="NetRunner"/>'s _Ready. Returns null (and adds nothing to the
        /// tree) unless this process is a bot.
        /// </summary>
        internal static BotRunner TryCreate(Node parent)
        {
            string behaviorName = null;
            int botId = 0;
            bool isBot = false;

            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument == BotArg)
                    isBot = true;
                else if (argument.StartsWith(BotIdArg))
                    int.TryParse(argument.Substring(BotIdArg.Length), out botId);
                else if (argument.StartsWith(BotBehaviorArg))
                    behaviorName = argument.Substring(BotBehaviorArg.Length);
            }

            if (!isBot)
                return null;

            var runner = new BotRunner
            {
                Name = "BotRunner",
                BotId = botId,
                _behaviorName = behaviorName,
            };
            parent.AddChild(runner);
            return runner;
        }

        private string _behaviorName;

        public override void _EnterTree()
        {
            Instance ??= this;
        }

        public override void _Ready()
        {
            if (string.IsNullOrEmpty(_behaviorName))
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"[Bot {BotId}] launched with {BotArg} but no {BotBehaviorArg}<TypeName>; the bot will connect and then do nothing.");
                return;
            }

            var type = ResolveBehaviorType(_behaviorName);
            if (type == null)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"[Bot {BotId}] no non-abstract {nameof(BotBehavior)} subclass named '{_behaviorName}' in the loaded assemblies.");
                return;
            }

            try
            {
                Behavior = (BotBehavior)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"[Bot {BotId}] could not construct '{type.FullName}': {ex.Message}. It needs a parameterless constructor.");
                return;
            }

            Behavior.BotId = BotId;
            Behavior.Name = type.Name;
            AddChild(Behavior);

            Debugger.Instance.Log($"[Bot {BotId}] behavior '{type.FullName}' ready; running startup.");
            StartupTask = RunStartup();
        }

        private async Task RunStartup()
        {
            try
            {
                // Yield a frame before running the behavior. This runs from NetRunner's _Ready, so
                // autoloads declared after it have not initialized yet — and a behavior that
                // establishes a session inevitably touches one of them. Assigning StartupTask
                // synchronously (above) while deferring the body is what lets the game's entry
                // scene await a task that is already pending by the time it looks.
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await Behavior.BotStartup();
            }
            catch (Exception ex)
            {
                // Swallowing here is deliberate: a failed startup should leave the bot inert and
                // loudly logged, not tear down a play session that may have other instances in it.
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"[Bot {BotId}] BotStartup failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Finds a concrete <see cref="BotBehavior"/> subclass by full name or short name. Matching
        /// on either means a configuration can hold the readable short name while a project with
        /// colliding names can still disambiguate with the full one.
        /// </summary>
        public static Type ResolveBehaviorType(string name)
        {
            Type shortNameMatch = null;
            foreach (var candidate in DiscoverBehaviorTypes())
            {
                if (candidate.FullName == name)
                    return candidate;
                if (candidate.Name == name)
                    shortNameMatch = candidate;
            }
            return shortNameMatch;
        }

        /// <summary>
        /// Every concrete <see cref="BotBehavior"/> subclass in the loaded assemblies. Used both to
        /// resolve a configured name and to populate the editor's behavior dropdown.
        /// </summary>
        public static List<Type> DiscoverBehaviorTypes()
        {
            var found = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // A partially-loadable assembly still yields the types that did load, which is
                    // enough — and is better than letting one bad reference hide every behavior.
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!typeof(BotBehavior).IsAssignableFrom(type)) continue;
                    found.Add(type);
                }
            }
            return found;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (Behavior == null) return;

            // The client's world does not exist until StartClient has run, and is replaced by
            // nothing afterwards, so a one-time subscribe on first sight is enough.
            var world = WorldRunner.CurrentWorld;
            if (world != null && world != _subscribedWorld)
            {
                if (_subscribedWorld != null)
                    _subscribedWorld.OnClientNetworkTick -= HandleClientTick;
                world.OnClientNetworkTick += HandleClientTick;
                _subscribedWorld = world;
                _lastTickUsec = Time.GetTicksUsec();
            }
        }

        private void HandleClientTick(Tick tick)
        {
            if (Behavior == null) return;

            ulong now = Time.GetTicksUsec();
            double tickDelta = (now - _lastTickUsec) / 1_000_000.0;
            _lastTickUsec = now;

            if (!_botReadyFired && Behavior.OwnedNodes.Count > 0)
            {
                _botReadyFired = true;
                try
                {
                    Behavior.BotReady();
                }
                catch (Exception ex)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"[Bot {BotId}] BotReady threw: {ex.Message}\n{ex.StackTrace}");
                }
            }

            try
            {
                Behavior.NetworkProcess(tick, tickDelta);
            }
            catch (Exception ex)
            {
                // One misbehaving tick should not kill the session; log and keep going.
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"[Bot {BotId}] NetworkProcess threw on tick {tick}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void _ExitTree()
        {
            if (_subscribedWorld != null)
            {
                _subscribedWorld.OnClientNetworkTick -= HandleClientTick;
                _subscribedWorld = null;
            }
            if (Instance == this)
                Instance = null;
        }
    }
}
