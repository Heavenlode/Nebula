using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Nebula.Testing.Integration;

/// <summary>
/// Runs a real server and two real clients, one of them on a deliberately bad link, and asserts the
/// session stays healthy.
///
/// <para>WHY THIS IS AN INTEGRATION TEST AND NOT A UNIT TEST. The pieces are unit-tested already —
/// the impairment scheduler, the render clock, the delay policy. What none of those can show is the
/// thing that actually went wrong historically: every one of those components measured fine in
/// isolation while remote entities visibly stuttered, because the defect lived in how they combined
/// under real arrival timing. This is the smallest harness that reproduces that combination.</para>
///
/// <para>It is also the first user of <see cref="IntegrationTestBase"/>, which has been present and
/// unexercised. Both <c>ServerConfig</c> and <c>ClientConfig</c> already carry an <c>ExtraArgs</c>
/// dictionary, so impairment needed no harness change at all.</para>
/// </summary>
public class ImpairedSoakTests : IntegrationTestBase
{
    /// <summary>Long enough for the adaptive buffer to react and settle: it evaluates once a second
    /// and needs five consecutive clean windows before giving a tick back.</summary>
    private static readonly TimeSpan SoakDuration = TimeSpan.FromSeconds(20);

    private const string WorldScene = "res://scenes/structures/Backslat.tscn";

    /// <summary>
    /// A healthy client and a badly impaired one in the same session.
    ///
    /// <para>The asymmetry is the point. A run where everyone is equally degraded hides the case that
    /// matters — one bad peer observed by a good one — which is exactly the shape of the remote-ship
    /// stutter that prompted this work.</para>
    /// </summary>
    [Fact]
    public Task ASessionSurvivesOneBadlyImpairedClient() => NebulaTest(async () =>
    {
        var server = StartServer(new ServerConfig
        {
            InitialWorldScene = WorldScene,
            // One JSON line per interval on stdout; the assertions below read it.
            ExtraArgs = { ["metrics"] = "1" },
        });
        await server.WaitForOutput("Server ready", TimeSpan.FromSeconds(60));

        var healthy = StartClient();

        var impaired = StartClient(new ClientConfig
        {
            ExtraArgs =
            {
                ["simLatencyMs"] = "80",
                ["simJitterMs"] = "30",
                ["simLossPct"] = "2",
                // Seeded, so a failure here can actually be re-run.
                ["simSeed"] = "20260826",
            },
        });

        await Task.Delay(SoakDuration);

        // 1. Nobody fell over. An impaired link must degrade smoothness, never liveness.
        Assert.False(server.HasExited, "server exited during the soak");
        Assert.False(healthy.HasExited, "the healthy client exited during the soak");
        Assert.False(impaired.HasExited, "the impaired client exited during the soak");

        // 2. The server kept ticking throughout, rather than stalling behind a struggling peer.
        var ticks = MetricValues(server.AllOutput, "tickCount");
        Assert.True(ticks.Count >= 2, $"expected repeated metrics lines, saw {ticks.Count}");
        Assert.True(ticks.Last() > ticks.First(), "server tick count did not advance during the soak");

        // 3. Neither client logged an error. The impairment exercises loss-recovery paths (spawn
        //    resend, delta baseline fallback); those are supposed to be exercised, not to complain.
        AssertNoErrors(healthy.AllOutput, "healthy client");
        AssertNoErrors(impaired.AllOutput, "impaired client");
    });

    /// <summary>
    /// The no-impairment control.
    ///
    /// <para>Worth its own test rather than being assumed: the impairment layer sits on the inbound
    /// path of every build, and the thing most worth knowing is that an unconfigured session is
    /// untouched by it.</para>
    /// </summary>
    [Fact]
    public Task AnUnimpairedSessionIsUnaffected() => NebulaTest(async () =>
    {
        var server = StartServer(new ServerConfig
        {
            InitialWorldScene = WorldScene,
            ExtraArgs = { ["metrics"] = "1" },
        });
        await server.WaitForOutput("Server ready", TimeSpan.FromSeconds(60));

        var client = StartClient();
        await Task.Delay(TimeSpan.FromSeconds(10));

        Assert.False(server.HasExited, "server exited during the control run");
        Assert.False(client.HasExited, "client exited during the control run");
        AssertNoErrors(client.AllOutput, "client");
    });

    /// <summary>
    /// Pulls a numeric field out of the <c>NEBULA_METRICS </c> stdout lines. Deliberately a shallow
    /// scrape rather than a JSON dependency — the line format is stable and the alternative is adding
    /// a parser to the test project for four numbers.
    /// </summary>
    private static List<double> MetricValues(string output, string field)
    {
        var values = new List<double>();
        foreach (var line in output.Split('\n'))
        {
            int start = line.IndexOf($"\"{field}\":", StringComparison.Ordinal);
            if (start < 0) continue;

            start += field.Length + 3;
            int end = start;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == '-')) end++;

            if (double.TryParse(line[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                values.Add(parsed);
        }
        return values;
    }

    private static void AssertNoErrors(string output, string label)
    {
        var offenders = output
            .Split('\n')
            .Where(line => line.Contains("(ERROR)", StringComparison.Ordinal))
            .Take(5)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{label} logged errors:\n{string.Join("\n", offenders)}");
    }
}
