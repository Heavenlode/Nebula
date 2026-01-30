using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
	/**
		<summary>
		Manages the network state of a <see cref="Nebula.NetNode"/> (including <see cref="NetNode2D"/> and <see cref="NetNode3D"/>).
		</summary>
	*/
	public partial class NetworkController : RefCounted
	{
		#region Snapshot Interpolation

		/// <summary>
		/// A snapshot of property values at a specific tick for smooth interpolation.
		/// </summary>
		internal struct Snapshot
		{
			public int Tick;
			public PropertyCache[] Properties;
		}

		public bool IsClient => NetRunner.Instance.IsClient;
		public bool IsServer => NetRunner.Instance.IsServer;

		/// <summary>
		/// Size of the circular snapshot buffer. ~130ms at 60Hz tick rate.
		/// </summary>
		internal const int SNAPSHOT_BUFFER_SIZE = 8;

		/// <summary>
		/// Circular buffer of snapshots for interpolation (client-side only).
		/// </summary>
		internal Snapshot[] SnapshotBuffer = new Snapshot[SNAPSHOT_BUFFER_SIZE];

		/// <summary>
		/// Current write index in the circular snapshot buffer.
		/// </summary>
		internal int SnapshotWriteIndex = 0;

		/// <summary>
		/// Number of snapshots currently stored in the buffer.
		/// </summary>
		internal int SnapshotCount = 0;

		/// <summary>
		/// The tick of the snapshot currently being built during import.
		/// -1 if no snapshot is being built.
		/// </summary>
		private int _currentSnapshotTick = -1;

		/// <summary>
		/// Called at the start of importing an entity's tick data.
		/// Creates a new snapshot slot and copies previous values (for delta compression).
		/// </summary>
		internal void BeginSnapshotForTick(int tick)
		{
			// Skip if we already started a snapshot for this tick (shouldn't happen)
			if (tick == _currentSnapshotTick) return;

			// Skip old ticks (only accept newer ticks)
			if (SnapshotCount > 0)
			{
				int prevIdx = (SnapshotWriteIndex - 1 + SNAPSHOT_BUFFER_SIZE) % SNAPSHOT_BUFFER_SIZE;
				if (tick <= SnapshotBuffer[prevIdx].Tick)
					return;
			}

			_currentSnapshotTick = tick;

			// Allocate property array if needed
			if (SnapshotBuffer[SnapshotWriteIndex].Properties == null)
				SnapshotBuffer[SnapshotWriteIndex].Properties = new PropertyCache[CachedProperties.Length];

			// Copy previous snapshot values (handles unchanged properties due to delta compression)
			if (SnapshotCount > 0)
			{
				int prevIdx = (SnapshotWriteIndex - 1 + SNAPSHOT_BUFFER_SIZE) % SNAPSHOT_BUFFER_SIZE;
				Array.Copy(SnapshotBuffer[prevIdx].Properties, SnapshotBuffer[SnapshotWriteIndex].Properties,
						   CachedProperties.Length);
			}
			else
			{
				// First snapshot - copy from CachedProperties
				Array.Copy(CachedProperties, SnapshotBuffer[SnapshotWriteIndex].Properties, CachedProperties.Length);
			}

			SnapshotBuffer[SnapshotWriteIndex].Tick = tick;
			SnapshotWriteIndex = (SnapshotWriteIndex + 1) % SNAPSHOT_BUFFER_SIZE;
			SnapshotCount = Math.Min(SnapshotCount + 1, SNAPSHOT_BUFFER_SIZE);
		}

		/// <summary>
		/// Updates a specific property in the current snapshot (called per imported property).
		/// </summary>
		internal void UpdateSnapshotProperty(int propertyIndex, ref PropertyCache value)
		{
			if (_currentSnapshotTick < 0) return;
			int idx = (SnapshotWriteIndex - 1 + SNAPSHOT_BUFFER_SIZE) % SNAPSHOT_BUFFER_SIZE;
			SnapshotBuffer[idx].Properties[propertyIndex] = value;
		}

		/// <summary>
		/// Finds the two snapshots bracketing the global render tick and returns interpolation factor.
		/// Returns false if insufficient snapshots (caller should snap to target).
		/// </summary>
		public bool GetInterpolationSnapshots(int propertyIndex, out PropertyCache from, out PropertyCache to, out float t)
		{
			from = default;
			to = default;
			t = 0f;

			if (SnapshotCount < 2) return false;

			// Use GLOBAL render tick from WorldRunner (not per-entity)
			float renderTick = CurrentWorld.GetRenderTick();

			// Find bracketing snapshots (linear scan - buffer is small)
			int fromIdx = -1, toIdx = -1;
			for (int i = 0; i < SnapshotCount; i++)
			{
				int idx = (SnapshotWriteIndex - SnapshotCount + i + SNAPSHOT_BUFFER_SIZE) % SNAPSHOT_BUFFER_SIZE;
				if (SnapshotBuffer[idx].Tick <= renderTick)
					fromIdx = idx;
				if (SnapshotBuffer[idx].Tick > renderTick && toIdx == -1)
					toIdx = idx;
			}

			// Edge case: render tick is beyond all snapshots - hold last value (no extrapolation)
			if (toIdx == -1)
			{
				int lastIdx = (SnapshotWriteIndex - 1 + SNAPSHOT_BUFFER_SIZE) % SNAPSHOT_BUFFER_SIZE;
				from = SnapshotBuffer[lastIdx].Properties[propertyIndex];
				to = from; // Same value = no interpolation
				t = 0f;
				return true;
			}

			// Edge case: render tick is before all snapshots - snap to earliest
			if (fromIdx == -1)
			{
				from = SnapshotBuffer[toIdx].Properties[propertyIndex];
				to = from;
				t = 0f;
				return true;
			}

			// Normal case: interpolate between bracketing snapshots
			from = SnapshotBuffer[fromIdx].Properties[propertyIndex];
			to = SnapshotBuffer[toIdx].Properties[propertyIndex];

			int fromTick = SnapshotBuffer[fromIdx].Tick;
			int toTick = SnapshotBuffer[toIdx].Tick;
			t = (renderTick - fromTick) / (toTick - fromTick);
			t = Math.Clamp(t, 0f, 1f); // Safety clamp

			return true;
		}

		/// <summary>
		/// Clears the snapshot buffer. Called on teleport or interest regain.
		/// </summary>
		public void ClearSnapshotBuffer()
		{
			SnapshotCount = 0;
			SnapshotWriteIndex = 0;
			_currentSnapshotTick = -1;
		}

		#endregion

		public Node RawNode { get; internal set; }
		public INetNodeBase NetNode;

		private NodePath _attachedNetNodePath;
		public NodePath NetNodePath
		{
			get
			{
				if (_attachedNetNodePath == null)
				{
					_attachedNetNodePath = RawNode.GetPath();
				}
				return _attachedNetNodePath;
			}
		}

		public NetworkController(Node owner)
		{
			if (owner is not INetNodeBase)
			{
				Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Node {owner.GetPath()} does not implement INetNode");
				return;
			}
			RawNode = owner;
			NetNode = owner as INetNodeBase;
		}

		private string _attachedNetNodeSceneFilePath;
		public string NetSceneFilePath
		{
			get
			{
				if (_attachedNetNodeSceneFilePath == null)
				{
					var rawPath = RawNode.SceneFilePath;
					_attachedNetNodeSceneFilePath = string.IsNullOrEmpty(rawPath)
						? NetParent?.RawNode?.SceneFilePath ?? ""
						: rawPath;
				}
				return _attachedNetNodeSceneFilePath;
			}
		}

		/// <summary>
		/// If true, the node will be despawned when the peer that owns it disconnects, otherwise the InputAuthority will simply be set to null.
		/// </summary>
		public bool DespawnOnUnowned = false;

		public bool IsQueuedForDespawn => CurrentWorld.QueueDespawnedNodes.Contains(this);

		/// <summary>
		/// Cached flag to avoid calling Godot's IsQueuedForDeletion() every tick.
		/// Set to true when QueueNodeForDeletion() is called.
		/// </summary>
		internal bool IsMarkedForDeletion { get; private set; } = false;

		/// <summary>
		/// Queue this node for deletion. Sets the cached flag and calls QueueFree().
		/// Use this instead of RawNode.QueueFree() to enable zero-allocation deletion checks.
		/// </summary>
		public void QueueNodeForDeletion()
		{
			IsMarkedForDeletion = true;
			RawNode.QueueFree();
		}

		bool? _isNetScene = null;
		public bool IsNetScene()
		{
			if (_isNetScene == null)
			{
				_isNetScene = Protocol.IsNetScene(RawNode.SceneFilePath);
			}
			return _isNetScene.Value;
		}

		internal List<Tuple<string, string>> InitialSetNetProperties = [];
		public WorldRunner CurrentWorld { get; internal set; }
		public Dictionary<UUID, long> InterestLayers { get; set; } = [];
		public NetworkController[] StaticNetworkChildren = [];

		/// <summary>
		/// The static child ID for this node within its parent NetScene.
		/// Assigned during Setup() from Protocol.StaticNetworkNodePathsMap.
		/// Root NetScene nodes use their own ID (typically 0 for ".").
		/// </summary>
		public byte StaticChildId { get; internal set; } = 0;

		/// <summary>
		/// Cached node path ID within parent NetScene for spawn serialization.
		/// Set during scene instantiation or WorldRunner.Spawn().
		/// 255 = direct child of parent root.
		/// </summary>
		internal byte CachedNodePathIdInParent = 255;

		/// <summary>
		/// Bitmask of dirty properties. Bit N is set if property index N has changed since last export.
		/// </summary>
		public long DirtyMask = 0;

		/// <summary>
		/// Cached property values. Populated by MarkDirty, read by serializer during Export.
		/// </summary>
		internal PropertyCache[] CachedProperties = new PropertyCache[64];

		public HashSet<NetworkController> DynamicNetworkChildren = [];

		/// <summary>
		/// Invoked when a peer's interest layers change. Parameters: (peerId, oldInterest, newInterest)
		/// </summary>
		public event Action<UUID, long, long> InterestChanged;

		/// <summary>
		/// Client-side only. Fired when this node's interest state changes.
		/// Parameter is true when interest is gained, false when lost.
		/// Use this to snap/teleport visual state on regain, or hide on loss.
		/// </summary>
		public event Action<bool> OnInterestChanged;

		/// <summary>
		/// Called by InterestResyncSerializer when the client receives an interest change signal.
		/// </summary>
		/// <param name="hasInterest">True if interest was gained, false if lost</param>
		internal void FireInterestChanged(bool hasInterest)
		{
			// Clear snapshot buffer on interest regain to prevent interpolating from stale data
			if (hasInterest)
			{
				ClearSnapshotBuffer();
			}
			OnInterestChanged?.Invoke(hasInterest);
		}

		public void SetPeerInterest(UUID peerId, long newInterest, bool recurse = true)
		{
			var oldInterest = InterestLayers.TryGetValue(peerId, out var value) ? value : 0;
			InterestLayers[peerId] = newInterest;
			if (recurse && IsNetScene())
			{
				foreach (var child in StaticNetworkChildren)
				{
					child?.SetPeerInterest(peerId, newInterest, recurse);
				}
				foreach (var child in DynamicNetworkChildren)
				{
					child.SetPeerInterest(peerId, newInterest, recurse);
				}
			}
			InterestChanged?.Invoke(peerId, oldInterest, newInterest);
		}

		public void AddPeerInterest(NetPeer peer, long interestLayers, bool recurse = true)
		{
			SetPeerInterest(NetRunner.Instance.GetPeerId(peer), interestLayers, recurse);
		}

		public void AddPeerInterest(UUID peerId, long interestLayers, bool recurse = true)
		{
			var currentInterest = InterestLayers.GetValueOrDefault(peerId, 0);
			SetPeerInterest(peerId, currentInterest | interestLayers, recurse);
		}

	public void RemovePeerInterest(NetPeer peer, long interestLayers, bool recurse = true)
		{
			SetPeerInterest(NetRunner.Instance.GetPeerId(peer), interestLayers, recurse);
		}

		public void RemovePeerInterest(UUID peerId, long interestLayers, bool recurse = true)
		{
			var currentInterest = InterestLayers.GetValueOrDefault(peerId, 0);
			SetPeerInterest(peerId, currentInterest & ~interestLayers, recurse);
		}

		public bool IsPeerInterested(UUID peerId)
		{
			// Root scene is always visible
			if (CurrentWorld.RootScene == this) return true;

			var peerLayers = InterestLayers.GetValueOrDefault(peerId, 0);
			if (peerLayers == 0) return false;

			// Check class-level interest requirements
			if (Protocol.TryGetSceneInterest(NetSceneFilePath, out var sceneInterest))
			{
				bool hasAnyInterest = sceneInterest.InterestAny == 0 || (sceneInterest.InterestAny & peerLayers) != 0;
				bool hasAllRequired = (sceneInterest.InterestRequired & peerLayers) == sceneInterest.InterestRequired;
				if (!hasAnyInterest || !hasAllRequired) return false;
			}

			return true;
		}

		public bool IsPeerInterested(NetPeer peer)
		{
			return IsPeerInterested(NetRunner.Instance.GetPeerId(peer));
		}

		public override void _Notification(int what)
		{
			if (what == NotificationPredelete)
			{
				if (!IsWorldReady) return;
				if (NetParent != null && NetParent.RawNode is INetNodeBase _netNodeParent)
				{
					_netNodeParent.Network.DynamicNetworkChildren.Remove(this);
				}
			}
		}

		/// <summary>
		/// Cleans up per-peer cached state when a peer disconnects.
		/// Called by WorldRunner.CleanupPlayer to prevent memory leaks.
		/// </summary>
		internal void CleanupPeerState(UUID peerId)
		{
			spawnReady.Remove(peerId);
			preparingSpawn.Remove(peerId);
			InterestLayers.Remove(peerId);
		}

		public bool IsWorldReady { get; internal set; } = false;

		private NetId _networkParentId;
		public NetId NetParentId
		{
			get
			{
				return _networkParentId;
			}
			set
			{
				{
					if (IsNetScene() && NetParent != null && NetParent.RawNode is INetNodeBase _netNodeParent)
					{
						_netNodeParent.Network.DynamicNetworkChildren.Remove(this);
					}
				}
				_networkParentId = value;
				{
					var parentController = IsNetScene() && value.IsValid ? CurrentWorld.GetNodeFromNetId(value) : null;
					if (parentController?.RawNode is INetNodeBase _netNodeParent)
					{
						_netNodeParent.Network.DynamicNetworkChildren.Add(this);
					}
				}
			}
		}
		public NetworkController NetParent
		{
			get
			{
				if (CurrentWorld == null) return null;
				return CurrentWorld.GetNodeFromNetId(NetParentId);
			}
			internal set
			{
				NetParentId = value?.NetId ?? NetId.None;
			}
		}
		public bool IsClientSpawn { get; internal set; } = false;

		/// <summary>
		/// Sets up the NetworkController, including setting up serializers and property change notifications.
		/// Called when the parent scene has finished being instantiated (before adding to scene tree).
		/// </summary>
		internal void Setup()
		{
			if (IsNetScene())
			{
				NetNode.SetupSerializers();
				InitializeStaticChildren();
				InitializeDynamicChildren();
			}
		}

		/// <summary>
		/// Discovers nested NetScenes in the scene tree and populates DynamicNetworkChildren.
		/// Also sets CachedNodePathIdInParent for spawn serialization.
		/// Called on server during Setup().
		/// </summary>
		private void InitializeDynamicChildren()
		{
			DynamicNetworkChildren.Clear();
			DiscoverDynamicChildrenRecursive(RawNode, RawNode);
		}

		private void DiscoverDynamicChildrenRecursive(Node treeRoot, Node node)
		{
			for (int i = 0; i < node.GetChildCount(); i++)
			{
				var child = node.GetChild(i);

				if (child is INetNodeBase netNode && netNode.Network != null && netNode.Network.IsNetScene())
				{
					DynamicNetworkChildren.Add(netNode.Network);

					// Compute and cache the node path ID for spawn serialization
					var relativePath = treeRoot.GetPathTo(child);
					if (relativePath == "." || relativePath.IsEmpty)
					{
						netNode.Network.CachedNodePathIdInParent = 255;
					}
					else if (Protocol.PackNode(NetSceneFilePath, relativePath, out var pathId))
					{
						netNode.Network.CachedNodePathIdInParent = pathId;
					}
					else
					{
						netNode.Network.CachedNodePathIdInParent = 255;
					}

					// Recurse into nested NetScene to discover its children
					netNode.Network.InitializeDynamicChildren();
					continue;
				}

				// Continue traversing non-NetScene nodes
				DiscoverDynamicChildrenRecursive(treeRoot, child);
			}
		}

		/// <summary>
		/// Initializes StaticNetworkChildren array and assigns StaticChildId to each child.
		/// Uses Protocol data to map node paths to IDs (init-time Godot calls are acceptable).
		/// Also propagates InputAuthority if parent already has it set.
		/// </summary>
		private void InitializeStaticChildren()
		{
			var scenePath = RawNode.SceneFilePath;

			if (!GeneratedProtocol.StaticNetworkNodePathsMap.TryGetValue(scenePath, out var nodeMap))
			{
				return;
			}

			// Find max ID to size the array correctly
			byte maxId = 0;
			foreach (var nodeId in nodeMap.Keys)
			{
				if (nodeId > maxId) maxId = nodeId;
			}

			StaticNetworkChildren = new NetworkController[maxId + 1];

			foreach (var (nodeId, nodePath) in nodeMap)
			{
				var childNode = RawNode.GetNodeOrNull(nodePath);
				if (childNode is INetNodeBase netChild)
				{
					netChild.Network.StaticChildId = nodeId;
					StaticNetworkChildren[nodeId] = netChild.Network;

					// Inherit InputAuthority from parent if already set
					if (InputAuthority.IsSet)
					{
						netChild.Network.SetInputAuthorityInternal(InputAuthority);
					}
				}
			}
		}

		#region Property Dirty Tracking

		/// <summary>
		/// Marks a value-type property as dirty and caches its value.
		/// Called by generated On{Prop}Changed methods. No boxing occurs.
		/// </summary>
		public void MarkDirty<T>(INetNodeBase sourceNode, string propertyName, T value) where T : struct
		{
			// Static children propagate to parent net scene (which owns the serializer)
			if (!IsNetScene())
			{
				if (NetParent == null)
				{
					return;
				}
				NetParent.MarkDirty(sourceNode, propertyName, value);
				return;
			}

			// Look up property using static child ID (no Godot calls)
			var staticChildId = sourceNode.Network.StaticChildId;
			if (!Protocol.LookupPropertyByStaticChildId(NetSceneFilePath, staticChildId, propertyName, out var prop))
			{
				return;
			}

			DirtyMask |= (1L << prop.Index);
			SetCachedValue(prop.Index, prop.VariantType, value);
		}

		/// <summary>
		/// Marks a reference-type property as dirty and caches its value.
		/// Called by generated On{Prop}Changed methods.
		/// </summary>
		public void MarkDirtyRef<T>(INetNodeBase sourceNode, string propertyName, T value) where T : class
		{
			// Static children propagate to parent net scene
			if (!IsNetScene())
			{
				if (NetParent == null)
				{
					return;
				}
				NetParent.MarkDirtyRef(sourceNode, propertyName, value);
				return;
			}

			// Look up property using static child ID (no Godot calls)
			var staticChildId = sourceNode.Network.StaticChildId;
			if (!Protocol.LookupPropertyByStaticChildId(NetSceneFilePath, staticChildId, propertyName, out var prop))
			{
				return;
			}

			DirtyMask |= (1L << prop.Index);

			// Reference types go in the RefValue slot (or StringValue for strings)
			if (value is string s)
			{
				CachedProperties[prop.Index].Type = SerialVariantType.String;
				CachedProperties[prop.Index].StringValue = s;
			}
			else
			{
				CachedProperties[prop.Index].Type = SerialVariantType.Object;
				CachedProperties[prop.Index].RefValue = value;
			}
		}

		/// <summary>
		/// Marks a property as dirty by its global property index.
		/// Used by INetPropertyBindable types (like NetArray) when internal state changes.
		/// </summary>
		public void MarkDirtyByIndex(int globalPropertyIndex)
		{
			// Static children propagate to parent net scene
			if (!IsNetScene())
			{
				NetParent?.MarkDirtyByIndex(globalPropertyIndex);
				return;
			}

			DirtyMask |= (1L << globalPropertyIndex);
		}

		/// <summary>
		/// Sets a cached property value based on its type. Uses pattern matching to avoid boxing.
		/// </summary>
		private void SetCachedValue<T>(int index, SerialVariantType variantType, T value) where T : struct
		{
			ref var cache = ref CachedProperties[index];
			cache.Type = variantType;

			// Use pattern matching to set the correct union field without boxing
			switch (value)
			{
				case bool b:
					cache.BoolValue = b;
					break;
				case byte by:
					cache.ByteValue = by;
					break;
				case int i:
					cache.IntValue = i;
					break;
				case long l:
					cache.LongValue = l;
					break;
				case ulong ul:
					cache.LongValue = (long)ul;
					break;
				case float f:
					cache.FloatValue = f;
					break;
				case double d:
					cache.DoubleValue = d;
					break;
				case Vector2 v2:
					cache.Vec2Value = v2;
					break;
				case Vector3 v3:
					cache.Vec3Value = v3;
					break;
				case Quaternion q:
					cache.QuatValue = q;
					break;
				case NetId netId:
					cache.NetIdValue = netId;
					break;
				case UUID uuid:
					cache.UUIDValue = uuid;
					break;
				default:
					// Check if it's an enum - enums are stored in appropriately-sized fields
					if (typeof(T).IsEnum)
					{
						var enumVal = value;
						int enumSize = Unsafe.SizeOf<T>();
						// Store in the correctly-sized field based on underlying type
						// Clear LongValue first to ensure upper bytes are zero
						cache.LongValue = 0;
						switch (enumSize)
						{
							case 1: // byte/sbyte
								cache.ByteValue = Unsafe.As<T, byte>(ref enumVal);
								break;
							case 2: // short/ushort
								cache.IntValue = Unsafe.As<T, short>(ref enumVal);
								break;
							case 4: // int/uint (most common)
								cache.IntValue = Unsafe.As<T, int>(ref enumVal);
								break;
							case 8: // long/ulong
								cache.LongValue = Unsafe.As<T, long>(ref enumVal);
								break;
							default:
								// Fallback - shouldn't happen for standard enums
								cache.IntValue = Unsafe.As<T, int>(ref enumVal);
								break;
						}
					}
					else
					{
						// For unknown value types, we have to box (rare case)
						cache.Type = SerialVariantType.Object;
						cache.RefValue = value;
						Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"SetCachedValue: Unknown value type {typeof(T).Name}, boxing");
					}
					break;
			}
		}

		/// <summary>
		/// Clears the dirty mask after export. Called by the serializer.
		/// </summary>
		internal void ClearDirtyMask()
		{
			DirtyMask = 0;
		}

		#endregion

		#region Input Handling

		private byte[] _inputData;
		private byte[] _previousInputData;
		private bool _inputChanged;

		/// <summary>
		/// Returns true if this node supports network input (InitializeInput was called).
		/// </summary>
		public bool HasInputSupport => _inputData != null;

		/// <summary>
		/// Returns true if the input has changed since the last network tick.
		/// </summary>
		public bool HasInputChanged => _inputChanged;

		/// <summary>
		/// Gets the current input as a byte span for network serialization.
		/// </summary>
		public ReadOnlySpan<byte> GetInputBytes() => _inputData;

		/// <summary>
		/// Sets the current input from bytes received from the network.
		/// </summary>
		public void SetInputBytes(ReadOnlySpan<byte> bytes)
		{
			if (_inputData == null || bytes.Length != _inputData.Length) return;
			bytes.CopyTo(_inputData);
		}

		/// <summary>
		/// Clears the input changed flag after the input has been sent.
		/// Also saves the sent input for comparison on next SetInput call.
		/// </summary>
		public void ClearInputChanged()
		{
			_inputChanged = false;
			// Save what we sent so we detect changes from the sent state, not from the previous frame
			_inputData.CopyTo(_previousInputData, 0);
		}

		/// <summary>
		/// Initializes input support for this node with the specified input struct type.
		/// Call this in your node's constructor.
		/// </summary>
		/// <typeparam name="TInput">The unmanaged struct type for network input.</typeparam>
		public void InitializeInput<TInput>() where TInput : unmanaged
		{
			var size = Unsafe.SizeOf<TInput>();
			_inputData = new byte[size];
			_previousInputData = new byte[size];
		}

		/// <summary>
		/// Sets the current input for this network tick. Only call on the client that owns this node.
		/// </summary>
		/// <typeparam name="TInput">The unmanaged struct type for network input.</typeparam>
		/// <param name="input">The input struct to send to the server.</param>
		public void SetInput<TInput>(in TInput input) where TInput : unmanaged
		{
			if (_inputData == null)
			{
				Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"SetInput called but input not initialized. Call InitializeInput<T>() first.");
				return;
			}

			// Write new input to current
			MemoryMarshal.Write(_inputData, in input);

			// Check if changed from last SENT input (previousInputData is only updated on send)
			// Use OR to preserve the flag - it's cleared when SendInput actually sends
			_inputChanged = _inputChanged || !_inputData.AsSpan().SequenceEqual(_previousInputData);
		}

		/// <summary>
		/// Gets the current input. Use this on the server to read client input.
		/// </summary>
		/// <typeparam name="TInput">The unmanaged struct type for network input.</typeparam>
		/// <returns>A readonly reference to the current input.</returns>
		public ref readonly TInput GetInput<TInput>() where TInput : unmanaged
		{
			return ref MemoryMarshal.AsRef<TInput>(_inputData);
		}

		#endregion

		#region Client-Side Prediction

		// ============================================================
		// HOT PATH OPTIMIZATION: All structures designed for zero-allocation tick processing
		// ============================================================

		// Input buffering (client-side, for redundancy and rollback)
		// Use array-based circular buffer instead of Dictionary to avoid allocations
		private const int INPUT_BUFFER_SIZE = 64; // Power of 2 for fast modulo
		internal const int INPUT_REDUNDANCY_COUNT = 8;
		private byte[][] _inputBuffer;  // Lazy-init: Pre-allocated slots
		private Tick[] _inputBufferTicks; // Tick for each slot
		private Tick _lastBufferedInputTick = -1;

		// Prediction tracking
		/// <summary>
		/// True when this entity is currently running predicted simulation (client-side).
		/// </summary>
		public bool IsPredicting { get; internal set; }

		/// <summary>
		/// True during rollback replay (re-simulation after misprediction).
		/// Use this to skip side effects (sounds, particles) during resimulation.
		/// </summary>
		public bool IsResimulating { get; internal set; }

		// Tick tracking for prediction
		private Tick _lastConfirmedTick = -1;
		private Tick _lastPredictedTick = -1;

		// Reusable list for GetRecentInputs (avoid allocation on every call)
		private List<(Tick, byte[])> _recentInputsCache;

		// Pooled buffer for sending input packets (avoids per-tick allocation)
		private NetBuffer _inputSendBuffer;

		/// <summary>
		/// Buffers input for the given tick in a circular buffer.
		/// Used for input redundancy (sending multiple inputs per packet) and rollback replay.
		/// </summary>
		/// <param name="tick">The tick this input is for</param>
		/// <param name="input">The input data to buffer</param>
		public void BufferInput(Tick tick, ReadOnlySpan<byte> input)
		{
			// Lazy init
			if (_inputBuffer == null)
			{
				_inputBuffer = new byte[INPUT_BUFFER_SIZE][];
				_inputBufferTicks = new Tick[INPUT_BUFFER_SIZE];
				for (int i = 0; i < INPUT_BUFFER_SIZE; i++)
				{
					_inputBufferTicks[i] = -1;
				}
			}

			// Circular buffer index using fast modulo (power of 2)
			int slot = (int)(tick & (INPUT_BUFFER_SIZE - 1));

			// Reuse existing byte array if same size, otherwise allocate once
			if (_inputBuffer[slot] == null || _inputBuffer[slot].Length != input.Length)
			{
				_inputBuffer[slot] = new byte[input.Length];
			}

			input.CopyTo(_inputBuffer[slot]);
			_inputBufferTicks[slot] = tick;
			_lastBufferedInputTick = tick;
		}

		/// <summary>
		/// Gets buffered input for the given tick, or null if not found.
		/// </summary>
		/// <param name="tick">The tick to retrieve input for</param>
		/// <returns>The input bytes, or null if not found (too old or never stored)</returns>
		public byte[] GetBufferedInput(Tick tick)
		{
			if (_inputBuffer == null) return null;

			int slot = (int)(tick & (INPUT_BUFFER_SIZE - 1));
			if (_inputBufferTicks[slot] == tick)
				return _inputBuffer[slot];
			return null;  // Input not found (too old or never stored)
		}

		/// <summary>
		/// Returns recent inputs for redundant transmission.
		/// WARNING: Returns cached list - caller must NOT store reference long-term.
		/// </summary>
		/// <param name="count">Number of recent inputs to retrieve</param>
		/// <returns>List of (tick, inputBytes) tuples, most recent first</returns>
		public List<(Tick, byte[])> GetRecentInputs(int count)
		{
			_recentInputsCache ??= new List<(Tick, byte[])>(INPUT_REDUNDANCY_COUNT);
			_recentInputsCache.Clear();

			if (_inputBuffer == null || _lastBufferedInputTick < 0) return _recentInputsCache;

			// Walk backwards from last buffered tick
			for (int i = 0; i < count && i < INPUT_BUFFER_SIZE; i++)
			{
				var tick = _lastBufferedInputTick - i;
				if (tick < 0) break;

				var input = GetBufferedInput(tick);
				if (input != null)
				{
					_recentInputsCache.Add((tick, input));
				}
			}

			return _recentInputsCache;
		}

		/// <summary>
		/// Gets the pooled NetBuffer for sending input packets.
		/// Reuses the same buffer across ticks to avoid allocation.
		/// </summary>
		public NetBuffer GetPooledInputBuffer()
		{
			_inputSendBuffer ??= new NetBuffer();
			_inputSendBuffer.Reset();
			return _inputSendBuffer;
		}

		/// <summary>
		/// Stores predicted state for the given tick by calling the generated method on the NetNode.
		/// </summary>
		public void StorePredictedState(Tick tick)
		{
			NetNode.StorePredictedState(tick);
			_lastPredictedTick = tick;
		}

		/// <summary>
		/// Stores confirmed server state by calling the generated method on the NetNode.
		/// </summary>
		public void StoreConfirmedState(Tick tick)
		{
			NetNode.StoreConfirmedState();
			_lastConfirmedTick = tick;
		}

		/// <summary>
		/// Compares predicted state with confirmed server state and restores mispredicted properties.
		/// Returns true if any misprediction was detected (rollback needed), false if all predictions correct.
		/// If forceRestoreAll is true, skips comparison and restores all properties to confirmed state.
		/// </summary>
		public bool Reconcile(Tick tick, bool forceRestoreAll = false)
		{
			return NetNode.Reconcile(tick, forceRestoreAll);
		}

		/// <summary>
		/// Restores properties from the prediction buffer for a given tick.
		/// Used when prediction was correct and we need to continue with predicted values after server state import.
		/// </summary>
		public void RestoreToPredictedState(Tick tick)
		{
			NetNode.RestoreToPredictedState(tick);
		}

		/// <summary>
		/// The last tick that was confirmed by the server for this entity.
		/// </summary>
		public Tick LastConfirmedTick => _lastConfirmedTick;

		/// <summary>
		/// The last tick that was predicted locally for this entity.
		/// </summary>
		public Tick LastPredictedTick => _lastPredictedTick;

		#endregion

		public NetId NetId { get; internal set; }
		public NetPeer InputAuthority { get; internal set; }
		public void SetInputAuthority(NetPeer inputAuthority)
		{
			if (!IsServer) throw new Exception("InputAuthority can only be set on the server");
			if (CurrentWorld == null) throw new Exception("Can only set input authority after node is assigned to a world");
			if (InputAuthority.IsSet)
			{
				CurrentWorld.GetPeerWorldState(InputAuthority).Value.OwnedNodes.Remove(this);
			}
			if (inputAuthority.IsSet)
			{
				CurrentWorld.GetPeerWorldState(inputAuthority).Value.OwnedNodes.Add(this);
			}
			InputAuthority = inputAuthority;

			// Propagate InputAuthority to all static network children
			foreach (var staticChild in StaticNetworkChildren)
			{
				if (staticChild == null) continue;
				staticChild.SetInputAuthorityInternal(inputAuthority);
			}
		}

		/// <summary>
		/// Internal method to set InputAuthority without server check (for propagation from parent).
		/// Also propagates to nested static children.
		/// </summary>
		internal void SetInputAuthorityInternal(NetPeer inputAuthority)
		{
			if (CurrentWorld != null)
			{
				if (InputAuthority.IsSet)
				{
					CurrentWorld.GetPeerWorldState(InputAuthority).Value.OwnedNodes.Remove(this);
				}
				if (inputAuthority.IsSet)
				{
					CurrentWorld.GetPeerWorldState(inputAuthority).Value.OwnedNodes.Add(this);
				}
			}
			InputAuthority = inputAuthority;

			// Recursively propagate to nested static children
			foreach (var staticChild in StaticNetworkChildren)
			{
				if (staticChild == null) continue;
				staticChild.SetInputAuthorityInternal(inputAuthority);
			}
		}

		public bool IsCurrentOwner
		{
			get { return IsServer || (IsClient && InputAuthority.IsSet); }
		}

		public static INetNodeBase FindFromChild(Node node)
		{
			while (node != null)
			{
				if (node is INetNodeBase netNode)
					return netNode;
				node = node.GetParent();
			}
			return null;
		}

		public void _OnPeerConnected(UUID peerId)
		{
			var peer = NetRunner.Instance.Peers[peerId];
			SetPeerInterest(peerId, NetNode.InitializeInterest(peer), recurse: false);
		}

		internal void _NetworkPrepare(WorldRunner world)
		{
			if (Engine.IsEditorHint())
			{
				return;
			}

			CurrentWorld = world;
			
			// Initialize bindings for INetPropertyBindable properties (like NetArray)
			// This ensures properties initialized inline get their mutation callbacks bound
			// and their initial values cached for network serialization
			// Must call through concrete type for polymorphic dispatch (interface has default empty impl)
			if (RawNode is NetNode3D n3d)
				n3d.InitializeNetPropertyBindings();
			else if (RawNode is NetNode2D n2d)
				n2d.InitializeNetPropertyBindings();
			else if (RawNode is NetNode nn)
				nn.InitializeNetPropertyBindings();
			if (IsNetScene())
			{
				if (IsServer)
				{
					foreach (var peer in NetRunner.Instance.Peers.Keys)
					{
						SetPeerInterest(peer, NetNode.InitializeInterest(NetRunner.Instance.Peers[peer]));
					}
					CurrentWorld.OnPlayerJoined += _OnPeerConnected;
				}
				if (!world.CheckStaticInitialization(this))
				{
					return;
				}
				for (var i = DynamicNetworkChildren.Count - 1; i >= 0; i--)
				{
					var networkChild = DynamicNetworkChildren.ElementAt(i);
					networkChild.InterestLayers = InterestLayers;
					// On client, don't overwrite InputAuthority if child already has it set
					// (server sends correct InputAuthority for each node via spawn data)
					if (IsServer || !networkChild.InputAuthority.IsSet)
					{
						networkChild.InputAuthority = InputAuthority;
					}
					networkChild.CurrentWorld = world;
					networkChild.NetParentId = NetId;
					networkChild._NetworkPrepare(world);
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 1; i--)
				{
					var networkChild = StaticNetworkChildren[i];
					networkChild.InterestLayers = InterestLayers;
					// On client, don't overwrite InputAuthority if child already has it set
					// (server sends correct InputAuthority for each node via spawn data)
					if (IsServer || !networkChild.InputAuthority.IsSet)
					{
						networkChild.InputAuthority = InputAuthority;
					}
					networkChild.CurrentWorld = world;
					networkChild.NetParentId = NetId;
					networkChild._NetworkPrepare(world);
				}
				if (IsClient)
				{
					return;
				}

				// Ensure every networked "INetNode" property is correctly linked to the WorldRunner.
				if (GeneratedProtocol.PropertiesMap.TryGetValue(RawNode.SceneFilePath, out var nodeMap))
				{
					foreach (var nodeEntry in nodeMap)
					{
						var nodePath = nodeEntry.Key;
						foreach (var propEntry in nodeEntry.Value)
						{
							var property = propEntry.Value;
							if (property.Metadata.TypeIdentifier == "NetNode")
							{
								var node = RawNode.GetNode(nodePath);
								var prop = node.Get(property.Name);
								var tempNetNode = prop.As<GodotObject>();
								if (tempNetNode == null)
								{
									continue;
								}
								if (tempNetNode is INetNodeBase netNode)
								{
									var referencedNodeInWorld = CurrentWorld.GetNodeFromNetId(netNode.Network._prepareNetId);
									if (referencedNodeInWorld == null)
									{
										continue;
									}
									if (referencedNodeInWorld.IsNetScene() && !string.IsNullOrEmpty(netNode.Network._prepareStaticChildPath))
									{
										referencedNodeInWorld = (referencedNodeInWorld.RawNode.GetNodeOrNull(netNode.Network._prepareStaticChildPath) as INetNodeBase)?.Network;
									}
									if (referencedNodeInWorld != null)
									{
										node.Set(property.Name, referencedNodeInWorld.RawNode);
									}
								}
							}
						}
					}
				}

				// Initial property values are now cached via MarkDirty calls during initialization
				// The old EmitSignal("NetPropertyChanged") pattern has been removed
			}
		}

		internal Dictionary<UUID, bool> spawnReady = [];
		internal Dictionary<UUID, bool> preparingSpawn = [];

		public void PrepareSpawn(NetPeer peer)
		{
			var peerId = NetRunner.Instance.GetPeerId(peer);
			spawnReady[peerId] = true;
			return;
		}

		internal NetId _prepareNetId;
		internal string _prepareStaticChildPath;
		public virtual void _WorldReady()
		{
			if (IsNetScene())
			{
				for (var i = DynamicNetworkChildren.Count - 1; i >= 0; i--)
				{
					DynamicNetworkChildren.ElementAt(i)._WorldReady();
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 1; i--)
				{
					StaticNetworkChildren[i]._WorldReady();
				}
			}
			// Direct interface call - avoids Godot's dynamic Call() which allocates StringName/Variant
			NetNode._WorldReady();
			IsWorldReady = true;
		}

		public virtual void _NetworkProcess(Tick tick)
		{
			// Direct interface call - avoids Godot's dynamic Call() which allocates StringName/Variant
			NetNode._NetworkProcess(tick);
		}


		/// <summary>
		/// Used by NetFunction to determine whether the call should be send over the network, or if it is coming from the network.
		/// </summary>
		internal bool IsInboundCall { get; set; } = false;
		public string NodePathFromNetScene()
		{
			if (IsNetScene())
			{
				return RawNode.GetPathTo(RawNode);
			}

			return NetParent.RawNode.GetPathTo(RawNode);
		}

		public void Despawn()
		{
			if (!IsServer)
			{
				Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Cannot despawn {RawNode.GetPath()}. Only the server can despawn nodes.");
				return;
			}
			if (!IsNetScene())
			{
				Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Cannot despawn {RawNode.GetPath()}. Only Net Scenes can be despawned.");
				return;
			}

			handleDespawn();
		}

	internal void handleDespawn()
	{
		if (CurrentWorld == null)
		{
			// Node was never fully initialized (e.g., placeholder node) - just queue free
			RawNode.QueueFree();
			return;
		}
		NetNode._Despawn();
		CurrentWorld.QueueDespawn(this);
	}
	}
}

