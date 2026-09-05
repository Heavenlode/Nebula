using Nebula.Diagnostics;
using Nebula.Testing.Unit;
using Xunit;

namespace Nebula.Tests
{
    /// <summary>
    /// The synthetic impairment scheduler. Pure and clock-injected, so all of this runs with no
    /// sockets, no processes and no wall-clock waiting.
    /// </summary>
    [NebulaUnitTest]
    public class NetworkImpairmentTests
    {
        private const byte TickChannel = (byte)NetRunner.ENetChannelId.Tick;
        private const byte FunctionChannel = (byte)NetRunner.ENetChannelId.Function;
        private const byte InputChannel = (byte)NetRunner.ENetChannelId.Input;

        [NebulaUnitTest]
        public void AnUnconfiguredImpairmentIsInertAndFree()
        {
            var impairment = NetworkImpairment.ForTest(0, 0, 0, seed: 1);

            Assert.False(impairment.IsActive);
            Assert.True(impairment.TryScheduleInbound(TickChannel, 1000, out var releaseAt));
            Assert.Equal(1000ul, releaseAt);
        }

        /// <summary>With no jitter, delay is a constant offset -- so packets stay in the order they
        /// arrived and simply land later.</summary>
        [NebulaUnitTest]
        public void PureLatencyPreservesOrder()
        {
            var impairment = NetworkImpairment.ForTest(latencyMs: 80, jitterMs: 0, lossPct: 0, seed: 1);

            ulong previous = 0;
            for (ulong now = 0; now < 1000; now += 33)
            {
                Assert.True(impairment.TryScheduleInbound(TickChannel, now, out var releaseAt));
                Assert.Equal(now + 80, releaseAt);
                Assert.True(releaseAt >= previous);
                previous = releaseAt;
            }
        }

        /// <summary>
        /// Reordering is not a separate knob -- it is what jitter DOES. This asserts the mechanism
        /// exists rather than that any particular packet overtakes another.
        /// </summary>
        [NebulaUnitTest]
        public void JitterProducesOutOfOrderRelease()
        {
            var impairment = NetworkImpairment.ForTest(latencyMs: 80, jitterMs: 40, lossPct: 0, seed: 7);

            bool sawOvertake = false;
            ulong previous = 0;
            for (ulong now = 0; now < 3000; now += 33)
            {
                Assert.True(impairment.TryScheduleInbound(TickChannel, now, out var releaseAt));

                // Never earlier than the moment it arrived: impairment may only hold a packet back.
                Assert.True(releaseAt >= now);

                if (releaseAt < previous) sawOvertake = true;
                previous = releaseAt;
            }

            Assert.True(sawOvertake, "jitter of +/-40ms against a 33ms cadence should reorder");
        }

        /// <summary>
        /// THE RULE THAT IS CORRECTNESS, NOT TASTE. Reliable channels have already been delivered by
        /// ENet; dropping one here loses it permanently and breaks the protocol rather than simulating
        /// a network. The unreliable ones -- Tick and Input -- are not retransmitted by ENet either,
        /// so both really can vanish on a live link.
        /// </summary>
        [NebulaUnitTest]
        public void OnlyUnreliableChannelsAreDropped()
        {
            var impairment = NetworkImpairment.ForTest(latencyMs: 0, jitterMs: 0, lossPct: 100, seed: 3);

            Assert.True(NetworkImpairment.IsDroppable(TickChannel));
            Assert.True(NetworkImpairment.IsDroppable(InputChannel));
            Assert.False(NetworkImpairment.IsDroppable(FunctionChannel));

            for (ulong now = 0; now < 500; now += 33)
            {
                Assert.True(impairment.TryScheduleInbound(FunctionChannel, now, out _));
                Assert.True(impairment.TrySendOutbound(FunctionChannel, now));

                // ...while the droppable ones at 100% always are, in both directions.
                Assert.False(impairment.TryScheduleInbound(TickChannel, now, out _));
                Assert.False(impairment.TrySendOutbound(InputChannel, now));
            }
        }

