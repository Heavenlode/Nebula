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
        /// owns the peer table.
        /// </summary>
        public void Emit(UUID worldId, Tick tick, int peers, double rttMean, uint rttMax, double elapsedSeconds)
        {
            Array.Copy(_tickMs, _sortScratch, _tickCount);
            Array.Sort(_sortScratch, 0, _tickCount);

            _line.Clear();
            _line.Append(LinePrefix);
            _line.Append("{\"world\":\"").Append(worldId.ToString()).Append('"');
            _line.Append(",\"tick\":").Append(tick);
            _line.Append(",\"window_s\":").Append(elapsedSeconds.ToString("F2", Inv));
            _line.Append(",\"peers\":").Append(peers);
            _line.Append(",\"ticks\":").Append(_ticksThisWindow);
            _line.Append(",\"tick_ms\":{");
            _line.Append("\"p50\":").Append(Percentile(0.50).ToString("F3", Inv));
            _line.Append(",\"p95\":").Append(Percentile(0.95).ToString("F3", Inv));
            _line.Append(",\"p99\":").Append(Percentile(0.99).ToString("F3", Inv));
            _line.Append(",\"max\":").Append(Percentile(1.0).ToString("F3", Inv));
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
            _line.Append('}');

            GD.Print(_line.ToString());

            _windowStartUsec = Time.GetTicksUsec();
            _tickCount = 0;
            _ticksThisWindow = 0;
            _bytesOut = 0;
            _packetsOut = 0;
            _mtuExceeded = 0;
            _ackTimeouts = 0;
            CaptureGcBaseline();
        }

        /// <summary>
        /// Nearest-rank percentile over the sorted scratch. Returns 0 with no samples, which reads
        /// correctly as "this world did not tick during the window".
        /// </summary>
        private double Percentile(double fraction)
        {
            if (_tickCount == 0) return 0;
            int index = (int)Math.Ceiling(fraction * _tickCount) - 1;
            if (index < 0) index = 0;
            if (index >= _tickCount) index = _tickCount - 1;
            return _sortScratch[index];
        }
    }
}
