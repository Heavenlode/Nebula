using System;
using System.Collections.Generic;
using System.Runtime;
using Godot;
using Nebula.Serialization;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// Entry point for the synthetic load client: parses the command line, starts the shards, emits
    /// one <c>NEBULA_LOAD</c> line per interval, and quits when the run is over.
    ///
    /// <para>Runs as its own scene, so the game's own entry flow never executes and this process
    /// builds no world, imports no spawn, and predicts nothing. It also does not use
    /// <see cref="NetRunner"/> — that is a singleton holding one host and one server peer, which is
    /// the opposite of what driving N connections needs. The autoload still initialises the ENet
    /// library, which is all this needs from it.</para>
    ///
    /// <para>EXIT CODES. 0 the run completed; 2 the command line was rejected; 3 a shard thread
    /// died. A harness should treat anything non-zero as a failed run rather than reporting the
    /// numbers it managed to collect.</para>
    /// </summary>
    public partial class LoadClientNode : Node
    {
        public const int ExitBadArguments = 2;
        public const int ExitShardFailure = 3;

        private LoadClientConfig _config;
        private readonly List<LoadShard> _shards = new();
        private readonly Dictionary<SyntheticPeer.PeerPhase, int> _census = new();

        private double _elapsedSec;
        private double _nextStatsAtSec;
        private bool _finishing;

        public override void _Ready()
        {
            _config = LoadClientConfig.FromCommandLine(out var problems);

            if (problems.Count > 0)
            {
                foreach (var problem in problems) GD.PrintErr($"[LoadClient] {problem}");
                Quit(ExitBadArguments);
                return;
            }

            // Matches what the dedicated server does. A collection here stalls every peer this
            // process owns at once, which the server reads as a mass ack timeout — the harness
            // manufacturing the exact failure it exists to detect.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            GD.Print($"[LoadClient] {_config}");
            GD.Print($"[LoadClient] protocol handshake 0x{Protocol.HandshakeHash:X8}, tps {NetRunner.TPS}");

            if (!TryResolveInputSource(out var sourceFactory, out string sourceName))
            {
                Quit(ExitBadArguments);
                return;
            }
            GD.Print($"[LoadClient] input source {sourceName}");

            for (int i = 0; i < _config.Shards; i++)
            {
                var shard = new LoadShard(i, _config, NetRunner.TPS, sourceFactory);
                _shards.Add(shard);
                shard.Start();
            }

            _nextStatsAtSec = _config.StatsIntervalSec;
        }

        public override void _Process(double delta)
        {
            if (_finishing) return;

            _elapsedSec += delta;

            foreach (var shard in _shards)
            {
                if (shard.Failure == null) continue;
                GD.PrintErr($"[LoadClient] Shard {shard.ShardIndex} died: {shard.Failure}");
                Quit(ExitShardFailure);
                return;
            }

            if (_elapsedSec >= _nextStatsAtSec)
            {
                _nextStatsAtSec += _config.StatsIntervalSec;
                EmitStats();
            }

            if (_config.DurationSec > 0 && _elapsedSec >= _config.DurationSec)
            {
                EmitStats();
                GD.Print("[LoadClient] Run complete.");
                Quit(0);
            }
        }

        /// <summary>
        /// Resolves <c>--loadBehavior</c> to a factory, one instance per peer.
        ///
        /// <para>Per peer, not shared: a source carries per-peer state (its bound slots, its wander
        /// phase), and one instance across a hundred peers would have them all move identically —
        /// which leaves every interest set static, and interest churn is much of what a load run
        /// exists to exercise.</para>
        ///
        /// <para>Resolved by type name through the same <see cref="Nebula.Utility.Tools.TypeDiscovery"/>
        /// the bot runner uses, so a game writes a source in its own project with no registration
        /// step.</para>
        /// </summary>
        private bool TryResolveInputSource(out Func<SyntheticInputSource> factory, out string name)
        {
            if (string.IsNullOrEmpty(_config.LoadBehavior))
            {
                factory = () => new ZeroInputSource();
                name = $"{nameof(ZeroInputSource)} (default — peers occupy the server but never move)";
                return true;
            }

            var type = Nebula.Utility.Tools.TypeDiscovery.Resolve<SyntheticInputSource>(_config.LoadBehavior);
            if (type == null)
            {
                GD.PrintErr($"[LoadClient] No non-abstract {nameof(SyntheticInputSource)} named "
                    + $"'{_config.LoadBehavior}' in the loaded assemblies.");
                factory = null;
                name = null;
                return false;
            }

            // Construct one up front rather than discovering at the first tick, on a shard thread,
            // that the type has no parameterless constructor.
            try
            {
                _ = (SyntheticInputSource)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[LoadClient] Could not construct '{type.FullName}': {ex.Message}. "
                    + "It needs a parameterless constructor.");
                factory = null;
                name = null;
                return false;
            }

            factory = () => (SyntheticInputSource)Activator.CreateInstance(type);
            name = type.FullName;
            return true;
        }

        private void EmitStats()
        {
            _census.Clear();
            foreach (var shard in _shards)
            {
                foreach (var peer in shard.Peers)
                {
                    _census.TryGetValue(peer.Phase, out int seen);
                    _census[peer.Phase] = seen + 1;
                }
            }

            var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            GD.Print(LoadClientStats.Render(_elapsedSec, ShardStats(), _census, gc));

            foreach (var shard in _shards) shard.Stats.ResetWindow();
        }

        private List<LoadClientStats> ShardStats()
        {
            var stats = new List<LoadClientStats>(_shards.Count);
            foreach (var shard in _shards) stats.Add(shard.Stats);
            return stats;
        }

        private void Quit(int exitCode)
        {
            _finishing = true;
            foreach (var shard in _shards) shard.Stop();
            GetTree().Quit(exitCode);
        }

        public override void _ExitTree()
        {
            if (_finishing) return;
            foreach (var shard in _shards) shard.Stop();
        }
    }
}
