using System;
using System.Linq;
using Godot;
using Nebula.Serialization;
using MethodBoundaryAspect.Fody.Attributes;
using Nebula.Utility.Tools;

namespace Nebula
{
    /**
    <summary>
    Marks a method as a network function. Similar to an RPC.
    </summary>
    */
    public sealed class NetFunction : OnMethodBoundaryAspect
    {
        public enum NetworkSources
        {
            Client = 1 << 0,
            Server = 1 << 1,
            All = Client | Server,
        }

        // TODO: Ensure this is used in WorldRunner to correctly filter out invalid calls
        public NetworkSources Source { get; set; } = NetworkSources.All;
        public bool ExecuteOnCaller { get; set; } = true;
        public bool WithPeer { get; set; } = false;
        public override void OnEntry(MethodExecutionArgs args)
        {
            Debugger.Instance.Log($"NetFunction: {args.Method.Name} called on {args.Instance.GetType().Name}", Debugger.DebugLevel.VERBOSE);
            if (args.Instance is INetNodeBase netNode)
            {
                if (netNode.Network.IsInboundCall)
                {
                    // We only send a remote call if the incoming call isn't already from remote.
                    return;
                }
                if (!ExecuteOnCaller)
                {
                    args.FlowBehavior = FlowBehavior.Return;
                }

                if (NetRunner.Instance.IsServer && (Source & NetworkSources.Server) == 0)
                {
                    return;
                }

                if (NetRunner.Instance.IsClient && (Source & NetworkSources.Client) == 0)
                {
                    return;
                }

                var networkScene = "";
                if (netNode.Network.IsNetScene())
                {
                    networkScene = netNode.Node.SceneFilePath;
                }
                else
                {
                    networkScene = netNode.Network.NetParent.Node.SceneFilePath;
                }

                NetId netId;
                if (netNode.Network.IsNetScene())
                {
                    netId = netNode.Network.NetId;
                }
                else
                {
                    netId = netNode.Network.NetParent.NetId;
                }

                ProtocolNetFunction functionInfo;
                if (!ProtocolRegistry.Instance.LookupFunction(networkScene, netNode.Network.NodePathFromNetScene(), args.Method.Name, out functionInfo))
                {
                    throw new Exception($"Function {args.Method.Name} not found in network scene {networkScene}");
                }

                var arguments = args.Arguments;
                if (functionInfo.WithPeer)
                {
                    arguments = arguments.Skip(1).ToArray();
                }

                netNode.Network.CurrentWorld
                    .SendNetFunction(netId, functionInfo.Index, arguments.ToList().Select((x, index) =>
                    {
                        switch (functionInfo.Arguments[index].VariantType)
                        {
                            case Variant.Type.Int:
                                if (functionInfo.Arguments[index].Metadata.TypeIdentifier == "Int")
                                    return Variant.From((int)x);
                                else if (functionInfo.Arguments[index].Metadata.TypeIdentifier == "Byte")
                                    return Variant.From((byte)x);
                                else
                                    return Variant.From((long)x);
                            case Variant.Type.Float:
                                return Variant.From((float)x);
                            case Variant.Type.String:
                                return Variant.From((string)x);
                            case Variant.Type.Vector2:
                                return Variant.From((Vector2)x);
                            case Variant.Type.Vector3:
                                return Variant.From((Vector3)x);
                            case Variant.Type.Quaternion:
                                return Variant.From((Quaternion)x);
                            case Variant.Type.Color:
                                return Variant.From((Color)x);
                            default:
                                throw new Exception($"Unsupported argument type {functionInfo.Arguments[index].VariantType}");
                        }
                    }).ToArray()
                    );
            }
            else
            {
                throw new Exception("NetFunction attribute can only be used on INetNode");
            }
        }
    }
}