        /// <summary>
        /// AN OUTAGE HAS TO TAKE OUT BOTH DIRECTIONS. Impairment is per process so one bad client can
        /// sit beside a healthy one, which leaves the server unimpaired -- so ingress filtering alone
        /// let a client sail through a "100% loss" burst still delivering flawless input. That is not
        /// an outage, and it hid the server's hold-last-input fallback from ever being exercised.
        /// </summary>
        [NebulaUnitTest]
        public void ABurstStopsInputAsWellAsTicks()
        {
            var impairment = NetworkImpairment.ForTest(
                latencyMs: 0, jitterMs: 0, lossPct: 0, seed: 21,
                burstLossPct: 100, burstEverySec: 1f, burstMs: 300);

            int inputRun = 0;
            int longestInputRun = 0;

            for (ulong now = 0; now < 30_000; now += 33)
            {
                if (impairment.TrySendOutbound(InputChannel, now))
                {
                    inputRun = 0;
                }
                else
                {
                    inputRun++;
                    if (inputRun > longestInputRun) longestInputRun = inputRun;
                }
            }

            // Input packets carry INPUT_REDUNDANCY_COUNT (8) ticks of history, so an outage only
            // actually starves the server once the run of lost packets approaches that window --
            // which is exactly what a 300ms burst at 33ms spacing must produce.
            Assert.True(longestInputRun >= 5,
                $"longest run of dropped input was {longestInputRun}; a burst must interrupt input");
        }

        /// <summary>An unconfigured impairment must not touch the send path at all.</summary>
        [NebulaUnitTest]
        public void AnInertImpairmentSendsEverything()
        {
            var impairment = NetworkImpairment.ForTest(0, 0, 0, seed: 1);

            for (ulong now = 0; now < 1000; now += 33)
            {
                Assert.True(impairment.TrySendOutbound(InputChannel, now));
                Assert.True(impairment.TrySendOutbound(TickChannel, now));
            }
        }

        [NebulaUnitTest]
        public void LossConvergesOnTheConfiguredRate()
        {
            var impairment = NetworkImpairment.ForTest(latencyMs: 0, jitterMs: 0, lossPct: 25, seed: 11);

            const int samples = 20_000;
            int dropped = 0;
            for (int i = 0; i < samples; i++)
            {
                if (!impairment.TryScheduleInbound(TickChannel, (ulong)i, out _)) dropped++;
            }

            double rate = 100.0 * dropped / samples;
            Assert.True(System.Math.Abs(rate - 25.0) < 2.0, $"observed {rate:F1}% loss, expected ~25%");
        }

        // ─── Bursty loss ─────────────────────────────────────────────────────
        //
        // The reason this exists at all: independent per-packet loss is the FRIENDLY case. Real links
        // lose runs of consecutive packets, and a run is what actually empties an interpolation
        // buffer. 2% uniform loss barely dents it; 100% for 300ms is a hole.

        /// <summary>
        /// A burst must actually produce a RUN of consecutive drops, not scattered ones. This is the
        /// whole difference from steady loss, so it is asserted directly rather than statistically.
        /// </summary>
        [NebulaUnitTest]
        public void ABurstDropsConsecutivePackets()
        {
            var impairment = NetworkImpairment.ForTest(
                latencyMs: 0, jitterMs: 0, lossPct: 0, seed: 5,
                burstLossPct: 100, burstEverySec: 1f, burstMs: 300);

            int longestRun = 0;
            int run = 0;

            // 30 seconds at a 33ms tick cadence.
            for (ulong now = 0; now < 30_000; now += 33)
            {
                if (impairment.TryScheduleInbound(TickChannel, now, out _))
                {
                    run = 0;
                }
                else
                {
                    run++;
                    if (run > longestRun) longestRun = run;
                }
            }

            // A 300ms burst at 33ms spacing is ~9 packets. Anything above a handful proves the drops
            // are contiguous rather than independent.
            Assert.True(longestRun >= 5, $"longest consecutive drop run was {longestRun}, expected a burst");
        }

        /// <summary>Between bursts the link must be clean, or this is just steady loss with extra
        /// steps.</summary>
        [NebulaUnitTest]
        public void TheLinkIsCleanBetweenBursts()
        {
            var impairment = NetworkImpairment.ForTest(
                latencyMs: 0, jitterMs: 0, lossPct: 0, seed: 9,
                burstLossPct: 100, burstEverySec: 5f, burstMs: 200);

            int delivered = 0;
            int dropped = 0;
            for (ulong now = 0; now < 60_000; now += 33)
            {
                if (impairment.TryScheduleInbound(TickChannel, now, out _)) delivered++;
                else dropped++;
            }

            // ~200ms of every ~5s is lost, so the overwhelming majority must get through.
            Assert.True(delivered > dropped * 5,
                $"delivered {delivered} vs dropped {dropped}; bursts should be occasional, not constant");
            Assert.True(dropped > 0, "no burst ever fired");
        }

        /// <summary>Bursts must not fire on the very first packet: a session should not begin
        /// mid-fault, which would make every startup measurement a burst measurement.</summary>
        [NebulaUnitTest]
        public void ASessionDoesNotBeginInsideABurst()
        {
            var impairment = NetworkImpairment.ForTest(
                latencyMs: 0, jitterMs: 0, lossPct: 0, seed: 13,
                burstLossPct: 100, burstEverySec: 5f, burstMs: 200);

            Assert.True(impairment.TryScheduleInbound(TickChannel, 0, out _));
            Assert.False(impairment.InBurst);
        }

