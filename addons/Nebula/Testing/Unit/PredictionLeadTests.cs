using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Tests for the adaptive prediction-lead slew. The client's prediction lead over the
/// confirmed tick is a ratchet (server stalls and clock drift only ever grow it), so
/// WorldRunner steers it toward an RTT-derived target: skip a prediction tick to shed
/// excess lead, run an extra one to build missing lead. These tests pin the pure
/// decision helpers and the convergence behavior of the control loop.
/// </summary>
[NebulaUnitTest]
public class PredictionLeadTests
{
    private const int Tps = 30;

    [NebulaUnitTest]
    public void TestTargetLeadFromRtt()
    {
        // Loopback: zero flight time, so the target is just the jitter margin (= min).
        Assert.Equal(2, WorldRunner.ComputeTargetLeadTicks(0, Tps));

        // 50ms RTT at TPS 30 is 2 ticks of flight time + 2 margin.
        Assert.Equal(4, WorldRunner.ComputeTargetLeadTicks(50, Tps));

        // 200ms RTT: 6 ticks + 2 margin.
        Assert.Equal(8, WorldRunner.ComputeTargetLeadTicks(200, Tps));

        // Absurd RTT clamps safely inside the 30-tick hard cap.
        Assert.Equal(28, WorldRunner.ComputeTargetLeadTicks(5000, Tps));
    }

    [NebulaUnitTest]
    public void TestSteadyStateRunsOneTickPerFrame()
    {
        // Lead within the slack band: never slews, regardless of frame phase.
        for (ulong frame = 0; frame < 16; frame++)
        {
            Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(5, 5, frame));
            Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(6, 5, frame));
            Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(3, 5, frame));
        }
    }

    [NebulaUnitTest]
    public void TestExcessLeadShedsOnSlewFrames()
    {
        // Pinned-at-cap scenario: lead 30, target 5 -> skip on slew frames only.
        Assert.Equal(0, WorldRunner.PredictionTicksThisFrame(30, 5, 0));
        Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(30, 5, 1));
        Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(30, 5, 2));
        Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(30, 5, 3));
        Assert.Equal(0, WorldRunner.PredictionTicksThisFrame(30, 5, 4));
    }

    [NebulaUnitTest]
    public void TestMissingLeadBuildsOnSlewFrames()
    {
        // Session start: lead 0, target 5 -> double-tick on slew frames only.
        Assert.Equal(2, WorldRunner.PredictionTicksThisFrame(0, 5, 0));
        Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(0, 5, 1));
        Assert.Equal(1, WorldRunner.PredictionTicksThisFrame(0, 5, 3));
        Assert.Equal(2, WorldRunner.PredictionTicksThisFrame(0, 5, 8));
    }

    /// <summary>
    /// Simulates the control loop: each eligible frame the confirmed tick advances by 1
    /// (healthy server) and the predicted tick advances by the slew decision. From either
    /// extreme the lead must converge into the slack band within 5 simulated seconds
    /// (150 eligible frames at TPS 30) and then stay there.
    /// </summary>
    private static void AssertConverges(int startLead, int targetLead)
    {
        int lead = startLead;
        int convergedAt = -1;

        for (int frame = 0; frame < 300; frame++)
        {
            int ticks = WorldRunner.PredictionTicksThisFrame(lead, targetLead, (ulong)frame);
            lead += ticks - 1; // predicted advances `ticks`, confirmed advances 1

            bool inBand = lead >= targetLead - 2 && lead <= targetLead + 2;
            if (convergedAt < 0)
            {
                if (inBand) convergedAt = frame;
            }
            else
            {
                Assert.True(inBand, $"lead {lead} left the slack band at frame {frame} after converging at {convergedAt}");
            }
        }

        Assert.True(convergedAt >= 0 && convergedAt <= 150,
            $"lead did not converge within 150 frames (start {startLead}, target {targetLead}, convergedAt {convergedAt})");
    }

    [NebulaUnitTest]
    public void TestConvergenceFromPinnedCap()
    {
        AssertConverges(startLead: 30, targetLead: 5);
    }

    [NebulaUnitTest]
    public void TestConvergenceFromColdStart()
    {
        AssertConverges(startLead: 0, targetLead: 5);
    }
}
