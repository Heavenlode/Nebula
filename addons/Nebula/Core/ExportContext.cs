using System.Collections.Generic;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
    /// <summary>
    /// The scratch one export lane needs while it assembles one peer's packet. There is one per
    /// lane: the tick thread's own (lane 0) and one per export worker. Reused across peers and
    /// ticks, never shared between lanes, so two lanes can export two peers at the same time
    /// without touching each other's buffers.
    ///
    /// Reachable as <see cref="Current"/> from code that is handed no context - SpawnSerializer's
    /// CommitExport reports nested spawn riders through WorldRunner.NoteNestedSpawnRider - in the
    /// same way TickProfiler.Current and NetworkController's per-peer scope are thread-bound.
    /// </summary>
    internal sealed class ExportContext
    {
        /// <summary>The lane exporting on this thread; null outside an export.</summary>
        [System.ThreadStatic] private static ExportContext _current;
        public static ExportContext Current => _current;

        /// <summary>
        /// Lane number of the calling thread: 0 on the tick thread and outside any export
        /// (a direct Export from a test), 1..N on an export worker. Indexes the serializers'
        /// per-lane scratch.
        /// </summary>
        public static int CurrentLane => _current?.Lane ?? 0;

        private static int? _laneCount;

        /// <summary>
        /// Number of lanes that can export at once: the export workers
        /// (Nebula/config/threading/export_workers) plus the tick thread. Read from the
        /// setting on first use, which is before any world ticks; serializers size their
        /// per-lane scratch from it at construction. Settable for tests only.
        /// </summary>
        public static int LaneCount
        {
            get => _laneCount ??= NetRunner.ExportWorkerCount + 1;
            set => _laneCount = value;
        }

        /// <summary>Binds <paramref name="ctx"/> to this thread around <paramref name="job"/>.</summary>
        public static void Run(ExportContext ctx, System.Action<ExportContext> job)
        {
            ctx.MakeCurrent();
            try
            {
                job(ctx);
            }
            finally
            {
                ClearCurrent();
            }
        }

        /// <summary>Lane number: 0 is the tick thread, 1..N the export workers.</summary>
        public readonly int Lane;

        public ExportContext(int lane)
        {
            Lane = lane;
        }

        /// <summary>Binds this context to the calling thread for the duration of its export work.</summary>
        public void MakeCurrent() => _current = this;
        public static void ClearCurrent() => _current = null;

        /// <summary>
        /// Per-peer partition of the tick's node snapshot: the nodes this peer has input authority
        /// over, and everything else. ExportPartition.Partition clears them on entry.
        /// </summary>
        public readonly List<NetworkController> OwnedList = new(8);
        public readonly List<NetworkController> SharedList = new(64);

        /// <summary>Where each serializer writes its section before it is charged and appended.</summary>
        public readonly NetBuffer TempSerializerBuffer = new();

        /// <summary>Hierarchical bitmask of the nodes with a section in the packet being assembled.</summary>
        public readonly long[] UpdatedNodesMask = NodeIdUtils.CreateMasks();

        /// <summary>Per peer-local node id: the sections appended so far, and which serializers ran.</summary>
        public readonly Dictionary<ushort, NetBuffer> NodeBuffers = new();
        public readonly Dictionary<ushort, byte> NodeSerializersList = new();

        /// <summary>Pool behind NodeBuffers, keyed by peer-local id; lives as long as the lane.</summary>
        public readonly Dictionary<ushort, NetBuffer> NodeBufferPool = new();

        /// <summary>
        /// Controller behind each peer-local node id that has a section in the packet being
        /// assembled. Written on a node's first section; only ever read behind a set bit of
        /// <see cref="UpdatedNodesMask"/>, so entries left over from an earlier peer are never
        /// observed.
        /// </summary>
        public readonly NetworkController[] NodeControllers = new NetworkController[NodeIdUtils.MAX_NETWORK_NODES];

        /// <summary>
        /// Nested scenes that rode an ancestor's spawn table in the packet being assembled without
        /// committing a section of their own (reported via WorldRunner.NoteNestedSpawnRider).
        /// Registered into the ack ring after the mask walk, minus any that also committed a
        /// section. Cleared per peer.
        /// </summary>
        public readonly List<NetworkController> NestedRiders = new(16);
        public readonly long[] RiderMask = NodeIdUtils.CreateMasks();

        /// <summary>The peer being exported, for the NEBULA_TRACE_WIRE lines.</summary>
        public UUID TraceWirePeer;

        /// <summary>Peers this lane has exported since it was created (test seam: proves a lane ran).</summary>
        public int PeersExported;

        private static bool _loggedUnpreparedInsert;

        /// <summary>
        /// A serializer had to insert a per-peer entry from inside an export lane: the
        /// PreparePeer pass missed it. Harmless with one lane, a dictionary race with more,
        /// so it is a bug either way. Silent outside an export (a direct Export from a test
        /// has no prepare pass), logged once per run inside one.
        /// </summary>
        public static void NoteUnpreparedInsert(string site)
        {
            if (_current == null || _loggedUnpreparedInsert) return;
            _loggedUnpreparedInsert = true;
            Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                $"[ExportPrepare] BUG: {site} inserted per-peer state inside an export lane; PreparePeer did not cover it. Further occurrences suppressed.");
        }
    }
}
