using System;
using System.Globalization;
using System.Text;
using Godot;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Per-world server instrumentation, emitted as one JSON line per interval.
    ///
    /// <para>Off unless the process was launched with <c>--metrics</c>, and allocation-free on the
    /// recording path so that having it on does not itself change what it measures — the counters
    /// are plain field writes and the tick samples land in a preallocated ring. Only the once-per-
    /// interval emit builds a string, through a reused builder.</para>
    ///
    /// <para>Goes to stdout rather than the debug channel on purpose: DebugHub only produces frames
    /// while a debugger is attached and drops lossy ones when its queue backs up, which would lose
    /// exactly the samples a loaded run exists to capture.</para>
    /// </summary>
    public sealed class ServerMetrics
    {
        public const string EnableArg = "--metrics";
        public const string IntervalArg = "--metricsInterval=";

        /// <summary>
        /// Environment variable that enables metrics like <see cref="EnableArg"/> does.
        /// Read through the Env autoload, so it works both as a real process variable
        /// (spawned processes inherit the editor's environment) and as an entry in the
        /// process's .env file (res://.env.server for servers). This gates the SERVER
        /// only - collection and reporting cost nothing unless it is set, so production
        /// instances leave it off. The editor's Performance tab always exists and simply
        /// stays empty when no server is reporting.
        /// </summary>
        public const string EnableEnvVar = "NEBULA_PERFORMANCE";


        /// <summary>Prefix on every emitted line, so a run can be filtered out of a noisy log.</summary>
        public const string LinePrefix = "NEBULA_METRICS ";

        private const int SampleCapacity = 2048;

        private static bool _parsed;
        private static bool _enabled;
        private static double _intervalSeconds = 1.0;

        /// <summary>Whether metrics were requested on the command line. Parsed once.</summary>
        public static bool Enabled
        {
            get
            {
                ParseArgs();
                return _enabled;
            }
        }

        public static double IntervalSeconds
        {
            get
            {
                ParseArgs();
                return _intervalSeconds;
            }
        }

        private static void ParseArgs()
        {
            if (_parsed) return;
            _parsed = true;

            // Process environment first, then the process's .env file, so both
            // configuration styles work (see Env.TryGetFlag).
            Nebula.Utility.Tools.Env.TryGetFlag(EnableEnvVar, out _enabled);

            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (argument == EnableArg)
                {
                    _enabled = true;
                }
                else if (argument.StartsWith(IntervalArg))
                {
                    if (double.TryParse(argument.Substring(IntervalArg.Length), out double parsed) && parsed > 0)
                        _intervalSeconds = parsed;
                }
            }
        }

        // ─── Recording state ─────────────────────────────────────────────────

        private readonly double[] _tickMs = new double[SampleCapacity];
        private int _tickCount;
        /// <summary>Ticks observed since the last emit, including any past the ring's capacity.</summary>
        private int _ticksThisWindow;

        private long _bytesOut;
        private long _packetsOut;
        private int _mtuExceeded;
        private int _ackTimeouts;

        // Budgeted serialization (MTU splitting).
        //
        // Per-peer payload sizes are sampled, not just summed: one saturated peer says
        // nothing about the other 21, and a mean alone reads as "plenty of headroom"
        // while individual packets sit against the cap. Percentiles show the spread.
        private readonly double[] _payloadBytes = new double[SampleCapacity];
        private int _payloadCount;
        private int _budgetBytes;
        private int _spawnSectionsDeferred;
        private int _propsSectionsDeferred;
        private int _spawnBacklogMax;

        private readonly double[] _sortScratch = new double[SampleCapacity];
        private readonly StringBuilder _line = new(512);

        /// <summary>
        /// Every number is formatted against this, never the ambient culture. On a machine with a
        /// comma decimal separator the default produces "p50":1,747 — which is not merely ugly, it
        /// is invalid JSON that silently reparses as two array elements.
        /// </summary>
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private ulong _windowStartUsec;
        private int _gc0, _gc1, _gc2;
        private bool _started;

        /// <summary>Records one completed server tick. Hot path — no allocation.</summary>
        public void RecordTick(double elapsedMs)
        {
            _ticksThisWindow++;
            if (_tickCount < SampleCapacity)
                _tickMs[_tickCount++] = elapsedMs;
        }

        /// <summary>Records one per-peer tick packet as it goes on the wire. Hot path.</summary>
        public void RecordPacket(int bytes)
        {
            _bytesOut += bytes;
            _packetsOut++;
        }

        public void RecordMtuExceeded() => _mtuExceeded++;

        public void RecordAckTimeout() => _ackTimeouts++;

        /// <summary>Records one per-peer tick payload against its byte budget. Hot path.</summary>
        public void RecordTickBudget(int usedBytes, int budgetBytes)
        {
            _budgetBytes = budgetBytes; // constant within a window; carried for context
            if (_payloadCount < SampleCapacity)
                _payloadBytes[_payloadCount++] = usedBytes;
        }

        /// <summary>Records sections dropped or deferred for budget in one per-peer export. Hot path.</summary>
        public void RecordDeferredSections(int spawnDeferred, int propsDeferred)
        {
            _spawnSectionsDeferred += spawnDeferred;
            _propsSectionsDeferred += propsDeferred;
        }

        /// <summary>Records a peer's count of in-flight (Spawning) spawn records. Hot path.</summary>
        public void RecordSpawnBacklog(int spawningCount)
        {
            if (spawningCount > _spawnBacklogMax)
                _spawnBacklogMax = spawningCount;
        }

        /// <summary>
        /// Whether the interval has elapsed. Separate from <see cref="Emit"/> so the caller only
        /// pays for a peer scan on the tick that actually reports.
        /// </summary>
        public bool IsDue(out double elapsedSeconds)
        {
            ulong now = Time.GetTicksUsec();
            if (!_started)
            {
                _started = true;
                _windowStartUsec = now;
                CaptureGcBaseline();
                elapsedSeconds = 0;
                return false;
            }

            elapsedSeconds = (now - _windowStartUsec) / 1_000_000.0;
            return elapsedSeconds >= IntervalSeconds;
        }

        private void CaptureGcBaseline()
        {
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
        }

        /// <summary>
        /// Writes one line and resets the window. Peer figures are supplied by the caller, which
        /// owns the peer table. Returns the JSON body (without <see cref="LinePrefix"/>) so the
        /// caller can also ship it over the debug channel to the editor's Performance tab.
        /// </summary>
        public string Emit(UUID worldId, Tick tick, int peers, double rttMean, uint rttMax, double elapsedSeconds)
        {
            // Percentiles are computed up front, not inline: both sets share _sortScratch,
            // so the second sort would otherwise invalidate the first set's reads.
            Array.Copy(_tickMs, _sortScratch, _tickCount);
            Array.Sort(_sortScratch, 0, _tickCount);
            double tickP50 = Percentile(_tickCount, 0.50);
            double tickP95 = Percentile(_tickCount, 0.95);
            double tickP99 = Percentile(_tickCount, 0.99);
            double tickMax = Percentile(_tickCount, 1.0);

            Array.Copy(_payloadBytes, _sortScratch, _payloadCount);
            Array.Sort(_sortScratch, 0, _payloadCount);
            double payloadP50 = Percentile(_payloadCount, 0.50);
            double payloadP95 = Percentile(_payloadCount, 0.95);
            double payloadP99 = Percentile(_payloadCount, 0.99);
            double payloadMax = Percentile(_payloadCount, 1.0);

            _line.Clear();
            _line.Append(LinePrefix);
            _line.Append("{\"world\":\"").Append(worldId.ToString()).Append('"');
            _line.Append(",\"tick\":").Append(tick);
            _line.Append(",\"window_s\":").Append(elapsedSeconds.ToString("F2", Inv));
            _line.Append(",\"peers\":").Append(peers);
            _line.Append(",\"ticks\":").Append(_ticksThisWindow);
            _line.Append(",\"tick_ms\":{");
            _line.Append("\"p50\":").Append(tickP50.ToString("F3", Inv));
            _line.Append(",\"p95\":").Append(tickP95.ToString("F3", Inv));
            _line.Append(",\"p99\":").Append(tickP99.ToString("F3", Inv));
            _line.Append(",\"max\":").Append(tickMax.ToString("F3", Inv));
            _line.Append('}');
            _line.Append(",\"bytes_out\":").Append(_bytesOut);
            _line.Append(",\"packets_out\":").Append(_packetsOut);
            // Per peer per second, which is the number that scales to a bandwidth bill.
            double bytesPerPeerPerSec = peers > 0 && elapsedSeconds > 0
                ? _bytesOut / (double)peers / elapsedSeconds
                : 0;
            _line.Append(",\"bytes_per_peer_s\":").Append(bytesPerPeerPerSec.ToString("F0", Inv));
            _line.Append(",\"rtt_ms\":{\"mean\":").Append(rttMean.ToString("F1", Inv));
            _line.Append(",\"max\":").Append(rttMax).Append('}');
            _line.Append(",\"gc\":[")
                 .Append(GC.CollectionCount(0) - _gc0).Append(',')
                 .Append(GC.CollectionCount(1) - _gc1).Append(',')
                 .Append(GC.CollectionCount(2) - _gc2).Append(']');
            _line.Append(",\"mtu_exceeded\":").Append(_mtuExceeded);
            _line.Append(",\"ack_timeouts\":").Append(_ackTimeouts);
            // One sample per peer per tick, in PRE-pack bytes - the size the splitting
            // logic bounds. What actually goes on the wire is this minus NebulaPack's
            // delta compression, so these run larger than bytes_out/packets_out.
            _line.Append(",\"payload\":{\"p50\":").Append(payloadP50.ToString("F0", Inv));
            _line.Append(",\"p95\":").Append(payloadP95.ToString("F0", Inv));
            _line.Append(",\"p99\":").Append(payloadP99.ToString("F0", Inv));
            _line.Append(",\"max\":").Append(payloadMax.ToString("F0", Inv));
            _line.Append(",\"cap\":").Append(_budgetBytes);
            _line.Append('}');
            _line.Append(",\"budget\":{\"spawn_deferred\":").Append(_spawnSectionsDeferred);
            _line.Append(",\"props_deferred\":").Append(_propsSectionsDeferred);
            _line.Append(",\"spawn_backlog_max\":").Append(_spawnBacklogMax).Append('}');
            _line.Append('}');

            string json = _line.ToString(LinePrefix.Length, _line.Length - LinePrefix.Length);

            _windowStartUsec = Time.GetTicksUsec();
            _tickCount = 0;
            _ticksThisWindow = 0;
            _bytesOut = 0;
            _packetsOut = 0;
            _mtuExceeded = 0;
            _ackTimeouts = 0;
            _payloadCount = 0;
            _spawnSectionsDeferred = 0;
            _propsSectionsDeferred = 0;
            _spawnBacklogMax = 0;
            CaptureGcBaseline();

            return json;
        }

        /// <summary>
        /// Nearest-rank percentile over the first <paramref name="count"/> entries of the sorted
        /// scratch. Returns 0 with no samples, which reads correctly as "this world did not tick
        /// during the window".
        /// </summary>
        private double Percentile(int count, double fraction)
        {
            if (count == 0) return 0;
            int index = (int)Math.Ceiling(fraction * count) - 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;
            return _sortScratch[index];
        }
    }
}
