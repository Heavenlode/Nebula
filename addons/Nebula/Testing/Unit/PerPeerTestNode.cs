using Nebula;
using Nebula.Serialization;

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

    /// <summary>
    /// Per-peer NetArray: compiles the generator's object-typed per-peer branch (getter routed
    /// through TryGetPerPeerArray, setter through TryWritePerPeerRef). A partial property cannot
    /// carry an initializer, so the base instance is created in the constructor - which is exactly
    /// the sequence NEBULA006's message tells users to follow.
    /// </summary>
    [NetProperty(PerPeerState = true)]
    public partial NetArray<byte> PerPeerArray { get; set; }

    [NetProperty]
    public int BroadcastValue { get; set; }

    public PerPeerTestNode()
    {
        PerPeerValue = -1;
        PerPeerArray = new NetArray<byte>(64, 8);
    }
}
