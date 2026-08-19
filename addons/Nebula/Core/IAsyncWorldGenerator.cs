using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// Implemented by a world's root scene when building that world is expensive enough that it
    /// must not happen inside a single frame.
    ///
    /// <para><see cref="NetRunner.CreateWorld"/> calls <see cref="GenerateWorldAsync"/> after the
    /// world is in the tree and <c>_NetworkPrepare</c>/<c>_WorldReady</c> have run, but before the
    /// world starts ticking or admits any peer. The task it returns is what the caller's
    /// <c>await CreateWorld(...)</c> is really waiting on, so a world is only ever handed out
    /// fully built -- and a generation failure surfaces as a faulted task rather than as a world
    /// that quietly came up empty.</para>
    ///
    /// <para><b>Implementations own their own threading.</b> Nebula does not move this call off the
    /// main thread, because only the implementation knows which parts of its work are pure
    /// computation and which have to touch the SceneTree. The shape that works:</para>
    ///
    /// <list type="bullet">
    /// <item>Do the expensive computation on a worker (<c>Task.Factory.StartNew(...,
    /// TaskCreationOptions.LongRunning, TaskScheduler.Default)</c>), building nodes that are
    /// <b>not</b> in the SceneTree. Godot permits creating nodes and resources off-thread; it is
    /// tree insertion that must not happen there.</item>
    /// <item>Come back to the main thread to attach what you built --
    /// <see cref="WorldRunner.Spawn"/> allocates NetIds, mutates the world's scene registry and
    /// walks the peer list, none of which is safe off-thread.</item>
    /// <item>Spread that attach phase across frames if it is large; a thousand colliders landing in
    /// one frame is its own stall, just a different one.</item>
    /// </list>
    ///
    /// <para>Awaiting pure I/O (an RPC, an upload) directly on the main thread is fine and does not
    /// need a worker -- it does not block anything.</para>
    /// </summary>
    public interface IAsyncWorldGenerator
    {
        /// <summary>
        /// Builds this world's contents. Throw to fail creation: the world is then torn down and
        /// deregistered, and the caller's task faults.
        /// </summary>
        /// <param name="ct">
        /// Cancelled if creation is abandoned (for example a generation timeout). Honour it on any
        /// long loop or remote call so a wedged generation cannot pin a world in
        /// <see cref="WorldRunner.WorldLifecycle.Generating"/> forever.
        /// </param>
        Task GenerateWorldAsync(CancellationToken ct);
    }
}
