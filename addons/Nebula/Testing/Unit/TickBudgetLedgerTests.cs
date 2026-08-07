using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// TickBudgetLedger mirrors the assembled payload layout of ExportState: 1 byte group
/// mask, 8 bytes node mask per active 64-node group, 1 serializersRun byte per included
/// node, then the section payloads. These tests pin the framing charges so the assembled
/// buffer can never exceed the budget the ledger was constructed with.
/// </summary>
[NebulaUnitTest]
public class TickBudgetLedgerTests
{
    [NebulaUnitTest]
    public void GroupMaskByte_ChargedUpfront()
    {
        var ledger = new TickBudgetLedger(100);

        Assert.Equal(100, ledger.Budget);
        Assert.Equal(1, ledger.Used);
        Assert.Equal(99, ledger.Remaining);
    }

    [NebulaUnitTest]
    public void FirstSectionForNode_ChargesMaskByteAndGroupMask()
    {
        var ledger = new TickBudgetLedger(100);

        // First node of a new group: payload 10 + 1 (serializersRun) + 8 (group node mask)
        Assert.True(ledger.TryCommitSection(10, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(1 + 10 + 1 + 8, ledger.Used);

        // Second section for the same node: payload only
        Assert.True(ledger.TryCommitSection(5, firstSectionForNode: false, opensNewGroup: false));
        Assert.Equal(1 + 10 + 1 + 8 + 5, ledger.Used);

        // New node in the already-open group: payload + serializersRun byte
        Assert.True(ledger.TryCommitSection(7, firstSectionForNode: true, opensNewGroup: false));
        Assert.Equal(1 + 10 + 1 + 8 + 5 + 7 + 1, ledger.Used);
    }

    [NebulaUnitTest]
    public void SectionBudget_SubtractsFramingAndClampsToZero()
    {
        var ledger = new TickBudgetLedger(30);

        // Remaining 29; new node in new group costs 9 framing
        Assert.Equal(20, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(28, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: false));
        Assert.Equal(29, ledger.SectionBudget(firstSectionForNode: false, opensNewGroup: false));

        Assert.True(ledger.TryCommitSection(20, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(0, ledger.Remaining);
        Assert.Equal(0, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: true));
    }

    [NebulaUnitTest]
    public void OverBudgetSection_RejectedWithoutCharging()
    {
        var ledger = new TickBudgetLedger(20);
        var usedBefore = ledger.Used;

        // 19 remaining; 11 payload + 9 framing = 20 > 19
        Assert.False(ledger.TryCommitSection(11, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(usedBefore, ledger.Used);

        // Exactly fitting section commits
        Assert.True(ledger.TryCommitSection(10, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(0, ledger.Remaining);
    }

    [NebulaUnitTest]
    public void TickPayloadBudget_Math()
    {
        // MTU 1200 - 4 tick - 1 flags - 2 checksum - 16 headroom
        Assert.Equal(1177, NetRunner.TickPayloadBudget(1200));
    }
}
