using System;
using NebulaDebugger = Nebula.Utility.Tools.Debugger;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Names main-thread work that took long enough to cost a frame.
    ///
    /// The problem this exists for: a client hitch is invisible in a log. You can see that a frame
    /// took 133ms -- <c>WorldTransition</c> already reports that -- but not what ran in it, and
    /// guessing costs a round trip each time. Wrapping a suspect region in one of these turns the
    /// next run into an answer:
    ///
    /// <code>
    /// using (MainThreadWork.Time("ChangeScene"))
    /// {
    ///     // whatever might be eating the frame
    /// }
    /// </code>
    ///
    /// Silent unless the region actually exceeds <see cref="ReportThresholdMs"/>, so wrapped code
    /// costs one Stopwatch and says nothing on a healthy run. Deliberately NOT a sampling profiler:
    /// this repo has already learned that dotnet-trace's frames on this workload are artifacts, and a
    /// timer around a named region cannot lie about what it measured.
    ///
    /// Distinct from <see cref="TickProfiler"/>, which breaks down a SERVER tick by phase. This is
    /// about a client frame that failed to draw.
    /// </summary>
    public static class MainThreadWork
    {
        /// <summary>
        /// A region at or past this many milliseconds has cost a visible frame at 60Hz and is worth a
        /// line. Well above scheduling noise, so a healthy run stays silent.
        /// </summary>
        public const long ReportThresholdMs = 30;

        /// <summary>Starts timing a named region. Dispose (or let <c>using</c> do it) to report.</summary>
        public static Scope Time(string name) => new(name);

        /// <summary>
        /// The timing scope. A struct, so a wrapped region allocates nothing on the path it is
        /// measuring -- which matters, because that path is one already suspected of stalling.
        /// </summary>
        public readonly struct Scope : IDisposable
        {
            private readonly string _name;
            private readonly long _startMs;
            private readonly Stopwatch _clock;

            internal Scope(string name)
            {
                _name = name;
                _clock = Stopwatch.StartNew();
                _startMs = 0;
            }

            public void Dispose()
            {
                if (_clock == null) return;

                var elapsed = _clock.ElapsedMilliseconds - _startMs;
                if (elapsed < ReportThresholdMs) return;

                // INFO, not WARN: this is diagnostic narration, and the editor puts warnings behind a
                // separate panel that has to be collected by hand. It belongs in the log everyone
                // already reads.
                NebulaDebugger.Instance?.Log(
                    $"[MainThread] {_name} held the main thread for {elapsed}ms -- that is a frame nobody drew.");
            }
        }
    }
}
