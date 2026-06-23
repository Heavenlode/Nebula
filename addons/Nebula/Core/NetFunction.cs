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
        public override void OnEntry(MethodExecutionArgs args)
        {
            // Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"NetFunction: {args.Method.Name} called on {args.Instance.GetType().Name}");
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

                // Use cached NetSceneFilePath to avoid Godot StringName allocations
                var networkScene = netNode.Network.NetSceneFilePath;

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
                if (!Protocol.LookupFunction(networkScene, netNode.Network.NodePathFromNetScene(), args.Method.Name, out functionInfo))
                {
                    throw new Exception($"Function {args.Method.Name} not found in network scene {networkScene}");
                }
                // Pass object[] directly with protocol metadata - no Variant conversion
                netNode.Network.CurrentWorld.SendNetFunction(netId, functionInfo, args.Arguments);
            }
            else
            {
                throw new Exception("NetFunction attribute can only be used on INetNode");
            }
        }
    }
}