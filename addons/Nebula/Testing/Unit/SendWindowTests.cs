using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// SendWindow backs the spawn/despawn ack commit rule under budget splitting: an acked
/// tick may only commit a record that provably rode that tick's packet. The window is the
/// contiguous run of send ticks; any budget-deferred tick restarts it, so a stale ack can
/// at worst cost one extra resend round, never a false commit.
/// </summary>
[NebulaUnitTest]
public class SendWindowTests
{
    [NebulaUnitTest]
    public void NeverSent_CoversNothing()
    {
        var window = new SendWindow();

        Assert.False(window.Covers(0));
        Assert.False(window.Covers(1));
    }

    [NebulaUnitTest]
    public void ContiguousSends_CoverTheWholeRun()
    {
        var window = new SendWindow();
        window.RecordSend(10);
        window.RecordSend(11);
        window.RecordSend(12);

        Assert.True(window.Covers(10));
        Assert.True(window.Covers(11));
        Assert.True(window.Covers(12));
        Assert.False(window.Covers(9));
        Assert.False(window.Covers(13));
    }

    // The budget-splitting scenario: the record rode ticks 10-11, was deferred on 12,
    // and rode again on 13. An ack of 12 (a packet without the record) must not commit,
    // and neither may the pre-gap ticks - the client may have dropped those packets and
    // the server can no longer distinguish "acked 13 because it saw 10" from "saw only 13".
    [NebulaUnitTest]
    public void GapRestartsWindow_PreGapTicksNoLongerCovered()
    {
        var window = new SendWindow();
        window.RecordSend(10);
        window.RecordSend(11);
        window.RecordSend(13);

        Assert.False(window.Covers(10));
        Assert.False(window.Covers(11));
        Assert.False(window.Covers(12));
        Assert.True(window.Covers(13));
    }

    [NebulaUnitTest]
    public void SendAfterGap_ExtendsFromRestart()
    {
        var window = new SendWindow();
        window.RecordSend(5);
        window.RecordSend(20);
        window.RecordSend(21);

        Assert.False(window.Covers(5));
        Assert.True(window.Covers(20));
        Assert.True(window.Covers(21));
    }

    [NebulaUnitTest]
    public void SingleSend_CoversExactlyThatTick()
    {
        var window = new SendWindow();
        window.RecordSend(7);

        Assert.False(window.Covers(6));
        Assert.True(window.Covers(7));
        Assert.False(window.Covers(8));
    }
}
