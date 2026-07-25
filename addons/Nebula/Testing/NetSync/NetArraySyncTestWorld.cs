using Godot;
using Nebula;
using Nebula.Serialization;

namespace Nebula.Testing.NetSync
{
    /// <summary>
    /// World-root node that exercises NetArray&lt;byte&gt; replication end-to-end through the real
    /// server→client stack (NetPropertiesSerializer Export/Import over ENet), which the in-process unit
    /// tests bypass. Three fill patterns on the root cover every sparse encoding:
    ///   AllDefault - all zero  -> header-only sparse window
    ///   Sparse     - scattered -> a few (index,value) entries
    ///   Dense      - fully set  -> multi-chunk sparse sync
    ///
    /// It also carries a static child (NetArraySyncTestChild) with its own NetArray -- reproducing the
    /// real planet/harvestable topology (a static-child object NetProperty next to parent value
    /// properties). Before the MarkDirtyByIndex fix, mutating the child's array corrupted the parent's
    /// value property at the child's class-local index (misapplied as the parent's global index),
    /// desyncing the stream. ParentCanary (declared first, so global index 0) is that corruption target.
    ///
    /// The client verifies every array + the canary, prints [NETSYNC PASS]/[NETSYNC FAIL], and quits.
    /// Driven by run_netsync_test.sh. Self-contained Nebula test node (no game-side dependencies).
    /// </summary>
    public partial class NetArraySyncTestWorld : NetNode3D
    {
        public const int Capacity = 1024;
        public const int Len = 1000;

        // Global index 0 (declared first): a value property, inline-initialized and never assigned, so
        // its cache stays Nil. A reintroduced static-child index bug would mark THIS dirty and ship it
        // with a Nil cache -> desync. The client keeps its own inline default; it must stay 7.
        [NetProperty]
        public byte ParentCanary { get; set; } = 7;

        // Minimal world-id sync so the client's world is identified, without a game-specific world node.
        [NetProperty(NotifyOnChange = true)]
        public UUID WorldId { get; set; }
        protected virtual void OnNetChangeWorldId(int tick, UUID oldVal, UUID newVal) { }

        [NetProperty(NotifyOnChange = true)]
        public NetArray<byte> AllDefault { get; set; } = new NetArray<byte>(Capacity);

        [NetProperty(NotifyOnChange = true)]
        public NetArray<byte> Sparse { get; set; } = new NetArray<byte>(Capacity);

        [NetProperty(NotifyOnChange = true)]
        public NetArray<byte> Dense { get; set; } = new NetArray<byte>(Capacity);

        protected virtual void OnNetChangeAllDefault(int tick, byte[] d, int[] c, byte[] a) { }
        protected virtual void OnNetChangeSparse(int tick, byte[] d, int[] c, byte[] a) { }
        protected virtual void OnNetChangeDense(int tick, byte[] d, int[] c, byte[] a) { }

        // Deterministic patterns, identical on server (fill) and client (verify).
        private static byte SparseVal(int i) => (i % 137 == 0) ? (byte)((i % 250) + 1) : (byte)0;
        private static byte DenseVal(int i) => (byte)((i % 250) + 1);
        public static byte ChildVal(int i) => (byte)((i % 251) + 1);

        private NetArraySyncTestChild Child => GetNodeOrNull<NetArraySyncTestChild>("TestChild");

        public override void _WorldReady()
        {
            base._WorldReady();
            if (!Network.IsServer) return;

            WorldId = Network.CurrentWorld.WorldId;
            // ParentCanary is deliberately NOT assigned (stays at its inline default 7, Nil cache).

            AllDefault.SetLength(Len); // left all-zero

            Sparse.SetLength(Len);
            for (int i = 0; i < Len; i++)
            {
                byte v = SparseVal(i);
                if (v != 0) Sparse[i] = v;
            }

            Dense.SetLength(Len);
            for (int i = 0; i < Len; i++) Dense[i] = DenseVal(i);

            // Mutate the static child's NetArray -- the exact operation that corrupted a parent value
            // property before the fix.
            var child = Child;
            child.ChildArray.SetLength(Len);
            for (int i = 0; i < Len; i++) child.ChildArray[i] = ChildVal(i);

            GD.Print($"[NETSYNC] server populated root arrays + static-child array (len={Len})");
        }

        private bool _done;
        private double _elapsed;
        private string _lastFail = "(arrays not yet full length)";

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (Network.IsServer || _done) return;

            _elapsed += delta;

            // Multi-chunk sync arrives over several ticks: length is set on the first chunk but content
            // fills incrementally, so we PASS only on a fully-correct verify and keep re-checking each
            // frame (a mid-sync mismatch is expected, not a failure). Only a timeout is a real failure.
            var child = Child;
            bool lengthsReady = AllDefault.Length == Len && Sparse.Length == Len && Dense.Length == Len
                                && child != null && child.ChildArray.Length == Len;
            if (lengthsReady)
            {
                string fail = Verify(child);
                if (fail == null)
                {
                    GD.Print("[NETSYNC PASS] root arrays, static-child array, and parent canary all correct");
                    Finish(0);
                    return;
                }
                _lastFail = fail;
            }

            if (_elapsed > 25.0)
            {
                int childLen = child?.ChildArray.Length ?? -1;
                GD.Print($"[NETSYNC FAIL] timeout after {_elapsed:F1}s; last mismatch: {_lastFail}; lengths AllDefault={AllDefault.Length} Sparse={Sparse.Length} Dense={Dense.Length} Child={childLen}");
                Finish(1);
            }
        }

        private string Verify(NetArraySyncTestChild child)
        {
            if (ParentCanary != 7) return $"ParentCanary={ParentCanary} expected 7 (corrupted!)";
            for (int i = 0; i < Len; i++)
                if (AllDefault[i] != 0) return $"AllDefault[{i}]={AllDefault[i]} expected 0";
            for (int i = 0; i < Len; i++)
                if (Sparse[i] != SparseVal(i)) return $"Sparse[{i}]={Sparse[i]} expected {SparseVal(i)}";
            for (int i = 0; i < Len; i++)
                if (Dense[i] != DenseVal(i)) return $"Dense[{i}]={Dense[i]} expected {DenseVal(i)}";
            for (int i = 0; i < Len; i++)
                if (child.ChildArray[i] != ChildVal(i)) return $"Child[{i}]={child.ChildArray[i]} expected {ChildVal(i)}";
            return null;
        }

        private void Finish(int code)
        {
            _done = true;
            GD.Print("[NETSYNC DONE]");
            GetTree().Quit(code);
        }
    }
}