        /// <summary>Even at 100% burst loss, a reliable channel is never dropped -- the same rule as
        /// steady loss, and for the same reason. In BOTH directions: egress is where a reliable drop
        /// would be easiest to introduce by accident, since nothing has been delivered yet.</summary>
        [NebulaUnitTest]
        public void BurstsStillNeverDropReliableChannels()
        {
            var impairment = NetworkImpairment.ForTest(
                latencyMs: 0, jitterMs: 0, lossPct: 0, seed: 17,
                burstLossPct: 100, burstEverySec: 1f, burstMs: 500);

            for (ulong now = 0; now < 20_000; now += 33)
            {
                Assert.True(impairment.TryScheduleInbound(FunctionChannel, now, out _));
                Assert.True(impairment.TrySendOutbound(FunctionChannel, now));
            }
        }

        /// <summary>Bursts are off unless both a loss level and a duration are configured, so the
        /// knobs cannot half-arm each other.</summary>
        [NebulaUnitTest]
        public void BurstsAreOffUnlessFullyConfigured()
        {
            var noDuration = NetworkImpairment.ForTest(0, 0, 0, seed: 1, burstLossPct: 100, burstMs: 0);
            Assert.False(noDuration.IsActive);

            var noLoss = NetworkImpairment.ForTest(0, 0, 0, seed: 1, burstLossPct: 0, burstMs: 500);
            Assert.False(noLoss.IsActive);

            var armed = NetworkImpairment.ForTest(0, 0, 0, seed: 1, burstLossPct: 100, burstMs: 500);
            Assert.True(armed.IsActive);
        }

        // ─── Fidelity ────────────────────────────────────────────────────────

        /// <summary>
        /// THE IMPAIRMENT MUST SIMULATE A LINK THAT COULD EXIST. ENet measures RTT inside the native
        /// transport and this layer holds packets after delivery, so the peer keeps reporting loopback
        /// latency however severe the configuration is. Anything sizing itself from RTT -- the
        /// prediction lead above all -- then aims for a link nobody is on: measured as an impaired
        /// client targeting a two-tick lead while its view of the server ran 2.4 ticks behind, so every
        /// input it sent was stamped for a tick the server had already simulated.
        /// </summary>
        [NebulaUnitTest]
        public void ConfiguredLatencyIsVisibleToRttConsumers()
        {
            var impaired = NetworkImpairment.ForTest(latencyMs: 80, jitterMs: 30, lossPct: 0, seed: 1);
            var inert = NetworkImpairment.ForTest(0, 0, 0, seed: 1);

            // An unconfigured impairment changes nothing.
            Assert.Equal(20u, WorldRunner.EffectiveRoundTripMs(20, inert.LatencyMs));

            // The transport's own reading is kept, not replaced: a real link's RTT and a synthetic
            // hold are both really in the path.
            Assert.Equal(80u, WorldRunner.EffectiveRoundTripMs(0, impaired.LatencyMs));
            Assert.Equal(100u, WorldRunner.EffectiveRoundTripMs(20, impaired.LatencyMs));

            // ...and that has to be enough to move the lead, or reporting it changes nothing. 80ms is
            // 2.4 ticks at 30 TPS, which is exactly the shortfall that starved the server of input.
            int loopback = WorldRunner.ComputeTargetLeadTicks(
                WorldRunner.EffectiveRoundTripMs(0, inert.LatencyMs), NetRunner.TPS);
            int simulated = WorldRunner.ComputeTargetLeadTicks(
                WorldRunner.EffectiveRoundTripMs(0, impaired.LatencyMs), NetRunner.TPS);

            Assert.True(simulated >= loopback + 3,
                $"an 80ms hold moved the target lead only {simulated - loopback} ticks ({loopback} -> {simulated})");
        }

        /// <summary>A failing impairment run is worthless if it cannot be repeated, so the seed has to
        /// fully determine the sequence.</summary>
        [NebulaUnitTest]
        public void AFixedSeedReproducesExactly()
        {
            var first = NetworkImpairment.ForTest(60, 30, 15, seed: 4242);
            var second = NetworkImpairment.ForTest(60, 30, 15, seed: 4242);

            for (ulong now = 0; now < 2000; now += 33)
            {
                bool keptA = first.TryScheduleInbound(TickChannel, now, out var releaseA);
                bool keptB = second.TryScheduleInbound(TickChannel, now, out var releaseB);

                Assert.Equal(keptA, keptB);
                Assert.Equal(releaseA, releaseB);
            }
        }
    }
}
