using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Per-phase timing of the server tick, measured in-process on the untraced server.
    ///
    /// <para>This exists because sampling profilers cannot answer "where does the tick go?" for
    /// this server. Attaching <c>dotnet-trace</c>'s SampleProfiler takes tick p50 from ~14.5 ms to
    /// ~31 ms and drops the server from 30 to ~25 TPS, and its two largest frames
    /// (<c>Thread.PollGCWorker</c> ~29%, <c>Monitor.Enter_Slowpath</c> ~23%) are thread-suspension
    /// artifacts rather than real cost. That was established by a controlled experiment: rewriting
    /// the inbound drain from one lock acquisition per packet to a single batched acquisition made
    /// its <c>Enter_Slowpath</c> share go <em>up</em>. Three separate optimizations the profile
    /// called large measured exactly zero in wall-clock A/B tests.</para>
    ///
    /// <para>So: real <see cref="Stopwatch"/> timestamps, on the real workload, with the profiler
    /// nowhere near it. Percentiles rather than means, because a mean hides the tail that actually
    /// costs a tick its deadline.</para>
    ///
    /// <para>Cost when disabled is one static bool test per phase boundary — <see cref="Now"/>
    /// returns 0 without reading the clock, and the world never allocates a profiler at all.
    /// Allocation-free when enabled: fixed arrays, a reused StringBuilder, no LINQ.</para>
    /// </summary>
    public sealed class TickProfiler
    {
        /// <summary>
        /// Enables phase timing. Read through the Env autoload, so it works as a real process
        /// variable or as an entry in res://.env.server. Deliberately separate from
        /// <see cref="ServerMetrics.EnableEnvVar"/>: this is a diagnostic you turn on to answer a
        /// question, not something a production instance should carry.
        /// </summary>
        public const string EnableEnvVar = "NEBULA_PROFILE";

        /// <summary>Prefix on every emitted line, so a run can be filtered out of a noisy log.</summary>
        public const string LinePrefix = "NEBULA_PHASES ";

        private const int SampleCapacity = 2048;

        private static bool _parsed;
        private static bool _enabled;

        public static bool Enabled
        {
            get
            {
                if (!_parsed)
                {
                    _parsed = true;
                    Nebula.Utility.Tools.Env.TryGetFlag(EnableEnvVar, out _enabled);
                }
                return _enabled;
            }
        }

        /// <summary>
        /// Phases of one server tick, in execution order.
        ///
        /// <para>Some are nested and the report says so rather than pretending otherwise:
        /// <see cref="Gameplay"/> runs inside <see cref="SceneScan"/>, and the five Export* phases
        /// run inside <see cref="Export"/>. Reporting them flat with honest labels beats
        /// subtracting, which quietly hides whatever is in the parent but in no child.</para>
        /// </summary>
        public enum Phase
        {
            /// <summary>Applying queued inbound packets. Runs every physics frame, not just tick frames.</summary>
            Inbound,
            /// <summary>Tick-aligned player-join callbacks.</summary>
            Joins,
            /// <summary>Ack-timeout sweep plus disconnecting whoever it found.</summary>
            AckSweep,
            /// <summary>The pass over NetScenes: validity, interest, auto-despawn. Contains Gameplay.</summary>
            SceneScan,
            /// <summary>Game code: _NetworkProcess on every networked node. Nested in SceneScan.</summary>
            Gameplay,
            /// <summary>Dispatching queued net functions.</summary>
            NetFunctions,
            /// <summary>Whole serialization pass. Contains the four Export* phases below.</summary>
            Export,
            /// <summary>Export phase 1: spawn/despawn records.</summary>
            ExportSpawn,
            /// <summary>Export phase 2: property sections, round-robin across nodes.</summary>
            ExportProps,
            /// <summary>Export phase 3: interest resync.</summary>
            ExportResync,
            /// <summary>Per-tick serializer Cleanup across every node. Nested in Export.</summary>
            ExportCleanup,
            /// <summary>Framing each peer's tick packet and handing it to the transport.</summary>
            Send,
            /// <summary>Handing the finished packet to ENet. Nested in Send.</summary>
            Transmit,
            /// <summary>Despawn bookkeeping and deleting fully-acked nodes.</summary>
            Despawn,

            Count,
        }

        private static readonly string[] PhaseNames =
        {
            "inbound", "joins", "ack_sweep", "scene_scan", "gameplay", "netfunctions",
            "export", "exp_spawn", "exp_props", "exp_resync", "exp_cleanup",
            "send", "transmit", "despawn",
        };

        private const int PhaseCount = (int)Phase.Count;

        /// <summary>Accumulated Stopwatch ticks for each phase within the tick being measured.</summary>
        private readonly long[] _currentPhase = new long[PhaseCount];

        /// <summary>Per-phase millisecond samples for the current window.</summary>
        private readonly double[][] _samples = NewSamples();

        private readonly int[] _sampleCounts = new int[PhaseCount];

        /// <summary>Whole-tick samples, so each phase can be reported as a share of the tick.</summary>
        private readonly double[] _tickMs = new double[SampleCapacity];
        private int _tickCount;

        private readonly double[] _sortScratch = new double[SampleCapacity];
        private readonly StringBuilder _line = new(1024);

        private static double[][] NewSamples()
        {
            var samples = new double[PhaseCount][];
            for (int i = 0; i < PhaseCount; i++) samples[i] = new double[SampleCapacity];
            return samples;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// The profiler for the world ticking on THIS thread, so library code reached from the tick
        /// (serializers, NetBuffer) can report without every signature growing a profiler
        /// parameter. Thread-static because worlds tick on their own threads when per-world thread
        /// groups are on — a plain static would interleave two worlds' numbers into one bucket.
        /// Null whenever profiling is off, or on any thread that is not mid-tick.
        /// </summary>
        [ThreadStatic] private static TickProfiler _current;

        public static TickProfiler Current => _current;

        /// <summary>Binds this profiler to the calling thread for the duration of its tick.</summary>
        public void MakeCurrent() => _current = this;

        /// <summary>Things worth counting rather than timing.</summary>
        public enum Counter
        {
            /// <summary>Props sections served from the section memo (bytes shared, writer skipped).</summary>
            PropsMemoHit,
            /// <summary>Eligible sections that encoded fresh and seeded a memo entry.</summary>
            PropsMemoMiss,
            /// <summary>Sections ineligible for the memo (per-peer/INetValue primitives, empty mask, budget).</summary>
            PropsMemoSlow,
            /// <summary>Eligible sections that found the memo full. Nonzero = raise MemoCapacity.</summary>
            PropsMemoOverflow,
            /// <summary>
            /// Nodes whose serializers were visited by an acknowledgement (PeerAcknowledge drain).
            /// Per tick this should track "nodes that shipped a section" x peers, not "nodes ever
            /// sent" x peers - the latter is what the pre-ring pending set cost.
            /// </summary>
            AckNodesVisited,
            /// <summary>
            /// Bits spent padding to a byte boundary by NetBuffer's silent auto-align (every
            /// byte-granular op on a mid-byte cursor, plus the engine's end-of-section pads).
            /// The honesty counter for the bit stream: padding is invisible everywhere else.
            /// </summary>
            PadBits,
            /// <summary>Interest resync sections committed (phase 3), summed over peers.</summary>
            ResyncSections,
            /// <summary>
            /// Wire bits those sections cost INCLUDING their worst-case framing charge - the
            /// only attribution of resync cost; PayloadCensus never sees these bits.
            /// </summary>
            ResyncBits,
            Count,
        }

        private static readonly string[] CounterNames =
        {
            "memo_hit", "memo_miss", "memo_slow", "memo_overflow",
            "ack_nodes_visited",
            "pad_bits",
            "resync_sections", "resync_bits",
        };
        private const int CounterCount = (int)Counter.Count;

        private readonly long[] _counterCurrent = new long[CounterCount];
        private readonly long[] _counterWindow = new long[CounterCount];

        /// <summary>Adds to a per-tick counter. No-op when profiling is off.</summary>
        public void Add(Counter counter, long amount) => _counterCurrent[(int)counter] += amount;

        /// <summary>
        /// Timestamp for a phase about to start, or 0 when profiling is off. Pair with
        /// <see cref="Record"/>; a 0 start is ignored there, so callers need no null checks.
        /// </summary>
        public static long Now() => _enabled ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>
        /// Charges the elapsed time since <paramref name="startTimestamp"/> to a phase. Additive:
        /// a phase entered many times in one tick (Gameplay, once per node) accumulates.
        /// </summary>
        public void Record(Phase phase, long startTimestamp)
        {
            if (startTimestamp == 0L) return;
            _currentPhase[(int)phase] += Stopwatch.GetTimestamp() - startTimestamp;
        }

        /// <summary>
        /// Closes the tick: converts this tick's accumulators into samples and resets them.
        /// <paramref name="tickMs"/> is the whole ServerProcessTick duration the caller already
        /// measures, so shares are against the real tick rather than the sum of the parts.
        /// </summary>
        public void EndTick(double tickMs)
        {
            if (_tickCount < SampleCapacity) _tickMs[_tickCount++] = tickMs;

            for (int i = 0; i < PhaseCount; i++)
            {
                if (_sampleCounts[i] < SampleCapacity)
                {
                    _samples[i][_sampleCounts[i]++] = _currentPhase[i] * MsPerTick;
                }
                _currentPhase[i] = 0;
            }

            for (int i = 0; i < CounterCount; i++)
            {
                _counterWindow[i] += _counterCurrent[i];
                _counterCurrent[i] = 0;
            }
        }

        private long _windowStartTs;
        private bool _started;

        /// <summary>
        /// True once a reporting window has elapsed. Kept independent of
        /// <see cref="ServerMetrics.IsDue"/> so phase timing can be switched on by itself, without
        /// also turning on the metrics channel.
        /// </summary>
        public bool IsDue(out double elapsedSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            if (!_started)
            {
                _started = true;
                _windowStartTs = now;
                elapsedSeconds = 0;
                return false;
            }

            elapsedSeconds = (now - _windowStartTs) / (double)Stopwatch.Frequency;
            if (elapsedSeconds < IntervalSeconds) return false;
            _windowStartTs = now;
            return true;
        }

        /// <summary>Reporting cadence. Matches ServerMetrics' default so lines interleave readably.</summary>
        public static double IntervalSeconds => 1.0;

        /// <summary>
        /// Prints the report line and clears the window. Printing rather than returning, because
        /// unlike ServerMetrics this has no editor-side consumer -- it is read from the log.
        /// </summary>
        public string Emit(UUID worldId, Tick tick, int peers, double elapsedSeconds)
        {
            double tickP50 = PercentileOf(_tickMs, _tickCount, 0.50);
            double tickMean = MeanOf(_tickMs, _tickCount);

            _line.Clear();
            _line.Append(LinePrefix);
            _line.Append("{\"world\":\"").Append(worldId.ToString()).Append('"');
            _line.Append(",\"tick\":").Append(tick.ToString(Inv));
            _line.Append(",\"window_s\":").Append(elapsedSeconds.ToString("F2", Inv));
            _line.Append(",\"peers\":").Append(peers.ToString(Inv));
            _line.Append(",\"ticks\":").Append(_tickCount.ToString(Inv));
            _line.Append(",\"tick_ms\":{\"p50\":").Append(tickP50.ToString("F3", Inv));
            _line.Append(",\"mean\":").Append(tickMean.ToString("F3", Inv)).Append('}');

            _line.Append(",\"phases\":{");
            for (int i = 0; i < PhaseCount; i++)
            {
                if (i > 0) _line.Append(',');
                int count = _sampleCounts[i];
                double mean = MeanOf(_samples[i], count);
                _line.Append('"').Append(PhaseNames[i]).Append("\":{");
                _line.Append("\"mean\":").Append(mean.ToString("F3", Inv));
                _line.Append(",\"p50\":").Append(PercentileOf(_samples[i], count, 0.50).ToString("F3", Inv));
                _line.Append(",\"p95\":").Append(PercentileOf(_samples[i], count, 0.95).ToString("F3", Inv));
                _line.Append(",\"max\":").Append(PercentileOf(_samples[i], count, 1.0).ToString("F3", Inv));
                // Share of the mean tick, not of the summed phases: the difference between the two
                // is the tick time no phase claims, which is itself worth seeing.
                double share = tickMean > 0 ? mean / tickMean * 100.0 : 0;
                _line.Append(",\"pct\":").Append(share.ToString("F1", Inv));
                _line.Append('}');
            }
            _line.Append('}');

            // Per tick, so "30 candidates measured" reads directly rather than needing division.
            _line.Append(",\"counters_per_tick\":{");
            int ticks = _tickCount > 0 ? _tickCount : 1;
            for (int i = 0; i < CounterCount; i++)
            {
                if (i > 0) _line.Append(',');
                _line.Append('"').Append(CounterNames[i]).Append("\":")
                     .Append((_counterWindow[i] / (double)ticks).ToString("F1", Inv));
            }
            _line.Append('}');


            _line.Append('}');

            for (int i = 0; i < CounterCount; i++) _counterWindow[i] = 0;
            _tickCount = 0;
            for (int i = 0; i < PhaseCount; i++) _sampleCounts[i] = 0;

            var report = _line.ToString();
            Godot.GD.Print(report);
            return report;
        }

        private double MeanOf(double[] values, int count)
        {
            if (count == 0) return 0;
            double total = 0;
            for (int i = 0; i < count; i++) total += values[i];
            return total / count;
        }

        /// <summary>Nearest-rank percentile. Sorts into scratch so the sample order survives.</summary>
        private double PercentileOf(double[] values, int count, double fraction)
        {
            if (count == 0) return 0;
            Array.Copy(values, _sortScratch, count);
            Array.Sort(_sortScratch, 0, count);
            int index = (int)Math.Ceiling(fraction * count) - 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;
            return _sortScratch[index];
        }
    }
}
