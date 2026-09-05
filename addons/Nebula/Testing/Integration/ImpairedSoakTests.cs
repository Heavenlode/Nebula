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

    /// <summary>The shared integration world, the same one <c>BasicIntegrationTests</c> uses.</summary>
    private const string WorldScene = "res://Integration/Basic/Scene.tscn";

    /// <summary>Spawned once both clients are in, so the soak has real spawn and props traffic to
    /// impair rather than an empty tick stream.</summary>
    private const string PlayerScene = "res://Integration/Basic/Player.tscn";

    /// <summary>
    /// Synchronisation runs over the debug channel, not stdout. Nebula's INFO logging is gated on
    /// a project setting the test project does not currently set, so lines like "Server ready"
    /// never reach stdout; the debug events are what <c>BasicIntegrationTests</c> already waits on.
    /// </summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    // Distinct per test: the two run back to back in the same collection, and reusing a port means
    // racing the previous run's process teardown.
    private const int SoakServerDebugPort = 17890;
    private const int SoakHealthyDebugPort = 17891;
    private const int SoakImpairedDebugPort = 17892;
    private const int ControlServerDebugPort = 17893;
    private const int ControlClientDebugPort = 17894;

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
        var worldId = Guid.NewGuid().ToString();
        var server = StartServer(new ServerConfig
        {
            WorldId = worldId,
            InitialWorldScene = WorldScene,
            DebugPort = SoakServerDebugPort,
            // One JSON line per interval on stdout; the assertions below read it.
            ExtraArgs = { ["metrics"] = "" },
        });

        var healthy = StartClient(new ClientConfig { DebugPort = SoakHealthyDebugPort });
        var impaired = StartClient(new ClientConfig
        {
            DebugPort = SoakImpairedDebugPort,
            ExtraArgs =
            {
                ["simLatencyMs"] = "80",
                ["simJitterMs"] = "30",
                ["simLossPct"] = "2",
                // Seeded, so a failure here can actually be re-run.
                ["simSeed"] = "20260826",
            },
        });

        await server.ConnectDebug(SoakServerDebugPort);
        await healthy.ConnectDebug(SoakHealthyDebugPort);
        await impaired.ConnectDebug(SoakImpairedDebugPort);

        await server.WaitForDebugEvent("WorldCreated", worldId, ReadyTimeout);
        await healthy.WaitForDebugEvent("WorldJoined", WorldScene, ReadyTimeout);
        await impaired.WaitForDebugEvent("WorldJoined", WorldScene, ReadyTimeout);

        // Give the soak something to replicate. Without a spawn the tick stream is empty and the
        // impairment has nothing to act on, which would make the run pass for the wrong reason.
        server.SendCommand($"spawn:{PlayerScene}");
        await healthy.WaitForDebugEvent("Spawn", $"Imported:{PlayerScene}", ReadyTimeout);
        await impaired.WaitForDebugEvent("Spawn", $"Imported:{PlayerScene}", ReadyTimeout);

        await Task.Delay(SoakDuration);

        // 1. Nobody fell over. An impaired link must degrade smoothness, never liveness.
        Assert.False(server.HasExited, "server exited during the soak");
        Assert.False(healthy.HasExited, "the healthy client exited during the soak");
        Assert.False(impaired.HasExited, "the impaired client exited during the soak");

        // 2. The server kept ticking throughout, rather than stalling behind a struggling peer.
        var ticks = MetricValues(server.AllOutput, "tick");
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
        var worldId = Guid.NewGuid().ToString();
        var server = StartServer(new ServerConfig
        {
            WorldId = worldId,
            InitialWorldScene = WorldScene,
            DebugPort = ControlServerDebugPort,
            ExtraArgs = { ["metrics"] = "" },
        });
        var client = StartClient(new ClientConfig { DebugPort = ControlClientDebugPort });

        await server.ConnectDebug(ControlServerDebugPort);
        await client.ConnectDebug(ControlClientDebugPort);

        await server.WaitForDebugEvent("WorldCreated", worldId, ReadyTimeout);
        await client.WaitForDebugEvent("WorldJoined", WorldScene, ReadyTimeout);

        server.SendCommand($"spawn:{PlayerScene}");
        await client.WaitForDebugEvent("Spawn", $"Imported:{PlayerScene}", ReadyTimeout);

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
