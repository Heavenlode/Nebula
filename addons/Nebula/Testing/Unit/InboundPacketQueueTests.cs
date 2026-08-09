using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Covers the per-world inbound packet queue.
///
/// The ENet pump runs on the main thread and used to apply acks, inputs and net functions to a
/// world inline as it read them. That is only sound while worlds also tick on the main thread; with
/// per-world thread groups the pump can be writing into a world that is simultaneously mid-tick on
/// its own thread. So the pump became a pure router: it queues here, and the world drains on its own
/// thread at the top of its next physics frame.
///
/// These tests pin the queue's mechanics -- FIFO order, the bounded-drain contract, overflow
/// behavior, and the fact that drained slots release their payload -- without needing a live world,
/// since applying a packet requires peer state the unit harness has no way to build.
/// </summary>
[NebulaUnitTest]
public class InboundPacketQueueTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Channel 4 is the World channel, which ApplyInboundPacket deliberately has no case for. That
    /// lets a drain exercise the ring's bookkeeping without touching peer state that cannot be
    /// constructed here.
    /// </summary>
    private const byte InertChannel = 4;

    private static int Capacity =>
        (int)typeof(WorldRunner)
            .GetField("InboundQueueCapacity", BindingFlags.NonPublic | BindingFlags.Static)
            .GetRawConstantValue();

    private static int CountOf(WorldRunner world) =>
        (int)typeof(WorldRunner).GetField("_inboundCount", Hidden).GetValue(world);

    private static void Drain(WorldRunner world) =>
        typeof(WorldRunner).GetMethod("DrainInboundPackets", Hidden).Invoke(world, null);

    private static WorldRunner NewWorld() => new() { WorldId = new UUID() };

    /// <summary>
    /// Rents a payload and enqueues it, matching the pump's ownership contract: every payload
    /// handed to the queue MUST come from ArrayPool&lt;byte&gt;.Shared, because the queue returns
    /// it there on drop, after apply, or at teardown.
    /// </summary>
    private static void Enqueue(WorldRunner world, byte fill = 1, int length = 1)
    {
        var payload = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        payload[0] = fill;
        world.EnqueueInboundPacket(default, InertChannel, payload, length);
    }

    [NebulaUnitTest]
    public void TestEnqueueThenDrainEmptiesTheQueue()
    {
        var world = NewWorld();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                Enqueue(world, fill: (byte)i);
            }
            Assert.Equal(8, CountOf(world));

            Drain(world);
            Assert.Equal(0, CountOf(world));
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestDrainedSlotsReleaseTheirPayload()
    {
        var world = NewWorld();
        try
        {
            Enqueue(world, length: 64);
            Drain(world);

            // A drained slot must not keep holding its payload: a world that goes quiet after a
            // burst would otherwise pin up to a full ring of packets until those slots are reused.
            var ring = (System.Array)typeof(WorldRunner).GetField("_inboundPackets", Hidden).GetValue(world);
            var slot = ring.GetValue(0);
            var payload = slot.GetType().GetField("Payload").GetValue(slot);
            Assert.Null(payload);
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestOverflowDropsRatherThanGrowing()
    {
        var world = NewWorld();
        try
        {
            // Deliberately overshoot: an unbounded queue would turn a traffic burst into a memory
            // problem, so the contract is drop-and-warn at the cap.
            for (int i = 0; i < Capacity + 50; i++)
            {
                Enqueue(world);
            }

            Assert.Equal(Capacity, CountOf(world));

            Drain(world);
            Assert.Equal(0, CountOf(world));
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestRingWrapsAroundCorrectly()
    {
        var world = NewWorld();
        try
        {
            // Push the head/tail past the end of the backing array several times over. A modulo
            // slip here would silently reorder or duplicate packets rather than fail outright.
            for (int round = 0; round < 3; round++)
            {
                int batch = (Capacity * 2) / 3;
                for (int i = 0; i < batch; i++)
                {
                    Enqueue(world, fill: (byte)i);
                }
                Assert.Equal(batch, CountOf(world));
                Drain(world);
                Assert.Equal(0, CountOf(world));
            }
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestSteadyStateEnqueueDrainAllocatesNothing()
    {
        var world = NewWorld();
        try
        {
            // Reflection Invoke allocates, so bind the drain once as a delegate for the
            // measured loop.
            var drain = (System.Action)System.Delegate.CreateDelegate(
                typeof(System.Action), world,
                typeof(WorldRunner).GetMethod("DrainInboundPackets", Hidden));

            // Warm up: first rents populate the pool's thread-local buckets, lazy statics run.
            for (int i = 0; i < 8; i++) Enqueue(world);
            drain();

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 32; round++)
            {
                for (int i = 0; i < 8; i++) Enqueue(world);
                drain();
            }
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            // The whole point of pooling the payloads and re-attaching one parse wrapper: a busy
            // server's inbound path must produce zero garbage per packet.
            Assert.True(allocated == 0, $"steady-state enqueue/drain allocated {allocated} bytes");
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestConcurrentEnqueuesAreNotLost()
    {
        var world = NewWorld();
        try
        {
            // The pump enqueues from the main thread while, in principle, other producers exist;
            // the lock has to account every packet exactly once.
            const int threads = 4;
            const int perThread = 100;

            var workers = new List<Thread>();
            using var start = new ManualResetEventSlim(false);
            for (int t = 0; t < threads; t++)
            {
                var thread = new Thread(() =>
                {
                    start.Wait();
                    for (int i = 0; i < perThread; i++)
                    {
                        Enqueue(world);
                    }
                });
                thread.Start();
                workers.Add(thread);
            }

            start.Set();
            foreach (var thread in workers)
            {
                Assert.True(thread.Join(10_000), "producer thread did not finish");
            }

            Assert.Equal(threads * perThread, CountOf(world));
        }
        finally { world.Free(); }
    }
}

/// <summary>
/// Covers NetRunner's main-thread marshalling queue: the mechanism by which work that originates on
/// a world's tick thread (peer registry teardown, and world creation once it is async) gets back
/// onto the main thread, where the SceneTree and the shared peer registries can be touched safely.
/// </summary>
[NebulaUnitTest]
public class MainThreadWorkQueueTests
{
    private static void Drain() =>
        typeof(NetRunner)
            .GetMethod("DrainMainThreadWork", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(NetRunner.Instance, null);

    [NebulaUnitTest]
    public void TestRunsInlineWhenAlreadyOnMainThread()
    {
        // Callers on the main thread must not be deferred by a frame -- that would add latency to
        // every peer join and leave for no reason, and would change behavior with the thread-group
        // flag off.
        bool ran = false;
        NetRunner.Instance.RunOnMainThread(() => ran = true);
        Assert.True(ran);
    }

    [NebulaUnitTest]
    public void TestDefersWorkQueuedFromAnotherThread()
    {
        bool ran = false;

        var worker = new Thread(() => NetRunner.Instance.RunOnMainThread(() => ran = true));
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");

        // Queued, not executed: the whole point is that it waits for a main-thread turn.
        Assert.False(ran);

        Drain();
        Assert.True(ran);
    }

    [NebulaUnitTest]
    public void TestAThrowingItemDoesNotStopTheRest()
    {
        // One bad deferred item must not strand every other peer's teardown behind it.
        bool laterRan = false;

        var worker = new Thread(() =>
        {
            NetRunner.Instance.RunOnMainThread(() => throw new System.InvalidOperationException("boom"));
            NetRunner.Instance.RunOnMainThread(() => laterRan = true);
        });
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");

        Drain();
        Assert.True(laterRan);
    }

    private static async Task HopAndRecordThread(System.Action<int> record)
    {
        await NetRunner.Instance.SwitchToMainThread();
        record(System.Environment.CurrentManagedThreadId);
    }

    [NebulaUnitTest]
    public void TestSwitchToMainThreadCompletesInlineOnMainThread()
    {
        Assert.True(NetRunner.Instance.SwitchToMainThread().GetAwaiter().IsCompleted);

        // And therefore an await runs straight through with no drain needed.
        bool ran = false;
        var hop = HopAndRecordThread(_ => ran = true);
        Assert.True(ran);
        Assert.True(hop.IsCompletedSuccessfully);
    }

    [NebulaUnitTest]
    public void TestSwitchToMainThreadResumesOnMainThreadAtDrain()
    {
        int resumedOnThread = 0;
        Task hop = null;
        var worker = new Thread(() => hop = HopAndRecordThread(id => resumedOnThread = id));
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");

        Assert.False(hop.IsCompleted);

        Drain();
        Assert.True(hop.Wait(10_000), "hop did not complete after a main-thread drain");

        // The regression this pins: a TaskCompletionSource-based hop completed *after* main
        // signalled but resumed on the ThreadPool, because a world thread has no
        // SynchronizationContext for the continuation to come back through. The awaitable must
        // resume the caller ON the draining thread itself.
        Assert.Equal(System.Environment.CurrentManagedThreadId, resumedOnThread);
    }

    // ---- Allocation-free form -------------------------------------------------------------------

    /// <summary>A work item that is also the thing being changed, which is the whole point: handing
    /// over `this` costs nothing, where a lambda would allocate.</summary>
    private sealed class Recorder : IMainThreadWork
    {
        internal readonly List<int> Tags = new();
        internal int Thread;

        public void OnMainThread(int tag)
        {
            Tags.Add(tag);
            Thread = System.Environment.CurrentManagedThreadId;
        }
    }

    [NebulaUnitTest]
    public void TestWorkItemRunsInlineWhenAlreadyOnMainThread()
    {
        var recorder = new Recorder();
        NetRunner.Instance.RunOnMainThread(recorder, 7);

        Assert.Equal(new[] { 7 }, recorder.Tags);
    }

    [NebulaUnitTest]
    public void TestWorkItemDefersAndCarriesItsTag()
    {
        var recorder = new Recorder();

        var worker = new Thread(() => NetRunner.Instance.RunOnMainThread(recorder, 42));
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");

        Assert.Empty(recorder.Tags);

        Drain();
        Assert.Equal(new[] { 42 }, recorder.Tags);
        Assert.Equal(System.Environment.CurrentManagedThreadId, recorder.Thread);
    }

    /// <summary>
    /// The two forms share one queue, so they must also share one order. A closure queued before a
    /// work item has to run before it -- otherwise a peer join and the node change that depends on it
    /// could swap places.
    /// </summary>
    [NebulaUnitTest]
    public void TestBothFormsKeepOneOrder()
    {
        var order = new List<string>();
        var recorder = new Recorder();

        var worker = new Thread(() =>
        {
            NetRunner.Instance.RunOnMainThread(() => order.Add("action"));
            NetRunner.Instance.RunOnMainThread(recorder, 1);
            NetRunner.Instance.RunOnMainThread(() => order.Add("after"));
        });
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");

        Drain();

        Assert.Equal(new[] { "action", "after" }, order);
        Assert.Equal(new[] { 1 }, recorder.Tags);
    }

    /// <summary>
    /// The reason this overload exists: deferring through the work-item form must allocate NOTHING,
    /// where the closure form allocates a delegate (and a display class) every single time.
    ///
    /// Measured on the WORKER, deliberately. The main-thread path short-circuits to an inline call and
    /// never touches the queue, so measuring there would pass no matter what the queued path did --
    /// which is exactly the false confidence this test exists to avoid.
    ///
    /// The closure figure is asserted too, as the control: if it ever reads zero the measurement itself
    /// has stopped working, and a green "allocates nothing" would mean nothing.
    /// </summary>
    [NebulaUnitTest]
    public void TestWorkItemFormAllocatesNothing()
    {
        var recorder = new Recorder();

        // Grow the queue's backing array first and drain it. Queue<T> keeps its capacity across a
        // drain, so the measured run below cannot be charged for a resize it did not cause.
        var warm = new Thread(() =>
        {
            for (int i = 0; i < 1024; i++) NetRunner.Instance.RunOnMainThread(recorder, i);
        });
        warm.Start();
        Assert.True(warm.Join(10_000), "warm-up thread did not finish");
        Drain();

        long workItemBytes = 0;
        long closureBytes = 0;

        var worker = new Thread(() =>
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++) NetRunner.Instance.RunOnMainThread(recorder, i);
            workItemBytes = System.GC.GetAllocatedBytesForCurrentThread() - before;

            before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++) NetRunner.Instance.RunOnMainThread(() => recorder.Thread = i);
            closureBytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
        });
        worker.Start();
        Assert.True(worker.Join(10_000), "worker thread did not finish");
        Drain();

        Assert.True(workItemBytes == 0,
            $"work-item hop allocated {workItemBytes} bytes over 256 deferrals; it must allocate none");
        Assert.True(closureBytes > 0,
            "the closure form allocated nothing either, so this measurement is not measuring anything");
    }
}
