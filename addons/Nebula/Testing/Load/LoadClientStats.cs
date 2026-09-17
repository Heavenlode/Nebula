using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// Counters for one shard, plus the aggregation that turns every shard's counters into one
    /// <c>NEBULA_LOAD</c> line on stdout — the same idiom <c>ServerMetrics</c> uses, and for the
    /// same reason: stdout survives a run with nothing attached to read a debug channel.
    ///
    /// <para>A shard writes only its own instance, and only from its own thread, so the counters
    /// need no synchronisation beyond the volatile reads the aggregator does. Nothing here
    /// allocates on the shard side; the once-a-second aggregation does, on the main thread, which
    /// is not a path whose cost is being measured.</para>
    /// </summary>
    public sealed class LoadClientStats
    {
        /// <summary>
        /// Lag samples kept per shard. Fixed and overwritten in place, like ServerMetrics' rings —
        /// a growing buffer would make the harness's own memory a function of run length.
        /// </summary>
        private const int LagSampleCapacity = 2048;

        private readonly double[] _lagSamplesMs = new double[LagSampleCapacity];
        private int _lagCount;
        private int _lagCursor;

        public long TickPacketsReceived;
        public long BytesReceived;
        public long AcksSent;
        public long InputPacketsSent;
        public long WorldHandoffsAcked;

        /// <summary>Disconnect reason code (ENet event data) to how many peers hit it.</summary>
        private readonly Dictionary<uint, int> _disconnects = new();

        public void RecordTickPacket(int bytes)
        {
            TickPacketsReceived++;
            BytesReceived += bytes;
        }

        public void RecordDisconnect(uint reasonCode)
        {
            _disconnects.TryGetValue(reasonCode, out int seen);
            _disconnects[reasonCode] = seen + 1;
        }

        /// <summary>
        /// How late this shard was running its tick step, in milliseconds.
        ///
        /// <para>This is the harness's tripwire, not a curiosity. A shard that runs late holds back
        /// every peer it owns at once, and the server reads the resulting gap as a mass ack timeout
        /// — the harness manufacturing the exact failure it exists to detect. A run whose p95 is
        /// past one tick period is not a slow server, it is an invalid measurement.</para>
        /// </summary>
        public void RecordShardLag(double lagMs)
        {
            _lagSamplesMs[_lagCursor] = lagMs;
            _lagCursor = (_lagCursor + 1) % LagSampleCapacity;
            if (_lagCount < LagSampleCapacity) _lagCount++;
        }

        public double[] CopyLagSamples()
        {
            int count = Volatile.Read(ref _lagCount);
            var copy = new double[count];
            Array.Copy(_lagSamplesMs, copy, count);
            return copy;
        }

        public Dictionary<uint, int> CopyDisconnects()
        {
            lock (_disconnects) return new Dictionary<uint, int>(_disconnects);
        }

        public void ResetWindow()
        {
            _lagCount = 0;
            _lagCursor = 0;
        }

        // ── Aggregation ──────────────────────────────────────────────────

        public const string LinePrefix = "NEBULA_LOAD ";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Renders one line covering every shard. <paramref name="states"/> is the peer-state
        /// census; <paramref name="gcCounts"/> the process's collection counts so far.
        /// </summary>
        public static string Render(
            double elapsedSeconds,
            IReadOnlyList<LoadClientStats> shards,
            IReadOnlyDictionary<SyntheticPeer.PeerPhase, int> states,
            int[] gcCounts)
        {
            long tickPackets = 0, bytes = 0, acks = 0, inputs = 0, handoffs = 0;
            var lag = new List<double>();
            var disconnects = new Dictionary<uint, int>();

            foreach (var shard in shards)
            {
                tickPackets += Interlocked.Read(ref shard.TickPacketsReceived);
                bytes += Interlocked.Read(ref shard.BytesReceived);
                acks += Interlocked.Read(ref shard.AcksSent);
                inputs += Interlocked.Read(ref shard.InputPacketsSent);
                handoffs += Interlocked.Read(ref shard.WorldHandoffsAcked);
                lag.AddRange(shard.CopyLagSamples());
                foreach (var (code, count) in shard.CopyDisconnects())
                {
                    disconnects.TryGetValue(code, out int seen);
                    disconnects[code] = seen + count;
                }
            }

            lag.Sort();

            var line = new StringBuilder(512);
            line.Append(LinePrefix);
            line.Append("{\"t\":").Append(elapsedSeconds.ToString("F1", Inv));

            line.Append(",\"peers\":{");
            bool firstState = true;
            foreach (var (phase, count) in states)
            {
                if (!firstState) line.Append(',');
                line.Append('"').Append(PhaseName(phase)).Append("\":").Append(count);
                firstState = false;
            }
            line.Append('}');

            line.Append(",\"ticks_rx\":").Append(tickPackets);
            line.Append(",\"bytes_rx\":").Append(bytes);
            line.Append(",\"acks_tx\":").Append(acks);
            line.Append(",\"input_tx\":").Append(inputs);
            line.Append(",\"world_handoffs\":").Append(handoffs);

            line.Append(",\"shard_lag_ms\":{");
            line.Append("\"p50\":").Append(Percentile(lag, 0.50).ToString("F2", Inv));
            line.Append(",\"p95\":").Append(Percentile(lag, 0.95).ToString("F2", Inv));
            line.Append(",\"max\":").Append(Percentile(lag, 1.0).ToString("F2", Inv));
            line.Append(",\"samples\":").Append(lag.Count);
            line.Append('}');

            line.Append(",\"gc\":[")
                .Append(gcCounts[0]).Append(',')
                .Append(gcCounts[1]).Append(',')
                .Append(gcCounts[2]).Append(']');

            line.Append(",\"disconnects\":{");
            bool firstCode = true;
            foreach (var (code, count) in disconnects)
            {
                if (!firstCode) line.Append(',');
                line.Append('"').Append(DisconnectName(code)).Append("\":").Append(count);
                firstCode = false;
            }
            line.Append('}');

            line.Append('}');
            return line.ToString();
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0) return 0;
            int index = (int)Math.Round(fraction * (sorted.Count - 1));
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }

        private static string PhaseName(SyntheticPeer.PeerPhase phase) => phase switch
        {
            SyntheticPeer.PeerPhase.Idle => "idle",
            SyntheticPeer.PeerPhase.Connecting => "connecting",
            SyntheticPeer.PeerPhase.Connected => "connected",
            SyntheticPeer.PeerPhase.Acking => "acking",
            SyntheticPeer.PeerPhase.InWorld => "in_world",
            SyntheticPeer.PeerPhase.Failed => "failed",
            SyntheticPeer.PeerPhase.Dead => "dead",
            _ => "unknown",
        };

        /// <summary>
        /// Names the two disconnect codes that mean something specific, because "peers never
        /// joined" is a useless report when the server actually said why.
        /// </summary>
        private static string DisconnectName(uint code) => code switch
        {
            NetRunner.ProtocolMismatchDisconnectCode => "PROT_protocol_mismatch",
            NetRunner.MalformedPacketDisconnectCode => "MALP_malformed_packet",
            0 => "none",
            _ => $"code_{code}",
        };
    }
}
