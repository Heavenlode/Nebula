using System;
using Nebula.Testing.Unit;
using Xunit;

namespace Nebula.Tests
{
    /// <summary>
    /// The clock every REMOTE entity is interpolated on (<see cref="WorldRunner.GetRenderTick"/>).
    ///
    /// <para>It used to be derived fresh each call as "last received tick, plus how long ago it
    /// arrived". That is exact and stateless on a perfectly regular packet cadence -- the accumulator
    /// reaches one tick exactly as the tick counter increments -- but it couples render time to
    /// ARRIVAL, so jitter lands on the screen. The failure it produced was not a jump: measured at
    /// 50fps against 30Hz ticks, remote entities advanced between 0.4 and 1.0 ticks per frame instead
    /// of a constant 0.6, which reads as a shimmer.</para>
    ///
    /// <para>Each test below is one property that separates the working clock from a version that has
    /// already shipped broken.</para>
    /// </summary>
    [NebulaUnitTest]
    public class RenderClockTests
    {
        private const float Frame = 1f / 60f;
        private const int Tps = 30;

        private static (float Tick, float Error, int Sampled) Fresh(float tick)
            => (tick, 0f, int.MinValue);

        /// <summary>
        /// Drives the clock with a tick counter advancing on REAL time, optionally with arrival
        /// jitter, and returns every position it passed through.
        /// </summary>
        private static (float Tick, System.Collections.Generic.List<float> Steps) Run(
            (float Tick, float Error, int Sampled) clock,
            int frames,
            float startTick = 0f,
            Func<int, float> jitterMs = null)
        {
            var steps = new System.Collections.Generic.List<float>();
            float elapsedMs = startTick * (1000f / Tps);
            float prev = clock.Tick;

            for (int i = 0; i < frames; i++)
            {
                elapsedMs += Frame * 1000f;

                // The tick counter only ever increments on ARRIVAL, so jitter moves the moment it
                // steps -- which is the whole input the clock has to tolerate.
                float shifted = elapsedMs + (jitterMs?.Invoke(i) ?? 0f);
                int targetTick = (int)(shifted / (1000f / Tps));

                var next = WorldRunner.AdvanceRenderClock(
                    clock.Tick, clock.Error, clock.Sampled, targetTick, 0, Frame);
                clock = (next.Tick, next.Error, next.SampledTick);

                steps.Add(clock.Tick - prev);
                prev = clock.Tick;
            }

            return (clock.Tick, steps);
        }

        /// <summary>
        /// THE BUG THIS REPLACED. Arrival jitter must not reach the screen. With packets landing up to
        /// half a tick early or late, the clock still has to advance by an even amount every frame --
        /// the old derivation swung between 0.4 and 1.0 ticks under exactly this input.
        /// </summary>
        [NebulaUnitTest]
        public void ArrivalJitterDoesNotMakeMotionUneven()
        {
            // Alternating +/-8ms against a 33.3ms cadence, which is milder than the 19-43ms measured.
            var (_, steps) = Run(Fresh(0f), frames: 300, jitterMs: i => (i % 2 == 0) ? 8f : -8f);

            float expected = Frame * Tps;
            for (int i = 120; i < steps.Count; i++)
            {
                Assert.True(Math.Abs(steps[i] - expected) < expected * 0.05f,
                    $"frame {i}: advanced {steps[i]:F4}t, expected ~{expected:F4}t");
            }
        }

        /// <summary>Time may only move forward. A clock that steps back replays motion already drawn,
        /// which is what the old derivation did when a packet arrived late.</summary>
        [NebulaUnitTest]
        public void TheClockNeverMovesBackwardOrStalls()
        {
            var (_, steps) = Run(Fresh(0f), frames: 300, jitterMs: i => (i % 3 == 0) ? 12f : -6f);

            foreach (var step in steps)
            {
                Assert.True(step > 0f, $"clock advanced by {step}, which is not forward");
            }
        }

        /// <summary>
        /// A standing offset has to be eaten. A correction rate equal to the arrival rate -- which is
        /// what the orbital version of this originally had -- can never close a gap, and leaves every
        /// entity rendering permanently behind.
        /// </summary>
        [NebulaUnitTest]
        public void AStandingErrorIsCorrectedAway()
        {
            // Two ticks behind, inside the resync threshold so this exercises the correction and not
            // the re-seed.
            var (final, _) = Run(Fresh(-2f), frames: 900);

            float aim = (int)(900 * Frame * Tps);
            Assert.True(Math.Abs(aim - final) < 1f,
                $"clock settled {aim - final:F2} ticks from its aim; the gap should have closed");
        }

        /// <summary>A discontinuity -- world change, long hitch, first frame -- is re-seeded outright,
        /// because converging over seconds would render the world visibly wrong for all of them.</summary>
        [NebulaUnitTest]
        public void ALargeJumpResyncsImmediately()
        {
            var next = WorldRunner.AdvanceRenderClock(0f, 0f, int.MinValue, 500, 0, Frame);

            Assert.Equal(500f, next.Tick);
            Assert.Equal(0f, next.Error);
        }

        /// <summary>An uninitialised clock adopts its target rather than sweeping up to it from zero.</summary>
        [NebulaUnitTest]
        public void AFreshClockStartsAtItsTarget()
        {
            var next = WorldRunner.AdvanceRenderClock(float.NaN, 0f, int.MinValue, 42, 0, Frame);

            Assert.Equal(42f, next.Tick);
        }

