using Nebula.Testing.Unit;
using Xunit;

namespace Nebula.Tests
{
    /// <summary>
    /// The adaptive jitter-buffer policy: how deep a buffer remote entities are interpolated through.
    ///
    /// <para>Too shallow and the render clock outruns the data — <c>GetInterpolationSnapshots</c> takes
    /// its hold-last branch and the entity freezes until more arrives. Too deep and everything is
    /// needlessly late, on every single frame, forever. The policy is pure so it can be tested without
    /// a network, the same way the render clock is.</para>
    ///
    /// <para>Sizing is by PERCENTILE of the observed arrival-gap distribution rather than by reacting
    /// to individual starvations, and these tests are mostly about why. The reactive version could not
    /// settle against a repeating fault: measured against a 200ms burst every 8s it sawtoothed between
    /// 3 and 6 ticks indefinitely, never deep enough to help and never cheap enough to stop paying
    /// for.</para>
    /// </summary>
    [NebulaUnitTest]
    public class InterpolationDelayTests
    {
        private const int Min = 2;
        private const int Max = 6;
        private const int CleanWindowsBeforeShrink = 5;
        private const float Coverage = 0.99f;

        private static int Next(int current, int target, int windowsBelowTarget)
            => WorldRunner.NextInterpolationDelay(current, target, windowsBelowTarget);

        private static int Target(int[] histogram)
            => WorldRunner.DelayForCoverage(histogram, Coverage, Min, Max);

        /// <summary>Builds a gap histogram: <c>(gapTicks, count)</c> pairs into buckets.</summary>
        private static int[] Gaps(params (int GapTicks, int Count)[] samples)
        {
            var histogram = new int[16];
            foreach (var (gapTicks, count) in samples) histogram[gapTicks] += count;
            return histogram;
        }

        // ─── Sizing from the distribution ────────────────────────────────────

        /// <summary>
        /// THE CASE THE REWRITE EXISTS FOR. A 200ms burst every 8s is ~0.4% of arrivals. Covering it
        /// would cost 100ms of permanent lag on every frame to improve four seconds in a thousand, so
        /// a 99th percentile must step straight over it and leave the buffer where ordinary traffic
        /// puts it.
        /// </summary>
        [NebulaUnitTest]
        public void ARareOutageDoesNotSizeTheBuffer()
        {
            // ~10s of a healthy 30 TPS link, plus one 8-tick hole per burst.
            var histogram = Gaps((1, 297), (8, 3));

            Assert.Equal(Min, Target(histogram));
        }

        /// <summary>
        /// ...but the same gap SIZE sizes the buffer once it stops being rare. This is the adaptation
        /// worth having, and the pair with the test above is the whole policy: the buffer reacts to how
        /// OFTEN a gap happens, not to how big the worst one was.
        /// </summary>
        [NebulaUnitTest]
        public void ACommonGapDoesSizeTheBuffer()
        {
            // The same 8-tick gap, now a tenth of all arrivals.
            var histogram = Gaps((1, 270), (8, 30));

            Assert.Equal(Max, Target(histogram));
        }

        /// <summary>Ordinary jitter — the continuous thing a jitter buffer is actually for — is
        /// covered rather than stepped over.</summary>
        [NebulaUnitTest]
        public void OrdinaryJitterIsCovered()
        {
            // A link that routinely runs a tick or two late, and rarely three.
            var histogram = Gaps((1, 200), (2, 80), (3, 19), (4, 1));

            Assert.Equal(3, Target(histogram));
        }

        /// <summary>
        /// The ceiling is not a taste choice: <c>NetworkController.SNAPSHOT_BUFFER_SIZE</c> is 8 per
        /// entity, so a deeper delay would point past the oldest snapshot still held. A link whose
        /// gaps exceed it pins here rather than reporting a depth that cannot be honoured.
        /// </summary>
        [NebulaUnitTest]
        public void ItPinsAtTheBufferCeilingRatherThanRunningPastIt()
        {
            Assert.Equal(Max, Target(Gaps((12, 300))));
        }

        /// <summary>The floor is the shipped default, and an unmeasured link reports it rather than
        /// guessing.</summary>
        [NebulaUnitTest]
        public void AnUnmeasuredLinkReportsTheDefault()
        {
            Assert.Equal(Min, Target(new int[16]));
            Assert.Equal(Min, Target(Gaps((1, 300))));
        }

        /// <summary>
        /// A gap of n ticks is absorbed by a buffer of n ticks, so the bucket index IS the depth. Worth
        /// pinning because an off-by-one here is invisible — it just leaves every link one tick
        /// under-buffered.
        /// </summary>
        [NebulaUnitTest]
        public void TheDepthMatchesTheGapItMustAbsorb()
        {
            // Every arrival exactly 4 ticks apart: 4 ticks of buffer covers it, 3 would not.
            Assert.Equal(4, Target(Gaps((4, 300))));
        }

