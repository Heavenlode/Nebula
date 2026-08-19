using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Pins the thread-locality of SpawnSerializer's shared spawn-table scratch.
///
/// Those buffers are static so that every SpawnSerializer instance can share one allocation, and
/// each is filled and consumed within a single Export/Import call chain. That is only sound while
/// one such chain runs at a time -- which stopped being guaranteed when worlds gained the option to
/// tick on their own threads. Two worlds exporting concurrently would otherwise interleave writes
/// into one spawn table, and the symptom is a client desync well downstream of the actual fault,
/// not a crash at the scene of it.
///
/// The specific regression guarded here is subtle enough to be worth a test rather than a comment:
/// [ThreadStatic] silently does not combine with a field initializer. An initializer runs only on
/// the first thread to touch the type, so every *other* thread sees null -- converting these fields
/// back to `[ThreadStatic] private static List&lt;T&gt; _x = new()` would look correct, compile, and
/// pass every single-threaded test. Hence the lazy-init properties, and hence these assertions run
/// on threads that have provably never touched the type before.
/// </summary>
[NebulaUnitTest]
public class SpawnSerializerThreadScratchTests
{
    private const BindingFlags Scratch = BindingFlags.NonPublic | BindingFlags.Static;

    private static object GetScratch(string propertyName)
    {
        var property = typeof(SpawnSerializer).GetProperty(propertyName, Scratch);
        Assert.NotNull(property);
        return property.GetValue(null);
    }

    /// <summary>Runs <paramref name="body"/> on a brand-new thread and returns its result.</summary>
    private static T OnFreshThread<T>(Func<T> body)
    {
        T result = default;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { failure = e; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "worker thread did not finish");
        if (failure != null) throw failure;
        return result;
    }

    [NebulaUnitTest]
    public void TestScratchIsNonNullOnAThreadThatNeverTouchedTheType()
    {
        foreach (var name in new[] { "NestedSceneBuffer", "NestedDataBuffer", "AllLocalNestedScenes" })
        {
            // Touch the type here first, deliberately. Under the field-initializer trap the FIRST
            // thread to reach the type does run the initializer and gets a valid buffer -- it is
            // every subsequent thread that silently sees null. Without this line the test would
            // pass or fail purely on whether it happened to run before any other consumer.
            Assert.NotNull(GetScratch(name));

            var onWorker = OnFreshThread(() => GetScratch(name));
            Assert.NotNull(onWorker);
        }
    }

    [NebulaUnitTest]
    public void TestEachThreadGetsItsOwnScratchInstance()
    {
        foreach (var name in new[] { "NestedSceneBuffer", "NestedDataBuffer", "AllLocalNestedScenes" })
        {
            var a = OnFreshThread(() => GetScratch(name));
            var b = OnFreshThread(() => GetScratch(name));

            Assert.NotNull(a);
            Assert.NotNull(b);
            // Reference inequality is the whole point: two concurrent exports must not be able to
            // reach the same buffer.
            Assert.False(ReferenceEquals(a, b), $"{name} was shared between two threads");
        }
    }

    [NebulaUnitTest]
    public void TestScratchWritesDoNotLeakAcrossThreads()
    {
        // The list buffers: a write on one thread must be invisible to another.
        var countSeenByFreshThread = OnFreshThread(() =>
        {
            var mine = (List<NetworkController>)GetScratch("NestedSceneBuffer");
            mine.Add(null);          // element value is irrelevant; only the count is observed
            return mine.Count;
        });
        Assert.Equal(1, countSeenByFreshThread);

        // A second thread must start from its own empty buffer, not inherit the entry above.
        var countOnNextThread = OnFreshThread(() =>
            ((List<NetworkController>)GetScratch("NestedSceneBuffer")).Count);
        Assert.Equal(0, countOnNextThread);
    }

    [NebulaUnitTest]
    public void TestNestedDataCountIsPerThread()
    {
        // _nestedDataCount is a bare [ThreadStatic] int rather than a lazy property (default 0 is
        // already the correct per-thread initial value), so it is checked separately: it pairs with
        // NestedDataBuffer and would desynchronize the table if it ever became genuinely shared.
        var field = typeof(SpawnSerializer).GetField("_nestedDataCount", Scratch);
        Assert.NotNull(field);

        OnFreshThread<object>(() => { field.SetValue(null, 7); return null; });

        var onAnotherThread = OnFreshThread(() => (int)field.GetValue(null));
        Assert.Equal(0, onAnotherThread);
    }
}
