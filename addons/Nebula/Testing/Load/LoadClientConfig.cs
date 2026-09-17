using System;
using System.Collections.Generic;
using Godot;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// Command line for the synthetic load client, parsed and validated in one place.
    ///
    /// <para>Launched as its own scene rather than as a mode of the game:
    /// <code>
    /// godot --path &lt;proj&gt; --headless res://addons/Nebula/Testing/Load/LoadClient.tscn \
    ///   --loadClient --peers=100 --serverAddress=127.0.0.1 --serverPort=8888 \
    ///   --shards=4 --rampPerSec=10 --durationSec=180
    /// </code>
    /// </para>
    /// </summary>
    public sealed class LoadClientConfig
    {
        /// <summary>Re-exported so a caller reads one name; the flag itself lives in Core,
        /// because autoloads need it in builds that strip Testing/**.</summary>
        public const string LoadClientArg = Nebula.Diagnostics.LoadClientProcess.LoadClientArg;
        public const string PeersArg = "--peers=";
        public const string ShardsArg = "--shards=";
        public const string AddressArg = "--serverAddress=";
        public const string PortArg = "--serverPort=";
        public const string RampPerSecArg = "--rampPerSec=";
        public const string DurationArg = "--durationSec=";
        public const string ConnectTimeoutArg = "--connectTimeoutSec=";
        public const string StatsIntervalArg = "--statsIntervalSec=";
        public const string LoadBehaviorArg = "--loadBehavior=";

        public const int DefaultPeers = 8;
        public const int DefaultShards = 4;
        public const int DefaultRampPerSec = 10;
        public const int DefaultDurationSec = 60;
        public const int DefaultConnectTimeoutSec = 20;
        public const double DefaultStatsIntervalSec = 1.0;

        /// <summary>
        /// Receive buffer per shard socket. ENet's default is 256 KB, which at 100 peers is only a
        /// few hundred milliseconds of headroom — one GC pause past that and the kernel drops
        /// packets, the peers miss acks, and the SERVER records ack timeouts the harness caused.
        /// </summary>
        public const int ShardSocketBufferBytes = 4 * 1024 * 1024;

        public int Peers = DefaultPeers;
        public int Shards = DefaultShards;
        public string Address = "127.0.0.1";
        public int Port = 8888;
        public int RampPerSec = DefaultRampPerSec;
        public int DurationSec = DefaultDurationSec;
        public int ConnectTimeoutSec = DefaultConnectTimeoutSec;
        public double StatsIntervalSec = DefaultStatsIntervalSec;

        /// <summary>
        /// SyntheticInputSource subclass driving the peers, by type name. Empty means the
        /// built-in ZeroInputSource, which occupies the server but never moves — see its doc
        /// for why that measures the floor rather than the game.
        /// </summary>
        public string LoadBehavior = "";


        public static LoadClientConfig FromCommandLine(out List<string> problems)
        {
            var config = new LoadClientConfig();
            problems = new List<string>();

            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (TryInt(argument, PeersArg, out int peers)) config.Peers = peers;
                else if (TryInt(argument, ShardsArg, out int shards)) config.Shards = shards;
                else if (TryInt(argument, PortArg, out int port)) config.Port = port;
                else if (TryInt(argument, RampPerSecArg, out int ramp)) config.RampPerSec = ramp;
                else if (TryInt(argument, DurationArg, out int duration)) config.DurationSec = duration;
                else if (TryInt(argument, ConnectTimeoutArg, out int connect)) config.ConnectTimeoutSec = connect;
                else if (argument.StartsWith(AddressArg)) config.Address = argument.Substring(AddressArg.Length);
                else if (argument.StartsWith(LoadBehaviorArg)) config.LoadBehavior = argument.Substring(LoadBehaviorArg.Length);
                else if (argument.StartsWith(StatsIntervalArg)
                         && double.TryParse(argument.Substring(StatsIntervalArg.Length),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double interval))
                {
                    config.StatsIntervalSec = interval;
                }
            }

            if (config.Peers < 1) problems.Add($"{PeersArg}<n> must be at least 1 (got {config.Peers}).");
            if (config.Shards < 1) problems.Add($"{ShardsArg}<n> must be at least 1 (got {config.Shards}).");
            if (config.Port is < 1 or > 65535) problems.Add($"{PortArg}<n> is out of range (got {config.Port}).");
            if (config.RampPerSec < 1) problems.Add($"{RampPerSecArg}<n> must be at least 1 (got {config.RampPerSec}).");
            if (config.StatsIntervalSec <= 0) problems.Add($"{StatsIntervalArg}<s> must be positive.");
            if (string.IsNullOrWhiteSpace(config.Address)) problems.Add($"{AddressArg}<host> must not be empty.");

            // More shards than peers would create hosts with no peers on them; harmless but it
            // means the run is not the one that was asked for, so say so rather than silently
            // reshaping it.
            if (config.Shards > config.Peers)
            {
                problems.Add($"{ShardsArg}{config.Shards} exceeds {PeersArg}{config.Peers}; "
                    + "there would be shards with no peers.");
            }

            return config;
        }

        private static bool TryInt(string argument, string prefix, out int value)
        {
            value = 0;
            return argument.StartsWith(prefix)
                && int.TryParse(argument.Substring(prefix.Length), out value);
        }

        /// <summary>Peers assigned to shard <paramref name="shardIndex"/>, spreading any remainder.</summary>
        public int PeersInShard(int shardIndex)
        {
            int baseCount = Peers / Shards;
            int remainder = Peers % Shards;
            return baseCount + (shardIndex < remainder ? 1 : 0);
        }

        /// <summary>Global index of the first peer in a shard, so peer numbering is stable across a run.</summary>
        public int FirstPeerIndexOfShard(int shardIndex)
        {
            int baseCount = Peers / Shards;
            int remainder = Peers % Shards;
            return shardIndex * baseCount + Math.Min(shardIndex, remainder);
        }

        public override string ToString()
            => $"peers={Peers} shards={Shards} target={Address}:{Port} "
             + $"rampPerSec={RampPerSec} durationSec={DurationSec} "
             + $"behavior={(string.IsNullOrEmpty(LoadBehavior) ? nameof(ZeroInputSource) : LoadBehavior)}";
    }
}
