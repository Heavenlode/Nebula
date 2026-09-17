using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ENet;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// One ENet host, its peers, and the thread that services them.
    ///
    /// <para>WHY SHARDS. One host for every peer would put the whole downstream through a single
    /// socket — at 100 peers that is megabytes a second arriving in per-tick bursts, and one stall
    /// past the receive buffer's headroom drops packets in the kernel, which the server then
    /// records as ack timeouts the harness caused. It also stalls every peer together, producing a
    /// correlated gap that looks exactly like a server-side mass timeout. One host PER peer is the
    /// other extreme: <c>enet_host_service</c> costs per call, not per peer, so that is two orders
    /// of magnitude more syscalls to model something the server cannot observe — it sees ENet
    /// peers, not sockets. Sharding buys the decorrelation and the buffer headroom at a fraction of
    /// the cost, and gives each group its own source port, which is closer to what real clients
    /// look like.</para>
    ///
    /// <para>NO GODOT API ON THIS THREAD. It is not Godot's main thread; the engine's singletons
    /// are not safe from it. Timing is <see cref="Stopwatch"/>, output is counters.</para>
    /// </summary>
    public sealed class LoadShard
    {
        /// <summary>
        /// Longest a service call may block. Short enough that a peer deadline is never missed by
        /// more than this, long enough that an idle shard is not spinning.
        /// </summary>
        private const int MaxServiceBlockMs = 4;

        /// <summary>Events drained per service call before re-checking deadlines.</summary>
        private const int MaxEventsPerDrain = 256;

        public readonly int ShardIndex;
        public readonly LoadClientStats Stats = new();

        private readonly LoadClientConfig _config;
        private readonly SyntheticPeer[] _peers;
        private readonly double _tickPeriodMs;
        private readonly int _tps;

        /// <summary>ENet peer id to the synthetic peer holding it; rewritten on every connect.</summary>
        private readonly Dictionary<uint, SyntheticPeer> _peersByEnetId = new();

        /// <summary>Reused for every inbound packet, grown only if something exceeds it.</summary>
        private byte[] _receiveBuffer = new byte[2048];

        private Host _host;
        private Address _address;
        private Thread _thread;
        private volatile bool _stopping;
        private readonly Stopwatch _clock = new();

        /// <summary>Set when the thread has torn its host down, so shutdown can wait for it.</summary>
        private readonly ManualResetEventSlim _finished = new(false);

        private Exception _failure;

        public LoadShard(int shardIndex, LoadClientConfig config, int tps, Func<SyntheticInputSource> sourceFactory)
        {
            ShardIndex = shardIndex;
            _config = config;
            _tickPeriodMs = 1000.0 / Math.Max(1, tps);
            _tps = tps;

            int count = config.PeersInShard(shardIndex);
            int firstPeer = config.FirstPeerIndexOfShard(shardIndex);
            _peers = new SyntheticPeer[count];
            for (int i = 0; i < count; i++)
            {
                _peers[i] = new SyntheticPeer(firstPeer + i, sourceFactory?.Invoke());
            }
        }

        public IReadOnlyList<SyntheticPeer> Peers => _peers;

        /// <summary>The failure that killed this shard's thread, or null.</summary>
        public Exception Failure => Volatile.Read(ref _failure);

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"NebulaLoadShard{ShardIndex}",
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _finished.Wait(TimeSpan.FromSeconds(5));
        }

        private void Run()
        {
            try
            {
                _clock.Start();
                _host = new Host();
                _address = new Address { Port = (ushort)_config.Port };
                _address.SetHost(_config.Address);

                // channelLimit must be NetRunner.MaxChannels, not the handful this peer actually
                // uses: ENet negotiates min(requested, host limit) and refuses a send above it, so a
                // small limit would make the test-harness channel unreachable. Matching a real
                // client also means the SERVER allocates identical per-peer channel state, leaving
                // the thing being measured unchanged.
                // address: null — a client host binds an ephemeral port rather than listening.
                _host.Create(
                    address: null,
                    peerLimit: _peers.Length,
                    channelLimit: NetRunner.MaxChannels,
                    incomingBandwidth: 0,
                    outgoingBandwidth: 0,
                    bufferSize: LoadClientConfig.ShardSocketBufferBytes);

                ScheduleRamp();
                Loop();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ex);
            }
            finally
            {
                TearDown();
                _finished.Set();
            }
        }

        /// <summary>
        /// Spreads this shard's connects across the ramp using each peer's GLOBAL index, so the
        /// whole run ramps at the configured rate rather than each shard ramping at it separately.
        /// </summary>
        private void ScheduleRamp()
        {
            foreach (var peer in _peers)
            {
                peer.ConnectAtMs = peer.PeerIndex * 1000.0 / _config.RampPerSec;
            }
        }

        private void Loop()
        {
            double connectTimeoutMs = _config.ConnectTimeoutSec * 1000.0;

            // Each peer runs its own tick, phase-offset within the shard. Without the offset a
            // shard fires every peer's traffic in one burst at its own boundary, which deepens the
            // server's inbound queue and inflates its p99 — the harness manufacturing a regression.
            // Real clients are separate processes and their sends are uncorrelated.
            var nextTickMs = new double[_peers.Length];
            for (int i = 0; i < _peers.Length; i++)
            {
                nextTickMs[i] = _peers.Length == 0 ? 0 : i * _tickPeriodMs / _peers.Length;
            }

            while (!_stopping)
            {
                double now = _clock.Elapsed.TotalMilliseconds;

                double earliest = double.MaxValue;
                for (int i = 0; i < _peers.Length; i++)
                {
                    var peer = _peers[i];
                    if (peer.Phase == SyntheticPeer.PeerPhase.Idle)
                    {
                        earliest = Math.Min(earliest, peer.ConnectAtMs);
                    }
                    else if (peer.Phase is SyntheticPeer.PeerPhase.Acking or SyntheticPeer.PeerPhase.InWorld)
                    {
                        earliest = Math.Min(earliest, nextTickMs[i]);
                    }
                }

                int blockMs = earliest == double.MaxValue
                    ? MaxServiceBlockMs
                    : Math.Clamp((int)(earliest - now), 0, MaxServiceBlockMs);

                Service(blockMs);

                now = _clock.Elapsed.TotalMilliseconds;

                for (int i = 0; i < _peers.Length; i++)
                {
                    var peer = _peers[i];

                    if (peer.Phase == SyntheticPeer.PeerPhase.Idle)
                    {
                        if (now >= peer.ConnectAtMs)
                        {
                            var enetId = peer.Connect(_host, _address, NetRunner.MaxChannels, now);
                            if (enetId.HasValue) _peersByEnetId[enetId.Value] = peer;
                        }
                        continue;
                    }

                    peer.CheckConnectTimeout(now, connectTimeoutMs);

                    if (peer.Phase is not (SyntheticPeer.PeerPhase.Acking or SyntheticPeer.PeerPhase.InWorld))
                    {
                        continue;
                    }

                    if (now < nextTickMs[i]) continue;

                    Stats.RecordShardLag(now - nextTickMs[i]);

                    // Advance past every deadline already missed rather than replaying them: a
                    // backlog of catch-up ticks would send a burst that misrepresents the load.
                    do { nextTickMs[i] += _tickPeriodMs; } while (nextTickMs[i] <= now);

                    peer.Tick(_tps, Stats);
                }

                _host.Flush();
            }
        }

        private void Service(int timeoutMs)
        {
            // Service blocks in select() until a packet arrives or the timeout expires, so an idle
            // shard costs nothing; CheckEvents then drains what else is already queued without
            // blocking again.
            if (_host.Service(timeoutMs, out Event netEvent) < 0) return;
            Dispatch(ref netEvent);

            for (int drained = 0; drained < MaxEventsPerDrain; drained++)
            {
                if (_host.CheckEvents(out netEvent) <= 0) break;
                Dispatch(ref netEvent);
            }
        }

        private void Dispatch(ref Event netEvent)
        {
            switch (netEvent.Type)
            {
                case EventType.None:
                    return;

                case EventType.Connect:
                    PeerFor(netEvent.Peer)?.OnConnected(Stats);
                    return;

                case EventType.Disconnect:
                case EventType.Timeout:
                    PeerFor(netEvent.Peer)?.OnDisconnected(netEvent.Data, Stats);
                    return;

                case EventType.Receive:
                    try
                    {
                        var peer = PeerFor(netEvent.Peer);
                        if (peer != null)
                        {
                            int length = netEvent.Packet.Length;
                            if (length > _receiveBuffer.Length)
                            {
                                _receiveBuffer = new byte[Math.Max(length, _receiveBuffer.Length * 2)];
                            }
                            // Marshal.Copy rather than a raw span over Packet.Data: the project does
                            // not enable unsafe blocks, and one reusable buffer per shard costs
                            // nothing after the first packet.
                            System.Runtime.InteropServices.Marshal.Copy(
                                netEvent.Packet.Data, _receiveBuffer, 0, length);
                            peer.OnPacket(
                                netEvent.ChannelID,
                                new ReadOnlySpan<byte>(_receiveBuffer, 0, length),
                                Stats);
                        }
                    }
                    finally
                    {
                        // Every received packet must be destroyed, on every path out.
                        netEvent.Packet.Dispose();
                    }
                    return;
            }
        }

        /// <summary>
        /// Resolves the ENet peer to the synthetic peer that opened it.
        ///
        /// <para>Via an explicit map, NOT by assuming ENet's peer id equals our own index. The two
        /// agree only while connects happen in index order and nothing disconnects — ENet allocates
        /// from a free list, so one peer dropping and another connecting silently swaps two peers'
        /// identities, which is precisely the state a soak spends its time in.</para>
        /// </summary>
        private SyntheticPeer PeerFor(Peer peer)
        {
            return _peersByEnetId.TryGetValue(peer.ID, out var found) ? found : null;
        }

        private void TearDown()
        {
            if (_host == null) return;
            foreach (var peer in _peers) peer.Disconnect();
            _host.Flush();
            _host.Dispose();
            _host = null;
        }
    }
}
