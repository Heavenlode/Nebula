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
				Debugger.Instance.Log($"Node {owner.GetPath()} does not implement INetNode", Debugger.DebugLevel.ERROR);
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

		public bool IsNetScene()
		{
			return Protocol.IsNetScene(RawNode.SceneFilePath);
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

		public void AddPeerInterest(UUID peerId, long interestLayers, bool recurse = true)
		{
			var currentInterest = InterestLayers.GetValueOrDefault(peerId, 0);
			SetPeerInterest(peerId, currentInterest | interestLayers, recurse);
		}

		public bool IsPeerInterested(UUID peerId)
		{
			return InterestLayers.GetValueOrDefault(peerId, 0) > 0 || CurrentWorld.RootScene == this;
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
		internal void CleanupPeerState(NetPeer peer)
		{
			spawnReady.Remove(peer);
			preparingSpawn.Remove(peer);
			InterestLayers.Remove(NetRunner.Instance.GetPeerId(peer));
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
			}
		}

		/// <summary>
		/// Initializes StaticNetworkChildren array and assigns StaticChildId to each child.
		/// Uses Protocol data to map node paths to IDs (init-time Godot calls are acceptable).
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
				Debugger.Instance.Log($"MarkDirtyRef: Property not found: staticChildId={staticChildId}, prop={propertyName}", Debugger.DebugLevel.ERROR);
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
					// For unknown value types, we have to box (rare case)
					cache.Type = SerialVariantType.Object;
					cache.RefValue = value;
					Debugger.Instance.Log($"SetCachedValue: Unknown value type {typeof(T).Name}, boxing", Debugger.DebugLevel.WARN);
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
				Debugger.Instance.Log("SetInput called but input not initialized. Call InitializeInput<T>() first.", Debugger.DebugLevel.ERROR);
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

		public NetId NetId { get; internal set; }
		public NetPeer InputAuthority { get; internal set; }
		public void SetInputAuthority(NetPeer inputAuthority)
		{
			if (!NetRunner.Instance.IsServer) throw new Exception("InputAuthority can only be set on the server");
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
		}

		public bool IsCurrentOwner
		{
			get { return NetRunner.Instance.IsServer || (NetRunner.Instance.IsClient && InputAuthority.Equals(NetRunner.Instance.ServerPeer)); }
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
			SetPeerInterest(peerId, NetNode.InitializeInterest(NetRunner.Instance.Peers[peerId]));
		}

		internal void _NetworkPrepare(WorldRunner world)
		{
			if (Engine.IsEditorHint())
			{
				return;
			}

			CurrentWorld = world;
			if (IsNetScene())
			{
				if (NetRunner.Instance.IsServer)
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
				for (var i = DynamicNetworkChildren.Count - 1; i >= 1; i--)
				{
					var networkChild = DynamicNetworkChildren.ElementAt(i);
					networkChild.InterestLayers = InterestLayers;
					networkChild.InputAuthority = InputAuthority;
					networkChild.CurrentWorld = world;
					networkChild.NetParentId = NetId;
					networkChild._NetworkPrepare(world);
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 1; i--)
				{
					var networkChild = StaticNetworkChildren[i];
					networkChild.InterestLayers = InterestLayers;
					networkChild.InputAuthority = InputAuthority;
					networkChild.CurrentWorld = world;
					networkChild.NetParentId = NetId;
					networkChild._NetworkPrepare(world);
				}
				if (NetRunner.Instance.IsClient)
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

		internal Dictionary<NetPeer, bool> spawnReady = [];
		internal Dictionary<NetPeer, bool> preparingSpawn = [];

		public void PrepareSpawn(NetPeer peer)
		{
			spawnReady[peer] = true;
			return;
		}

		internal NetId _prepareNetId;
		internal string _prepareStaticChildPath;
		public virtual void _WorldReady()
		{
			if (IsNetScene())
			{
				for (var i = DynamicNetworkChildren.Count - 1; i >= 1; i--)
				{
					DynamicNetworkChildren.ElementAt(i)._WorldReady();
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 1; i--)
				{
					StaticNetworkChildren[i]._WorldReady();
				}
			}
			RawNode.Call("_WorldReady");
			IsWorldReady = true;
		}

		public virtual void _NetworkProcess(Tick tick)
		{
			RawNode.Call("_NetworkProcess", tick);
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
			if (!NetRunner.Instance.IsServer)
			{
				Debugger.Instance.Log($"Cannot despawn {RawNode.GetPath()}. Only the server can despawn nodes.", Debugger.DebugLevel.ERROR);
				return;
			}
			if (!IsNetScene())
			{
				Debugger.Instance.Log($"Cannot despawn {RawNode.GetPath()}. Only Net Scenes can be despawned.", Debugger.DebugLevel.ERROR);
				return;
			}

			handleDespawn();
		}

		internal void handleDespawn()
		{
			Debugger.Instance.Log($"Despawning node {RawNode.GetPath()}", Debugger.DebugLevel.VERBOSE);
			CurrentWorld.QueueDespawn(this);
		}
	}
}

