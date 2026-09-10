using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Client-side tests for the snapshot-delta baseline in NetPropertiesSerializer.Deserialize:
/// a payload whose baseline the client cannot resolve must be reported as discarded (so the
/// tick is never acked - acking a discarded tick latches the server onto a baseline the
/// client never recorded, which was the "missing applied-state baseline" spam every tick),
/// and a malformed age byte must discard rather than index the ring negatively and throw.
///
/// Built on the Protocol-free test constructor; wire format per payload is
/// [maskMode:1][baselineAge:5][presence mask][property data] padded to a byte, where the
/// mask is flat (one bit per property; this fixture: 2 props, 2 bits) for scenes with one
/// or two mask bytes and two-level for wider ones (see PresenceMask). These tests use an
/// empty presence mask so no property bits follow.
/// </summary>
[NebulaUnitTest]
public class SnapshotBaselineTests
{
    private static NetPropertiesSerializer CreateClientSerializer(out NetNode node)
    {
        node = new NetNode();
        return new NetPropertiesSerializer(
            node.Network,
            [SerialVariantType.Int, SerialVariantType.Bool]);
    }

    /// <summary>One payload with an empty presence mask: [reuse=0][age][mask=00], no property bits.</summary>
    private static NetBuffer Payload(byte baselineAge)
    {
        var buf = new NetBuffer(16, usePool: false);
        buf.WriteBool(false);
        buf.WriteBits(baselineAge, 5);
        buf.WriteBits(0, 2);
        buf.AlignWrite();
        buf.ResetRead();
        return buf;
    }

    // 1. The respawn scenario: a fresh serializer (empty applied ring) receives a delta
    //    payload. It must report "not applied" and record nothing - the caller withholding
    //    the ack on this signal is what lets the server fall back to an absolute send.
    [NebulaUnitTest]
    public void MissingBaseline_DiscardsAndRecordsNothing()
    {
        var serializer = CreateClientSerializer(out var node);

        bool applied = serializer.DeserializeForTests(Payload(baselineAge: 5), currentTick: 40);

        Assert.False(applied);
        Assert.False(serializer.HasAppliedEntryForTests(40));

        node.Free();
    }

    // 2. The exact numbers from the field bug: baseline recorded at tick 37, delta age 19
    //    arriving at tick 56 resolves against it and is applied.
    [NebulaUnitTest]
    public void AppliedAbsolute_RecordsBaseline_LaterDeltaResolves()
    {
        var serializer = CreateClientSerializer(out var node);

        Assert.True(serializer.DeserializeForTests(Payload(baselineAge: 0), currentTick: 37));
        Assert.True(serializer.HasAppliedEntryForTests(37));

        Assert.True(serializer.DeserializeForTests(Payload(baselineAge: 19), currentTick: 56));
        Assert.True(serializer.HasAppliedEntryForTests(56));

        node.Free();
    }

    // 3. The latch, and the recovery: a discarded tick must not become a usable baseline
    //    (a delta referencing it discards too), and one absolute payload restores the
    //    chain. With the ack now gated on the applied flag, this sequence is exactly
    //    "one error line, self-heals" instead of "error every tick forever".
    [NebulaUnitTest]
    public void DiscardedTickIsNotABaseline_AbsoluteRecovers()
    {
        var serializer = CreateClientSerializer(out var node);

        Assert.False(serializer.DeserializeForTests(Payload(baselineAge: 5), currentTick: 40));
        Assert.False(serializer.DeserializeForTests(Payload(baselineAge: 1), currentTick: 41)); // references 40
        Assert.False(serializer.HasAppliedEntryForTests(41));

        Assert.True(serializer.DeserializeForTests(Payload(baselineAge: 0), currentTick: 42)); // absolute
        Assert.True(serializer.DeserializeForTests(Payload(baselineAge: 1), currentTick: 43)); // references 42

        node.Free();
    }

    // 4. A corrupt age (beyond MAX_DELTA_AGE = 30; the 5-bit field's only such value is 31)
    //    discards instead of trusting garbage.
    [NebulaUnitTest]
    public void AgeBeyondMaxDeltaAge_Discarded()
    {
        var serializer = CreateClientSerializer(out var node);

        Assert.False(serializer.DeserializeForTests(Payload(baselineAge: 31), currentTick: 300));

        node.Free();
    }

    // 5. Regression: age exceeding a young world's tick count used to compute a negative
    //    baseline tick and index the ring with it - IndexOutOfRangeException, aborting the
    //    whole tick import. Must be a plain discard.
    [NebulaUnitTest]
    public void AgeOlderThanWorld_DiscardedNotThrown()
    {
        var serializer = CreateClientSerializer(out var node);

        Assert.False(serializer.DeserializeForTests(Payload(baselineAge: 19), currentTick: 5));

        node.Free();
    }
}
