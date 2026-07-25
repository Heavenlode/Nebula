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
                // Peer-targeted variants (generated *ForPeers overloads) stash the recipient set in a
                // thread-static right before invoking the woven method. Consume it here so the send
                // targets only those peers, and clear it immediately so a nested RPC fired from the
                // body (when ExecuteOnCaller is true) doesn't inherit the same targets.
                var targetPeers = NetFunctionCall.TargetPeers;
                NetFunctionCall.TargetPeers = null;

                // Pass object[] directly with protocol metadata - no Variant conversion
                netNode.Network.CurrentWorld.SendNetFunction(netId, functionInfo, args.Arguments, targetPeers);
            }
            else
            {
                throw new Exception("NetFunction attribute can only be used on INetNode");
            }
        }
    }

    /// <summary>
    /// Carries the recipient set for a peer-targeted NetFunction call from a generated *ForPeers
    /// overload into <see cref="NetFunction.OnEntry"/>. Thread-static because the send happens
    /// synchronously on the calling thread; <see cref="NetFunction.OnEntry"/> consumes and clears it
    /// so it never leaks into nested calls.
    /// </summary>
    internal static class NetFunctionCall
    {
        [ThreadStatic]
        internal static UUID[] TargetPeers;
    }
}