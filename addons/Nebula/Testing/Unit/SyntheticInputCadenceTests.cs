using Nebula.Testing.Load;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The synthetic load client's input cadence, which has to match <c>WorldRunner.SendInput</c>'s.
///
/// <para>These matter because getting them wrong does not break a run — it silently changes the
/// inbound packet rate the server sees, which is the quantity a load run exists to measure. A peer
/// that sent on every tick would inflate the server's inbound work roughly fourfold against a real
/// client holding its keys, and the resulting numbers would look like a server regression.</para>
/// </summary>
[NebulaUnitTest]
public class SyntheticInputCadenceTests
{
    [NebulaUnitTest]
    public void UnchangedInput_SendsOnlyOnEveryFourthTick()
    {
        for (int tick = 0; tick < 64; tick++)
        {
            bool expected = (tick & 3) == 0;
            Assert.Equal(expected, SyntheticInputCadence.ShouldSend(inputChanged: false, tick));
        }
    }

    [NebulaUnitTest]
    public void ChangedInput_AlwaysSends()
    {
        for (int tick = 0; tick < 64; tick++)
        {
            Assert.True(SyntheticInputCadence.ShouldSend(inputChanged: true, tick));
        }
    }

    /// <summary>
    /// Mirrors the rule at WorldRunner.cs's SendInput: the acknowledgement is per PEER while
    /// SendInput runs per owned NODE, so only the first packet of a tick may carry it. A
    /// Heavenlode peer sends two packets per tick (ship and character), so this is the ordinary
    /// case, not an edge one.
    /// </summary>
    [NebulaUnitTest]
    public void AckRidesTheFirstPacketOfATickAndNoOther()
    {
        var cadence = default(SyntheticInputCadence);
        cadence.BeginTick();

        Assert.True(cadence.TryClaimAck(pendingAckTick: 100));
        Assert.False(cadence.TryClaimAck(pendingAckTick: 100));
        Assert.False(cadence.TryClaimAck(pendingAckTick: 100));
        Assert.True(cadence.AckAttachedThisTick);
    }

    [NebulaUnitTest]
    public void EachTickGetsItsOwnAckClaim()
    {
        var cadence = default(SyntheticInputCadence);

        for (int tick = 0; tick < 8; tick++)
        {
            cadence.BeginTick();
            Assert.False(cadence.AckAttachedThisTick);
            Assert.True(cadence.TryClaimAck(pendingAckTick: tick));
            Assert.False(cadence.TryClaimAck(pendingAckTick: tick));
        }
    }

    /// <summary>
    /// With nothing pending, no packet claims an ack — otherwise a peer would stamp tick -1 into a
    /// packet and the server would log it as an invalid acknowledgement every time.
    /// </summary>
    [NebulaUnitTest]
    public void NothingPending_IsNeverClaimed()
    {
        var cadence = default(SyntheticInputCadence);
        cadence.BeginTick();

        Assert.False(cadence.TryClaimAck(pendingAckTick: -1));
        Assert.False(cadence.AckAttachedThisTick);

        // And the slot is still free for a real ack later in the same tick.
        Assert.True(cadence.TryClaimAck(pendingAckTick: 7));
    }

    /// <summary>
    /// Over a long run of unchanged input the keepalive must carry exactly a quarter of the ticks.
    /// Pinning the ratio catches a mask that is right at tick 0 but drifts.
    /// </summary>
    [NebulaUnitTest]
    public void KeepaliveCarriesExactlyAQuarterOfUnchangedTicks()
    {
        const int Ticks = 4000;
        int sent = 0;
        for (int tick = 0; tick < Ticks; tick++)
        {
            if (SyntheticInputCadence.ShouldSend(inputChanged: false, tick)) sent++;
        }
        Assert.Equal(Ticks / 4, sent);
    }
}
