#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace Nebula.Generators
{
    [Generator(LanguageNames.CSharp)]
    public sealed class ProtocolGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Find project.godot to determine project root
            var projectRoot = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith("project.godot"))
                .Select(static (file, ct) => GetDirectoryPath(file.Path))
                .Collect()
                .Select(static (roots, ct) => roots.FirstOrDefault() ?? "");

            // Collect all .tscn files
            var tscnFiles = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith(".tscn"))
                .Select(static (file, ct) => (
                    Path: NormalizePath(file.Path),
                    Content: file.GetText(ct)?.ToString() ?? ""
                ))
                .Collect();

            // Combine compilation, project root, and tscn files
            var combined = context.CompilationProvider
                .Combine(projectRoot)
                .Combine(tscnFiles);

            // Generate the protocol
            context.RegisterSourceOutput(combined, static (spc, source) =>
            {
                var ((compilation, projectRoot), files) = source;
                Execute(spc, compilation, projectRoot, files);
            });
        }

        private static string GetDirectoryPath(string filePath)
        {
            var normalized = filePath.Replace("\\", "/");
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash >= 0 ? normalized.Substring(0, lastSlash) : "";
        }

        private static void Execute(
            SourceProductionContext context,
            Compilation compilation,
            string projectRoot,
            ImmutableArray<(string Path, string Content)> tscnFiles)
        {
            // Analyze types from compilation, passing project root for path resolution
            var analysisResult = TypeAnalyzer.Analyze(compilation, projectRoot);

            // Parse all tscn files
            var parser = new TscnParser();
            var parsedScenes = new Dictionary<string, TscnParser.ParsedTscn>();
            var fileContents = new Dictionary<string, string>();

            foreach (var (path, content) in tscnFiles)
            {
                if (string.IsNullOrEmpty(content)) continue;
                
                var resPath = ToResPath(path, projectRoot);
                fileContents[resPath] = content;
                
                // Create fresh parser for each file to reset resource mappings
                var sceneParser = new TscnParser();
                parsedScenes[resPath] = sceneParser.Parse(content);
            }

            // Build protocol data
            var protocolData = BuildProtocol(
                parsedScenes,
                fileContents,
                analysisResult);

            // Emit code
            var code = CodeEmitter.Emit(protocolData);
            context.AddSource("Protocol.g.cs", SourceText.From(code, Encoding.UTF8));
        }

        private static ProtocolData BuildProtocol(
            Dictionary<string, TscnParser.ParsedTscn> parsedScenes,
            Dictionary<string, string> fileContents,
            TypeAnalyzer.AnalysisResult analysisResult)
        {
            var data = new ProtocolData();
            var sceneDataCache = new Dictionary<string, SceneBytecode>();

            // Build static methods from serializable types
            var methodIndex = 0;
            foreach (var serType in analysisResult.SerializableTypes)
            {
                var methodType = 0;
                if (serType.HasNetworkSerialize) methodType |= 1;
                if (serType.HasNetworkDeserialize) methodType |= 2;
                if (serType.HasBsonDeserialize) methodType |= 4;

                data.StaticMethods[methodIndex] = new SerializableMethodData
                {
                    MethodType = methodType,
                    TypeFullName = serType.TypeFullName,
                    IsValueType = serType.IsValueType
                };
                data.SerialTypePack[serType.TypeFullName] = methodIndex;
                methodIndex++;
            }

            // Process all scenes
            byte sceneId = 0;
            foreach (var kvp in parsedScenes)
            {
                var sceneResPath = kvp.Key;
                var parsed = kvp.Value;

                var bytecode = GenerateSceneBytecode(
                    sceneResPath,
                    parsedScenes,
                    fileContents,
                    analysisResult,
                    sceneDataCache);

                if (!bytecode.IsNetScene) continue;

                data.ScenesMap[sceneId] = sceneResPath;
                data.ScenesPack[sceneResPath] = sceneId;
                sceneId++;

                // Scene-level interest requirements
                if (bytecode.InterestAny != 0 || bytecode.InterestRequired != 0)
                {
                    data.SceneInterestMap[sceneResPath] = new SceneInterestData
                    {
                        InterestAny = bytecode.InterestAny,
                        InterestRequired = bytecode.InterestRequired
                    };
                }

                // Static network node paths
                if (bytecode.StaticNetNodes.Count > 0)
                {
                    data.StaticNetworkNodePathsMap[sceneResPath] = new Dictionary<byte, string>();
                    data.StaticNetworkNodePathsPack[sceneResPath] = new Dictionary<string, byte>();

                    foreach (var node in bytecode.StaticNetNodes)
                    {
                        var nodeId = (byte)node.Id;
                        data.StaticNetworkNodePathsMap[sceneResPath][nodeId] = node.Path;
                        data.StaticNetworkNodePathsPack[sceneResPath][node.Path] = nodeId;
                    }
                }

                // Properties
                if (bytecode.Properties.Count > 0)
                {
                    data.PropertiesMap[sceneResPath] = new Dictionary<string, Dictionary<string, PropertyData>>();
                    data.PropertiesLookup[sceneResPath] = new Dictionary<int, PropertyData>();
                    data.PropertiesByStaticChildId[sceneResPath] = new Dictionary<byte, Dictionary<string, PropertyData>>();

                    foreach (var nodeKvp in bytecode.Properties)
                    {
                        var nodePath = nodeKvp.Key;
                        data.PropertiesMap[sceneResPath][nodePath] = nodeKvp.Value;

                        foreach (var prop in nodeKvp.Value.Values)
                        {
                            data.PropertiesLookup[sceneResPath][prop.Index] = prop;
                        }
                        
                        // Also populate PropertiesByStaticChildId for direct lookup
                        // Root node (".") always uses staticChildId 0
                        if (nodePath == ".")
                        {
                            data.PropertiesByStaticChildId[sceneResPath][0] = nodeKvp.Value;
                        }
                        else if (data.StaticNetworkNodePathsPack.TryGetValue(sceneResPath, out var nodePathsPack) &&
                            nodePathsPack.TryGetValue(nodePath, out var staticChildId))
                        {
                            data.PropertiesByStaticChildId[sceneResPath][staticChildId] = nodeKvp.Value;
                        }
                    }
                }

                // Functions
                if (bytecode.Functions.Count > 0)
                {
                    data.FunctionsMap[sceneResPath] = new Dictionary<string, Dictionary<string, FunctionData>>();
                    data.FunctionsLookup[sceneResPath] = new Dictionary<int, FunctionData>();

                    foreach (var nodeKvp in bytecode.Functions)
                    {
                        data.FunctionsMap[sceneResPath][nodeKvp.Key] = nodeKvp.Value;

                        foreach (var func in nodeKvp.Value.Values)
                        {
                            data.FunctionsLookup[sceneResPath][func.Index] = func;
                        }
                    }
                }
            }

            return data;
        }

        private static SceneBytecode GenerateSceneBytecode(
            string sceneResPath,
            Dictionary<string, TscnParser.ParsedTscn> parsedScenes,
            Dictionary<string, string> fileContents,
            TypeAnalyzer.AnalysisResult analysisResult,
            Dictionary<string, SceneBytecode> cache)
        {
            if (cache.TryGetValue(sceneResPath, out var cached))
                return cached;

            var result = new SceneBytecode();

            if (!parsedScenes.TryGetValue(sceneResPath, out var parsed))
            {
                cache[sceneResPath] = result;
                return result;
            }

            // Check if root node has a script that's a net node
            if (parsed.RootNode == null ||
                !parsed.RootNode.Properties.TryGetValue("script", out var rootScript))
            {
                cache[sceneResPath] = result;
                return result;
            }

            result.IsNetScene = IsNetNode(rootScript, analysisResult);

            // Extract class-level interest from the root node's type info
            if (result.IsNetScene && analysisResult.NetNodesByScriptPath.TryGetValue(rootScript, out var rootTypeInfo))
            {
                result.InterestAny = rootTypeInfo.InterestAny;
                result.InterestRequired = rootTypeInfo.InterestRequired;
            }

            // Start at 1 because staticChildId 0 is reserved for the root node (".")
            var nodePathId = 1;
            var propertyCount = 0;
            var functionCount = 0;

            foreach (var node in parsed.Nodes)
            {
                var nodePath = node.Parent == null
                    ? "."
                    : node.Parent == "."
                        ? node.Name
                        : $"{node.Parent}/{node.Name}";

                var nodeHasScript = node.Properties.TryGetValue("script", out var scriptPath);
                var nodeIsNetNode = nodeHasScript && IsNetNode(scriptPath!, analysisResult);
                var nodeIsNestedScene = node.Instance != null;

                if (!nodeIsNetNode && !nodeIsNestedScene)
                    continue;

                // Handle nested scenes
                if (nodeIsNestedScene)
                {
                    var nestedBytecode = GenerateSceneBytecode(
                        node.Instance!,
                        parsedScenes,
                        fileContents,
                        analysisResult,
                        cache);

                    // Nested network scenes don't roll up
                    if (nestedBytecode.IsNetScene) continue;

                    // Merge static net nodes
                    foreach (var entry in nestedBytecode.StaticNetNodes)
                    {
                        result.StaticNetNodes.Add(new StaticNetNode
                        {
                            Id = nodePathId++,
                            Path = $"{nodePath}/{entry.Path}"
                        });
                    }

                    // Merge properties
                    foreach (var propKvp in nestedBytecode.Properties)
                    {
                        var newNodePath = $"{nodePath}/{propKvp.Key}";
                        result.Properties[newNodePath] = new Dictionary<string, PropertyData>();

                        foreach (var prop in propKvp.Value)
                        {
                        result.Properties[newNodePath][prop.Key] = new PropertyData
                        {
                            NodePath = $"{nodePath}/{prop.Value.NodePath}",
                            Name = prop.Value.Name,
                            TypeFullName = prop.Value.TypeFullName,
                            SubtypeIdentifier = prop.Value.SubtypeIdentifier,
                            Index = (byte)propertyCount++,
                            LocalIndex = prop.Value.LocalIndex, // Preserve class-local index from nested scene
                            InterestMask = prop.Value.InterestMask,
                            InterestRequired = prop.Value.InterestRequired,
                            ClassIndex = prop.Value.ClassIndex,
                            NotifyOnChange = prop.Value.NotifyOnChange,
                            Interpolate = prop.Value.Interpolate,
                            InterpolateSpeed = prop.Value.InterpolateSpeed,
                            IsEnum = prop.Value.IsEnum,
                            Predicted = prop.Value.Predicted
                        };
                        }
                    }

                    // Merge functions
                    foreach (var funcKvp in nestedBytecode.Functions)
                    {
                        var newNodePath = $"{nodePath}/{funcKvp.Key}";
                        result.Functions[newNodePath] = new Dictionary<string, FunctionData>();

                        foreach (var func in funcKvp.Value)
                        {
                            var newFunc = new FunctionData
                            {
                                NodePath = $"{nodePath}/{func.Value.NodePath}",
                                Name = func.Value.Name,
                                Index = (byte)functionCount++,
                                Sources = func.Value.Sources
                            };
                            newFunc.Arguments.AddRange(func.Value.Arguments);
                            result.Functions[newNodePath][func.Key] = newFunc;
                        }
                    }

                    continue;
                }

                // Node with INetNode script (skip root - it's not its own child)
                if (nodePath != ".")
                {
                    result.StaticNetNodes.Add(new StaticNetNode
                    {
                        Id = nodePathId++,
                        Path = nodePath
                    });
                }

                if (!analysisResult.NetNodesByScriptPath.TryGetValue(scriptPath!, out var typeInfo))
                    continue;

                // Collect properties
                if (typeInfo.Properties.Count > 0)
                {
                    result.Properties[nodePath] = new Dictionary<string, PropertyData>();

                    foreach (var prop in typeInfo.Properties)
                    {
                        var classIndex = analysisResult.SerializableTypeIndices
                            .TryGetValue(prop.TypeFullName, out var idx) ? idx : -1;

                        result.Properties[nodePath][prop.Name] = new PropertyData
                        {
                            NodePath = nodePath,
                            Name = prop.Name,
                            TypeFullName = prop.TypeFullName,
                            SubtypeIdentifier = prop.IsEnum ? prop.EnumUnderlyingTypeName : null,
                            Index = (byte)propertyCount++,
                            LocalIndex = prop.ClassLocalIndex, // Use class-local index from analyzer
                            InterestMask = prop.InterestMask,
                            InterestRequired = prop.InterestRequired,
                            ClassIndex = classIndex,
                            NotifyOnChange = prop.NotifyOnChange,
                            Interpolate = prop.Interpolate,
                            InterpolateSpeed = prop.InterpolateSpeed,
                            IsEnum = prop.IsEnum,
                            Predicted = prop.Predicted
                        };
                    }
                }

                // Collect functions
                if (typeInfo.Functions.Count > 0)
                {
                    result.Functions[nodePath] = new Dictionary<string, FunctionData>();

                    foreach (var func in typeInfo.Functions)
                    {
                        var funcData = new FunctionData
                        {
                            NodePath = nodePath,
                            Name = func.Name,
                            Index = (byte)functionCount++,
                            Sources = func.Sources
                        };

                        foreach (var param in func.Parameters)
                        {
                            funcData.Arguments.Add(new ArgumentData
                            {
                                TypeFullName = param.TypeFullName
                            });
                        }

                        result.Functions[nodePath][func.Name] = funcData;
                    }
                }
            }

            cache[sceneResPath] = result;
            return result;
        }

        private static bool IsNetNode(string scriptPath, TypeAnalyzer.AnalysisResult analysis)
        {
            var normalized = scriptPath.Replace("\\", "/");
            return analysis.NetNodesByScriptPath.ContainsKey(normalized);
        }

        /// <summary>
        /// Normalize file path from AdditionalTexts to res:// format.
        /// </summary>
        private static string NormalizePath(string path)
        {
            return path.Replace("\\", "/");
        }

        /// <summary>
        /// Convert absolute filesystem path to Godot res:// path.
        /// </summary>
        private static string ToResPath(string absolutePath, string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
                return "";

            var normalized = absolutePath.Replace("\\", "/");
            var normalizedRoot = projectRoot.Replace("\\", "/");
            
            // Ensure root doesn't end with slash for consistent stripping
            if (normalizedRoot.EndsWith("/"))
                normalizedRoot = normalizedRoot.Substring(0, normalizedRoot.Length - 1);

            if (normalized.StartsWith(normalizedRoot))
            {
                var relativePath = normalized.Substring(normalizedRoot.Length);
                if (relativePath.StartsWith("/"))
                    relativePath = relativePath.Substring(1);
                return "res://" + relativePath;
            }

            // Fallback if path doesn't start with project root
            return "";
        }
    }
}