using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MongoDB.Bson;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula.Utility
{
    /// <summary>
    /// This class contains methods for serializing and deserializing network nodes to and from BSON.
    /// The logic is extracted to this utility class to reuse it across <see cref="NetNode"/>, <see cref="NetNode2D"/>, and <see cref="NetNode3D"/>.
    /// </summary>
    internal static class NetNodeCommon
    {
        readonly public static BsonDocument NullBsonDocument = new BsonDocument("value", BsonNull.Value);

        internal static BsonDocument ToBSONDocument(
            INetNodeBase netNode,
            NetBsonContext context = default
        )
        {
            var network = netNode.Network;
            if (!network.IsNetScene())
            {
                // Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Only network scenes can be converted to BSON: {network.RawNode.GetPath()} with scene {network.RawNode.SceneFilePath}");
            }

            // Cycle guard (opt-in via context.Visited): mutually-referencing
            // NetNodes would otherwise recurse forever. Emit a marker so the
            // reader can still see what was pointed at.
            if (context.Visited != null && !context.Visited.Add(network.RawNode))
            {
                return new BsonDocument
                {
                    ["$ref"] = network.RawNode.Name.ToString(),
                    ["scene"] = network.RawNode.SceneFilePath,
                };
            }

            BsonDocument result = new BsonDocument();
            result["data"] = new BsonDocument();
            result["scene"] = network.RawNode.SceneFilePath;
            // We retain this for debugging purposes.
            result["nodeName"] = network.RawNode.Name.ToString();

            if (GeneratedProtocol.PropertiesMap.TryGetValue(network.RawNode.SceneFilePath, out var nodeMap))
            {
                foreach (var nodeEntry in nodeMap)
                {
                    var nodePath = nodeEntry.Key;
                    
                    // Get the target node
                    var targetNode = network.RawNode.GetNodeOrNull(nodePath);
                    if (targetNode == null) continue;
                    
                    var nodeData = new BsonDocument();
                    
                    // Call WriteBsonProperties through concrete base types to use virtual dispatch
                    // (not interface dispatch, which would call the empty default implementation)
                    if (targetNode is NetNode3D nn3d)
                        nn3d.WriteBsonProperties(nodeData, context);
                    else if (targetNode is NetNode2D nn2d)
                        nn2d.WriteBsonProperties(nodeData, context);
                    else if (targetNode is NetNode nn)
                        nn.WriteBsonProperties(nodeData, context);
                    
                    // Only add if there are actual properties
                    if (nodeData.ElementCount > 0)
                    {
                        result["data"][nodePath] = nodeData;
                    }
                }
            }

            if (context.Recurse)
            {
                result["children"] = new BsonDocument();
                foreach (var child in network.DynamicNetworkChildren)
                {
                    if (context.NodeFilter != null && !context.NodeFilter(child.RawNode))
                    {
                        continue;
                    }
                    string pathTo = network.RawNode.GetPathTo(child.RawNode.GetParent());
                    if (!result["children"].AsBsonDocument.Contains(pathTo))
                    {
                        result["children"][pathTo] = new BsonArray();
                    }
                    result["children"][pathTo].AsBsonArray.Add(ToBSONDocument(child.NetNode, context));
                }
            }

            return result;
        }

        internal static async Task<T> FromBSON<T>(NetBsonContext context, BsonDocument data, T fillNode = null) where T : Node, INetNodeBase
        {
            // Main thread FIRST, before anything is loaded or instantiated -- not merely before the
            // AddChild below, which is where this hop used to sit.
            //
            // Deserialization reaches here from wherever its caller's awaits happened to resume, and
            // with per-world thread groups that is never anywhere good: DataBuddyRpc.ImportWorld and
            // LoadCharacter resume from a gRPC await on the ThreadPool, and a character load kicked
            // off by OnPlayerJoined starts on a world tick thread. Instantiating a scene there
            // allocates RenderingServer RIDs (every VisualInstance3D constructor, every ArrayMesh)
            // from that thread -- and on a HEADLESS server the dummy renderer's RID owners are the
            // non-thread-safe ones (unlike the RD renderer's), so an instantiate racing any other
            // thread's allocation corrupts the RID freelist. That surfaced as a burst of
            // "Attempting to initialize the wrong RID" / "Parameter mem is null" /
            // "unimplemented base type encountered in renderer scene cull" at the AddChild (where
            // the queued RID initializations actually run), followed by "Parameter m is null" from
            // dummy mesh_storage on every later use of the corrupted mesh RIDs.
            //
            // The cost of hopping first is that big scenes (a saved world is Backslat-sized) load
            // and instantiate ON main -- a boot-time hitch. If that hitch ever matters, the shape of
            // the real fix is known, in two independent halves:
            //   1. Load: ResourceLoader.LoadThreadedRequest(path, useSubThreads: true) + poll +
            //      LoadThreadedGet, fired as soon as the scene path is known. Unconditionally safe
            //      on the CLIENT (real renderers use thread-safe RID owners); on the headless
            //      server it only narrows this same race, so the idiomatic server answer is the
            //      dedicated-server export mode (placeholder meshes/textures -- the server's
            //      collision runs through the spatial mirror, never these meshes).
            //   2. Instantiate: no partial Instantiate exists, so chunking means decomposing the
            //      scene (the SphereStructure exported_scene_path split is the existing seam) and
            //      instantiating N subtrees per frame under a Time.GetTicksMsec budget, gating the
            //      world's go-Live on completion exactly as IAsyncWorldGenerator already does.
            //
            // Hopping here rather than asking each caller to do it keeps the requirement with the
            // code that actually has it.
            await NetRunner.Instance.SwitchToMainThread();

            T node = fillNode;
            if (fillNode == null)
            {
                if (data.Contains("scene"))
                {
                    // Instantiate the scene naturally, then cast to T
                    // This allows the scene to create the correct derived type
                    // Timed: this is a full GD.Load + Instantiate of the character scene, per
                    // deserialize, on main. A COLD load here pays the whole dependency graph -
                    // and the server never calls PreloadScenes (ShaderWarmup gates it on
                    // !HasServerFeatures), so on the server this is cold on first use.
                    var scenePathForLoad = data["scene"].AsString;
                    var bsonLoadTs = System.Diagnostics.Stopwatch.GetTimestamp();
                    var packedForBson = GD.Load<PackedScene>(scenePathForLoad);
                    var bsonLoadMs = (System.Diagnostics.Stopwatch.GetTimestamp() - bsonLoadTs)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    var bsonInstTs = System.Diagnostics.Stopwatch.GetTimestamp();
                    var sceneInstance = packedForBson.Instantiate();
                    var bsonInstMs = (System.Diagnostics.Stopwatch.GetTimestamp() - bsonInstTs)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    if (bsonLoadMs + bsonInstMs >= Diagnostics.MainThreadWork.ReportThresholdMs)
                    {
                        Debugger.Instance.Log(
                            $"[BsonSceneBuild] {scenePathForLoad} load={bsonLoadMs:F0}ms "
                            + $"instantiate={bsonInstMs:F0}ms",
                            Debugger.DebugLevel.WARN);
                    }
                    node = sceneInstance as T;
                    if (node == null)
                    {
                        throw new System.Exception($"Scene {data["scene"].AsString} does not contain a node of type {typeof(T).Name}");
                    }
                }
                else
                {
                    throw new System.Exception($"No scene path found in BSON data: {data.ToJson()}");
                }
            }

            // Mark imported nodes accordingly
            if (!node.GetMeta("import_from_external", false).AsBool())
            {
                var tcs = new TaskCompletionSource<bool>();
                // Create the event handler as a separate method so we can disconnect it later
                Action treeEnteredHandler = () =>
                {
                    foreach (var dyanmicChild in node.Network.DynamicNetworkChildren)
                    {
                        dyanmicChild.RawNode.Free();
                    }
                    foreach (var staticChild in node.Network.StaticNetworkChildren)
                    {
                        if (staticChild == null) continue;
                        staticChild.RawNode.SetMeta("import_from_external", true);
                    }
                    node.SetMeta("import_from_external", true);
                    tcs.SetResult(true);
                };

                node.TreeEntered += treeEnteredHandler;
                NetRunner.Instance.AddChild(node);
                await tcs.Task;
                NetRunner.Instance.RemoveChild(node);
                // Disconnect the TreeEntered event handler before removing the child
                node.TreeEntered -= treeEnteredHandler;
            }

            if (data.Contains("nodeName"))
            {
                node.Name = data["nodeName"].AsString;
            }

            foreach (var netNodePathAndProps in data["data"] as BsonDocument)
            {
                var nodePath = netNodePathAndProps.Name;
                var nodeProps = netNodePathAndProps.Value as BsonDocument;
                var targetNode = node.GetNodeOrNull(nodePath);
                if (targetNode == null)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Node not found for: ${nodePath}");
                    continue;
                }
                
                // Get the INetNodeBase interface for network setup
                if (targetNode is INetNodeBase netNodeBase)
                {
                    netNodeBase.Network.NetParent = node.Network;
                }
                
                // Track which properties are being set for network initialization
                foreach (var prop in nodeProps)
                {
                    node.Network.InitialSetNetProperties.Add(new Tuple<string, string>(nodePath, prop.Name));
                }
                
                // Call ReadBsonProperties through concrete base types to use virtual dispatch
                // (not interface dispatch, which would call the empty default implementation)
                try
                {
                    if (targetNode is NetNode3D nn3d)
                        nn3d.ReadBsonProperties(nodeProps);
                    else if (targetNode is NetNode2D nn2d)
                        nn2d.ReadBsonProperties(nodeProps);
                    else if (targetNode is NetNode nn)
                        nn.ReadBsonProperties(nodeProps);
                }
                catch (Exception e)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Failed to read BSON properties for {nodePath}: {e.Message}");
                }
            }
            if (data.Contains("children"))
            {
                foreach (var child in data["children"] as BsonDocument)
                {
                    var nodePath = child.Name;
                    var children = child.Value as BsonArray;
                    if (children == null)
                    {
                        continue;
                    }
                    foreach (var childData in children)
                    {
                        var childNode = await FromBSON<T>(context, childData as BsonDocument);
                        var parent = node.GetNodeOrNull(nodePath);
                        if (parent == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Parent node not found for: {nodePath}");
                            continue;
                        }
                        parent.AddChild(childNode);
                    }
                }
            }
            return node;
        }

        /// <summary>One-shot guard for the "reference can never be packed" diagnostic.</summary>
        private static bool _loggedUnpackableReference;

        /// <summary>
        /// Writes a node reference for one peer: the peer-local node id, plus the packed child
        /// id when the target is a static child. Shared by <see cref="NetNode"/>,
        /// <see cref="NetNode2D"/> and <see cref="NetNode3D"/>, whose reference serializers are
        /// otherwise identical and have silently drifted apart before.
        ///
        /// <para>Returns false having written NOTHING when the reference cannot be delivered
        /// yet. The property framework reads that as "nothing to send" and retries on a later
        /// tick, so a deferred reference costs latency, never correctness.</para>
        ///
        /// <para>The spawn gate is the load-bearing part. A peer-local id is assigned inside the
        /// spawn <c>Export</c>, BEFORE <c>WorldRunner.ExportState</c> decides whether that spawn
        /// section fits the tick budget — so a registered id is not by itself a promise that the
        /// client has the node. Shipping one anyway decodes as null on the client. That used to
        /// self-heal only because references were re-sent every tick; once they are sent on
        /// change, an ungated id would strand the reference at null permanently. Requiring
        /// <see cref="WorldRunner.ClientSpawnState.Spawned"/> — the peer ACKED the spawn — is
        /// what makes send-on-change safe.</para>
        /// </summary>
        internal static bool TryWriteNodeReference(
            WorldRunner currentWorld, NetPeer peer, INetNodeBase obj, NetBuffer buffer)
        {
            if (obj == null)
            {
                // A null reference is a real value and must be sent, not deferred.
                NetWriter.WriteUInt16(buffer, 0);
                return true;
            }

            var network = obj.Network;
            NetId targetNetId;
            byte staticChildId = 0;
            if (network.IsNetScene())
            {
                targetNetId = network.NetId;
            }
            else if (Protocol.PackNode(
                network.NetSceneFilePath,
                network.NetParent.RawNode.GetPathTo(network.RawNode),
                out staticChildId))
            {
                targetNetId = network.NetParent.NetId;
            }
            else
            {
                // Previously an exception, which the object-property loop caught and logged
                // once per node per peer per TICK. It is a build-data fault, not a per-peer
                // one, so say it once and decline to write.
                if (!_loggedUnpackableReference)
                {
                    _loggedUnpackableReference = true;
                    Debugger.Instance.Log(
                        $"[NodeReference] Cannot pack {network.NetParent.NetSceneFilePath} static child "
                        + $"{network.NetParent.RawNode.GetPathTo(network.RawNode)} ({network.RawNode.GetPath()}); "
                        + "the reference will never replicate. Further occurrences suppressed.",
                        Debugger.DebugLevel.ERROR);
                }
                return false;
            }

            // The peer must have ACKED the target's spawn, or it cannot resolve the id.
            if (currentWorld.GetClientSpawnState(targetNetId, peer) != WorldRunner.ClientSpawnState.Spawned)
            {
                return false;
            }

            var peerState = currentWorld.GetPeerWorldState(peer);
            if (peerState == null
                || !peerState.Value.WorldToPeerNodeMap.TryGetValue(targetNetId, out var peerNodeId))
            {
                return false;
            }

            NetWriter.WriteUInt16(buffer, peerNodeId);
            NetWriter.WriteByte(buffer, staticChildId);
            return true;
        }
    }
}
