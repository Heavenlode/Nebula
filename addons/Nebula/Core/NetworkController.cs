using System;
using System.Collections.Generic;
using System.Linq;
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


		private StringName _attachedNetNodeSceneFilePath;
		public StringName NetSceneFilePath
		{
			get
			{
				if (_attachedNetNodeSceneFilePath == null)
				{
					_attachedNetNodeSceneFilePath = RawNode.SceneFilePath ?? NetParent.RawNode.SceneFilePath;
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
		public long[] DirtyProps = new long[64];
		public HashSet<NetworkController> DynamicNetworkChildren = [];
		
		/// <summary>
		/// Invoked when a peer's interest layers change. Parameters: (peerId, oldInterest, newInterest)
		/// </summary>
		public event Action<UUID, long, long> InterestChanged;
		
		public void SetPeerInterest(UUID peerId, long newInterest, bool recurse = true)
		{
			var oldInterest = InterestLayers.TryGetValue(peerId, out var value) ? value : 0;
			InterestLayers[peerId] = newInterest;
			// if (recurse)
			// {
			// 	foreach (var child in GetNetworkChildren(NetworkChildrenSearchToggle.INCLUDE_SCENES))
			// 	{
			// 		child.SetPeerInterest(peerId, newInterest, recurse);
			// 	}
			// }
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
				if (IsNetScene() && value.IsValid && CurrentWorld.GetNodeFromNetId(value).RawNode is INetNodeBase _netNodeParent)
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
			}
		}

		

		public NetId NetId { get; internal set; }
		public NetPeer InputAuthority { get; private set; }
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
				for (var i = DynamicNetworkChildren.Count - 1; i >= 0; i--)
				{
					var networkChild = DynamicNetworkChildren.ElementAt(i);
					networkChild.InterestLayers = InterestLayers;
					networkChild.InputAuthority = InputAuthority;
					networkChild.CurrentWorld = world;
					networkChild.NetParentId = NetId;
					networkChild._NetworkPrepare(world);
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 0; i--)
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
								if (referencedNodeInWorld.IsNetScene() && !string.IsNullOrEmpty(netNode.Network._prepareStaticChildPath))
								{
									referencedNodeInWorld = (referencedNodeInWorld.RawNode.GetNodeOrNull(netNode.Network._prepareStaticChildPath) as INetNodeBase).Network;
								}
								node.Set(property.Name, referencedNodeInWorld.RawNode);
							}
						}
					}
				}
			}

				foreach (var initialSetProp in InitialSetNetProperties)
				{
					EmitSignal("NetPropertyChanged", initialSetProp.Item1, initialSetProp.Item2);
				}
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
				for (var i = DynamicNetworkChildren.Count - 1; i >= 0; i--)
				{
					DynamicNetworkChildren.ElementAt(i)._WorldReady();
				}
				for (var i = StaticNetworkChildren.Length - 1; i >= 0; i--)
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

