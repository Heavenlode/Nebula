using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
	/**
		<summary>
		Manages the network state of a <see cref="NetNode"/> (including <see cref="NetNode2D"/> and <see cref="NetNode3D"/>).
		</summary>
	*/
	public partial class NetworkController : RefCounted
	{
		public NetNodeWrapper Owner { get; internal set; }
		public bool IsNetScene() {
			return ProtocolRegistry.Instance.IsNetScene(Owner.Node.SceneFilePath);
		}

		internal HashSet<NetNodeWrapper> NetSceneChildren = [];
		internal List<Tuple<string, string>> InitialSetNetProperties = [];
		public WorldRunner CurrentWorld { get; internal set; }
		internal Godot.Collections.Dictionary<byte, Variant> InputBuffer = [];
		internal Godot.Collections.Dictionary<byte, Variant> PreviousInputBuffer = [];
		public Godot.Collections.Dictionary<UUID, long> InterestLayers = [];

		[Signal]
		public delegate void InterestChangedEventHandler(UUID peerId, long interestLayers);
		public void SetPeerInterest(UUID peerId, long interestLayers, bool recurse = true)
		{
			InterestLayers[peerId] = interestLayers;
			EmitSignal("InterestChanged", peerId, interestLayers);
			if (recurse)
			{
				foreach (var child in GetNetworkChildren(NetworkChildrenSearchToggle.INCLUDE_SCENES))
				{
					child.SetPeerInterest(peerId, interestLayers, recurse);
				}
			}
		}

		public override void _Notification(int what)
		{
			if (what == NotificationPredelete)
			{
				if (!IsWorldReady) return;
				if (NetParent != null && NetParent.Node is INetNode _netNodeParent)
				{
					_netNodeParent.Network.NetSceneChildren.Remove(Owner);
				}
			}
		}

		public void SetNetworkInput(byte input, Variant value)
		{
			if (IsNetScene())
			{
				InputBuffer[input] = value;
			}
			else
			{
				NetParent.SetNetworkInput(input, value);
			}
		}

		public Variant GetNetworkInput(byte input, Variant defaultValue)
		{
			if (IsNetScene())
			{
				return InputBuffer.GetValueOrDefault(input, defaultValue);
			}
			else
			{
				return NetParent.GetNetworkInput(input, defaultValue);
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
					if (IsNetScene() && NetParent != null && NetParent.Node is INetNode _netNodeParent)
					{
						_netNodeParent.Network.NetSceneChildren.Remove(Owner);
					}
				}
				_networkParentId = value;
				{
					if (IsNetScene() && value != null && CurrentWorld.GetNodeFromNetId(value).Node is INetNode _netNodeParent)
					{
						_netNodeParent.Network.NetSceneChildren.Add(Owner);
					}
				}
			}
		}
		public NetNodeWrapper NetParent
		{
			get
			{
				if (CurrentWorld == null) return null;
				return CurrentWorld.GetNodeFromNetId(NetParentId);
			}
			internal set
			{
				NetParentId = value?.NetId;
			}
		}
		public bool IsClientSpawn { get; internal set; } = false;
		public NetworkController(Node owner)
		{
			if (owner is not INetNode)
			{
				Debugger.Instance.Log($"Node {owner.GetPath()} does not implement INetNode", Debugger.DebugLevel.ERROR);
				return;
			}
			Owner = new NetNodeWrapper(owner);
		}

		[Signal]
		public delegate void NetPropertyChangedEventHandler(string nodePath, StringName propertyName);
		public NetId NetId { get; internal set; }
		public NetPeer InputAuthority { get; internal set; } = null;

		public bool IsCurrentOwner
		{
			get { return NetRunner.Instance.IsServer || (NetRunner.Instance.IsClient && InputAuthority == NetRunner.Instance.ENetHost); }
		}

		public static INetNode FindFromChild(Node node)
		{
			while (node != null)
			{
				if (node is INetNode netNode)
					return netNode;
				node = node.GetParent();
			}
			return null;
		}

		public enum NetworkChildrenSearchToggle { INCLUDE_SCENES, EXCLUDE_SCENES, ONLY_SCENES }
		public IEnumerable<NetNodeWrapper> GetNetworkChildren(NetworkChildrenSearchToggle searchToggle = NetworkChildrenSearchToggle.EXCLUDE_SCENES, bool nestedSceneChildren = true)
		{
			var children = Owner.Node.GetChildren();
			while (children.Count > 0)
			{
				var child = children[0];
				children.RemoveAt(0);
				var isNetScene = ProtocolRegistry.Instance.IsNetScene(child.SceneFilePath);
				if (isNetScene && searchToggle == NetworkChildrenSearchToggle.EXCLUDE_SCENES)
				{
					continue;
				}
				if (nestedSceneChildren || (!nestedSceneChildren && !isNetScene))
				{
					children.AddRange(child.GetChildren());
				}
				if (child is not INetNode)
				{
					continue;
				}
				if (!isNetScene && searchToggle == NetworkChildrenSearchToggle.ONLY_SCENES)
				{
					continue;
				}
				if (searchToggle == NetworkChildrenSearchToggle.INCLUDE_SCENES || isNetScene)
				{
					yield return new NetNodeWrapper(child);
				}
			}
		}

		public void Despawn()
		{
			if (NetRunner.Instance.IsClient)
				return;
			// NetRunner.Instance.Despawn(this);
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
				if (!world.CheckStaticInitialization(Owner))
				{
					return;
				}
				var networkChildren = GetNetworkChildren(NetworkChildrenSearchToggle.INCLUDE_SCENES, false).ToList();
				networkChildren.Reverse();
				networkChildren.ForEach(child =>
				{
					// TODO: This is a little messed up.
					child.CurrentWorld = world;
					child.NetParentId = NetId;
					child._NetworkPrepare(world);
				});
				if (NetRunner.Instance.IsClient)
				{
					return;
				}
				foreach (var nodePropertyDetail in ProtocolRegistry.Instance.ListProperties(Owner.Node.SceneFilePath))
				{
					var nodePath = nodePropertyDetail["nodePath"].AsString();
					var nodeProps = nodePropertyDetail["properties"].As<Godot.Collections.Array<ProtocolNetProperty>>();

					// Ensure every networked "INetNode" property is correctly linked to the WorldRunner.
					foreach (var property in nodeProps)
					{
						if (property.Metadata.TypeIdentifier == "NetNode")
						{
							var node = Owner.Node.GetNode(nodePath);
							var prop = node.Get(property.Name);
							var tempNetNode = prop.As<RefCounted>();
							if (tempNetNode == null)
							{
								continue;
							}
							if (tempNetNode is INetNode netNode)
							{
								var referencedNodeInWorld = CurrentWorld.GetNodeFromNetId(netNode.Network._prepareNetId).Node as INetNode;
								if (referencedNodeInWorld.Network.IsNetScene() && !string.IsNullOrEmpty(netNode.Network._prepareStaticChildPath))
								{
									referencedNodeInWorld = referencedNodeInWorld.Network.Owner.Node.GetNodeOrNull(netNode.Network._prepareStaticChildPath) as INetNode;
								}
								node.Set(property.Name, referencedNodeInWorld.Network.Owner);
							}
						}
					}

					// Ensure all property changes are linked up to the signal
					var networkChild = Owner.Node.GetNodeOrNull<INetNode>(nodePath);
					if (networkChild == null)
					{
						continue;
					}
					if (networkChild.Network.Owner.Node is INotifyPropertyChanged propertyChangeNode)
					{
						propertyChangeNode.PropertyChanged += (object sender, PropertyChangedEventArgs e) =>
						{
							if (!ProtocolRegistry.Instance.LookupProperty(Owner.Node.SceneFilePath, nodePath, e.PropertyName, out _))
							{
								return;
							}
							EmitSignal("NetPropertyChanged", nodePath, e.PropertyName);
						};
					}
					else
					{
						Debugger.Instance.Log($"NetworkChild {nodePath} is not INotifyPropertyChanged. Ensure your custom NetNode implements INotifyPropertyChanged.", Debugger.DebugLevel.ERROR);
					}
				}

				if (IsNetScene()) {
					(Owner.Node as INetNode).SetupSerializers();
				}
				foreach (var initialSetProp in InitialSetNetProperties)
				{
					EmitSignal("NetPropertyChanged", initialSetProp.Item1, initialSetProp.Item2);
				}
			}
		}

		internal Godot.Collections.Dictionary<NetPeer, bool> spawnReady = new Godot.Collections.Dictionary<NetPeer, bool>();
		internal Godot.Collections.Dictionary<NetPeer, bool> preparingSpawn = new Godot.Collections.Dictionary<NetPeer, bool>();

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
				var networkChildren = GetNetworkChildren(NetworkChildrenSearchToggle.INCLUDE_SCENES, false).ToList();
				networkChildren.Reverse();
				networkChildren.ForEach(child =>
				{
					child._WorldReady();
				});
			}
			Owner.Node.Call("_WorldReady");
			IsWorldReady = true;
		}

		public virtual void _NetworkProcess(Tick tick)
		{
			Owner.Node.Call("_NetworkProcess", tick);
		}

		public Godot.Collections.Dictionary<int, Variant> GetInput()
		{
			if (!IsCurrentOwner) return null;
			if (!CurrentWorld.InputStore.ContainsKey(InputAuthority))
				return null;

			byte netId;
			if (NetRunner.Instance.IsServer)
			{
				netId = CurrentWorld.GetPeerNodeId(InputAuthority, Owner);
			}
			else
			{
				netId = (byte)NetId.Value;
			}

			if (!CurrentWorld.InputStore[InputAuthority].ContainsKey(netId))
				return null;

			var inputs = CurrentWorld.InputStore[InputAuthority][netId];
			CurrentWorld.InputStore[InputAuthority].Remove(netId);
			return inputs;
		}

		/// <summary>
		/// Used by NetFunction to determine whether the call should be send over the network, or if it is coming from the network.
		/// </summary>
		internal bool IsInboundCall { get; set; } = false;
		public string NodePathFromNetScene()
		{
			if (IsNetScene())
			{
				return Owner.Node.GetPathTo(Owner.Node);
			}

			return NetParent.Node.GetPathTo(Owner.Node);
		}
	}
}