        // ─── Dropouts ────────────────────────────────────────────────────────
        //
        // A burst of loss stops the tick stream outright. The target then stands still while the
        // clock free-runs, which is NOT an error even though it looks exactly like one.

        /// <summary>
        /// Coasts the clock through a silent stretch, returning where it ended up.
        /// </summary>
        private static (float Tick, float Error, int Sampled) Coast(
            (float Tick, float Error, int Sampled) clock, int frames, int frozenTarget)
        {
            for (int i = 0; i < frames; i++)
            {
                var next = WorldRunner.AdvanceRenderClock(
                    clock.Tick, clock.Error, clock.Sampled, frozenTarget, 0, Frame);
                clock = (next.Tick, next.Error, next.SampledTick);
            }
            return clock;
        }

        /// <summary>
        /// THE DROPOUT REWIND. Silence is absence of news, not evidence that render time is wrong --
        /// but a free-running clock outruns a frozen target, and a symmetric re-seed reads that as
        /// error and snaps render time BACKWARD, replaying motion already drawn. Measured under 200ms
        /// bursts as a -3.4 tick step (~115ms) on every burst, and it survived at the deepest buffer
        /// the controller can reach, because no amount of buffer depth addresses it.
        /// </summary>
        [NebulaUnitTest]
        public void SilenceDoesNotRewindTheClock()
        {
            // 200ms of nothing at 60fps: six ticks of silence against a three-tick resync threshold,
            // so the old symmetric check tripped roughly halfway through.
            var clock = (Tick: 100f, Error: 0f, Sampled: 100);
            float previous = clock.Tick;

            for (int frame = 0; frame < 12; frame++)
            {
                var next = WorldRunner.AdvanceRenderClock(
                    clock.Tick, clock.Error, clock.Sampled, 100, 0, Frame);
                clock = (next.Tick, next.Error, next.SampledTick);

                Assert.True(clock.Tick > previous,
                    $"frame {frame}: clock moved {previous:F3} -> {clock.Tick:F3}, which is not forward");
                previous = clock.Tick;
            }
        }

        /// <summary>
        /// WHY COASTING COSTS NOTHING TO RECOVER FROM. The server kept ticking through the silence, so
        /// when the stream resumes the target jumps by exactly the length of the gap and lands back on
        /// the clock. The arrival confirms the clock rather than moving it -- the recovery frame is an
        /// ordinary step, not a correction.
        /// </summary>
        [NebulaUnitTest]
        public void ADropoutRecoversWithoutACorrection()
        {
            var clock = Coast((Tick: 100f, Error: 0f, Sampled: 100), frames: 12, frozenTarget: 100);

            // Six ticks of real time passed, so the resuming stream reports six ticks of progress.
            var resumed = WorldRunner.AdvanceRenderClock(
                clock.Tick, clock.Error, clock.Sampled, 106, 0, Frame);

            float step = resumed.Tick - clock.Tick;
            float ordinary = Frame * Tps;
            Assert.True(Math.Abs(step - ordinary) < ordinary * 0.05f,
                $"recovery frame advanced {step:F3}t; an ordinary step is {ordinary:F3}t");
        }

        /// <summary>
        /// RESIZING THE BUFFER IS NOT AN ARRIVAL. The target is `currentTick - delayTicks`, so the
        /// adaptive buffer moves it -- and it moves it precisely in the window a dropout was just
        /// detected in, which is when the clock is coasting furthest ahead. A gate keyed on the target
        /// therefore opens mid-dropout with nothing behind it and re-seeds against a stale target:
        /// observed as a -8.3 tick step on a 300ms burst that grew the buffer from 4 to 5.
        /// </summary>
        [NebulaUnitTest]
        public void GrowingTheBufferMidDropoutDoesNotRewindTheClock()
        {
            // Silent throughout: the tick counter never moves, so nothing here is an arrival.
            const int FrozenTick = 100;
            var clock = Coast((Tick: 100f, Error: 0f, Sampled: FrozenTick), frames: 12, frozenTarget: FrozenTick);

            float before = clock.Tick;

            // The dropout is detected and the controller grows the buffer by a tick.
            var next = WorldRunner.AdvanceRenderClock(
                clock.Tick, clock.Error, clock.Sampled, FrozenTick, 1, Frame);

            Assert.True(next.Tick > before,
                $"resizing the buffer moved the clock {before:F3} -> {next.Tick:F3} instead of advancing it");
        }

        /// <summary>
        /// A GENUINE PAUSE IS STILL CAUGHT, one instant later. If the stream resumes with the target
        /// still far behind, that arrival is the proof the server did not tick through the gap -- and
        /// re-seeding is then correct, because the clock really is rendering a future that never
        /// happened.
        /// </summary>
        [NebulaUnitTest]
        public void AGenuinePauseResyncsWhenTheStreamResumes()
        {
            // A full second of silence: the clock coasts ~30 ticks ahead.
            var clock = Coast((Tick: 100f, Error: 0f, Sampled: 100), frames: 60, frozenTarget: 100);
            Assert.True(clock.Tick > 125f, $"clock should have coasted well ahead, reached {clock.Tick:F1}");

            // The server picks up where it left off rather than having advanced.
            var resumed = WorldRunner.AdvanceRenderClock(
                clock.Tick, clock.Error, clock.Sampled, 101, 0, Frame);

            Assert.Equal(101f, resumed.Tick);
            Assert.Equal(0f, resumed.Error);
        }
    }
}
