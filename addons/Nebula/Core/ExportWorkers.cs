using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nebula.Diagnostics;

namespace Nebula
{
    /// <summary>
    /// The export worker threads of one world: N long-lived threads, each an export lane
    /// (1..N; the tick thread is lane 0), that run the tick's per-peer export job alongside
    /// the tick thread. Per tick: <see cref="Run"/> wakes every worker, runs the job on the
    /// calling thread as well, waits for the workers, merges their profiler shards and
    /// rethrows the first worker failure - an Export exception aborts the tick exactly as it
    /// did when the loop was serial, instead of leaving the tick thread waiting forever.
    ///
    /// The job claims peers from a shared atomic counter, so lanes balance themselves and no
    /// peer is ever assigned to a lane. Threads are stopped by <see cref="Dispose"/> when the
    /// world leaves the tree.
    /// </summary>
    internal sealed class ExportWorkers : IDisposable
    {
        private readonly Thread[] _threads;
        private readonly ExportContext[] _contexts;
        private readonly TickProfiler[] _profilerShards;
        private readonly SemaphoreSlim[] _go;
        private readonly CountdownEvent _done;
        private Action<ExportContext> _job;
        private Exception _failure;
        private volatile bool _stopping;

        /// <summary>Number of worker lanes (the tick thread is not counted).</summary>
        public int Count => _threads.Length;

        /// <summary>Test seam: peers exported by each worker lane so far.</summary>
        public int PeersExportedByWorkersForTests
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _contexts.Length; i++) total += _contexts[i].PeersExported;
                return total;
            }
        }

        public ExportWorkers(int count, UUID worldId, bool profiling)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            _threads = new Thread[count];
            _contexts = new ExportContext[count];
            _profilerShards = profiling ? new TickProfiler[count] : null;
            _go = new SemaphoreSlim[count];
            _done = new CountdownEvent(count);
            for (int i = 0; i < count; i++)
            {
                int lane = i + 1;
                _contexts[i] = new ExportContext(lane);
                if (_profilerShards != null) _profilerShards[i] = new TickProfiler();
                _go[i] = new SemaphoreSlim(0);
                var worker = i;
                _threads[i] = new Thread(() => WorkerLoop(worker))
                {
                    IsBackground = true,
                    Name = $"NebulaExport-{worldId}-{lane}",
                };
                _threads[i].Start();
            }
        }

        private void WorkerLoop(int worker)
        {
            // Bound once for the thread's life: the lane number and the profiler shard are
            // properties of the thread, not of any one tick.
            _profilerShards?[worker].MakeCurrent();
            while (true)
            {
                _go[worker].Wait();
                if (_stopping) return;
                try
                {
                    ExportContext.Run(_contexts[worker], _job);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _failure, e, null);
                }
                finally
                {
                    _done.Signal();
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="job"/> on every worker and on the calling thread (as
        /// <paramref name="tickContext"/>), returns when all lanes are done, merges the
        /// workers' profiler shards into <paramref name="profiler"/> (may be null), and
        /// rethrows the first worker failure.
        /// </summary>
        public void Run(Action<ExportContext> job, ExportContext tickContext, TickProfiler profiler)
        {
            _job = job;
            _failure = null;
            _done.Reset();
            for (int i = 0; i < _go.Length; i++) _go[i].Release();

            try
            {
                ExportContext.Run(tickContext, job);
            }
            finally
            {
                // The workers must finish before the tick thread touches shared state again,
                // whether or not this thread's share threw.
                _done.Wait();
                _job = null;
            }

            if (profiler != null && _profilerShards != null)
            {
                for (int i = 0; i < _profilerShards.Length; i++)
                {
                    profiler.MergeFrom(_profilerShards[i]);
                }
            }

            var failure = _failure;
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        public void Dispose()
        {
            _stopping = true;
            for (int i = 0; i < _go.Length; i++) _go[i].Release();
            for (int i = 0; i < _threads.Length; i++) _threads[i].Join();
            for (int i = 0; i < _go.Length; i++) _go[i].Dispose();
            _done.Dispose();
        }
    }
}
