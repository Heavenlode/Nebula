using Godot;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Synthetic network impairment for inbound packets: added latency, jitter, and loss.
    ///
    /// <para>WHY THIS EXISTS. Everything about the render clock and the interpolation buffer was
    /// developed and measured on localhost, where arrival jitter is 19-43ms against a 33.3ms nominal
    /// and loss is zero. Those are the conditions under which the OLD render clock also looked fine --
    /// its uneven stepping only became obvious once it was measured properly. A jitter buffer that has
    /// never seen jitter is a guess, so this makes a bad link something that can be produced on
    /// purpose rather than waited for.</para>
    ///
    /// <para>Pure and clock-injected: it decides, given a moment and a packet, whether the packet is
    /// dropped and when it should be released. The queueing and the pool bookkeeping belong to the two
    /// call sites, which differ (the server already has an inbound ring; the client does not).</para>
    ///
    /// <para>Inert unless configured, and configured per PROCESS so a single play session can run one
    /// healthy client beside one bad one -- which is the case that matters, because an impaired peer is
    /// only interesting when a healthy observer is watching it.</para>
    /// </summary>
    public sealed class NetworkImpairment
    {
        // Per-client, so these must be command-line capable: a project setting is process-global and
        // would apply identically to every instance the Play tab spawns. Env vars cover the
        // headless/CI case, project settings the editor default. Same three-tier shape as
        // Env.Instance.InitialWorldScene.
        private const string LatencyArg = "--simLatencyMs=";
        private const string JitterArg = "--simJitterMs=";
        private const string LossArg = "--simLossPct=";
        private const string SeedArg = "--simSeed=";
        private const string BurstLossArg = "--simBurstLossPct=";
        private const string BurstEveryArg = "--simBurstEverySec=";
        private const string BurstMsArg = "--simBurstMs=";

        private const string LatencyEnvVar = "NEBULA_SIM_LATENCY_MS";
        private const string JitterEnvVar = "NEBULA_SIM_JITTER_MS";
        private const string LossEnvVar = "NEBULA_SIM_LOSS_PCT";
        private const string SeedEnvVar = "NEBULA_SIM_SEED";
        private const string BurstLossEnvVar = "NEBULA_SIM_BURST_LOSS_PCT";
        private const string BurstEveryEnvVar = "NEBULA_SIM_BURST_EVERY_SEC";
        private const string BurstMsEnvVar = "NEBULA_SIM_BURST_MS";

        private const string LatencySetting = "Nebula/config/debug/sim_latency_ms";
        private const string JitterSetting = "Nebula/config/debug/sim_jitter_ms";
        private const string LossSetting = "Nebula/config/debug/sim_loss_pct";
        private const string BurstLossSetting = "Nebula/config/debug/sim_burst_loss_pct";
        private const string BurstEverySetting = "Nebula/config/debug/sim_burst_every_sec";
        private const string BurstMsSetting = "Nebula/config/debug/sim_burst_ms";

        /// <summary>
        /// The setting the original client-side loss simulator used. Still honoured so existing
        /// project files and any muscle memory keep working; it feeds the same loss knob.
        /// </summary>
        private const string LegacyLossSetting = "Nebula/config/debug/simulate_incoming_tick_loss";

        /// <summary>One-way delay added to every packet, in milliseconds.</summary>
        public int LatencyMs { get; private set; }

        /// <summary>Random spread around <see cref="LatencyMs"/>, plus or minus, in milliseconds.</summary>
        public int JitterMs { get; private set; }

        /// <summary>Percentage of droppable packets discarded, 0-100. Applies steadily.</summary>
        public int LossPct { get; private set; }

        /// <summary>
        /// Loss percentage DURING a burst, 0-100. Zero disables bursts entirely.
        ///
        /// <para>Steady loss and burst loss model different things and both are worth having. Real
        /// links rarely lose packets independently -- a handover, a congestion event or interference
        /// takes out a RUN of consecutive packets, and a run leaves a hole that uniform loss almost
        /// never produces. 2% independent loss barely dents an interpolation buffer; 100% loss for
        /// 300ms empties it. Set this to 100 for a full dropout, which is what a connection hiccup
        /// looks like from the application's side.</para>
        /// </summary>
        public int BurstLossPct { get; private set; }

        /// <summary>Average seconds between burst onsets. The actual interval is drawn from an
        /// exponential distribution so bursts arrive like real faults rather than on a metronome.</summary>
        public float BurstEverySec { get; private set; }

        /// <summary>How long each burst lasts, in milliseconds.</summary>
        public int BurstMs { get; private set; }

        /// <summary>Whether anything here will actually do something. Callers skip the whole path when
        /// false, so an unconfigured build pays a single bool test.</summary>
        public bool IsActive =>
            LatencyMs > 0 || JitterMs > 0 || LossPct > 0 || (BurstLossPct > 0 && BurstMs > 0);

        /// <summary>Whether a burst is in progress as of the last scheduling decision. Exposed so the
        /// health readout can say WHY a window looked bad.</summary>
        public bool InBurst { get; private set; }

        private ulong _burstUntilMsec;
        private ulong _nextBurstAtMsec;

        private readonly RandomNumberGenerator _rng = new();

        /// <summary>
        /// Builds an impairment from the process's own configuration: command line first (per
        /// instance), then environment, then project setting.
        /// </summary>
        public static NetworkImpairment FromProcessConfig()
        {
            var impairment = new NetworkImpairment
            {
                LatencyMs = ResolveInt(LatencyArg, LatencyEnvVar, LatencySetting, 0, 0, 10_000),
                JitterMs = ResolveInt(JitterArg, JitterEnvVar, JitterSetting, 0, 0, 10_000),
                LossPct = ResolveInt(LossArg, LossEnvVar, LossSetting, LegacyLoss(), 0, 100),
                BurstLossPct = ResolveInt(BurstLossArg, BurstLossEnvVar, BurstLossSetting, 0, 0, 100),
                BurstEverySec = ResolveInt(BurstEveryArg, BurstEveryEnvVar, BurstEverySetting, 10, 1, 3600),
                BurstMs = ResolveInt(BurstMsArg, BurstMsEnvVar, BurstMsSetting, 0, 0, 10_000),
            };

            // Seeded so a bad run reproduces. Without this an impairment test that fails once cannot
            // be re-run, which is most of the value of having it.
            int seed = ResolveInt(SeedArg, SeedEnvVar, null, 0, int.MinValue, int.MaxValue);
            if (seed != 0) impairment._rng.Seed = (ulong)seed;
            else impairment._rng.Randomize();

            return impairment;
        }

        /// <summary>Test seam: an impairment with explicit values and a fixed seed.</summary>
        public static NetworkImpairment ForTest(
            int latencyMs, int jitterMs, int lossPct, ulong seed,
            int burstLossPct = 0, float burstEverySec = 10f, int burstMs = 0)
        {
            var impairment = new NetworkImpairment
            {
                LatencyMs = latencyMs,
                JitterMs = jitterMs,
                LossPct = lossPct,
                BurstLossPct = burstLossPct,
                BurstEverySec = burstEverySec,
                BurstMs = burstMs,
            };
            impairment._rng.Seed = seed;
            return impairment;
        }

        /// <summary>
        /// Whether a packet on <paramref name="channel"/> may be discarded.
        ///
        /// <para>THE UNRELIABLE CHANNELS ONLY -- Tick (Unsequenced) and Input (None). Neither is
        /// retransmitted by ENet, so both really can vanish on a live link and the protocol already
        /// has to survive it: ticks are re-sent as newer ticks, and input packets carry
        /// INPUT_REDUNDANCY_COUNT ticks of history precisely so a lost one is recoverable.</para>
        ///
        /// <para>Function and World are Reliable, and ENet has ALREADY delivered them by the time
        /// they reach this layer. Discarding one here does not simulate a lost packet -- it loses it
        /// permanently, which no retransmission can recover, and breaks the protocol rather than
        /// stressing it.</para>
        /// </summary>
        public static bool IsDroppable(byte channel) =>
            channel == (byte)NetRunner.ENetChannelId.Tick
            || channel == (byte)NetRunner.ENetChannelId.Input;

        /// <summary>
        /// Decides a packet's fate.
        ///
        /// <para>Returns false when the packet is dropped. Otherwise <paramref name="releaseAtMsec"/>
        /// is when it should be delivered -- never earlier than <paramref name="nowMsec"/>, so an
        /// impairment can only ever hold a packet back.</para>
        ///
        /// <para>REORDERING NEEDS NO KNOB and is not simulated separately: jitter alone releases
        /// packets out of the order they arrived, which is how reordering happens on a real link. Note
        /// what that means on the Tick channel specifically -- it is sent Unsequenced and ordering is
        /// enforced in software by <c>ClientProcessTick</c>'s "ignore ticks at or behind the current
        /// one" guard, so a tick that arrives late is DISCARDED. Reordering therefore presents as
        /// loss there, which is also what happens in production.</para>
        /// </summary>
        public bool TryScheduleInbound(byte channel, ulong nowMsec, out ulong releaseAtMsec)
        {
            releaseAtMsec = nowMsec;
            if (!IsActive) return true;

            // The RNG and the burst state are mutable and now reached from two threads: ingress from
            // the network pump, egress from a world's tick thread (see NetRunner.SendPacket's
            // EnetLock note). IsActive is fixed at construction, so an unconfigured build never
            // reaches this lock.
            lock (_gate)
            {
                int lossPct = AdvanceBurstState(nowMsec) ? BurstLossPct : LossPct;

                if (lossPct > 0 && IsDroppable(channel) && _rng.RandiRange(1, 100) <= lossPct)
                {
                    return false;
                }

                int delay = LatencyMs;
                if (JitterMs > 0) delay += _rng.RandiRange(-JitterMs, JitterMs);
                if (delay > 0) releaseAtMsec = nowMsec + (ulong)delay;
                return true;
            }
        }

        /// <summary>Guards the RNG and burst state; see TryScheduleInbound.</summary>
        private readonly object _gate = new();

        /// <summary>
        /// Whether an outgoing packet survives the link.
        ///
        /// <para>AN OUTAGE HAS TO BE BIDIRECTIONAL, which ingress alone cannot express. Impairment is
        /// configured per process so one bad client can sit beside a healthy one, and that means the
        /// SERVER is unimpaired -- so with ingress-only filtering a client sailed through a "100%
        /// loss" burst still delivering perfect input, which is not an outage at all. It also hid a
        /// whole class of bug: the server holds the last-known input across a gap, so an outage that
        /// never interrupts input never exercises that fallback.</para>
        ///
        /// <para>Loss only, deliberately. The configured latency is modelled as a single one-way hop
        /// on the RECEIVE path -- which is what <c>WorldRunner.EffectiveRoundTripMs</c> accounts for --
        /// so delaying here as well would silently double it. Loss is the part of an outage that has
        /// no direction.</para>
        ///
        /// <para>Shares the burst state machine with <see cref="TryScheduleInbound"/> on purpose: the
        /// same outage must take out both directions, not two independently-rolled ones.</para>
        /// </summary>
        public bool TrySendOutbound(byte channel, ulong nowMsec)
        {
            if (!IsActive) return true;
            if (!IsDroppable(channel)) return true;

            lock (_gate)
            {
                int lossPct = AdvanceBurstState(nowMsec) ? BurstLossPct : LossPct;
                return !(lossPct > 0 && _rng.RandiRange(1, 100) <= lossPct);
            }
        }

        /// <summary>
        /// Advances the good/bad link state and reports whether a burst is currently in progress.
        ///
        /// <para>A two-state model, which is the standard way bursty loss is described: the link sits
        /// in a good state for an exponentially-distributed while, then drops into a bad state for a
        /// fixed span. Exponential intervals matter -- a burst every N seconds ON THE DOT is a pattern
        /// the rest of the system could accidentally sync with, and real faults do not schedule
        /// themselves.</para>
        ///
        /// <para>Driven entirely off the caller's clock, so it is deterministic under a fixed seed and
        /// testable without waiting in real time.</para>
        /// </summary>
        private bool AdvanceBurstState(ulong nowMsec)
        {
            if (BurstLossPct <= 0 || BurstMs <= 0)
            {
                InBurst = false;
                return false;
            }

            if (_nextBurstAtMsec == 0)
            {
                // First call: schedule the opening burst rather than firing one immediately, so a
                // session does not begin mid-fault.
                _nextBurstAtMsec = nowMsec + NextBurstDelayMsec();
            }

            if (InBurst && nowMsec >= _burstUntilMsec)
            {
                InBurst = false;
                _nextBurstAtMsec = nowMsec + NextBurstDelayMsec();
            }
            else if (!InBurst && nowMsec >= _nextBurstAtMsec)
            {
                InBurst = true;
                _burstUntilMsec = nowMsec + (ulong)BurstMs;
            }

            return InBurst;
        }

        /// <summary>
        /// Time to the next burst, drawn from an exponential distribution with mean
        /// <see cref="BurstEverySec"/> -- the memoryless spacing of a Poisson process, which is how
        /// independent faults actually arrive.
        /// </summary>
        private ulong NextBurstDelayMsec()
        {
            // Guard the tail: Randf can return values arbitrarily close to 1, and log(0) is infinite.
            float u = Mathf.Clamp(1f - _rng.Randf(), 0.0001f, 1f);
            float seconds = -BurstEverySec * Mathf.Log(u);
            return (ulong)Mathf.Max(seconds * 1000f, 0f);
        }

        /// <summary>Describes the configuration for a startup log line, so a run's conditions are
        /// recoverable from its output.</summary>
        public override string ToString()
            => BurstLossPct > 0 && BurstMs > 0
                ? $"latency={LatencyMs}ms jitter=+/-{JitterMs}ms loss={LossPct}% "
                  + $"burst={BurstLossPct}%/{BurstMs}ms every~{BurstEverySec:F0}s"
                : $"latency={LatencyMs}ms jitter=+/-{JitterMs}ms loss={LossPct}%";

        private static int LegacyLoss()
            => (int)ProjectSettings.GetSetting(LegacyLossSetting, 0).AsInt32();

        private static int ResolveInt(
            string arg, string envVar, string setting, int fallback, int min, int max)
        {
            foreach (var argument in OS.GetCmdlineArgs())
            {
                if (!argument.StartsWith(arg)) continue;
                if (int.TryParse(argument.Substring(arg.Length), out int fromArg))
                    return Mathf.Clamp(fromArg, min, max);
            }

            if (OS.HasEnvironment(envVar)
                && int.TryParse(OS.GetEnvironment(envVar), out int fromEnv))
            {
                return Mathf.Clamp(fromEnv, min, max);
            }

            if (setting != null)
            {
                int fromSetting = ProjectSettings.GetSetting(setting, fallback).AsInt32();
                return Mathf.Clamp(fromSetting, min, max);
            }

            return Mathf.Clamp(fallback, min, max);
        }
    }
}
