using System;
using System.Collections.Generic;
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;

namespace Nebula
{

    /**
    <summary>
    Helper class to safely interface with NetNodes across languages (e.g. C#, GDScript) or in situations where the node type is unknown.
    </summary>
    <remarks>
    This class automatically handles NetNode validation. For example:
    <code language="csharp">
    var maybeNetNode = new NetNodeWrapper(GetNodeOrNull("MyAmbiguousNode"));
    // If MyAmbiguousNode is not a NetNode, maybeNetNode == null
    </code>
    </remarks>
    **/
    public partial class NetNodeWrapper : RefCounted
    {

        // Custom operator overload to validate null
        public static bool operator ==(NetNodeWrapper a, NetNodeWrapper b)
        {
            if (!ReferenceEquals(a, null))
            {
                if (ReferenceEquals(b, null))
                {
                    return a.Node == null;
                }
            }
            if (!ReferenceEquals(b, null))
            {
                if (ReferenceEquals(a, null))
                {
                    return b.Node == null;
                }
            }
            if (ReferenceEquals(a, null) && ReferenceEquals(b, null))
            {
                return true;
            }
            return a.Equals(b) || a.Node == b.Node;
        }

        public static bool operator !=(NetNodeWrapper a, NetNodeWrapper b)
        {
            return !(a == b);
        }

        public Node Node { get; private set; } = null;
        public INetNodeBase NetNode { get; private set; } = null;
        public NetworkController Network => NetNode.Network;
        private Dictionary<string, StringName> properties = new Dictionary<string, StringName>();
        private Dictionary<string, StringName> methods = new Dictionary<string, StringName>();
        public NetNodeWrapper(Node node)
        {
            if (node == null) return;

            // TODO: Better way to figure out if network node?
            if (node is not INetNodeBase)
            {
                // The node will remain null if it is not a NetNode
                return;
            }
            Node = node;
            NetNode = node as INetNodeBase;
            // TODO: Validate the node implements the interface correctly
            var requiredProperties = new HashSet<string> {
                "InputAuthority",
                "NetId",
                "NetParentId",
                "IsClientSpawn",
                "CurrentWorld",
                "InputBuffer",
                "PreviousInputBuffer",
                "InterestLayers"
            };
            foreach (var prop in node.GetPropertyList())
            {
                var pascalName = ToPascalCase(prop["name"].AsString());

                if (requiredProperties.Contains(pascalName))
                {
                    properties[pascalName] = new StringName(prop["name"].AsString());
                }

                foreach (var requiredProperty in requiredProperties)
                {
                    if (!properties.ContainsKey(requiredProperty))
                    {
                        // Assume the default property is an non-exported PascalCase
                        properties[requiredProperty] = new StringName(requiredProperty);
                    }
                }
            }
        }

        // Convert from snake_case to PascalCase
        private static string ToPascalCase(string name)
        {
            var pascalCase = "";
            var capitalize = true;
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '_')
                {
                    capitalize = true;
                }
                else if (capitalize)
                {
                    pascalCase += char.ToUpper(name[i]);
                    capitalize = false;
                }
                else
                {
                    pascalCase += name[i];
                }
            }
            return pascalCase;
        }

        private static string ToSnakeCase(string name)
        {
            var snakeCase = "";
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]))
                {
                    if (i > 0 && name[i - 1] != '_')
                    {
                        snakeCase += "_";
                    }
                    snakeCase += char.ToLower(name[i]);
                }
                else
                {
                    snakeCase += name[i];
                }
            }
            return snakeCase;
        }
        private Variant Get(string name)
        {
            return Network.Get(properties[name]);
        }

        private void Set(string name, Variant value)
        {
            Network.Set(properties[name], value);
        }

        private Variant Call(string name, params Variant[] args)
        {
            if (methods.ContainsKey(name))
            {
                return Network.Call(methods[name], args);
            }

            if (Network.HasMethod(name))
            {
                var result = Network.Call(name, args);
                methods[name] = new StringName(name);
                return result;
            }

            var snakeCase = ToSnakeCase(name);
            if (Network.HasMethod(snakeCase))
            {
                methods[name] = new StringName(snakeCase);
                return Network.Call(snakeCase, args);
            }

            throw new Exception($"Method {snakeCase} not found on {Node.GetPath()}");
        }

        public ENetPacketPeer InputAuthority
        {
            get
            {
                return Get("InputAuthority").As<ENetPacketPeer>();
            }

            internal set
            {
                Set("InputAuthority", value);
            }
        }

        public NetId NetId
        {
            get
            {
                return Get("NetId").As<NetId>();
            }

            internal set
            {
                Set("NetId", value);
            }
        }

        public WorldRunner CurrentWorld
        {
            get
            {
                return Get("CurrentWorld").As<WorldRunner>();
            }

            internal set
            {
                Set("CurrentWorld", value);
            }
        }

        public NetNodeWrapper NetParent => CurrentWorld.GetNodeFromNetId(NetParentId);

        // TODO: Handle null?
        internal byte NetSceneId => ProtocolRegistry.Instance.PackScene(Node.SceneFilePath);

        public NetId NetParentId
        {
            get
            {
                return Get("NetParentId").As<NetId>();
            }

            internal set
            {
                Set("NetParentId", value);
            }
        }

        public bool IsClientSpawn
        {
            get
            {
                return Get("IsClientSpawn").AsBool();
            }

            internal set
            {
                Set("IsClientSpawn", value);
            }
        }

        internal Godot.Collections.Dictionary<byte, Variant> InputBuffer
        {
            get
            {
                return Get("InputBuffer").As<Godot.Collections.Dictionary<byte, Variant>>();
            }

            set
            {
                Set("InputBuffer", value);
            }
        }

        internal Godot.Collections.Dictionary<byte, Variant> PreviousInputBuffer
        {
            get
            {
                return Get("PreviousInputBuffer").As<Godot.Collections.Dictionary<byte, Variant>>();
            }

            set
            {
                Set("PreviousInputBuffer", value);
            }
        }

        public Godot.Collections.Dictionary<UUID, long> InterestLayers
        {
            get
            {
                return Get("InterestLayers").As<Godot.Collections.Dictionary<UUID, long>>();
            }

            internal set
            {
                Set("InterestLayers", value);
            }
        }

        public IStateSerializer[] Serializers
        {
            get
            {
                // TODO: Support serializers across other languages / node types
                if (Node is NetNode3D netNode)
                {
                    return netNode.Serializers;
                }
                return [];
            }
        }

        internal void _NetworkPrepare(WorldRunner worldRunner)
        {
            Call("_NetworkPrepare", [worldRunner]);
        }

        internal void _WorldReady()
        {
            Call("_WorldReady");
        }

        public void _NetworkProcess(Tick tick)
        {
            Call("_NetworkProcess", tick);
        }

        public void SetPeerInterest(UUID peerId, long interestLayers, bool recurse = true)
        {
            Call("SetPeerInterest", peerId, interestLayers, recurse);
        }

        public void SetNetworkInput(byte input, Variant value)
        {
            Call("SetNetworkInput", input, value);
        }

        public Variant GetNetworkInput(byte input, Variant defaultValue)
        {
            return Call("GetNetworkInput", input, defaultValue);
        }

        public Godot.Collections.Array<NetNodeWrapper> StaticNetworkChildren
        {
            get
            {
                return ProtocolRegistry.Instance.ListNetworkChildren(Node);
            }
        }

        public bool IsNetScene()
        {
            return ProtocolRegistry.Instance.IsNetScene(Node.SceneFilePath);
        }
    }
}