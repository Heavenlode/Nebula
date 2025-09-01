using System;
using System.ComponentModel;
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using MongoDB.Bson;

namespace Nebula
{
	/**
		<summary>
		<see cref="Node3D">Node3D</see>, extended with Nebula networking capabilities. This is the most basic networked 3D object.
		See <see cref="NetNode"/> for more information.
		</summary>
	*/
	[SerialTypeIdentifier("NetNode"), Icon("res://addons/Nebula/Core/NetNode3D.png")]
	public partial class NetNode3D : Node3D, INetNode, INotifyPropertyChanged, INetSerializable<NetNode3D>, IBsonSerializable<NetNode3D>
	{
		public Node Node => this;
		public NetworkController Network { get; internal set; }
		public NetNode3D() {
			Network = new NetworkController(this);
		}
		// Cannot have more than 8 serializers
		public IStateSerializer[] Serializers { get; private set; } = [];

		public void SetupSerializers()
		{
			var spawnSerializer = new SpawnSerializer();
			AddChild(spawnSerializer);
			var propertySerializer = new NetPropertiesSerializer();
			AddChild(propertySerializer);
			Serializers = [spawnSerializer, propertySerializer];
		}

		public virtual void _WorldReady() {}
		public virtual void _NetworkProcess(int _tick) {}

		/// <inheritdoc/>
		public override void _PhysicsProcess(double delta) {}
		public static HLBuffer NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetNode3D obj)
		{
			var buffer = new HLBuffer();
			if (obj == null)
			{
				HLBytes.Pack(buffer, (byte)0);
				return buffer;
			}
			NetId targetNetId;
			byte staticChildId = 0;
			if (obj.Network.IsNetScene())
			{
				targetNetId = obj.Network.NetId;
			}
			else
			{
				if (ProtocolRegistry.Instance.PackNode(obj.Network.NetParent.Node.SceneFilePath, obj.Network.NetParent.Node.GetPathTo(obj), out staticChildId))
				{
					targetNetId = obj.Network.NetParent.NetId;
				}
				else
				{
					throw new Exception($"Failed to pack node: {obj.GetPath()}");
				}
			}
			var peerNodeId = currentWorld.GetPeerWorldState(peer).Value.WorldToPeerNodeMap[targetNetId];
			HLBytes.Pack(buffer, peerNodeId);
			HLBytes.Pack(buffer, staticChildId);
			return buffer;
		}

		public static Variant GetDeserializeContext(NetNode3D obj)
        {
            return new Variant();
        }
		public static NetNode3D NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, Variant ctx)
		{
			var networkID = HLBytes.UnpackByte(buffer);
			if (networkID == 0)
			{
				return null;
			}
			var staticChildId = HLBytes.UnpackByte(buffer);
			var node = currentWorld.GetNodeFromNetId(networkID).Node as NetNode3D;
			if (staticChildId > 0)
			{
				node = node.GetNodeOrNull(ProtocolRegistry.Instance.UnpackNode(node.SceneFilePath, staticChildId)) as NetNode3D;
			}
			return node;
		}

		public virtual BsonValue BsonSerialize(Variant context)
		{
			var doc = new BsonDocument();
			if (Network.IsNetScene())
			{
				doc["NetId"] = Network.NetId.BsonSerialize(context);
			}
			else
			{
				doc["NetId"] = Network.NetParent.NetId.BsonSerialize(context);
				doc["StaticChildPath"] = Network.NetParent.Node.GetPathTo(this).ToString();
			}
			return doc;
		}

		public static NetNode3D BsonDeserialize(Variant context, byte[] bson, NetNode3D obj)
		{
			var data = BsonTransformer.Instance.DeserializeBsonValue<BsonDocument>(bson);
			if (data.IsBsonNull) return null;
			var doc = data.AsBsonDocument;
			var node = obj == null ? new NetNode3D() : obj;
			node.Network._prepareNetId = NetId.BsonDeserialize(context, BsonTransformer.Instance.SerializeBsonValue(doc["NetId"]), node.Network.NetId);
			if (doc.Contains("StaticChildPath"))
			{
				node.Network._prepareStaticChildPath = doc["StaticChildPath"].AsString;
			}
			return node;
		}

		public string NodePathFromNetScene()
		{
			if (Network.IsNetScene())
			{
				return GetPathTo(this);
			}

			return Network.NetParent.Node.GetPathTo(this);
		}
	}
}
