using Nebula;

namespace Nebula.Testing.Unit;

/// <summary>
/// Test fixture for PerPeerStateTests: a real NetNode with a generated per-peer partial
/// property (exercising the actual generator output + Fody interaction) and a plain
/// broadcast property as the weaving positive control. Not part of any scene; the
/// constructor write deliberately replicates the "per-peer default assigned before
/// SceneFilePath exists" sequence that once poisoned the IsNetScene cache.
/// </summary>
public partial class PerPeerTestNode : NetNode
{
    [NetProperty(PerPeerState = true)]
    public partial int PerPeerValue { get; set; }

    [NetProperty]
    public int BroadcastValue { get; set; }

    public PerPeerTestNode()
    {
        PerPeerValue = -1;
    }
}
