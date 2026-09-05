using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Client-side cost of turning spawn records into live scenes, which is the one part of
    /// receiving a tick that can stall a frame for hundreds of milliseconds.
    ///
    /// <para>A spawn import does two expensive things on the MAIN thread, inside
    /// <c>NetRunner._PhysicsProcess</c>: it resolves the <c>PackedScene</c> (a cold resolve is a
    /// synchronous <c>GD.Load</c> of the scene AND its whole dependency graph — rigs, animations,
    /// materials, textures), and then it instantiates it. Neither is throttled: every spawn record
    /// that rides a tick's packet is built in that one frame. A burst of joins therefore lands as
    /// N instantiates back to back, and the client stops drawing until they finish.</para>
    ///
    /// <para>Always on, because a stall this size is never acceptable and a player cannot report
    /// what they cannot see. The cost when healthy is one timestamp pair per SPAWN — not per tick,
    /// not per node — and spawns are rare. Nothing is emitted below
    /// <see cref="ReportThresholdMs"/>.</para>
    /// </summary>
    internal static class SpawnImportProfiler
    {
        /// <summary>
        /// A tick whose spawn imports cost more than this gets a line. One frame at 30 Hz is
        /// 33 ms, so this is already "the client visibly hitched", not a tuning knob.
        /// </summary>
        private const double ReportThresholdMs = 25.0;

        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

        private sealed class SceneCost
        {
            public double LoadMs;
            public double InstantiateMs;
            public int Count;
            public int ColdLoads;
        }

        private static readonly Dictionary<string, SceneCost> _thisTick = new();
        private static double _tickLoadMs;
        private static double _tickInstantiateMs;
        private static int _tickCount;

        /// <summary>Records one spawn import. <paramref name="coldLoad"/> = the scene was not
        /// already in Nebula's scene cache, so resolving it hit the filesystem.</summary>
        public static void Record(string scenePath, double loadMs, double instantiateMs, bool coldLoad)
        {
            if (string.IsNullOrEmpty(scenePath)) scenePath = "(unknown scene)";
            if (!_thisTick.TryGetValue(scenePath, out var entry))
            {
                entry = new SceneCost();
                _thisTick[scenePath] = entry;
            }
            entry.LoadMs += loadMs;
            entry.InstantiateMs += instantiateMs;
            entry.Count++;
            if (coldLoad) entry.ColdLoads++;

            _tickLoadMs += loadMs;
            _tickInstantiateMs += instantiateMs;
            _tickCount++;
        }

        /// <summary>Converts a Stopwatch timestamp delta to milliseconds.</summary>
        public static double Elapsed(long fromTimestamp)
            => (Stopwatch.GetTimestamp() - fromTimestamp) * ToMs;

        /// <summary>
        /// Called once at the end of a tick's import. Emits a line only when this tick's spawn
        /// work alone blew the frame budget, then resets.
        /// </summary>
        public static void EndTick(int tick, double wholeImportMs)
        {
            // A tick with no spawns can still stall - report that too, otherwise a stall whose
            // cause is NOT spawn building looks like silence and sends the reader hunting here.
            if (_tickCount == 0)
            {
                if (wholeImportMs >= ReportThresholdMs)
                {
                    Utility.Tools.Debugger.Instance?.Log(
                        $"[SpawnStall] tick {tick}: import took {wholeImportMs:F1} ms with NO spawns - "
                        + "the cost is property apply / change handlers, not scene building.",
                        Utility.Tools.Debugger.DebugLevel.WARN);
                }
                return;
            }

            var total = _tickLoadMs + _tickInstantiateMs;
            if (wholeImportMs >= ReportThresholdMs)
            {
                var sb = new StringBuilder(256);
                sb.Append("[SpawnStall] tick ").Append(tick).Append(": ")
                  .Append(_tickCount).Append(" spawn(s) cost ")
                  .Append(total.ToString("F1")).Append(" ms on the main thread (load ")
                  .Append(_tickLoadMs.ToString("F1")).Append(" ms + instantiate ")
                  .Append(_tickInstantiateMs.ToString("F1")).Append(" ms) of ")
                  .Append(wholeImportMs.ToString("F1")).Append(" ms total import.");
                foreach (var kv in _thisTick)
                {
                    sb.Append("\n    ").Append(kv.Key)
                      .Append(" x").Append(kv.Value.Count)
                      .Append(kv.Value.ColdLoads > 0 ? $" ({kv.Value.ColdLoads} COLD)" : "")
                      .Append(" load=").Append(kv.Value.LoadMs.ToString("F1"))
                      .Append("ms inst=").Append(kv.Value.InstantiateMs.ToString("F1")).Append("ms");
                }
                // A cold load names the fix directly: mark the scene [Nebula.Preload] so the
                // dependency graph is paid for at a loading screen instead of mid-play.
                Utility.Tools.Debugger.Instance?.Log(sb.ToString(),
                    Utility.Tools.Debugger.DebugLevel.WARN);
            }

            _thisTick.Clear();
            _tickLoadMs = 0;
            _tickInstantiateMs = 0;
            _tickCount = 0;
        }
    }
}
