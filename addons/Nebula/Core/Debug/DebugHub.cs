using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
    /// <summary>
    /// The process-wide debug channel: one loopback TCP listener that carries
    /// world announcements and every world's debug traffic, multiplexed by the
    /// worldId in each frame header (see <see cref="DebugFrame"/>).
    ///
    /// <para>This replaces two older mechanisms: a separate ENet "discovery"
    /// host on a fixed port that told clients which port each world listened
    /// on, and a per-world <see cref="TcpListener"/> that actually carried the
    /// data. One socket per process handles both, and works identically on
    /// servers and clients.</para>
    ///
    /// <para>Started only when asked for, via <c>--debugPort=N</c> (what the
    /// editor's Play button passes). Dedicated servers expose nothing.</para>
    /// </summary>
    public sealed class DebugHub
    {
        /// <summary>
        /// Frames allowed to sit in the writer queue before lossy ones start
        /// getting dropped. A slow reader must never apply backpressure to the
        /// tick loop.
        /// </summary>
        private const int MaxQueuedFrames = 512;

        /// <summary>Cap on frames buffered before the first client attaches.</summary>
        private const int MaxPendingFrames = 256;

        /// <summary>
        /// One queued frame. <see cref="Data"/> is rented from
        /// <see cref="ArrayPool{T}.Shared"/> and is therefore usually LONGER than the
        /// frame — always write <see cref="Length"/> bytes, never Data.Length.
        ///
        /// <para>Ownership passes to the queue on enqueue and back to the pool once the
        /// frame has been broadcast or dropped, so steady-state framing allocates
        /// nothing. Every exit path (broadcast, lossy trim, backstop drop, Stop) must
        /// return the array exactly once.</para>
        /// </summary>
        private readonly struct QueuedFrame
        {
            public readonly byte[] Data;
            public readonly int Length;
            public readonly bool Lossy;

            public QueuedFrame(byte[] data, int length, bool lossy)
            {
                Data = data;
                Length = length;
                Lossy = lossy;
            }
        }

        private static void ReturnFrame(in QueuedFrame frame)
        {
            if (frame.Data != null)
                ArrayPool<byte>.Shared.Return(frame.Data);
        }

        private TcpListener _listener;

        private readonly List<TcpClient> _clients = new();
        private readonly object _clientsLock = new();
        private int _clientCount;

        /// <summary>
        /// Scratch lists for <see cref="Broadcast"/>, which copies the client list
        /// out of the lock before writing to it. Reused rather than allocated per
        /// frame, and touched only by the writer thread, so they need no
        /// synchronization of their own.
        /// </summary>
        private readonly List<TcpClient> _broadcastSnapshot = new();
        private readonly List<TcpClient> _failedClients = new();

        private readonly List<WorldRunner> _worlds = new();

        private readonly Queue<QueuedFrame> _queue = new();
        /// <summary>Reused staging queue for <see cref="TrimLossyFrames"/>. Guarded by <see cref="_queueLock"/>.</summary>
        private readonly Queue<QueuedFrame> _trimScratch = new();
        private readonly object _queueLock = new();
        private readonly ManualResetEventSlim _queueSignal = new(false);
        private Thread _writerThread;
        private volatile bool _running;
        private long _droppedFrames;

        /// <summary>
        /// Frames produced before any client attached. Replayed once to the
        /// first client to connect — the integration harness relies on this,
        /// since spawn events fire long before a test finishes connecting.
        /// These hold rented arrays too, on the same ownership rules as
        /// <see cref="_queue"/>.
        /// </summary>
        private readonly List<QueuedFrame> _pendingFrames = new();
        private bool _hasFlushedPending;

        /// <summary>The port actually bound, or 0 when not running.</summary>
        public int BoundPort { get; private set; }

        public bool IsRunning => _listener != null;

        /// <summary>
        /// The gate every emitter checks. False means the debug channel costs
        /// nothing beyond this property read.
        /// </summary>
        public bool HasClients => Volatile.Read(ref _clientCount) > 0;

        /// <summary>
        /// Binds the listener. <paramref name="port"/> (from
        /// <c>--debugPort=</c>) is used verbatim and never falls back, so the
        /// editor and the test harness can rely on the port they chose.
        /// </summary>
        public bool Start(int port)
        {
            if (_listener != null)
                return true;

            if (port <= 0 || !TryBind(port))
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                    $"Debug channel: port {port} is unavailable; debugging disabled for this process");
                return false;
            }

            _running = true;
            _writerThread = new Thread(WriterLoop)
            {
                Name = "NebulaDebugHubWriter",
                IsBackground = true,
            };
            _writerThread.Start();

            Debugger.Instance.Log(Debugger.DebugLevel.INFO, $"Debug channel listening on 127.0.0.1:{BoundPort}");
            return true;
        }

        private bool TryBind(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                BoundPort = port;
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            _queueSignal.Set();

            if (_writerThread != null)
            {
                _writerThread.Join(500);
                _writerThread = null;
            }

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { /* already gone */ }
                }
                _clients.Clear();
                Volatile.Write(ref _clientCount, 0);
            }

            try { _listener?.Stop(); } catch { /* already gone */ }
            _listener = null;
            BoundPort = 0;

            lock (_queueLock)
            {
                while (_queue.Count > 0)
                    ReturnFrame(_queue.Dequeue());
                foreach (var frame in _pendingFrames)
                    ReturnFrame(frame);
                _pendingFrames.Clear();
            }
        }

        /// <summary>
        /// Accepts new debug clients and prunes dead ones. Driven from
        /// <see cref="NetRunner._Process"/> rather than _PhysicsProcess, so it
        /// runs even before the network has been started — the editor and the
        /// test harness both connect during startup.
        /// </summary>
        public void Poll()
        {
            if (_listener == null)
                return;

            try
            {
                while (_listener.Pending())
                    AcceptClient();
            }
            catch (Exception ex)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Debug channel: accept failed: {ex.Message}");
            }

            PruneDisconnectedClients();
        }

        private void AcceptClient()
        {
            var client = _listener.AcceptTcpClient();
            client.NoDelay = true;
            client.SendTimeout = 1000;

            lock (_clientsLock)
            {
                _clients.Add(client);
                Volatile.Write(ref _clientCount, _clients.Count);
            }

            Debugger.Instance.Log("Debug channel: client attached", Debugger.DebugLevel.VERBOSE);

            // Announcements and the replay buffer go through the queue rather
            // than a direct write, so the writer thread stays the only producer
            // on these sockets. A client that attaches later therefore re-sends
            // announcements to everyone; readers treat a repeat announcement
            // for a known world as a no-op.
            foreach (var world in _worlds)
                EnqueueWorldAnnounce(world);

            FlushPendingFrames();
        }

        private void PruneDisconnectedClients()
        {
            lock (_clientsLock)
            {
                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    var client = _clients[i];
                    bool dead;
                    try
                    {
                        // Readable with nothing available == the peer closed.
                        dead = !client.Connected ||
                               (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0);
                    }
                    catch
                    {
                        dead = true;
                    }

                    if (!dead)
                        continue;

                    try { client.Close(); } catch { /* already gone */ }
                    _clients.RemoveAt(i);
                }
                Volatile.Write(ref _clientCount, _clients.Count);
            }
        }

        // ─── World registration ──────────────────────────────────────────────

        /// <summary>
        /// Called from <see cref="WorldRunner._Ready"/>. Registering from the
        /// world (rather than reading NetRunner.Worlds) is what makes this work
        /// on clients, where Worlds is never populated and the world lives only
        /// in WorldRunner.CurrentWorld.
        /// </summary>
        public void RegisterWorld(WorldRunner world)
        {
            if (world == null || _worlds.Contains(world))
                return;
            _worlds.Add(world);
            if (HasClients)
                EnqueueWorldAnnounce(world);
        }

        /// <summary>
        /// Re-announces a world whose id changed (a client learning its world
        /// on migration). The old entry is retired first so an attached
        /// debugger replaces its panel instead of accumulating a stale one.
        /// </summary>
        public void ReannounceWorld(WorldRunner world, UUID previousId)
        {
            if (world == null || !_worlds.Contains(world) || !HasClients)
                return;

            if (!previousId.IsEmpty)
            {
                using var payload = new NetBuffer(16, usePool: false);
                Enqueue(previousId, WorldRunner.DebugDataType.WORLD_REMOVED, payload, lossy: false);
            }

            EnqueueWorldAnnounce(world);
        }

        public void UnregisterWorld(WorldRunner world)
        {
            if (world == null || !_worlds.Remove(world))
                return;
            if (!HasClients)
                return;

            using var payload = new NetBuffer(16, usePool: false);
            Enqueue(world.WorldId, WorldRunner.DebugDataType.WORLD_REMOVED, payload, lossy: false);
        }

        private void EnqueueWorldAnnounce(WorldRunner world)
        {
            string scenePath = world.RootScene?.RawNode?.SceneFilePath ?? "";

            // string is length-prefixed UTF-8; size generously, this is rare.
            using var payload = new NetBuffer(scenePath.Length * 4 + 32, usePool: false);
            NetWriter.WriteBool(payload, NetRunner.Instance != null && NetRunner.Instance.IsServer);
            NetWriter.WriteString(payload, scenePath);
            NetWriter.WriteInt32(payload, world.CurrentTick);

            Enqueue(world.WorldId, WorldRunner.DebugDataType.WORLD_ANNOUNCE, payload, lossy: false);
        }

        // ─── Frame production ────────────────────────────────────────────────

        /// <summary>
        /// Queues a frame for delivery. Lossy frames (per-tick state dumps) are
        /// dropped oldest-first when the queue backs up; everything else is
        /// guaranteed, in order.
        /// </summary>
        public void Enqueue(UUID worldId, WorldRunner.DebugDataType type, NetBuffer payload, bool lossy)
        {
            if (_listener == null)
                return;

            // Frames that nobody will ever read are not worth building. Lossy frames
            // are never replayed, so before the first client attaches they cost
            // nothing at all.
            if (!HasClients && (lossy || _hasFlushedPending))
                return;

            var frame = RentFrame(worldId, type, payload.WrittenSpan, lossy);

            if (!HasClients)
            {
                BufferUntilFirstClient(frame);
                return;
            }

            lock (_queueLock)
            {
                _queue.Enqueue(frame);
                if (_queue.Count > MaxQueuedFrames)
                    TrimLossyFrames();

                // Hard backstop. TrimLossyFrames only drops lossy frames, so a
                // client that stops reading entirely (the editor tab is open but
                // the user switched to another main screen) would otherwise let
                // the reliable frames grow without bound.
                while (_queue.Count > MaxQueuedFrames * 4)
                {
                    ReturnFrame(_queue.Dequeue());
                    _droppedFrames++;
                }
            }
            _queueSignal.Set();
        }

        /// <summary>
        /// Serializes one frame into a pooled buffer. The only per-frame cost in
        /// steady state is the copy itself — no allocation.
        /// </summary>
        private static QueuedFrame RentFrame(UUID worldId, WorldRunner.DebugDataType type,
            ReadOnlySpan<byte> payload, bool lossy)
        {
            int frameSize = DebugFrame.FrameSize(payload.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
            int written = DebugFrame.Write(buffer, (byte)type, worldId, payload);
            return new QueuedFrame(buffer, written, lossy);
        }

        private void BufferUntilFirstClient(in QueuedFrame frame)
        {
            lock (_queueLock)
            {
                // Re-checked under the lock: the unsynchronized peek in Enqueue can
                // race a client attaching, and a frame added after the flush would
                // never be delivered or returned.
                if (_hasFlushedPending)
                {
                    ReturnFrame(frame);
                    return;
                }

                if (_pendingFrames.Count >= MaxPendingFrames)
                {
                    ReturnFrame(_pendingFrames[0]);
                    _pendingFrames.RemoveAt(0);
                }
                _pendingFrames.Add(frame);
            }
        }

        private void FlushPendingFrames()
        {
            lock (_queueLock)
            {
                if (_hasFlushedPending || _pendingFrames.Count == 0)
                {
                    _hasFlushedPending = true;
                    return;
                }

                // Ownership of the rented arrays moves from the pending list to the
                // queue; the writer returns them once broadcast.
                foreach (var frame in _pendingFrames)
                    _queue.Enqueue(frame);
                _pendingFrames.Clear();
                _hasFlushedPending = true;
            }
            _queueSignal.Set();
        }

        /// <summary>
        /// Drops the oldest lossy frames until the queue is back under the
        /// watermark, preserving relative order of what survives. Caller holds
        /// <see cref="_queueLock"/>.
        ///
        /// <para>Rebuilds in place through a reused scratch queue rather than
        /// allocating one per trim — this fires exactly when the channel is already
        /// under pressure, which is the worst moment to add GC work.</para>
        /// </summary>
        private void TrimLossyFrames()
        {
            int toDrop = _queue.Count - MaxQueuedFrames;

            while (_queue.Count > 0)
            {
                var frame = _queue.Dequeue();
                if (toDrop > 0 && frame.Lossy)
                {
                    toDrop--;
                    _droppedFrames++;
                    ReturnFrame(frame);
                    continue;
                }
                _trimScratch.Enqueue(frame);
            }

            while (_trimScratch.Count > 0)
                _queue.Enqueue(_trimScratch.Dequeue());
        }

        private void WriterLoop()
        {
            while (_running)
            {
                _queueSignal.Wait(100);
                _queueSignal.Reset();

                while (_running)
                {
                    QueuedFrame frame;
                    lock (_queueLock)
                    {
                        if (_queue.Count == 0)
                            break;
                        frame = _queue.Dequeue();
                    }

                    try
                    {
                        Broadcast(frame.Data, frame.Length);
                    }
                    finally
                    {
                        // The pool must get its array back on every path, or the
                        // "allocation-free" framing quietly becomes allocating.
                        ReturnFrame(frame);
                    }
                }
            }
        }

        /// <summary>
        /// Sends one frame to every attached client.
        ///
        /// <para>The writes deliberately happen OUTSIDE <see cref="_clientsLock"/>.
        /// The main thread takes that lock every frame in <see cref="Poll"/>, so
        /// holding it across a blocking socket write (SendTimeout is 1s) would let
        /// one wedged debug client stall the tick loop for up to a second — exactly
        /// what this writer thread exists to prevent.</para>
        /// </summary>
        /// <param name="length">
        /// Bytes to send. <paramref name="data"/> is pooled and typically longer.
        /// </param>
        private void Broadcast(byte[] data, int length)
        {
            _broadcastSnapshot.Clear();
            lock (_clientsLock)
            {
                if (_clients.Count == 0)
                    return;
                _broadcastSnapshot.AddRange(_clients);
            }

            _failedClients.Clear();
            foreach (var client in _broadcastSnapshot)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        stream.Write(data, 0, length);
                        stream.Flush();
                        continue;
                    }
                }
                catch
                {
                    // Includes the benign race where Stop() or
                    // PruneDisconnectedClients() closed this socket mid-write.
                }

                _failedClients.Add(client);
            }
            _broadcastSnapshot.Clear();

            if (_failedClients.Count == 0)
                return;

            lock (_clientsLock)
            {
                foreach (var client in _failedClients)
                {
                    // Remove-then-close, and only if it's still listed: another
                    // thread may already have retired and closed it while we were
                    // writing.
                    if (_clients.Remove(client))
                    {
                        try { client.Close(); } catch { /* already gone */ }
                    }
                }
                Volatile.Write(ref _clientCount, _clients.Count);
            }
            _failedClients.Clear();
        }
    }
}
