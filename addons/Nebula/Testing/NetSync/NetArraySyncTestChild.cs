using Nebula;
using Nebula.Serialization;

namespace Nebula.Testing.NetSync
{
    /// <summary>
    /// Static child NetNode of NetArraySyncTestWorld, carrying its own NetArray. This reproduces the
    /// real planet/harvestable topology: a static-child object NetProperty whose mutation propagates
    /// dirty-marking to the parent NetScene. It exists so the harness verifies a static-child NetArray
    /// replicates cleanly and does not corrupt the parent's value properties (see ParentCanary).
    /// </summary>
    public partial class NetArraySyncTestChild : NetNode3D
    {
        [NetProperty(NotifyOnChange = true)]
        public NetArray<byte> ChildArray { get; set; } = new NetArray<byte>(NetArraySyncTestWorld.Capacity);

        protected virtual void OnNetChangeChildArray(int tick, byte[] d, int[] c, byte[] a) { }
    }
}