        // ─── Moving toward the target ────────────────────────────────────────

        /// <summary>Under-buffering is visible the moment it happens, so it is approached at the
        /// fastest rate that stays continuous.</summary>
        [NebulaUnitTest]
        public void ItGrowsTowardTheTargetImmediately()
        {
            Assert.Equal(3, Next(current: 2, target: 3, windowsBelowTarget: 0));
            Assert.Equal(4, Next(current: 2, target: 4, windowsBelowTarget: 0));
        }

        /// <summary>
        /// A STEP THE RENDER CLOCK CANNOT FOLLOW IS A JUMP BACKWARD. The delay is part of what the
        /// clock aims at, so a resize larger than RenderClockResyncTicks re-seeds the clock at the next
        /// arrival and replays motion already drawn — the exact artifact the clock work removed.
        /// Measured as a -4.5 tick step when a four-tick growth landed in one window.
        /// </summary>
        [NebulaUnitTest]
        public void ItNeverStepsFurtherThanTheRenderClockCanAbsorb()
        {
            // Whatever the target, one window may not move the aim past the clock's re-seed threshold.
            for (int current = Min; current <= Max; current++)
            {
                int next = Next(current, target: 99, windowsBelowTarget: 0);
                Assert.True(next - current < 3,
                    $"grew {current} -> {next}, which re-seeds the render clock instead of slewing");
            }
        }

        /// <summary>
        /// Giving buffer back takes a sustained run below target. The asymmetry is the point:
        /// over-buffering only costs latency nobody can see directly, and a slow release keeps a link
        /// that is merely BETWEEN faults from being re-measured as healthy.
        /// </summary>
        [NebulaUnitTest]
        public void ShrinkingRequiresASustainedRunBelowTarget()
        {
            for (int window = 0; window < CleanWindowsBeforeShrink; window++)
            {
                Assert.Equal(4, Next(current: 4, target: 2, windowsBelowTarget: window));
            }

            Assert.Equal(3, Next(current: 4, target: 2, windowsBelowTarget: CleanWindowsBeforeShrink));
        }

        /// <summary>It gives back one tick at a time, never dropping straight to the target.</summary>
        [NebulaUnitTest]
        public void ItShrinksOneTickAtATime()
        {
            Assert.Equal(5, Next(current: 6, target: Min, windowsBelowTarget: 100));
        }

        [NebulaUnitTest]
        public void ItHoldsWhenItIsAlreadyAtTheTarget()
        {
            Assert.Equal(4, Next(current: 4, target: 4, windowsBelowTarget: 100));
        }

        [NebulaUnitTest]
        public void ItNeverLeavesTheUsableRange()
        {
            Assert.Equal(Min, Next(current: Min, target: Min, windowsBelowTarget: 100));
            Assert.Equal(Max, Next(current: Max, target: Max, windowsBelowTarget: 0));
        }

        // ─── Settling ────────────────────────────────────────────────────────

        /// <summary>
        /// THE REGRESSION THIS REPLACES, driven end to end. A repeating burst against a steady link
        /// must reach a depth and STAY there — the old policy sawtoothed 3↔6 forever on exactly this
        /// input, because it grew on any starvation and shrank on a fixed timer.
        /// </summary>
        [NebulaUnitTest]
        public void ARepeatingBurstSettlesInsteadOfHunting()
        {
            var histogram = Gaps((1, 297), (8, 3)); // steady link, one rare burst
            int delay = Min;
            int below = 0;

            int? settledAt = null;
            for (int window = 0; window < 60; window++)
            {
                int target = Target(histogram);
                below = target < delay ? below + 1 : 0;

                int next = Next(delay, target, below);
                if (next != delay) { below = 0; settledAt = null; } else settledAt ??= window;
                delay = next;
            }

            Assert.Equal(Min, delay);
            Assert.NotNull(settledAt);
        }

        /// <summary>A link that genuinely degrades and then recovers walks up and back down once,
        /// rather than oscillating on the way.</summary>
        [NebulaUnitTest]
        public void ItWalksUpOnceAndBackDownOnce()
        {
            int delay = Min;
            int below = 0;
            int changes = 0;

            // Degraded: 4-tick gaps are the norm.
            for (int window = 0; window < 20; window++)
            {
                int target = Target(Gaps((4, 300)));
                below = target < delay ? below + 1 : 0;
                int next = Next(delay, target, below);
                if (next != delay) { changes++; below = 0; }
                delay = next;
            }
            Assert.Equal(4, delay);
            Assert.Equal(1, changes); // one step of two ticks, not four separate ones

            // Recovered.
            for (int window = 0; window < 40; window++)
            {
                int target = Target(Gaps((1, 300)));
                below = target < delay ? below + 1 : 0;
                int next = Next(delay, target, below);
                if (next != delay) below = 0;
                delay = next;
            }
            Assert.Equal(Min, delay);
        }
    }
}
