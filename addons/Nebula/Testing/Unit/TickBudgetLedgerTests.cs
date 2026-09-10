using System;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// TickBudgetLedger mirrors the assembled payload layout of ExportState in BITS: the 8-bit
/// group presence, a node set per active 64-node group (charged at its dense worst case,
/// 65 bits), a serializers-run word per included node (charged at its long form, 4 bits),
/// then the section payloads. These tests pin the framing charges and the property that
/// matters: a packet committed under the ledger never assembles past the byte budget.
/// </summary>
[NebulaUnitTest]
public class TickBudgetLedgerTests
{
    private const int GroupPresence = 8;
    private const int NodeSetWorst = 1 + 64;
    private const int RunWorst = 1 + 3;

    [NebulaUnitTest]
    public void GroupPresence_ChargedUpfront()
    {
        var ledger = new TickBudgetLedger(800);

        Assert.Equal(800, ledger.Budget);
        Assert.Equal(GroupPresence, ledger.Used);
        Assert.Equal(792, ledger.Remaining);
        Assert.Equal(1, ledger.UsedBytes);
        Assert.Equal(100, ledger.BudgetBytes);
    }

    [NebulaUnitTest]
    public void FirstSectionForNode_ChargesRunWordAndNodeSet()
    {
        var ledger = new TickBudgetLedger(800);

        // First node of a new group: payload 80 + run word + node set
        Assert.True(ledger.TryCommitSection(80, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(GroupPresence + 80 + RunWorst + NodeSetWorst, ledger.Used);

        // Second section for the same node: payload only
        Assert.True(ledger.TryCommitSection(40, firstSectionForNode: false, opensNewGroup: false));
        Assert.Equal(GroupPresence + 80 + RunWorst + NodeSetWorst + 40, ledger.Used);

        // New node in the already-open group: payload + run word
        Assert.True(ledger.TryCommitSection(56, firstSectionForNode: true, opensNewGroup: false));
        Assert.Equal(GroupPresence + 80 + RunWorst + NodeSetWorst + 40 + 56 + RunWorst, ledger.Used);
    }

    [NebulaUnitTest]
    public void SectionBudget_SubtractsFramingAndClampsToZero()
    {
        var ledger = new TickBudgetLedger(240);

        // Remaining 232; new node in new group costs 69 framing
        Assert.Equal(232 - RunWorst - NodeSetWorst, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(232 - RunWorst, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: false));
        Assert.Equal(232, ledger.SectionBudget(firstSectionForNode: false, opensNewGroup: false));

        Assert.True(ledger.TryCommitSection(232 - RunWorst - NodeSetWorst, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(0, ledger.Remaining);
        Assert.Equal(0, ledger.SectionBudget(firstSectionForNode: true, opensNewGroup: true));
    }

    [NebulaUnitTest]
    public void OverBudgetSection_RejectedWithoutCharging()
    {
        var ledger = new TickBudgetLedger(160);
        var usedBefore = ledger.Used;

        // 152 remaining; 84 payload + 69 framing = 153 > 152
        Assert.False(ledger.TryCommitSection(84, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(usedBefore, ledger.Used);

        // Exactly fitting section commits
        Assert.True(ledger.TryCommitSection(83, firstSectionForNode: true, opensNewGroup: true));
        Assert.Equal(0, ledger.Remaining);
    }

    // The property the whole design rests on: random sections committed under the ledger,
    // then assembled with the real framing codec, never exceed the byte budget - because
    // the ledger charges the worst-case framing and the writer only ever picks shorter.
    [NebulaUnitTest]
    public void CommittedPacket_NeverAssemblesPastBudget()
    {
        var rng = new Random(17);
        for (int trial = 0; trial < 200; trial++)
        {
            int budgetBytes = rng.Next(20, 300);
            var ledger = new TickBudgetLedger(budgetBytes * 8);
            var groupMasks = new long[8];
            var runs = new byte[512];
            var sectionBits = new int[512];

            for (int attempt = 0; attempt < 60; attempt++)
            {
                int nodeId = rng.Next(0, 512);
                int g = nodeId >> 6, local = nodeId & 63;
                bool first = (groupMasks[g] & (1L << local)) == 0;
                bool opens = groupMasks[g] == 0;
                int bits = rng.Next(1, 200);
                byte serializer = (byte)(1 << rng.Next(0, 3));
                if (!ledger.TryCommitSection(bits, first, opens)) continue;
                groupMasks[g] |= 1L << local;
                runs[nodeId] |= serializer;
                sectionBits[nodeId] += bits;
            }

            // Assemble exactly as ExportState does.
            var packet = new NetBuffer(4096, usePool: false);
            byte presence = 0;
            for (int g = 0; g < 8; g++) if (groupMasks[g] != 0) presence |= (byte)(1 << g);
            packet.WriteBits(presence, 8);
            for (int g = 0; g < 8; g++) if (groupMasks[g] != 0) PacketFraming.WriteNodeSet(packet, groupMasks[g]);
            for (int nodeId = 0; nodeId < 512; nodeId++)
            {
                if ((groupMasks[nodeId >> 6] & (1L << (nodeId & 63))) == 0) continue;
                PacketFraming.WriteSerializersRun(packet, runs[nodeId], 1);
            }
            for (int nodeId = 0; nodeId < 512; nodeId++)
            {
                if ((groupMasks[nodeId >> 6] & (1L << (nodeId & 63))) == 0) continue;
                int bits = sectionBits[nodeId];
                while (bits > 0) { int take = Math.Min(64, bits); packet.WriteBits(0xA5A5A5A5A5A5A5A5UL, take); bits -= take; }
            }
            packet.AlignWrite();

            Assert.True(packet.WrittenBits <= ledger.Used + 7, $"trial {trial}: assembled {packet.WrittenBits} bits > ledger {ledger.Used}");
            Assert.True(packet.Length <= budgetBytes, $"trial {trial}: {packet.Length} B > budget {budgetBytes} B");
        }
    }

    [NebulaUnitTest]
    public void TickPayloadBudget_Math()
    {
        // MTU 1200 - 4 tick - 16 headroom
        Assert.Equal(1180, NetRunner.TickPayloadBudget(1200));
    }
}
