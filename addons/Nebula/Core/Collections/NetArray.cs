using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    }

    /// <summary>
    /// Information about what changed in a NetArray during a network update.
    /// Used for NotifyOnChange callbacks on NetArray properties.
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

        public static PeerSyncState Create() => new PeerSyncState
        {
            AckedUpToIndex = 0,
            PendingSyncIndex = 0,
            LastSyncedLength = 0,
            InitialSyncComplete = false,
            HasPendingChunk = false
        };
    }

    /// <summary>
    /// A network-synchronized array that tracks element-level changes for efficient delta sync.
    /// Only modified indices are sent over the network, significantly reducing bandwidth for large arrays.
    /// Implements INetPropertyBindable to notify parent NetworkController on internal mutations.
    /// </summary>
    /// <typeparam name="T">Element type (must be a supported primitive or Godot struct)</typeparam>
    public sealed class NetArray<T> : INetSerializable<NetArray<T>>, INetPropertyBindable, IEnumerable<T> where T : struct
    {
        private T[] _data;
        private ulong[] _dirtyMask; // Bit array for tracking dirty indices
        private int _length;
        private bool _isFullDirty; // True if entire array needs sync (e.g., after resize)
        
        /// <summary>
        /// Client-side: tracks how many elements have been received during chunked sync.
        /// Used to correctly identify "added" elements across multiple chunks.
        /// Reset to -1 when not in chunked sync.
        /// </summary>
        private int _clientReceivedUpTo = -1;
        
        /// <summary>
        /// Callback to notify parent NetworkController when internal state changes.
        /// Set via BindToNetProperty.
        /// </summary>
        private Action _onMutated;
        
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
        /// Default chunk size in bytes for initial sync to new clients.
        /// </summary>
        public const int DefaultChunkBudget = 256;

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
            
            _data = new T[source.Length];
            Array.Copy(source, _data, source.Length);
            _dirtyMask = new ulong[(source.Length + 63) / 64];
            _length = source.Length;
            _isFullDirty = true; // Mark all as dirty for initial sync
        }

        /// <summary>
        /// Number of elements currently in the array.
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Maximum capacity of the array.
        /// </summary>
        public int Capacity => _data.Length;

        #region INetPropertyBindable

        /// <summary>
        /// Binds a callback to be invoked when internal state changes.
        /// Called by the source generator when the property is set.
        /// </summary>
        public void BindToNetProperty(Action onMutated)
        {
            _onMutated = onMutated;
        }

        #endregion

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
                return _data[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException($"Index {index} out of range [0, {_length})");
                
                // Only mark dirty if value actually changed
                if (!EqualityComparer<T>.Default.Equals(_data[index], value))
                {
                    _data[index] = value;
                    MarkDirty(index);
                    _onMutated?.Invoke();
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
            
            _data[index] = value;
            MarkDirty(index);
            _onMutated?.Invoke();
        }

        /// <summary>
        /// Adds an element to the end of the array.
        /// </summary>
        public void Add(T item)
        {
            if (_length >= _data.Length)
                throw new InvalidOperationException($"Array is at capacity ({_data.Length})");
            
            _data[_length] = item;
            MarkDirty(_length);
            _length++;
            _isFullDirty = true; // Length changed
            _onMutated?.Invoke();
        }

        /// <summary>
        /// Sets the length of the array.
        /// If increasing, new elements are default(T).
        /// If decreasing, excess elements are discarded.
        /// </summary>
        public void SetLength(int newLength)
        {
            if (newLength < 0 || newLength > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(newLength));
            
            if (newLength != _length)
            {
                // Clear removed elements
                if (newLength < _length)
                {
                    Array.Clear(_data, newLength, _length - newLength);
                }
                
                _length = newLength;
                _isFullDirty = true; // Length changed, need full sync
                _onMutated?.Invoke();
            }
        }

        /// <summary>
        /// Clears all elements (sets length to 0).
        /// </summary>
        public void Clear()
        {
            if (_length > 0)
            {
                Array.Clear(_data, 0, _length);
                Array.Clear(_dirtyMask, 0, _dirtyMask.Length);
                _length = 0;
                _isFullDirty = true;
                _onMutated?.Invoke();
            }
        }

        /// <summary>
        /// Gets a span of the current elements (read-only access, doesn't mark dirty).
        /// </summary>
        public ReadOnlySpan<T> AsSpan() => _data.AsSpan(0, _length);

        /// <summary>
        /// Copies elements to a destination array.
        /// </summary>
        public void CopyTo(T[] destination, int startIndex = 0)
        {
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
                _data[index] = value;
            }
        }

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
        /// Marks the entire array as needing a full sync.
        /// </summary>
        public void MarkFullDirty()
        {
            _isFullDirty = true;
            _onMutated?.Invoke();
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
        private ref PeerSyncState GetOrCreatePeerState(UUID peerId)
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

            // Check if we need to restart initial sync (array was resized or marked for full sync)
            if (state.InitialSyncComplete && (obj._length != state.LastSyncedLength || obj._isFullDirty))
            {
                state.InitialSyncComplete = false;
                state.AckedUpToIndex = 0;
                state.PendingSyncIndex = 0;
                state.HasPendingChunk = false;
            }

            // Initial sync not complete - send chunked
            if (!state.InitialSyncComplete)
            {
                return WriteChunkedSync(obj, buffer, ref state, maxBytes);
            }

            // Initial sync complete - check if we have dirty elements (individual changes only)
            if (obj.DirtyCount == 0)
            {
                return false; // Nothing to send
            }

            // Send delta sync
            WriteDeltaSync(obj, buffer, ref state);
            return true;
        }

        private static bool WriteChunkedSync(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state, int maxBytes)
        {
            // If we have a pending (unacked) chunk, re-send from the acked position
            int startIndex = state.AckedUpToIndex;
            int elementSize = ElementSize;
            
            // Calculate how many elements fit in the budget
            // Header: 1 (flags) + 4 (total length) + 4 (start index) + 2 (chunk count) = 11 bytes
            int headerSize = 11;
            int availableBytes = maxBytes - headerSize;
            int maxElements = Math.Max(1, availableBytes / elementSize);
            int elementsToSend = Math.Min(maxElements, obj._length - startIndex);
            
            if (elementsToSend <= 0)
            {
                // We've sent everything, mark as complete
                state.InitialSyncComplete = true;
                state.LastSyncedLength = obj._length;
                return false; // Nothing to send
            }
            
            // Write chunked header
            NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Chunked);
            NetWriter.WriteInt32(buffer, obj._length);
            NetWriter.WriteInt32(buffer, startIndex);
            NetWriter.WriteUInt16(buffer, (ushort)elementsToSend);
            
            // Write elements
            for (int i = 0; i < elementsToSend; i++)
            {
                WriteElement(buffer, obj._data[startIndex + i]);
            }
            
            // Mark this chunk as pending (awaiting ack)
            state.PendingSyncIndex = startIndex + elementsToSend;
            state.LastSyncedLength = obj._length;
            state.HasPendingChunk = true;
            
            return true; // We wrote data
        }

        private static void WriteDeltaSync(NetArray<T> obj, NetBuffer buffer, ref PeerSyncState state)
        {
            int dirtyCount = obj.DirtyCount;
            
            if (dirtyCount == 0)
            {
                // No changes - write empty delta
                NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Delta);
                NetWriter.WriteUInt16(buffer, 0);
                return;
            }

            // Write delta header
            NetWriter.WriteByte(buffer, (byte)NetArraySyncFlags.Delta);
            NetWriter.WriteUInt16(buffer, (ushort)Math.Min(dirtyCount, ushort.MaxValue));
            
            // Write changed indices and values - iterate without LINQ
            int written = 0;
            for (int block = 0; block < obj._dirtyMask.Length && written < dirtyCount; block++)
            {
                var mask = obj._dirtyMask[block];
                if (mask == 0) continue;
                
                int baseIndex = block * 64;
                while (mask != 0 && written < dirtyCount)
                {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    int index = baseIndex + bit;
                    if (index < obj._length)
                    {
                        NetWriter.WriteUInt16(buffer, (ushort)index);
                        WriteElement(buffer, obj._data[index]);
                        written++;
                    }
                    mask &= mask - 1; // Clear lowest set bit
                }
            }
        }

        /// <summary>
        /// Deserializes the array from the network buffer.
        /// </summary>
        public static NetArray<T> NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, NetArray<T> existing = null)
        {
            var flags = (NetArraySyncFlags)NetReader.ReadByte(buffer);
            
            // Note: Full = 0, so bitwise AND check (flags & Full) == Full is always true.
            // We must check non-zero flags first and treat Full as the default fallback.
            if ((flags & NetArraySyncFlags.Chunked) == NetArraySyncFlags.Chunked)
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

        private static NetArray<T> ReadChunkedSync(NetBuffer buffer, NetArray<T> existing)
        {
            int totalLength = NetReader.ReadInt32(buffer);
            int startIndex = NetReader.ReadInt32(buffer);
            int chunkCount = NetReader.ReadUInt16(buffer);
            
            // Validate network data to prevent crashes from corrupted packets
            if (totalLength < 0 || startIndex < 0 || chunkCount < 0)
            {
                return existing ?? new NetArray<T>(64);
            }
            
            // For determining "added" vs "changed", we need to know the ORIGINAL length
            // before any chunks in this sync were received. Use _clientReceivedUpTo to track this.
            // If startIndex == 0, this is the first chunk - capture the original state.
            int originalPopulatedLength;
            if (startIndex == 0)
            {
                // First chunk - capture the true previous length
                originalPopulatedLength = existing?.Length ?? 0;
            }
            else if (existing != null && existing._clientReceivedUpTo >= 0)
            {
                // Continuation chunk - use the tracked original length
                originalPopulatedLength = existing._clientReceivedUpTo;
            }
            else
            {
                // Fallback (shouldn't happen normally)
                originalPopulatedLength = existing?.Length ?? 0;
            }
            
            // Capture deleted values before they're removed (if array is shrinking)
            T[] deletedValues = Array.Empty<T>();
            int existingLength = existing?.Length ?? 0;
            if (existing != null && existingLength > totalLength)
            {
                int deleteCount = existingLength - totalLength;
                deletedValues = new T[deleteCount];
                for (int i = 0; i < deleteCount; i++)
                {
                    deletedValues[i] = existing._data[totalLength + i];
                }
            }
            
            // Create or resize array as needed
            NetArray<T> result;
            if (existing == null || existing.Capacity < totalLength)
            {
                // Create with totalLength as initial length (not 0)
                result = new NetArray<T>(Math.Max(totalLength, 64), totalLength);
            }
            else
            {
                result = existing;
                // Set length (this may shrink or grow the array)
                // Do this without triggering _onMutated since this is from network
                if (result._length != totalLength)
                {
                    if (totalLength < result._length)
                    {
                        Array.Clear(result._data, totalLength, result._length - totalLength);
                    }
                    result._length = totalLength;
                }
            }
            
            // Track the original populated length for subsequent chunks
            if (startIndex == 0)
            {
                result._clientReceivedUpTo = originalPopulatedLength;
            }
            
            // Track changed indices and added values - pre-allocate to avoid List resizing
            int changedCount = 0;
            int addedCount = 0;
            
            // First pass: count using originalPopulatedLength (not current _length)
            for (int i = 0; i < chunkCount; i++)
            {
                int index = startIndex + i;
                if (index < totalLength)
                {
                    if (index >= originalPopulatedLength) addedCount++;
                    else changedCount++;
                }
            }
            
            // Nebula.Utility.Tools.Debugger.Instance.Log(Nebula.Utility.Tools.Debugger.DebugLevel.INFO,
            //     $"[NetArray.ReadChunkedSync] totalLen={totalLength}, start={startIndex}, chunkCount={chunkCount}, originalPopulatedLen={originalPopulatedLength}, changedCount={changedCount}, addedCount={addedCount}");
            
            var changedIndices = changedCount > 0 ? new int[changedCount] : Array.Empty<int>();
            var addedValues = addedCount > 0 ? new T[addedCount] : Array.Empty<T>();
            int changedIdx = 0;
            int addedIdx = 0;
            
            // Read chunk elements
            for (int i = 0; i < chunkCount; i++)
            {
                int index = startIndex + i;
                T value = ReadElement(buffer);
                
                if (index < result._length)
                {
                    result._data[index] = value;
                    
                    if (index >= originalPopulatedLength)
                    {
                        addedValues[addedIdx++] = value;
                    }
                    else
                    {
                        changedIndices[changedIdx++] = index;
                    }
                }
            }
            
            // Check if chunked sync is complete (we've received all elements)
            int receivedUpTo = startIndex + chunkCount;
            if (receivedUpTo >= totalLength)
            {
                // Sync complete - reset the tracking
                result._clientReceivedUpTo = -1;
            }
            
            result.LastChangeInfo = new NetArrayChangeInfo<T>(deletedValues, changedIndices, addedValues);
            result.ClearDirty();
            return result;
        }

        private static NetArray<T> ReadFullSync(NetBuffer buffer, NetArray<T> existing)
        {
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
        /// Called when peer acknowledges receipt. Commits pending state to confirmed.
        /// </summary>
        public static void OnPeerAcknowledge(NetArray<T> obj, UUID peerId)
        {
            if (obj == null || obj._peerState == null) return;
            if (!obj._peerState.TryGetValue(peerId, out var state)) return;
            
            // Commit pending chunk progress
            if (state.HasPendingChunk)
            {
                state.AckedUpToIndex = state.PendingSyncIndex;
                state.HasPendingChunk = false;
                
                // Check if initial sync is now complete
                if (state.AckedUpToIndex >= state.LastSyncedLength && state.LastSyncedLength > 0)
                {
                    state.InitialSyncComplete = true;
                }
            }
            
            // Write back the modified struct
            obj._peerState[peerId] = state;
            
            // Clear dirty flags after successful ack (elements have been confirmed received)
            obj.ClearDirty();
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
        private static void WriteElement(NetBuffer buffer, T value)
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
        private static T ReadElement(NetBuffer buffer)
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
