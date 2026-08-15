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

            // Nebula's own version, from the addon's plugin.cfg. Folded into the protocol
            // hash so builds on different Nebula versions refuse to connect.
            var nebulaVersion = context.AdditionalTextsProvider
                .Where(static file => NormalizePath(file.Path).EndsWith("addons/Nebula/plugin.cfg"))
                .Select(static (file, ct) => ParsePluginVersion(file.GetText(ct)?.ToString() ?? ""))
                .Collect()
                .Select(static (versions, ct) => versions.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "");

            // Combine compilation, project root, tscn files, and Nebula version
            var combined = context.CompilationProvider
                .Combine(projectRoot)
                .Combine(tscnFiles)
                .Combine(nebulaVersion);

            // Generate the protocol
            context.RegisterSourceOutput(combined, static (spc, source) =>
            {
                var (((compilation, projectRoot), files), nebulaVersion) = source;
                Execute(spc, compilation, projectRoot, files, nebulaVersion);
            });
        }

        /// <summary>
        /// Extracts the <c>version="..."</c> value from a Godot plugin.cfg. Returns an empty
        /// string if the key is absent, which Execute turns into a build error - a silently
        /// missing version would weaken the protocol hash rather than fail loudly.
        /// </summary>
        private static string ParsePluginVersion(string cfgContents)
        {
            foreach (var rawLine in cfgContents.Split('\n'))
            {
                var line = rawLine.Trim();
                var equals = line.IndexOf('=');
                if (equals < 0) continue;
                if (line.Substring(0, equals).Trim() != "version") continue;
                return line.Substring(equals + 1).Trim().Trim('"');
            }
            return "";
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
            ImmutableArray<(string Path, string Content)> tscnFiles,
            string nebulaVersion)
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

            protocolData.NebulaVersion = nebulaVersion;

            // The version is a hash input, not decoration: without it the hash would no
            // longer separate builds whose wire format changed but whose protocol data
            // didn't. Fail the build rather than emit a weaker hash.
            if (string.IsNullOrEmpty(nebulaVersion))
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingVersionDescriptor, Location.None));
                return;
            }

            // Enforce the per-scene property limit before emitting anything. The runtime
            // tracks dirty properties in a single 64-bit mask (NetworkController.DirtyMask)
            // and sizes CachedProperties to 64 - a 65th property would silently alias bit 0
            // (C# masks shift counts) and index out of bounds. Fail the build instead.
            ReportPropertyLimitViolations(context, protocolData);

            // Enforce the NetScene parenting invariant (NEBULA009): a nested NetScene under
            // a plain container node is unaddressable by late spawn delivery at runtime.
            ReportInvalidNestedContainers(context, protocolData);

            // Emit code
            var code = CodeEmitter.Emit(protocolData);
            context.AddSource("Protocol.g.cs", SourceText.From(code, Encoding.UTF8));
        }

        /// <summary>
        /// Maximum networked properties per NetScene, including properties rolled up from
        /// static children and nested non-NetScene instances. Bound by the 64-bit dirty
        /// mask in NetworkController. Mirrored at runtime by BitConstants.MaxSceneProperties.
        /// </summary>
        private const int MaxSceneProperties = 64;

        private static readonly DiagnosticDescriptor MissingVersionDescriptor = new(
            "NEBULA005",
            "Nebula version could not be determined",
            "Could not read version from addons/Nebula/plugin.cfg. The Nebula version is folded into the protocol handshake hash, so it must be resolvable at build time. Ensure Nebula.props includes plugin.cfg in <AdditionalFiles> and that the file declares a version key.",
            "Nebula.Generator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor PropertyLimitDescriptor = new(
            "NEBULA004",
            "NetScene exceeds the 64-property limit",
            "NetScene '{0}' has {1} networked properties, exceeding the maximum of {2} per scene (dirty tracking uses a 64-bit mask). Move properties onto nested NetScenes (which have their own limit), or aggregate related values into a single property such as a NetArray or a custom INetSerializable type.",
            "Nebula.Generator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor InvalidNestedContainerDescriptor = new(
            "NEBULA009",
            "Nested NetScene under a non-networked container",
            "Scene '{0}': nested NetScene '{1}' is parented under '{2}', which is neither a NetNode nor the scene root. A NetScene must be a child of another NetScene or a NetNode: late spawn delivery (interest gained after the parent scene committed) addresses the attachment point through the protocol registry, which only contains networked nodes - this spawn would be undeliverable at runtime. Attach a NetNode-derived script to '{2}', or reparent '{1}'.",
            "Nebula.Generator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static void ReportInvalidNestedContainers(SourceProductionContext context, ProtocolData data)
        {
            foreach (var violation in data.InvalidNestedContainers)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidNestedContainerDescriptor,
                    Location.None,
                    violation.ScenePath,
                    violation.NestedNodePath,
                    violation.ContainerPath));
            }
        }

        private static void ReportPropertyLimitViolations(SourceProductionContext context, ProtocolData data)
        {
            foreach (var sceneEntry in data.PropertiesLookup)
            {
                if (sceneEntry.Value.Count > MaxSceneProperties)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        PropertyLimitDescriptor,
                        Location.None,
                        sceneEntry.Key,
                        sceneEntry.Value.Count,
                        MaxSceneProperties));
                }
            }
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

                // Collected before the NetScene filter: a violation inside a rolled-up
                // (non-NetScene) scene is just as unaddressable at runtime.
                data.InvalidNestedContainers.AddRange(bytecode.InvalidNestedContainers);

                if (!bytecode.IsNetScene) continue;

                data.ScenesMap[sceneId] = sceneResPath;
                data.ScenesPack[sceneResPath] = sceneId;
                sceneId++;

                // Scene-level interest requirements
                if (bytecode.Preload)
                {
                    data.PreloadScenes.Add(sceneResPath);
                }

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

            RegisterConcreteGenericTypes(data);

            return data;
        }

        /// <summary>
        /// Registers each distinct CONCRETE generic instantiation (e.g. NetArray&lt;Vector3&gt;) as its
        /// own serializable type with a unique class index, and repoints the properties that use it.
        ///
        /// Why: a property whose type is a concrete generic is resolved by <see cref="LookupClassIndex"/>
        /// to the OPEN generic's single class index (NetArray&lt;T&gt;). Without this, every element type
        /// shares one index → one serializer, and a second element type is dispatched through the wrong
        /// serializer (InvalidCastException). Giving each concrete type its own index makes the existing
        /// StaticMethods-walking emitters generate a correct serializer per element type.
        ///
        /// Deterministic: concrete types are sorted by name before index assignment so server and client
        /// (which compile the same protocol) derive identical indices — the handshake hash folds in both
        /// prop.ClassIndex and the StaticMethods entries.
        /// </summary>
        private static void RegisterConcreteGenericTypes(ProtocolData data)
        {
            // Find concrete-generic properties that fell back to an OPEN generic index, and the open
            // generic they resolved to (whose MethodType/IsValueType the concrete type inherits).
            var concreteToOpen = new Dictionary<string, SerializableMethodData>();
            foreach (var sceneProps in data.PropertiesLookup.Values)
            {
                foreach (var prop in sceneProps.Values)
                {
                    if (prop.ClassIndex < 0) continue;
                    if (!data.StaticMethods.TryGetValue(prop.ClassIndex, out var resolved)) continue;
                    if (!CodeEmitter.IsOpenGenericType(resolved.TypeFullName)) continue; // resolved to the open generic

                    var concrete = prop.TypeFullName;
                    if (string.IsNullOrEmpty(concrete) || concrete.IndexOf('<') < 0 || CodeEmitter.IsOpenGenericType(concrete))
                        continue; // property type must itself be a concrete generic

                    concreteToOpen[concrete] = resolved;
                }
            }

            if (concreteToOpen.Count == 0)
                return;

            // Deterministic assignment: sort by name, continue the class-index counter.
            var sortedConcrete = new List<string>(concreteToOpen.Keys);
            sortedConcrete.Sort(System.StringComparer.Ordinal);
            var nextIndex = data.StaticMethods.Count == 0 ? 0 : data.StaticMethods.Keys.Max() + 1;

            var concreteToIndex = new Dictionary<string, int>();
            foreach (var concrete in sortedConcrete)
            {
                var openGen = concreteToOpen[concrete];
                data.StaticMethods[nextIndex] = new SerializableMethodData
                {
                    MethodType = openGen.MethodType,
                    TypeFullName = concrete,
                    IsValueType = openGen.IsValueType
                };
                data.SerialTypePack[concrete] = nextIndex;
                concreteToIndex[concrete] = nextIndex;
                nextIndex++;
            }

            // Repoint every property using a now-registered concrete type. PropertyData is a shared
            // reference, so patching via PropertiesLookup also updates PropertiesMap /
            // PropertiesByStaticChildId (same objects) and the protocol hash (reads prop.ClassIndex).
            foreach (var sceneProps in data.PropertiesLookup.Values)
                foreach (var prop in sceneProps.Values)
                    if (concreteToIndex.TryGetValue(prop.TypeFullName, out var concreteIndex))
                        prop.ClassIndex = concreteIndex;
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
                result.Preload = rootTypeInfo.Preload;
            }

            // Start at 1 because staticChildId 0 is reserved for the root node (".")
            var nodePathId = 1;
            var propertyCount = 0;
            var functionCount = 0;

            // Paths of nested NetScene instances seen so far in this scene - legal parents
            // for deeper nested NetScenes (tscn orders parents before children).
            var nestedNetScenePaths = new HashSet<string>();

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

                    // Nested network scenes don't roll up. Their PLACEMENT is validated
                    // instead: a NetScene may only be a child of another NetScene, a NetNode,
                    // or the scene root (architectural invariant). Late spawn delivery
                    // addresses the attachment point as (parent scene, packed node path), and
                    // the registry only contains networked nodes - a plain container there
                    // makes the spawn unaddressable at runtime. Fail the build (NEBULA009)
                    // rather than let it surface as an undeliverable spawn.
                    if (nestedBytecode.IsNetScene)
                    {
                        var containerPath = node.Parent;
                        bool legalContainer = string.IsNullOrEmpty(containerPath)
                            || containerPath == "."
                            || nestedNetScenePaths.Contains(containerPath)
                            || HasStaticNetNodePath(result, containerPath);
                        if (!legalContainer)
                        {
                            result.InvalidNestedContainers.Add(new InvalidNestedContainer
                            {
                                ScenePath = sceneResPath,
                                NestedNodePath = nodePath,
                                ContainerPath = containerPath!
                            });
                        }

                        // Legal parent for any deeper nested NetScenes in this scene
                        // (NetScene-under-NetScene is allowed by the invariant).
                        nestedNetScenePaths.Add(nodePath);
                        continue;
                    }

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
                                Predicted = prop.Value.Predicted,
                                ChunkBudget = prop.Value.ChunkBudget,
                                IsObjectProperty = prop.Value.IsObjectProperty,
                                IsPerPeer = prop.Value.IsPerPeer
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
                        // Look up class index - try exact type first, then generic type definition
                        var classIndex = LookupClassIndex(analysisResult, prop.TypeFullName);
                        
                        // Determine if this is an object property (INetSerializable reference type)
                        // vs a primitive/value property (INetValue value type)
                        var isObjectProperty = false;
                        if (classIndex >= 0 && classIndex < analysisResult.SerializableTypes.Count)
                        {
                            var serializableType = analysisResult.SerializableTypes[classIndex];
                            isObjectProperty = !serializableType.IsValueType;
                        }
                        
                        // Determine SubtypeIdentifier:
                        // - For enums: use the underlying type name
                        // - For custom/Object types (including generics like NetArray<T>): 
                        //   preserve the full type name for runtime type detection
                        // - Otherwise: null (will be resolved by MapTypeToVariant)
                        string? subtypeId = null;
                        if (prop.IsEnum)
                        {
                            subtypeId = prop.EnumUnderlyingTypeName;
                        }
                        else if (IsCustomObjectType(prop.TypeFullName))
                        {
                            // Custom serializable type - preserve full type name for NetArray detection etc.
                            subtypeId = prop.TypeFullName;
                        }

                        result.Properties[nodePath][prop.Name] = new PropertyData
                        {
                            NodePath = nodePath,
                            Name = prop.Name,
                            TypeFullName = prop.TypeFullName,
                            SubtypeIdentifier = subtypeId,
                            Index = (byte)propertyCount++,
                            LocalIndex = prop.ClassLocalIndex, // Use class-local index from analyzer
                            InterestMask = prop.InterestMask,
                            InterestRequired = prop.InterestRequired,
                            ClassIndex = classIndex,
                            NotifyOnChange = prop.NotifyOnChange,
                            Interpolate = prop.Interpolate,
                            InterpolateSpeed = prop.InterpolateSpeed,
                            IsEnum = prop.IsEnum,
                            Predicted = prop.Predicted,
                            ChunkBudget = prop.ChunkBudget,
                            IsObjectProperty = isObjectProperty,
                            IsPerPeer = prop.IsPerPeer
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
                                TypeFullName = param.TypeFullName,
                                IsEnum = param.IsEnum,
                                EnumUnderlyingTypeName = param.EnumUnderlyingTypeName,
                                SubtypeIdentifier = param.IsEnum ? param.EnumUnderlyingTypeName : null
                            });
                        }

                        result.Functions[nodePath][func.Name] = funcData;
                    }
                }
            }

            cache[sceneResPath] = result;
            return result;
        }

        /// <summary>
        /// Whether a path is already registered as a static net node for this scene. Guards
        /// against duplicate ids when several nested NetScenes share one container, or when
        /// the container itself is a scripted NetNode (registered when its own tscn entry was
        /// processed - parents precede children in tscn order).
        /// </summary>
        private static bool HasStaticNetNodePath(SceneBytecode result, string path)
        {
            for (int i = 0; i < result.StaticNetNodes.Count; i++)
            {
                if (result.StaticNetNodes[i].Path == path) return true;
            }
            return false;
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
        
        /// <summary>
        /// Determines if a type would map to SerialVariantType.Object (custom types).
        /// These types need their full type name preserved in metadata for runtime detection.
        /// </summary>
        private static bool IsCustomObjectType(string typeFullName)
        {
            // Built-in primitive types
            if (typeFullName is "System.Boolean" or "bool" or
                "System.Int16" or "short" or
                "System.Int32" or "int" or
                "System.Byte" or "byte" or
                "System.Int64" or "long" or
                "System.UInt64" or "ulong" or
                "System.Single" or "float" or
                "System.Double" or "double" or
                "System.String" or "string")
            {
                return false;
            }
            
            // Built-in array types
            if (typeFullName is "System.Byte[]" or "byte[]" or
                "System.Int64[]" or "long[]")
            {
                return false;
            }
            
            // Built-in Godot types
            if (typeFullName is "Godot.Vector2" or "Godot.Vector2I" or
                "Godot.Vector3" or "Godot.Vector3I" or
                "Godot.Vector4" or "Godot.Quaternion" or
                "Godot.Color" or "Godot.Transform2D" or
                "Godot.Transform3D" or "Godot.Basis" or
                "Godot.Rect2" or "Godot.Rect2I" or
                "Godot.Aabb" or "Godot.Plane" or
                "Godot.Projection")
            {
                return false;
            }
            
            // Everything else is a custom Object type
            return true;
        }
        
        /// <summary>
        /// Looks up the class index for a type, handling generic types by
        /// falling back to the generic type definition if the exact type isn't found.
        /// </summary>
        private static int LookupClassIndex(TypeAnalyzer.AnalysisResult analysisResult, string typeFullName)
        {
            // Try exact match first
            if (analysisResult.SerializableTypeIndices.TryGetValue(typeFullName, out var idx))
            {
                return idx;
            }
            
            // If it's a generic type (contains '<'), try to find the generic type definition
            // e.g., "Nebula.Serialization.NetArray<Godot.Vector3>" -> "Nebula.Serialization.NetArray<T>"
            var genericBracket = typeFullName.IndexOf('<');
            if (genericBracket > 0)
            {
                var genericBase = typeFullName.Substring(0, genericBracket);
                
                // Count type arguments to construct the right generic definition
                // For single type arg: NetArray<T>, for two: Dict<TKey, TValue>, etc.
                var typeArgs = typeFullName.Substring(genericBracket + 1);
                var depth = 0;
                var argCount = 1;
                foreach (var c in typeArgs)
                {
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    else if (c == ',' && depth == 0) argCount++;
                }
                
                // Build the generic definition name based on arg count
                string genericDef;
                if (argCount == 1)
                {
                    genericDef = genericBase + "<T>";
                }
                else
                {
                    // For multiple type args, use T1, T2, etc.
                    var args = string.Join(", ", Enumerable.Range(1, argCount).Select(i => $"T{i}"));
                    genericDef = genericBase + "<" + args + ">";
                }
                
                if (analysisResult.SerializableTypeIndices.TryGetValue(genericDef, out idx))
                {
                    return idx;
                }
            }
            
            return -1;
        }
    }
}