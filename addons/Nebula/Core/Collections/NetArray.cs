using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

namespace Nebula.Serialization
{
    /// <summary>
    /// Sync operation flags for NetArray serialization protocol.
    /// </summary>
    [Flags]
    public enum NetArraySyncFlags : byte
    {
        /// <summary>Full array sync (initial or reset)</summary>
        Full = 0,
        /// <summary>Delta sync - only changed indices</summary>
        Delta = 1,
        /// <summary>Chunked sync - partial array for initial sync</summary>
        Chunked = 2,
        /// <summary>Length change - array was resized</summary>
        Resized = 4,
        /// <summary>Chunked sync with delta updates for already-sent indices</summary>
        ChunkedWithDelta = 8,
    }

    /// <summary>
    /// Information about what changed in a NetArray during a network update.
    /// Used for NotifyOnChange callbacks on NetArray properties.
    ///
    /// SPARSE-SYNC SEMANTICS: for the initial (chunked) sync, this enumerates ONLY the explicitly-sent
    /// non-default entries. Indices covered by an initial-sync window but left at their default value
    /// (including an index the server reset from non-default → default) are applied to the array via a
    /// window zero-fill but are NOT listed in <see cref="ChangedIndices"/>/<see cref="AddedValues"/>.
    /// A consumer that must observe complete state therefore has to reconcile from the array itself,
    /// not diff this ChangeInfo — a diff-based handler will miss default-valued and reverted indices.
    /// Delta (post-initial) syncs are unaffected: they enumerate every changed index as before.
    /// </summary>
    public readonly struct NetArrayChangeInfo<T> where T : struct
    {
        /// <summary>
        /// Values that were deleted from the end of the array (when array shrinks).
        /// Empty array if no elements were deleted.
        /// </summary>
        public readonly T[] DeletedValues;

        /// <summary>
        /// Indices that had their values changed.
        /// Includes both delta updates and chunked sync updates.
        /// </summary>
        public readonly int[] ChangedIndices;

        /// <summary>
        /// The actual values that were added to the end of the array (when array grows).
        /// Empty array if no elements were added.
        /// </summary>
        public readonly T[] AddedValues;

        public NetArrayChangeInfo(T[] deletedValues, int[] changedIndices, T[] addedValues)
        {
            DeletedValues = deletedValues ?? Array.Empty<T>();
            ChangedIndices = changedIndices ?? Array.Empty<int>();
            AddedValues = addedValues ?? Array.Empty<T>();
        }

        /// <summary>
        /// Returns true if there were any changes.
        /// </summary>
        public bool HasChanges => DeletedValues.Length > 0 || ChangedIndices.Length > 0 || AddedValues.Length > 0;

        /// <summary>
        /// Creates an empty change info (no changes).
        /// </summary>
        public static NetArrayChangeInfo<T> Empty => new(null, null, null);
    }

    /// <summary>
    /// Per-peer synchronization state for a NetArray.
    /// Stored as a struct to avoid allocation.
    /// </summary>
    internal struct PeerSyncState
    {
        /// <summary>
        /// How much of the array has been ACKNOWLEDGED by the peer.
        /// Only advances when the peer acks the chunk.
        /// </summary>
        public int AckedUpToIndex;

        /// <summary>
        /// How much we've sent (pending ack). May be ahead of AckedUpToIndex.
        /// On ack, this becomes the new AckedUpToIndex.
        /// </summary>
        public int PendingSyncIndex;

        /// <summary>
        /// The array length when we last synced to this peer.
        /// Used to detect length changes.
        /// </summary>
        public int LastSyncedLength;

        /// <summary>
        /// Whether the peer has completed initial sync (all chunks acked).
        /// </summary>
        public bool InitialSyncComplete;

        /// <summary>
        /// Whether we have pending (unacked) chunk data.
        /// </summary>
        public bool HasPendingChunk;

        /// <summary>
        /// Per-peer copy of dirty element bits that have been sent to this peer but not
        /// yet acknowledged. Global dirty bits are merged in at each export, so a single
        /// peer's ack can no longer erase resend state that other peers still need.
        /// Lazily allocated on first merge.
        /// </summary>
        public ulong[] PendingDirty;

        /// <summary>
        /// Tick of the last export that included PendingDirty elements for this peer.
        /// An ack only clears PendingDirty when it covers this tick — an older ack proves
        /// nothing about packets still in flight.
        /// </summary>
        public Tick LastSendTick;

        /// <summary>
        /// Dirty elements that did not fit in the last delta's byte budget and have NOT been sent.
        /// Kept apart from <see cref="PendingDirty"/> because an ack clears that set wholesale on the
        /// assumption everything in it was written by <see cref="LastSendTick"/> -- an unsent element
        /// left in it would be erased by the first ack and never reach the peer. Merged back into the
        /// pending set at the next export, where it is re-stamped with that tick.
        /// </summary>
        public ulong[] DeferredDirty;

        /// <summary>
        /// Tick of the last chunk send. Chunk progress only commits on an ack covering it.
        /// </summary>
        public Tick ChunkSentTick;

        public static PeerSyncState Create() => new PeerSyncState
        {
            AckedUpToIndex = 0,
            PendingSyncIndex = 0,
            LastSyncedLength = 0,
            InitialSyncComplete = false,
            HasPendingChunk = false,
            PendingDirty = null,
            LastSendTick = -1,
            DeferredDirty = null,
            ChunkSentTick = -1
        };
    }

    /// <summary>
    /// A network-synchronized array that tracks element-level changes for efficient delta sync.
    /// Only modified indices are sent over the network, significantly reducing bandwidth for large arrays.
    /// As an INetSerializable object it owns its per-peer sync state and self-filters each tick, so it
    /// needs no per-mutation dirty callback; the generator seeds its reference into the property cache
    /// at init (inline initialization bypasses the property setter).
    /// </summary>
    /// <typeparam name="T">Element type (must be a supported primitive or Godot struct)</typeparam>
    public sealed class NetArray<T> : INetSerializable<NetArray<T>>, INetExportAware, IEnumerable<T> where T : struct
    {
        private T[] _data;
        // BOOL SPECIALIZATION: when T == bool, elements are bit-packed into _bits (64 bools/word, ~128 B
        // for 1024) and _data stays null. All bool branches key on `typeof(T) == typeof(bool)`, a JIT
        // compile-time constant per instantiation, so the branch is eliminated for non-bool T (zero cost)
        // and the element machinery below is byte-for-byte unchanged.
        private ulong[] _bits;
        private ulong[] _dirtyMask; // Bit array for tracking dirty indices (per-element, valid for bool too)
        private int _length;
        private bool _isFullDirty; // True if entire array needs sync (e.g., after resize)

        /// <summary>
        /// Client-side: tracks how many elements have been received during chunked sync.
        /// Used to correctly identify "added" elements across multiple chunks.
        /// Reset to -1 when not in chunked sync.
        /// </summary>
        private int _clientReceivedUpTo = -1;

        /// <summary>
        /// Per-peer synchronization state. Keyed by peer UUID.
        /// </summary>
        private Dictionary<UUID, PeerSyncState> _peerState;

        /// <summary>
        /// Information about the most recent network change.
        /// Populated during deserialization, used by NotifyOnChange callbacks.
        /// </summary>
        public NetArrayChangeInfo<T> LastChangeInfo { get; internal set; }

        /// <summary>
        /// Creates a new NetArray with the specified capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of elements</param>
        /// <param name="initialLength">Initial length (defaults to 0). If specified, elements are initialized to default(T).</param>
        public NetArray(int capacity = 64, int initialLength = 0)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");
            if (initialLength < 0 || initialLength > capacity)
                throw new ArgumentOutOfRangeException(nameof(initialLength), "Initial length must be between 0 and capacity");

            if (typeof(T) == typeof(bool))
                _bits = new ulong[(capacity + 63) / 64];
            else
                _data = new T[capacity];
            _dirtyMask = new ulong[(capacity + 63) / 64]; // Round up to nearest 64-bit block
            _length = initialLength;
            _isFullDirty = initialLength > 0; // Mark dirty if we have initial data
        }

