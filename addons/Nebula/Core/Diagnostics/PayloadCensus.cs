using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nebula.Serialization.Serializers;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Per-property byte accounting for the tick payload, so "the packet is full" can be
    /// answered with WHAT is filling it rather than a guess.
    ///
    /// <para>Off unless <c>NEBULA_CENSUS</c> is set. Deliberately NOT allocation-free — it
    /// exists to be switched on for one measuring run, not to ride production. It does
    /// allocate on the record path (dictionary growth, and the boxed key on first sight of
    /// a property), so read tick timings from a run with it OFF.</para>
    ///
    /// <para>Emits one <c>NEBULA_CENSUS</c> line per interval listing the heaviest
    /// properties by total bytes across all peers in the window, with a per-tick-per-peer
    /// average so entries compare directly against the payload cap.</para>
    /// </summary>
    public static class PayloadCensus
    {
        public const string EnableEnvVar = "NEBULA_CENSUS";
        public const string LinePrefix = "NEBULA_CENSUS ";

        /// <summary>How many of the heaviest properties to list.</summary>
        private const int TopN = 40;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

        private sealed class Entry
        {
            /// <summary>Wire bits (the props section is bit-packed); reported as bytes.</summary>
            public long Bits;
            public long Writes;
            public long DeltaWrites;
        }

        private static readonly Dictionary<string, Entry> _entries = new();
        private static readonly StringBuilder _line = new(2048);
        private static int _peerSections;

        /// <summary>Records one property write of <paramref name="bits"/> wire bits. <paramref name="key"/> is scene-scoped.</summary>
        public static void Record(string key, int bits, bool delta)
        {
            if (bits <= 0) return;
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }
                entry.Bits += bits;
                entry.Writes++;
                if (delta) entry.DeltaWrites++;
            }
        }

        /// <summary>Counts one per-peer props section, for the per-section average.</summary>
        public static void RecordSection()
        {
            lock (_entries) { _peerSections++; }
        }

        // ─── Gameplay time census ────────────────────────────────────────────
        //
        // The payload answers "what is on the wire"; this answers "what is the
        // tick spending itself on", which the phase profiler only reports as one
        // lump called `gameplay`. Keyed by scene file path, so it names the scene
        // to go look at rather than a node instance.

        private sealed class TimeEntry
        {
            public long Ticks;
            public long Calls;
        }

        private static readonly Dictionary<string, TimeEntry> _times = new();

        /// <summary>Charges one node's _NetworkProcess to its scene. Stopwatch ticks.</summary>
        public static void RecordGameplay(string scenePath, long elapsedTicks)
        {
            if (string.IsNullOrEmpty(scenePath)) scenePath = "(no scene)";
            lock (_times)
            {
                if (!_times.TryGetValue(scenePath, out var entry))
                {
                    entry = new TimeEntry();
                    _times[scenePath] = entry;
                }
                entry.Ticks += elapsedTicks;
                entry.Calls++;
            }
        }

        private static void EmitGameplay(int ticksInWindow)
        {
            lock (_times)
            {
                if (_times.Count == 0) return;

                var ordered = new List<KeyValuePair<string, TimeEntry>>(_times);
                ordered.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));

                long total = 0;
                foreach (var kv in ordered) total += kv.Value.Ticks;
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                var sb = new StringBuilder(2048);
                sb.Append("NEBULA_GAMEPLAY {\"ticks\":").Append(ticksInWindow.ToString(Inv));
                sb.Append(",\"total_ms_per_tick\":")
                  .Append((ticksInWindow > 0 ? total * toMs / ticksInWindow : 0).ToString("F3", Inv));
                sb.Append(",\"top\":[");
                int listed = 0;
                foreach (var kv in ordered)
                {
                    if (listed >= TopN) break;
                    if (listed > 0) sb.Append(',');
                    sb.Append("{\"scene\":\"").Append(kv.Key).Append('"');
                    sb.Append(",\"ms_per_tick\":")
                      .Append((ticksInWindow > 0 ? kv.Value.Ticks * toMs / ticksInWindow : 0).ToString("F3", Inv));
                    sb.Append(",\"pct\":")
                      .Append((total > 0 ? kv.Value.Ticks * 100.0 / total : 0).ToString("F1", Inv));
                    sb.Append(",\"calls_per_tick\":")
                      .Append((ticksInWindow > 0 ? kv.Value.Calls / (double)ticksInWindow : 0).ToString("F1", Inv));
                    sb.Append(",\"us_per_call\":")
                      .Append((kv.Value.Calls > 0 ? kv.Value.Ticks * toMs * 1000.0 / kv.Value.Calls : 0).ToString("F1", Inv));
                    sb.Append('}');
                    listed++;
                }
                sb.Append("]}");
                Godot.GD.Print(sb.ToString());

                _times.Clear();
            }
        }

        /// <summary>Writes the window report to stdout and resets. Called once per interval.</summary>
        public static void Emit(int peers, double elapsedSeconds, int ticksInWindow)
        {
            EmitGameplay(ticksInWindow);

            lock (_entries)
            {
                if (_entries.Count == 0) return;

                var ordered = new List<KeyValuePair<string, Entry>>(_entries);
                ordered.Sort((a, b) => b.Value.Bits.CompareTo(a.Value.Bits));

                long totalBits = 0;
                foreach (var kv in ordered) totalBits += kv.Value.Bits;
                double total = totalBits / (double)BitConstants.BitsInByte;

                _line.Clear();
                _line.Append(LinePrefix);
                _line.Append("{\"peers\":").Append(peers.ToString(Inv));
                _line.Append(",\"window_s\":").Append(elapsedSeconds.ToString("F2", Inv));
                _line.Append(",\"sections\":").Append(_peerSections.ToString(Inv));
                _line.Append(",\"total_bytes\":").Append(total.ToString("F1", Inv));
                // Bytes of property payload per peer per second: the bandwidth number,
                // before framing and before pack compression.
                double perPeerSec = peers > 0 && elapsedSeconds > 0
                    ? total / (double)peers / elapsedSeconds : 0;
                _line.Append(",\"prop_bytes_per_peer_s\":").Append(perPeerSec.ToString("F0", Inv));
                _line.Append(",\"top\":[");

                int listed = 0;
                foreach (var kv in ordered)
                {
                    if (listed >= TopN) break;
                    if (listed > 0) _line.Append(',');
                    var e = kv.Value;
                    _line.Append("{\"prop\":\"").Append(kv.Key).Append('"');
                    double bytes = e.Bits / (double)BitConstants.BitsInByte;
                    _line.Append(",\"bytes\":").Append(bytes.ToString("F1", Inv));
                    _line.Append(",\"pct\":")
                         .Append((totalBits > 0 ? e.Bits * 100.0 / totalBits : 0).ToString("F1", Inv));
                    _line.Append(",\"writes\":").Append(e.Writes.ToString(Inv));
                    _line.Append(",\"b_per_write\":")
                         .Append((e.Writes > 0 ? bytes / e.Writes : 0).ToString("F2", Inv));
                    // The share of writes that used delta encoding rather than an
                    // absolute. A hot property sitting at 0 means the delta gate is
                    // never opening for it.
                    _line.Append(",\"delta_pct\":")
                         .Append((e.Writes > 0 ? e.DeltaWrites * 100.0 / e.Writes : 0).ToString("F0", Inv));
                    _line.Append('}');
                    listed++;
                }
                _line.Append("]}");

                Godot.GD.Print(_line.ToString());

                _entries.Clear();
                _peerSections = 0;
            }
        }
    }
}