        /// <summary>
        /// Creates a NetArray from an existing array.
        /// </summary>
        public NetArray(T[] source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            _dirtyMask = new ulong[(source.Length + 63) / 64];
            _length = source.Length;
            _isFullDirty = true; // Mark all as dirty for initial sync

            if (typeof(T) == typeof(bool))
            {
                _bits = new ulong[(source.Length + 63) / 64];
                for (int i = 0; i < source.Length; i++)
                {
                    if (Unsafe.As<T, bool>(ref source[i]))
                        _bits[i >> 6] |= 1UL << (i & 63);
                }
            }
            else
            {
                _data = new T[source.Length];
                Array.Copy(source, _data, source.Length);
            }
        }

        /// <summary>
        /// Number of elements currently in the array.
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Maximum capacity of the array.
        /// </summary>
        public int Capacity => typeof(T) == typeof(bool) ? _bits.Length * 64 : _data.Length;

        /// <summary>
        /// Gets or sets an element at the specified index.
        /// Setting marks the index as dirty for network sync.
        /// </summary>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException($"Index {index} out of range [0, {_length})");
                if (typeof(T) == typeof(bool))
                {
                    bool b = (_bits[index >> 6] & (1UL << (index & 63))) != 0;
                    return Unsafe.As<bool, T>(ref b);
                }
                return _data[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException($"Index {index} out of range [0, {_length})");

                if (typeof(T) == typeof(bool))
                {
                    bool nv = Unsafe.As<T, bool>(ref value);
                    int w = index >> 6;
                    ulong m = 1UL << (index & 63);
                    if (((_bits[w] & m) != 0) != nv) // only mark dirty on real change
                    {
                        if (nv) _bits[w] |= m; else _bits[w] &= ~m;
                        MarkDirty(index);
                    }
                    return;
                }

                // Only mark dirty if value actually changed
                if (!EqualityComparer<T>.Default.Equals(_data[index], value))
                {
                    _data[index] = value;
                    MarkDirty(index);
                }
            }
        }

        /// <summary>
        /// Sets an element without checking if it changed (always marks dirty).
        /// Use when you know the value is different to avoid comparison overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetUnchecked(int index, T value)
        {
            if ((uint)index >= (uint)_length)
                throw new IndexOutOfRangeException($"Index {index} out of range [0, {_length})");

            if (typeof(T) == typeof(bool))
                WriteBit(index, Unsafe.As<T, bool>(ref value));
            else
                _data[index] = value;
            MarkDirty(index);
        }

        /// <summary>
        /// Adds an element to the end of the array.
        /// </summary>
        public void Add(T item)
        {
            if (_length >= Capacity)
                throw new InvalidOperationException($"Array is at capacity ({Capacity})");

            if (typeof(T) == typeof(bool))
                WriteBit(_length, Unsafe.As<T, bool>(ref item));
            else
                _data[_length] = item;
            MarkDirty(_length);
            _length++;
            _isFullDirty = true; // Length changed
        }

        /// <summary>
        /// Sets the length of the array.
        /// If increasing, new elements are default(T).
        /// If decreasing, excess elements are discarded.
        /// </summary>
        public void SetLength(int newLength)
        {
            if (newLength < 0 || newLength > Capacity)
                throw new ArgumentOutOfRangeException(nameof(newLength));

            if (newLength != _length)
            {
                // Clear removed elements
                if (newLength < _length)
                {
                    if (typeof(T) == typeof(bool))
                        ClearBitRange(newLength, _length);
                    else
                        Array.Clear(_data, newLength, _length - newLength);
                }

                _length = newLength;
                _isFullDirty = true; // Length changed, need full sync
            }
        }

        /// <summary>
        /// Clears all elements (sets length to 0).
        /// </summary>
        public void Clear()
        {
            if (_length > 0)
            {
                if (typeof(T) == typeof(bool))
                    Array.Clear(_bits, 0, _bits.Length);
                else
                    Array.Clear(_data, 0, _length);
                Array.Clear(_dirtyMask, 0, _dirtyMask.Length);
                _length = 0;
                _isFullDirty = true;
            }
        }

        /// <summary>
        /// Gets a span of the current elements (read-only access, doesn't mark dirty).
        /// </summary>
        public ReadOnlySpan<T> AsSpan()
        {
            if (typeof(T) == typeof(bool))
                throw new NotSupportedException("AsSpan is not supported for NetArray<bool> (bit-packed); use the indexer.");
            return _data.AsSpan(0, _length);
        }

        /// <summary>
        /// Copies elements to a destination array.
        /// </summary>
        public void CopyTo(T[] destination, int startIndex = 0)
        {
            if (typeof(T) == typeof(bool))
                throw new NotSupportedException("CopyTo is not supported for NetArray<bool> (bit-packed); use the indexer.");
            Array.Copy(_data, 0, destination, startIndex, _length);
        }

        /// <summary>
        /// Sets an element from network data without marking it dirty.
        /// Used by the deserializer to avoid re-syncing received data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetFromNetwork(int index, T value)
        {
            if ((uint)index < (uint)_length)
            {
                if (typeof(T) == typeof(bool))
                    WriteBit(index, Unsafe.As<T, bool>(ref value));
                else
                    _data[index] = value;
            }
        }

        #region Bit Backing (bool only)

        // These operate on the packed _bits store; only ever called when T == bool.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ReadBit(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBit(int index, bool value)
        {
            int w = index >> 6;
            ulong m = 1UL << (index & 63);
            if (value) _bits[w] |= m; else _bits[w] &= ~m;
        }

        // Clears bits in [from, to). Word-aligned interior clears could be bulkier, but this only runs on
        // resize/shrink (cold path).
        private void ClearBitRange(int from, int to)
        {
            for (int i = from; i < to; i++)
                _bits[i >> 6] &= ~(1UL << (i & 63));
        }

        #endregion

        #region Dirty Tracking

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkDirty(int index)
        {
            int block = index / 64;
            int bit = index % 64;
            _dirtyMask[block] |= (1UL << bit);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsDirty(int index)
        {
            int block = index / 64;
            int bit = index % 64;
            return (_dirtyMask[block] & (1UL << bit)) != 0;
        }

        /// <summary>
        /// Returns true if any elements have been modified since last sync.
        /// </summary>
        public bool HasDirtyElements()
        {
            if (_isFullDirty) return true;

            for (int i = 0; i < _dirtyMask.Length; i++)
            {
                if (_dirtyMask[i] != 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Clears all dirty flags. Called after successful sync.
        /// </summary>
        public void ClearDirty()
        {
            Array.Clear(_dirtyMask, 0, _dirtyMask.Length);
            _isFullDirty = false;
        }

        /// <summary>
        /// Called once per server tick after Export has run for every peer.
        /// By this point each exported peer has absorbed the global dirty bits into its
        /// own PendingDirty, so the global set can be cleared. Peers that join later get
        /// a full chunked sync and don't rely on these bits.
        /// </summary>
        public void OnExportComplete()
        {
            ClearDirty();
        }

        /// <summary>
        /// Marks the entire array as needing a full sync.
        /// </summary>
        public void MarkFullDirty()
        {
            _isFullDirty = true;
        }

        /// <summary>
        /// Returns true if a full sync is needed (after resize or initial sync).
        /// </summary>
        public bool NeedsFullSync => _isFullDirty;

        /// <summary>
        /// Returns the count of dirty indices.
        /// </summary>
        public int DirtyCount
        {
            get
            {
                int count = 0;
                for (int block = 0; block < _dirtyMask.Length; block++)
                {
                    var mask = _dirtyMask[block];
                    // Only count bits within valid length
                    int maxBitInBlock = Math.Min(64, _length - block * 64);
                    if (maxBitInBlock <= 0) break;
                    if (maxBitInBlock < 64)
                    {
                        mask &= (1UL << maxBitInBlock) - 1;
                    }
                    count += BitOperations.PopCount(mask);
                }
                return count;
            }
        }

        #endregion

        #region IEnumerable

        public IEnumerator<T> GetEnumerator()
        {
            if (typeof(T) == typeof(bool))
            {
                for (int i = 0; i < _length; i++)
                {
                    bool b = ReadBit(i);
                    yield return Unsafe.As<bool, T>(ref b);
                }
                yield break;
            }
            for (int i = 0; i < _length; i++)
                yield return _data[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Per-Peer State Management

        /// <summary>
        /// Gets or creates the sync state for a specific peer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref PeerSyncState GetOrCreatePeerState(UUID peerId)
        {
            _peerState ??= new Dictionary<UUID, PeerSyncState>();

            if (!_peerState.ContainsKey(peerId))
            {
                _peerState[peerId] = PeerSyncState.Create();
            }

            // Note: We need to get the value, modify it, and put it back since it's a struct
            // This is a limitation of Dictionary with struct values
            return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_peerState, peerId, out _);
        }

        /// <summary>
        /// Creates a content-identical copy of <paramref name="source"/> to serve one peer whose
        /// view is about to diverge (see NetProperty.PerPeerState). Backs the fork-on-first-scoped-
        /// access path in NetworkController.TryGetPerPeerArray.
        ///
        /// The peer's <see cref="PeerSyncState"/> is MOVED out of <paramref name="source"/> into the
        /// fork rather than copied. That entry records what the peer has already acked, and because
        /// the fork's contents are byte-identical to the base at this moment, it remains exactly
        /// correct: a fork with no subsequent mutation sends nothing, and a fork that is then mutated
        /// ships an ordinary delta. Without the move the fork would start with an empty _peerState,
        /// NetworkSerialize would see InitialSyncComplete == false, and the whole array would be
        /// re-chunked to a client that already holds identical bytes.
        ///
        /// Removing the entry from the base also stops the base array from tracking a peer it no
        /// longer serves.
        /// </summary>
        internal static NetArray<T> ForkForPeer(NetArray<T> source, UUID peerId)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var fork = new NetArray<T>(source.Capacity);
            fork._length = source._length;

            if (typeof(T) == typeof(bool))
                Array.Copy(source._bits, fork._bits, Math.Min(source._bits.Length, fork._bits.Length));
            else
                Array.Copy(source._data, fork._data, Math.Min(source._data.Length, fork._data.Length));

            // Carry the base's un-exported dirty state across. A base mutation earlier in this same
            // tick is present in the copied contents but has NOT reached the peer yet, so its dirty
            // bit has to come along or the fork would silently swallow it. (Base mutations AFTER the
            // fork are a different matter - those deliberately never reach this peer.)
            Array.Copy(source._dirtyMask, fork._dirtyMask, Math.Min(source._dirtyMask.Length, fork._dirtyMask.Length));
            fork._isFullDirty = source._isFullDirty;

            if (source._peerState != null && source._peerState.TryGetValue(peerId, out var state))
            {
                source._peerState.Remove(peerId);
                fork._peerState = new Dictionary<UUID, PeerSyncState> { [peerId] = state };
            }

            return fork;
        }

        /// <summary>Test seam: whether this instance still tracks sync state for a peer.</summary>
        internal bool HasPeerStateForTests(UUID peerId) => _peerState != null && _peerState.ContainsKey(peerId);

        /// <summary>
        /// Checks if we have state for a peer and they've completed initial sync.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasCompletedInitialSync(UUID peerId)
        {
            if (_peerState == null) return false;
            if (!_peerState.TryGetValue(peerId, out var state)) return false;
            return state.InitialSyncComplete;
        }

        #endregion

        #region Network Serialization

        /// <summary>
        /// Serializes the array to the network buffer for a specific peer.
        /// Returns true if data was written, false if nothing to send.
        /// Uses chunked sync for initial data and delta sync for changes.
        /// </summary>
        public static bool NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetArray<T> obj, NetBuffer buffer, int maxBytes)
        {
            if (obj == null)
            {
                // Null array - write empty full sync
                NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Full);
                NetWriter.WriteInt32(buffer, 0);
                return true; // We wrote data
            }

            var peerId = NetRunner.Instance.GetPeerId(peer);
            ref var state = ref obj.GetOrCreatePeerState(peerId);
            Tick currentTick = currentWorld.CurrentTick;

            // Merge global dirty bits into this peer's pending set. Every connected peer
            // is exported each tick, so each absorbs the bits before OnExportComplete
            // clears the global mask at end of tick.
            obj.MergeDirtyIntoPending(ref state, currentTick);

            // Check if we need to restart initial sync (array was resized or marked for full sync)
            if (state.InitialSyncComplete && (obj._length != state.LastSyncedLength || obj._isFullDirty))
            {
                state.InitialSyncComplete = false;
                state.AckedUpToIndex = 0;
                state.PendingSyncIndex = 0;
                state.HasPendingChunk = false;
                // Full resync supersedes any pending element resends
                if (state.PendingDirty != null)
                    Array.Clear(state.PendingDirty, 0, state.PendingDirty.Length);
                state.LastSendTick = -1; // pending queue emptied
            }

            // Initial sync not complete - send chunked
            if (!state.InitialSyncComplete)
            {
                return WriteChunkedSync(obj, buffer, ref state, maxBytes, currentTick);
            }

            // Initial sync complete - check if we have pending elements for this peer
            if (CountPendingBits(state.PendingDirty, obj._length) == 0)
            {
                return false; // Nothing to send
            }

            // Send delta sync, within the property's byte budget: what does not fit is deferred to
            // the next tick rather than overrunning the packet.
            WriteDeltaSync(obj, buffer, ref state, currentTick, maxBytes);
            return true;
        }

        /// <summary>
        /// ORs the global dirty mask into the peer's pending set (lazy-allocating it).
        /// Stamps LastSendTick = currentTick whenever it enqueues NEW work: the ack that clears
        /// PendingDirty gates on this tick, so it must mark when work entered the queue, NOT the last
        /// resend. Bumping it on every resend (as the writers used to) let the lagging ack never catch
        /// up, so a changed-once array resent its delta forever.
        /// </summary>
        internal void MergeDirtyIntoPending(ref PeerSyncState state, Tick currentTick)
        {
            bool hasGlobalDirty = false;
            for (int i = 0; i < _dirtyMask.Length; i++)
            {
                if (_dirtyMask[i] != 0) { hasGlobalDirty = true; break; }
            }

            // Elements deferred by the last delta's budget re-enter the queue here, exactly as new
            // global bits do: stamped with THIS tick, so the ack that clears them has to cover the
            // send they will actually be in.
            bool hasDeferred = false;
            if (state.DeferredDirty != null)
            {
                for (int i = 0; i < state.DeferredDirty.Length; i++)
                {
                    if (state.DeferredDirty[i] != 0) { hasDeferred = true; break; }
                }
            }
            if (!hasGlobalDirty && !hasDeferred) return;

            state.PendingDirty ??= new ulong[_dirtyMask.Length];
            for (int i = 0; i < _dirtyMask.Length; i++)
            {
                state.PendingDirty[i] |= _dirtyMask[i];
            }
            if (hasDeferred)
            {
                for (int i = 0; i < state.DeferredDirty.Length && i < state.PendingDirty.Length; i++)
                {
                    state.PendingDirty[i] |= state.DeferredDirty[i];
                }
                Array.Clear(state.DeferredDirty, 0, state.DeferredDirty.Length);
            }
            state.LastSendTick = currentTick; // new bits enqueued this tick
        }

        /// <summary>
        /// Counts set bits within [0, length) in a pending mask. Null mask counts as 0.
        /// </summary>
        private static int CountPendingBits(ulong[] mask, int length)
        {
            if (mask == null) return 0;

            int count = 0;
            for (int block = 0; block < mask.Length; block++)
            {
                var bits = mask[block];
                int maxBitInBlock = Math.Min(64, length - block * 64);
                if (maxBitInBlock <= 0) break;
                if (maxBitInBlock < 64)
                {
                    bits &= (1UL << maxBitInBlock) - 1;
                }
                count += BitOperations.PopCount(bits);
            }
            return count;
        }

        // Cached comparer for the sparse initial-sync default test (mirrors the indexer setter's use).
        private static readonly EqualityComparer<T> _defaultComparer = EqualityComparer<T>.Default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDefault(T value) => _defaultComparer.Equals(value, default);

        /// <summary>
        /// SPARSE initial sync. A chunk still advances a contiguous array-index frontier
        /// (<see cref="PeerSyncState.AckedUpToIndex"/> → <c>windowEnd</c>), so the ack machinery is
        /// unchanged, but within the covered window <c>[startIndex, windowEnd)</c> only the
        /// non-default elements are transmitted as (index, value) pairs; the client zero-fills the
        /// window. An all-default array collapses to a single header-only window (zero entries).
        /// </summary>
        internal static bool WriteChunkedSync(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state, int maxBytes, Tick currentTick)
        {
            if (typeof(T) == typeof(bool))
                return WriteChunkedSyncBool(obj, buffer, ref state, maxBytes, currentTick);

            // If we have a pending (unacked) chunk, re-send from the acked position
            int startIndex = state.AckedUpToIndex;
            int elementSize = ElementSize;

            // First, collect pending indices BELOW startIndex (already sent in previous chunks)
            // These need to be re-sent as delta updates. Reads this PEER's pending set, not
            // the global dirty mask, so other peers' acks can't erase them.
            List<int> dirtyResendIndices = null;
            int pendingBlockCount = state.PendingDirty?.Length ?? 0;
            for (int block = 0; block < pendingBlockCount; block++)
            {
                var mask = state.PendingDirty[block];
                if (mask == 0) continue;

                int baseIndex = block * 64;
                if (baseIndex >= startIndex) break; // Past the already-sent region

                while (mask != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    int index = baseIndex + bit;
                    if (index < startIndex && index < obj._length)
                    {
                        dirtyResendIndices ??= new List<int>();
                        dirtyResendIndices.Add(index);
                    }
                    mask &= mask - 1; // Clear lowest set bit
                }
            }

            int dirtyResendCount = dirtyResendIndices?.Count ?? 0;
            bool hasDirtyResends = dirtyResendCount > 0;

            // Budget in ENTRIES (index + value), not dense elements.
            // Sparse Chunked header: 1(flags)+4(totalLength)+4(windowStart)+4(windowEnd)+2(entryCount) = 15.
            // ChunkedWithDelta adds 2(resendCount) = 17, plus (2 + elementSize) per resend entry.
            int entrySize = 2 + elementSize;
            int headerSize = hasDirtyResends ? 17 : 15;
            int deltaBytes = hasDirtyResends ? dirtyResendCount * entrySize : 0;
            int availableBytes = maxBytes - headerSize - deltaBytes;
            int maxEntries = Math.Max(1, availableBytes / entrySize);

            // Completion keys on the FRONTIER, not entry count: a length-N all-default array has zero
            // entries but must still send its covering window (the only carrier of the array length).
            if (startIndex >= obj._length && !hasDirtyResends)
            {
                state.InitialSyncComplete = true;
                state.LastSyncedLength = obj._length;
                return false; // Truly nothing left to cover
            }

            // Pass 1 (count, allocation-free): non-default entries from startIndex up to the budget,
            // then extend windowEnd greedily over the trailing default run (free -- zero payload).
            int entryCount = 0;
            int windowEnd = startIndex;
            {
                int i = startIndex;
                for (; i < obj._length && entryCount < maxEntries; i++)
                {
                    if (!IsDefault(obj._data[i]))
                    {
                        entryCount++;
                        windowEnd = i + 1;
                    }
                }
                if (entryCount < maxEntries)
                {
                    // Ran off the end without filling the budget -> cover everything remaining.
                    windowEnd = obj._length;
                }
                else
                {
                    // Budget filled -> extend the window over any immediately-following defaults.
                    int j = windowEnd;
                    while (j < obj._length && IsDefault(obj._data[j])) j++;
                    windowEnd = j;
                }
            }

            // Write header - use ChunkedWithDelta if we have dirty resends
            var flags = hasDirtyResends ? NetArraySyncFlags.ChunkedWithDelta : NetArraySyncFlags.Chunked;
            NetWriter.WriteByte(buffer, (byte)flags);
            NetWriter.WriteInt32(buffer, obj._length);  // totalLength
            NetWriter.WriteInt32(buffer, startIndex);   // windowStart
            NetWriter.WriteInt32(buffer, windowEnd);    // windowEnd (client zero-fills [windowStart, windowEnd))
            NetWriter.WriteUInt16(buffer, (ushort)entryCount);

            // Pass 2 (write): only the non-default elements in the window, each with its index.
            // The two passes see the same immutable _data, so the count matches exactly.
            for (int i = startIndex; i < windowEnd; i++)
            {
                if (!IsDefault(obj._data[i]))
                {
                    NetWriter.WriteUInt16(buffer, (ushort)i);
                    WriteElement(buffer, obj._data[i]);
                }
            }

            // Write dirty resends if any.
            // NOTE: We do NOT clear pending bits here - they are cleared when an ack covering the tick
            // they were ENQUEUED (MergeDirtyIntoPending stamps LastSendTick) arrives. We must not stamp
            // LastSendTick here: these bits were already stamped at merge, and re-stamping on every
            // resend would let the lagging ack never catch up (perpetual resend).
            if (hasDirtyResends)
            {
                NetWriter.WriteUInt16(buffer, (ushort)dirtyResendCount);
                foreach (int index in dirtyResendIndices)
                {
                    NetWriter.WriteUInt16(buffer, (ushort)index);
                    WriteElement(buffer, obj._data[index]);
                }
            }

            // Mark this chunk as pending (awaiting ack). Gate on the FRONTIER advancing, not entry
            // count -- a zero-entry all-default window still advances windowEnd and must be tracked;
            // a resend-only send (windowEnd == startIndex) advances nothing (matches dense).
            if (windowEnd > startIndex)
            {
                // Stamp ChunkSentTick only when the frontier reaches NEW ground; a pure resend of the
                // same unacked window keeps the original first-send tick so a lagging ack can catch up.
                bool newGround = windowEnd > state.PendingSyncIndex;
                state.PendingSyncIndex = windowEnd;
                state.HasPendingChunk = true;
                if (newGround) state.ChunkSentTick = currentTick;
            }
            state.LastSyncedLength = obj._length;

            // Check if we're done with initial sync (sent everything and no more to send)
            if (state.PendingSyncIndex >= obj._length && !state.HasPendingChunk)
            {
                state.InitialSyncComplete = true;
            }

            return true; // We wrote data
        }

        /// <summary>Delta header: 1 (flags) + 2 (entry count).</summary>
        private const int DeltaHeaderBytes = 3;

        internal static void WriteDeltaSync(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state, Tick currentTick, int maxBytes = int.MaxValue)
        {
            if (typeof(T) == typeof(bool))
            {
                WriteDeltaSyncBool(obj, buffer, ref state, currentTick, maxBytes);
                return;
            }

            int pendingCount = CountPendingBits(state.PendingDirty, obj._length);

            if (pendingCount == 0)
            {
                // No changes - write empty delta
                NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Delta);
                NetWriter.WriteUInt16(buffer, 0);
                return;
            }

            // BUDGETED. A delta used to write every pending element regardless of the budget the
            // serializer was handed, which was fine for a handful of changed slots and a buffer
            // overflow for a few hundred -- a water grid draining into a dug pit dirtied thousands of
            // cells per publish and overran the packet every tick. Elements past the budget are moved
            // to the peer's deferred set and come back next tick (MergeDirtyIntoPending); the value
            // sent for a deferred element is whatever it holds when it is finally written, so a slot
            // that changed again in the meantime costs nothing extra.
            int entrySize = 2 + ElementSize;
            int maxEntries = maxBytes == int.MaxValue
                ? int.MaxValue
                : Math.Max(1, (maxBytes - DeltaHeaderBytes) / entrySize);
            int toWrite = Math.Min(pendingCount, Math.Min(maxEntries, ushort.MaxValue));

            // Write delta header
            NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Delta);
            NetWriter.WriteUInt16(buffer, (ushort)toWrite);

            // Write this peer's pending indices and values - iterate without LINQ
            int written = 0;
            for (int block = 0; block < state.PendingDirty.Length; block++)
            {
                var mask = state.PendingDirty[block];
                if (mask == 0) continue;

                int baseIndex = block * 64;
                while (mask != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    int index = baseIndex + bit;
                    ulong bitMask = 1UL << bit;
                    mask &= mask - 1; // Clear lowest set bit

                    if (index >= obj._length) continue;

                    if (written < toWrite)
                    {
                        NetWriter.WriteUInt16(buffer, (ushort)index);
                        WriteElement(buffer, obj._data[index]);
                        written++;
                    }
                    else
                    {
                        // Over budget: out of the pending set (which the ack will clear) and into the
                        // deferred set (which it will not).
                        state.DeferredDirty ??= new ulong[state.PendingDirty.Length];
                        state.DeferredDirty[block] |= bitMask;
                        state.PendingDirty[block] &= ~bitMask;
                    }
                }
            }
            // LastSendTick is stamped at enqueue (MergeDirtyIntoPending), not on this resend.
        }

        #region Bool (bit-packed) Wire Format

        // BOOL SPECIALIZATION of the sync path. Elements are bits; the wire carries 64-bit WORDS, and the
        // per-peer PeerSyncState frontier (AckedUpToIndex/PendingSyncIndex) counts WORDS here, not elements.
        // Fast paths: an all-false array sends a header-only window (no words); a fully-populated 1024-bit
        // array caps at ~130 B; steady-state changes send only the touched words (~10 B each). Unlike the
        // element path there is no ChunkedWithDelta -- below-frontier changes made during the (typically
        // single-chunk) initial sync stay in PendingDirty and are flushed by the first delta.

        // Chunked initial sync: a word window [startWord, endWord) written as 8-word groups, each a 1-byte
        // presence mask followed by the non-zero words it flags. The client zero-fills the window.
        internal static bool WriteChunkedSyncBool(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state, int maxBytes, Tick currentTick)
        {
            int startWord = state.AckedUpToIndex;
            int wordCount = (obj._length + 63) >> 6;

            // Completion keys on the WORD frontier covering the array (the window carries the array length
            // even when all-false).
            if (startWord >= wordCount)
            {
                state.InitialSyncComplete = true;
                state.LastSyncedLength = obj._length;
                return false;
            }

            // Pick endWord greedily under budget. Fixed header = 1(flags)+4(len)+4(start)+4(end) = 13.
            // Covering word w costs ~1 mask bit (1 byte per 8 words) + 8 bytes if the word is non-zero.
            int available = maxBytes - 13;
            int endWord = startWord;
            int payloadBytes = 0;
            for (int w = startWord; w < wordCount; w++)
            {
                int windowWords = (w - startWord) + 1;
                int maskBytes = (windowWords + 7) >> 3;
                int wordBytes = obj._bits[w] != 0 ? 8 : 0;
                if (maskBytes + payloadBytes + wordBytes > available && w > startWord)
                    break;
                payloadBytes += wordBytes;
                endWord = w + 1;
            }
            if (endWord == startWord) endWord = startWord + 1; // always advance at least one word

            NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Chunked);
            NetWriter.WriteInt32(buffer, obj._length);  // totalLength (bits)
            NetWriter.WriteInt32(buffer, startWord);
            NetWriter.WriteInt32(buffer, endWord);

            // 8-word groups: [maskByte][non-zero words...]. Interleaved so the reader is single-pass and
            // never buffers an attacker-controlled mask length.
            for (int groupBase = startWord; groupBase < endWord; groupBase += 8)
            {
                int groupEnd = Math.Min(groupBase + 8, endWord);
                byte mask = 0;
                for (int w = groupBase; w < groupEnd; w++)
                    if (obj._bits[w] != 0) mask |= (byte)(1 << (w - groupBase));
                NetWriter.WriteByte(buffer, mask);
                for (int w = groupBase; w < groupEnd; w++)
                    if (obj._bits[w] != 0) NetWriter.WriteUInt64(buffer, obj._bits[w]);
            }

            // Mark chunk pending (awaiting ack). Initial sync completes when an ack covers the frontier
            // reaching wordCount (see OnPeerAcknowledge), mirroring the element path. Stamp ChunkSentTick
            // only when the frontier reaches NEW ground; a pure resend of the same unacked window keeps
            // the original first-send tick so a lagging ack can catch up.
            bool newGround = endWord > state.PendingSyncIndex;
            state.PendingSyncIndex = endWord;
            state.HasPendingChunk = true;
            if (newGround) state.ChunkSentTick = currentTick;
            state.LastSyncedLength = obj._length;
            return true;
        }

        // Delta: only words that have a pending-dirty bit, sent whole as (wordIndex, word).
        internal static void WriteDeltaSyncBool(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state, Tick currentTick, int maxBytes = int.MaxValue)
        {
            int wordCount = (obj._length + 63) >> 6;
            int pendingLen = state.PendingDirty?.Length ?? 0;

            int changedWords = 0;
            for (int w = 0; w < wordCount && w < pendingLen; w++)
                if (state.PendingDirty[w] != 0) changedWords++;

            // Budgeted the same way as the element path: 2 (word index) + 8 (word) per entry.
            const int wordEntrySize = 2 + 8;
            int maxWords = maxBytes == int.MaxValue
                ? int.MaxValue
                : Math.Max(1, (maxBytes - DeltaHeaderBytes) / wordEntrySize);
            int toWrite = Math.Min(changedWords, Math.Min(maxWords, ushort.MaxValue));

            NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Delta);
            NetWriter.WriteUInt16(buffer, (ushort)toWrite);

            int written = 0;
            for (int w = 0; w < wordCount && w < pendingLen; w++)
            {
                if (state.PendingDirty[w] == 0) continue;
                if (written < toWrite)
                {
                    NetWriter.WriteUInt16(buffer, (ushort)w);
                    NetWriter.WriteUInt64(buffer, obj._bits[w]);
                    written++;
                }
                else
                {
                    // Over budget: deferred, see the element path. For bool the pending mask's bits
                    // ARE the changed bits within the word, so the whole word's mask moves across.
                    state.DeferredDirty ??= new ulong[state.PendingDirty.Length];
                    state.DeferredDirty[w] |= state.PendingDirty[w];
                    state.PendingDirty[w] = 0;
                }
            }
            // LastSendTick is stamped at enqueue (MergeDirtyIntoPending), not on this resend.
        }

        // Creates/resizes the client array to hold totalLength bits (mirrors the element read helpers).
        private static NetArray<T> GetOrCreateBoolResult(NetArray<T> existing, int totalLength)
        {
            if (existing == null || existing.Capacity < totalLength)
                return new NetArray<T>(Math.Max(totalLength, 64), totalLength);

            NetArray<T> result = existing;
            if (result._length != totalLength)
            {
                if (totalLength < result._length)
                    result.ClearBitRange(totalLength, result._length);
                result._length = totalLength;
            }
            return result;
        }

        internal static NetArray<T> ReadChunkedSyncBool(NetBuffer buffer, NetArray<T> existing)
        {
            int totalLength = NetReader.ReadInt32(buffer);  // bits
            int startWord = NetReader.ReadInt32(buffer);
            int endWord = NetReader.ReadInt32(buffer);

            int totalWords = (totalLength + 63) >> 6;
            if (totalLength < 0 || startWord < 0 || endWord < startWord || endWord > totalWords)
                return existing ?? new NetArray<T>(64);

            // Capture deleted values (shrink) before resizing.
            int existingLength = existing?._length ?? 0;
            T[] deletedValues = Array.Empty<T>();
            if (existing != null && existingLength > totalLength)
            {
                int deleteCount = existingLength - totalLength;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                {
                    bool b = existing.ReadBit(totalLength + i);
                    deletedValues[i] = Unsafe.As<bool, T>(ref b);
                }
            }

            // Original populated length distinguishes "added" (>= it) from "changed" (< it).
            int originalPopulatedLength;
            if (startWord == 0)
                originalPopulatedLength = existing?._length ?? 0;
            else if (existing != null && existing._clientReceivedUpTo >= 0)
                originalPopulatedLength = existing._clientReceivedUpTo;
            else
                originalPopulatedLength = existing?._length ?? 0;

            NetArray<T> result = GetOrCreateBoolResult(existing, totalLength);
            if (startWord == 0) result._clientReceivedUpTo = originalPopulatedLength;

            var changedList = new List<int>();
            var addedList = new List<T>();

            // Apply 8-word groups; diff old vs new per word to build change-info. Setting every window word
            // to its received value (0 for non-flagged words) is equivalent to zero-fill + apply.
            for (int groupBase = startWord; groupBase < endWord; groupBase += 8)
            {
                int groupEnd = Math.Min(groupBase + 8, endWord);
                byte mask = NetReader.ReadByte(buffer);
                for (int w = groupBase; w < groupEnd; w++)
                {
                    ulong newWord = ((mask >> (w - groupBase)) & 1) != 0 ? NetReader.ReadUInt64(buffer) : 0UL;
                    ulong oldWord = result._bits[w];
                    if (oldWord == newWord) continue;
                    result._bits[w] = newWord;

                    ulong diff = oldWord ^ newWord;
                    while (diff != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(diff);
                        diff &= diff - 1;
                        int index = (w << 6) + bit;
                        if (index >= result._length) continue;
                        if (index >= originalPopulatedLength)
                        {
                            bool nv = (newWord & (1UL << bit)) != 0;
                            addedList.Add(Unsafe.As<bool, T>(ref nv));
                        }
                        else
                        {
                            changedList.Add(index);
                        }
                    }
                }
            }

            if (endWord >= totalWords)
                result._clientReceivedUpTo = -1;

            result.LastChangeInfo = new NetArrayChangeInfo<T>(
                deletedValues,
                changedList.Count > 0 ? changedList.ToArray() : Array.Empty<int>(),
                addedList.Count > 0 ? addedList.ToArray() : Array.Empty<T>()
            );
            result.ClearDirty();
            return result;
        }

        private static NetArray<T> ReadDeltaSyncBool(NetBuffer buffer, NetArray<T> existing)
        {
            int changedWords = NetReader.ReadUInt16(buffer);

            if (existing == null)
            {
                for (int i = 0; i < changedWords; i++)
                {
                    NetReader.ReadUInt16(buffer);
                    NetReader.ReadUInt64(buffer);
                }
                var empty = new NetArray<T>(64);
                empty.LastChangeInfo = NetArrayChangeInfo<T>.Empty;
                return empty;
            }

            var changedList = new List<int>();
            for (int i = 0; i < changedWords; i++)
            {
                int w = NetReader.ReadUInt16(buffer);
                ulong newWord = NetReader.ReadUInt64(buffer);
                if ((uint)w >= (uint)existing._bits.Length) continue;

                ulong oldWord = existing._bits[w];
                if (oldWord == newWord) continue;
                existing._bits[w] = newWord;

                ulong diff = oldWord ^ newWord;
                while (diff != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(diff);
                    diff &= diff - 1;
                    int index = (w << 6) + bit;
                    if (index < existing._length) changedList.Add(index);
                }
            }

            existing.LastChangeInfo = new NetArrayChangeInfo<T>(
                Array.Empty<T>(),
                changedList.Count > 0 ? changedList.ToArray() : Array.Empty<int>(),
                Array.Empty<T>());
            return existing;
        }

        // Bool Full is only emitted for a null array (length 0); it carries no word payload.
        private static NetArray<T> ReadFullSyncBool(NetBuffer buffer, NetArray<T> existing)
        {
            int length = NetReader.ReadInt32(buffer);
            if (length < 0)
                return existing ?? new NetArray<T>(64);

            int previousLength = existing?._length ?? 0;
            T[] deletedValues = Array.Empty<T>();
            if (existing != null && previousLength > length)
            {
                int deleteCount = previousLength - length;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                {
                    bool b = existing.ReadBit(length + i);
                    deletedValues[i] = Unsafe.As<bool, T>(ref b);
                }
            }

            NetArray<T> result = GetOrCreateBoolResult(existing, length); // clears bits on shrink
            result.LastChangeInfo = new NetArrayChangeInfo<T>(deletedValues, Array.Empty<int>(), Array.Empty<T>());
            result.ClearDirty();
            return result;
        }

        #endregion

        /// <summary>
        /// Deserializes the array from the network buffer.
        /// </summary>
        public static NetArray<T> NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, NetArray<T> existing = null)
        {
            var flags = (NetArraySyncFlags)NetReader.ReadByte(buffer);

            // Note: Full = 0, so bitwise AND check (flags & Full) == Full is always true.
            // We must check non-zero flags first and treat Full as the default fallback.
            if ((flags & NetArraySyncFlags.ChunkedWithDelta) == NetArraySyncFlags.ChunkedWithDelta)
            {
                return ReadChunkedWithDeltaSync(buffer, existing);
            }
            else if ((flags & NetArraySyncFlags.Chunked) == NetArraySyncFlags.Chunked)
            {
                return ReadChunkedSync(buffer, existing);
            }
            else if ((flags & NetArraySyncFlags.Delta) == NetArraySyncFlags.Delta)
            {
                return ReadDeltaSync(buffer, existing);
            }
            else // Full = 0, treat as default when no other bits set
            {
                return ReadFullSync(buffer, existing);
            }
        }

        internal static NetArray<T> ReadChunkedSync(NetBuffer buffer, NetArray<T> existing)
        {
            if (typeof(T) == typeof(bool))
                return ReadChunkedSyncBool(buffer, existing);

            int totalLength = NetReader.ReadInt32(buffer);
            int windowStart = NetReader.ReadInt32(buffer);
            int windowEnd = NetReader.ReadInt32(buffer);
            int entryCount = NetReader.ReadUInt16(buffer);

            // Validate network data to prevent crashes from corrupted packets.
            if (totalLength < 0 || windowStart < 0 || windowEnd < windowStart || windowEnd > totalLength || entryCount < 0)
            {
                return existing ?? new NetArray<T>(64);
            }

            // For "added" vs "changed", we need the ORIGINAL length before this sync's first window.
            int originalPopulatedLength;
            if (windowStart == 0)
                originalPopulatedLength = existing?.Length ?? 0;
            else if (existing != null && existing._clientReceivedUpTo >= 0)
                originalPopulatedLength = existing._clientReceivedUpTo;
            else
                originalPopulatedLength = existing?.Length ?? 0;

            // Capture deleted values before they're removed (if array is shrinking)
            T[] deletedValues = Array.Empty<T>();
            int existingLength = existing?.Length ?? 0;
            if (existing != null && existingLength > totalLength)
            {
                int deleteCount = existingLength - totalLength;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                    deletedValues[i] = existing._data[totalLength + i];
            }

            // Create or resize array as needed
            NetArray<T> result;
            if (existing == null || existing.Capacity < totalLength)
            {
                result = new NetArray<T>(Math.Max(totalLength, 64), totalLength);
            }
            else
            {
                result = existing;
                if (result._length != totalLength)
                {
                    if (totalLength < result._length)
                        Array.Clear(result._data, totalLength, result._length - totalLength);
                    result._length = totalLength;
                }
            }

            if (windowStart == 0)
                result._clientReceivedUpTo = originalPopulatedLength;

            // Zero-fill the covered window UNCONDITIONALLY: the window declares [windowStart, windowEnd)
            // default except for the sparse entries below, so any index the server reset to default
            // (or never set) must be cleared here -- sparse only carries non-defaults.
            if (windowEnd > windowStart)
                Array.Clear(result._data, windowStart, windowEnd - windowStart);

            // Apply the sparse entries. Change-info enumerates only these explicitly-sent non-default
            // values (see NetArrayChangeInfo docs) -- allocation is proportional to entries, not window.
            var changedIndices = entryCount > 0 ? new int[entryCount] : Array.Empty<int>();
            var addedValues = entryCount > 0 ? new T[entryCount] : Array.Empty<T>();
            int changedIdx = 0;
            int addedIdx = 0;

            for (int e = 0; e < entryCount; e++)
            {
                int index = NetReader.ReadUInt16(buffer);
                T value = ReadElement(buffer);

                if (index < result._length)
                {
                    result._data[index] = value;
                    if (index >= originalPopulatedLength)
                        addedValues[addedIdx++] = value;
                    else
                        changedIndices[changedIdx++] = index;
                }
            }

            if (changedIdx < changedIndices.Length) Array.Resize(ref changedIndices, changedIdx);
            if (addedIdx < addedValues.Length) Array.Resize(ref addedValues, addedIdx);

            // Sync is complete once the covered frontier reaches the end.
            if (windowEnd >= totalLength)
                result._clientReceivedUpTo = -1;

            result.LastChangeInfo = new NetArrayChangeInfo<T>(deletedValues, changedIndices, addedValues);
            result.ClearDirty();
            return result;
        }

        internal static NetArray<T> ReadChunkedWithDeltaSync(NetBuffer buffer, NetArray<T> existing)
        {
            int totalLength = NetReader.ReadInt32(buffer);
            int windowStart = NetReader.ReadInt32(buffer);
            int windowEnd = NetReader.ReadInt32(buffer);
            int entryCount = NetReader.ReadUInt16(buffer);

            // Validate network data to prevent crashes from corrupted packets.
            if (totalLength < 0 || windowStart < 0 || windowEnd < windowStart || windowEnd > totalLength || entryCount < 0)
            {
                return existing ?? new NetArray<T>(64);
            }

            // For "added" vs "changed", we need the ORIGINAL length before this sync's first window.
            int originalPopulatedLength;
            if (windowStart == 0)
                originalPopulatedLength = existing?.Length ?? 0;
            else if (existing != null && existing._clientReceivedUpTo >= 0)
                originalPopulatedLength = existing._clientReceivedUpTo;
            else
                originalPopulatedLength = existing?.Length ?? 0;

            // Capture deleted values before they're removed (if array is shrinking)
            T[] deletedValues = Array.Empty<T>();
            int existingLength = existing?.Length ?? 0;
            if (existing != null && existingLength > totalLength)
            {
                int deleteCount = existingLength - totalLength;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                    deletedValues[i] = existing._data[totalLength + i];
            }

            // Create or resize array as needed
            NetArray<T> result;
            if (existing == null || existing.Capacity < totalLength)
            {
                result = new NetArray<T>(Math.Max(totalLength, 64), totalLength);
            }
            else
            {
                result = existing;
                if (result._length != totalLength)
                {
                    if (totalLength < result._length)
                        Array.Clear(result._data, totalLength, result._length - totalLength);
                    result._length = totalLength;
                }
            }

            if (windowStart == 0)
                result._clientReceivedUpTo = originalPopulatedLength;

            // Zero-fill the covered window (see ReadChunkedSync), then apply the sparse window entries.
            if (windowEnd > windowStart)
                Array.Clear(result._data, windowStart, windowEnd - windowStart);

            var changedIndicesList = new List<int>();
            var addedValuesList = new List<T>();

            for (int e = 0; e < entryCount; e++)
            {
                int index = NetReader.ReadUInt16(buffer);
                T value = ReadElement(buffer);

                if (index < result._length)
                {
                    result._data[index] = value;
                    if (index >= originalPopulatedLength)
                        addedValuesList.Add(value);
                    else
                        changedIndicesList.Add(index);
                }
            }

            // Read resend updates (changes to already-sent, below-frontier indices). These target
            // indices < windowStart, disjoint from the window above, so no dedup is needed.
            int resendCount = NetReader.ReadUInt16(buffer);
            for (int i = 0; i < resendCount; i++)
            {
                int index = NetReader.ReadUInt16(buffer);
                T value = ReadElement(buffer);

                if (index < result._length)
                {
                    result._data[index] = value;
                    changedIndicesList.Add(index);
                }
            }

            // Sync is complete once the covered frontier reaches the end.
            if (windowEnd >= totalLength)
                result._clientReceivedUpTo = -1;

            result.LastChangeInfo = new NetArrayChangeInfo<T>(
                deletedValues,
                changedIndicesList.Count > 0 ? changedIndicesList.ToArray() : Array.Empty<int>(),
                addedValuesList.Count > 0 ? addedValuesList.ToArray() : Array.Empty<T>()
            );
            result.ClearDirty();
            return result;
        }

        private static NetArray<T> ReadFullSync(NetBuffer buffer, NetArray<T> existing)
        {
            if (typeof(T) == typeof(bool))
                return ReadFullSyncBool(buffer, existing);

            int length = NetReader.ReadInt32(buffer);

            // Validate network data
            if (length < 0)
            {
                Nebula.Utility.Tools.Debugger.Instance.Log(Nebula.Utility.Tools.Debugger.DebugLevel.ERROR,
                    $"[NetArray.ReadFullSync] Invalid length={length}");
                return existing ?? new NetArray<T>(64);
            }

            int previousLength = existing?.Length ?? 0;

            // Capture deleted values before they're removed (if array is shrinking)
            T[] deletedValues = Array.Empty<T>();
            if (existing != null && previousLength > length)
            {
                int deleteCount = previousLength - length;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                {
                    deletedValues[i] = existing._data[length + i];
                }
            }

            if (length == 0)
            {
                NetArray<T> emptyResult;
                if (existing != null)
                {
                    Array.Clear(existing._data, 0, existing._length);
                    Array.Clear(existing._dirtyMask, 0, existing._dirtyMask.Length);
                    existing._length = 0;
                    existing._isFullDirty = false;
                    emptyResult = existing;
                }
                else
                {
                    emptyResult = new NetArray<T>(64);
                }

                emptyResult.LastChangeInfo = new NetArrayChangeInfo<T>(deletedValues, Array.Empty<int>(), Array.Empty<T>());
                return emptyResult;
            }

            NetArray<T> result;
            if (existing != null && existing.Capacity >= length)
            {
                result = existing;
                if (length < result._length)
                {
                    Array.Clear(result._data, length, result._length - length);
                }
                result._length = length;
            }
            else
            {
                // Create with length as initial length (not 0)
                result = new NetArray<T>(Math.Max(length, 64), length);
            }

            // Pre-allocate arrays
            int changedCount = Math.Min(length, previousLength);
            int addedCount = Math.Max(0, length - previousLength);
            var changedIndices = changedCount > 0 ? new int[changedCount] : Array.Empty<int>();
            var addedValues = addedCount > 0 ? new T[addedCount] : Array.Empty<T>();

            for (int i = 0; i < length; i++)
            {
                T value = ReadElement(buffer);
                result._data[i] = value;

                if (i < previousLength)
                {
                    changedIndices[i] = i;
                }
                else
                {
                    addedValues[i - previousLength] = value;
                }
            }

            result.LastChangeInfo = new NetArrayChangeInfo<T>(deletedValues, changedIndices, addedValues);
            result.ClearDirty();
            return result;
        }

        private static NetArray<T> ReadDeltaSync(NetBuffer buffer, NetArray<T> existing)
        {
            if (typeof(T) == typeof(bool))
                return ReadDeltaSyncBool(buffer, existing);

            int count = NetReader.ReadUInt16(buffer);

            if (existing == null)
            {
                // Can't apply delta to non-existent array - skip data
                for (int i = 0; i < count; i++)
                {
                    NetReader.ReadUInt16(buffer);
                    ReadElement(buffer);
                }
                var emptyResult = new NetArray<T>(64);
                emptyResult.LastChangeInfo = NetArrayChangeInfo<T>.Empty;
                return emptyResult;
            }

            // Pre-allocate changed indices array
            var changedIndices = count > 0 ? new int[count] : Array.Empty<int>();
            int changedIdx = 0;

            for (int i = 0; i < count; i++)
            {
                int index = NetReader.ReadUInt16(buffer);
                T value = ReadElement(buffer);

                if (index < existing._length)
                {
                    existing._data[index] = value;
                    changedIndices[changedIdx++] = index;
                }
            }

            // Trim array if we didn't fill it
            if (changedIdx < changedIndices.Length)
            {
                Array.Resize(ref changedIndices, changedIdx);
            }

            // Delta sync doesn't change length, so no deletions or additions
            existing.LastChangeInfo = new NetArrayChangeInfo<T>(Array.Empty<T>(), changedIndices, Array.Empty<T>());
            return existing;
        }

        /// <summary>
        /// Called when peer acknowledges receipt of the packet exported at <paramref name="tick"/>.
        /// Only commits state that was sent at or before that tick — an older ack proves
        /// nothing about sends still in flight (fixes lost updates from stale acks).
        /// </summary>
        public static void OnPeerAcknowledge(NetArray<T> obj, UUID peerId, Tick tick)
        {
            if (obj == null || obj._peerState == null) return;
            // In place: one hash, no struct copy, no write-back. This runs per array per peer
            // per ack, which is the hottest per-node path the ack drain has.
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(obj._peerState, peerId);
            if (Unsafe.IsNullRef(ref state)) return;

            // Commit pending chunk progress, but only if this ack covers the chunk send
            if (state.HasPendingChunk && state.ChunkSentTick >= 0 && tick >= state.ChunkSentTick)
            {
                state.AckedUpToIndex = state.PendingSyncIndex;
                state.HasPendingChunk = false;

                // Check if initial sync is now complete. For bool the frontier counts WORDS, so the
                // completion threshold is the word count, not the bit length.
                int completeThreshold = typeof(T) == typeof(bool)
                    ? (state.LastSyncedLength + 63) >> 6
                    : state.LastSyncedLength;
                if (state.AckedUpToIndex >= completeThreshold && state.LastSyncedLength > 0)
                {
                    state.InitialSyncComplete = true;
                }
            }

            // Clear this peer's pending element bits only if the ack covers the last send
            // that included them. Bits merged after that send stay pending and get resent.
            if (state.PendingDirty != null && state.LastSendTick >= 0 && tick >= state.LastSendTick)
            {
                Array.Clear(state.PendingDirty, 0, state.PendingDirty.Length);
                state.LastSendTick = -1;
            }
        }

        /// <summary>
        /// Called when a peer disconnects. Clean up per-peer state.
        /// </summary>
        public static void OnPeerDisconnected(NetArray<T> obj, UUID peerId)
        {
            if (obj == null || obj._peerState == null) return;
            obj._peerState.Remove(peerId);
        }

        /// <summary>
        /// Writes a single element based on type T.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteElement(NetBuffer buffer, T value)
        {
            // Use pattern matching to write the correct type
            // This gets optimized by JIT for concrete T
            if (typeof(T) == typeof(int))
            {
                NetWriter.WriteInt32(buffer, Unsafe.As<T, int>(ref value));
            }
            else if (typeof(T) == typeof(float))
            {
                NetWriter.WriteFloat(buffer, Unsafe.As<T, float>(ref value));
            }
            else if (typeof(T) == typeof(byte))
            {
                NetWriter.WriteByte(buffer, Unsafe.As<T, byte>(ref value));
            }
            else if (typeof(T) == typeof(bool))
            {
                // Safety fallback only: the bool sync path is word-packed and never routes here.
                NetWriter.WriteByte(buffer, Unsafe.As<T, bool>(ref value) ? (byte)1 : (byte)0);
            }
            else if (typeof(T) == typeof(long))
            {
                NetWriter.WriteInt64(buffer, Unsafe.As<T, long>(ref value));
            }
            else if (typeof(T) == typeof(short))
            {
                NetWriter.WriteInt16(buffer, Unsafe.As<T, short>(ref value));
            }
            else if (typeof(T) == typeof(Vector2))
            {
                NetWriter.WriteVector2(buffer, Unsafe.As<T, Vector2>(ref value));
            }
            else if (typeof(T) == typeof(Vector3))
            {
                NetWriter.WriteVector3(buffer, Unsafe.As<T, Vector3>(ref value));
            }
            else if (typeof(T) == typeof(Quaternion))
            {
                NetWriter.WriteQuaternion(buffer, Unsafe.As<T, Quaternion>(ref value));
            }
            else if (typeof(T) == typeof(Vector2I))
            {
                var v = Unsafe.As<T, Vector2I>(ref value);
                NetWriter.WriteInt32(buffer, v.X);
                NetWriter.WriteInt32(buffer, v.Y);
            }
            else if (typeof(T) == typeof(Vector3I))
            {
                var v = Unsafe.As<T, Vector3I>(ref value);
                NetWriter.WriteInt32(buffer, v.X);
                NetWriter.WriteInt32(buffer, v.Y);
                NetWriter.WriteInt32(buffer, v.Z);
            }
            else if (typeof(T) == typeof(Color))
            {
                var c = Unsafe.As<T, Color>(ref value);
                NetWriter.WriteFloat(buffer, c.R);
                NetWriter.WriteFloat(buffer, c.G);
                NetWriter.WriteFloat(buffer, c.B);
                NetWriter.WriteFloat(buffer, c.A);
            }
            else
            {
                throw new NotSupportedException($"NetArray element type {typeof(T).Name} is not supported");
            }
        }

        /// <summary>
        /// Reads a single element based on type T.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T ReadElement(NetBuffer buffer)
        {
            if (typeof(T) == typeof(int))
            {
                int val = NetReader.ReadInt32(buffer);
                return Unsafe.As<int, T>(ref val);
            }
            else if (typeof(T) == typeof(float))
            {
                float val = NetReader.ReadFloat(buffer);
                return Unsafe.As<float, T>(ref val);
            }
            else if (typeof(T) == typeof(byte))
            {
                byte val = NetReader.ReadByte(buffer);
                return Unsafe.As<byte, T>(ref val);
            }
            else if (typeof(T) == typeof(bool))
            {
                bool val = NetReader.ReadByte(buffer) != 0; // safety fallback (see WriteElement)
                return Unsafe.As<bool, T>(ref val);
            }
            else if (typeof(T) == typeof(long))
            {
                long val = NetReader.ReadInt64(buffer);
                return Unsafe.As<long, T>(ref val);
            }
            else if (typeof(T) == typeof(short))
            {
                short val = NetReader.ReadInt16(buffer);
                return Unsafe.As<short, T>(ref val);
            }
            else if (typeof(T) == typeof(Vector2))
            {
                Vector2 val = NetReader.ReadVector2(buffer);
                return Unsafe.As<Vector2, T>(ref val);
            }
            else if (typeof(T) == typeof(Vector3))
            {
                Vector3 val = NetReader.ReadVector3(buffer);
                return Unsafe.As<Vector3, T>(ref val);
            }
            else if (typeof(T) == typeof(Quaternion))
            {
                Quaternion val = NetReader.ReadQuaternion(buffer);
                return Unsafe.As<Quaternion, T>(ref val);
            }
            else if (typeof(T) == typeof(Vector2I))
            {
                var val = new Vector2I(
                    NetReader.ReadInt32(buffer),
                    NetReader.ReadInt32(buffer)
                );
                return Unsafe.As<Vector2I, T>(ref val);
            }
            else if (typeof(T) == typeof(Vector3I))
            {
                var val = new Vector3I(
                    NetReader.ReadInt32(buffer),
                    NetReader.ReadInt32(buffer),
                    NetReader.ReadInt32(buffer)
                );
                return Unsafe.As<Vector3I, T>(ref val);
            }
            else if (typeof(T) == typeof(Color))
            {
                var val = new Color(
                    NetReader.ReadFloat(buffer),
                    NetReader.ReadFloat(buffer),
                    NetReader.ReadFloat(buffer),
                    NetReader.ReadFloat(buffer)
                );
                return Unsafe.As<Color, T>(ref val);
            }
            else
            {
                throw new NotSupportedException($"NetArray element type {typeof(T).Name} is not supported");
            }
        }

        /// <summary>
        /// Gets the size in bytes of a single element.
        /// </summary>
        public static int ElementSize
        {
            get
            {
                if (typeof(T) == typeof(int)) return 4;
                if (typeof(T) == typeof(float)) return 4;
                if (typeof(T) == typeof(byte)) return 1;
                if (typeof(T) == typeof(bool)) return 1; // safety fallback (bool sync is word-packed)
                if (typeof(T) == typeof(long)) return 8;
                if (typeof(T) == typeof(short)) return 2;
                if (typeof(T) == typeof(Vector2)) return 4; // Half precision
                if (typeof(T) == typeof(Vector3)) return 12;
                if (typeof(T) == typeof(Quaternion)) return 8; // Half precision
                if (typeof(T) == typeof(Vector2I)) return 8;
                if (typeof(T) == typeof(Vector3I)) return 12;
                if (typeof(T) == typeof(Color)) return 16;
                return Unsafe.SizeOf<T>();
            }
        }

        #endregion
    }

    /// <summary>
    /// Helper class for bit operations.
    /// </summary>
    internal static class BitOperations
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TrailingZeroCount(ulong value)
        {
            if (value == 0) return 64;

            int count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong value)
        {
            // Brian Kernighan's algorithm
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }
    }
}
