#nullable enable
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Nebula.Generators
{
    /// <summary>
    /// Analyzes compilation to find INetNode, INetSerializable, and IBsonSerializable types.
    /// </summary>
    internal sealed class TypeAnalyzer
    {
        public sealed class NetPropertyInfo
        {
            public string Name { get; init; } = "";
            public string TypeFullName { get; init; } = "";
            public long InterestMask { get; init; }
            public long InterestRequired { get; init; }
            public bool NotifyOnChange { get; init; } = false;
            public bool Interpolate { get; init; } = false;
            public float InterpolateSpeed { get; init; } = 15f;
            public bool IsEnum { get; init; } = false;
            /// <summary>
            /// The underlying type name of the enum (e.g., "System.Int32", "System.Byte").
            /// Null if not an enum.
            /// </summary>
            public string? EnumUnderlyingTypeName { get; init; }
            /// <summary>
            /// Index of this property within its declaring class (used for SetNetPropertyByIndex).
            /// </summary>
            public byte ClassLocalIndex { get; init; }
            /// <summary>
            /// When true, this property participates in client-side prediction.
            /// </summary>
            public bool Predicted { get; init; } = false;
        }

        public sealed class NetFunctionInfo
        {
            public string Name { get; init; } = "";
            public List<ParameterInfo> Parameters { get; init; } = new();
            public int Sources { get; init; } = 3; // All by default
        }

        public sealed class ParameterInfo
        {
            public string TypeFullName { get; init; } = "";
        }

        public sealed class NetworkTypeInfo
        {
            public string ScriptPath { get; init; } = "";
            public string TypeFullName { get; init; } = "";
            public List<NetPropertyInfo> Properties { get; init; } = new();
            public List<NetFunctionInfo> Functions { get; init; } = new();
            public long InterestAny { get; init; } = 0;
            public long InterestRequired { get; init; } = 0;
        }

        public sealed class SerializableTypeInfo
        {
            public string TypeFullName { get; init; } = "";
            public bool HasNetworkSerialize { get; init; }
            public bool HasNetworkDeserialize { get; init; }
            public bool HasBsonDeserialize { get; init; }
            /// <summary>
            /// True if this type implements INetValue (value type), false if INetSerializable (reference type).
            /// </summary>
            public bool IsValueType { get; init; }
        }

        public sealed class AnalysisResult
        {
            public Dictionary<string, NetworkTypeInfo> NetNodesByScriptPath { get; } = new();
            public List<SerializableTypeInfo> SerializableTypes { get; } = new();
            public Dictionary<string, int> SerializableTypeIndices { get; } = new();
        }

        public static AnalysisResult Analyze(Compilation compilation, string projectRoot)
        {
            var result = new AnalysisResult();
            
            // Find all types
            var allTypes = GetAllTypes(compilation.GlobalNamespace).ToList();

            // Find serializable types (INetSerializable<T> and IBsonSerializable<T>)
            var serializableIndex = 0;
            foreach (var type in allTypes)
            {
                if (type.IsAbstract || type.TypeKind == TypeKind.Interface) continue;

                var interfaces = type.AllInterfaces;
                var hasNetValue = interfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetValue");
                var hasNetSerializable = interfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetSerializable");
                var hasAnyNetSerializable = hasNetValue || hasNetSerializable;
                var hasBsonSerializable = interfaces.Any(i => 
                    i.IsGenericType && (i.OriginalDefinition.Name == "IBsonSerializable" || 
                                        i.OriginalDefinition.Name == "IBsonValue"));

                if (!hasAnyNetSerializable && !hasBsonSerializable) continue;

                var info = new SerializableTypeInfo
                {
                    TypeFullName = GetFullTypeName(type),
                    HasNetworkSerialize = hasAnyNetSerializable && HasStaticMethod(type, "NetworkSerialize"),
                    HasNetworkDeserialize = hasAnyNetSerializable && HasStaticMethod(type, "NetworkDeserialize"),
                    HasBsonDeserialize = hasBsonSerializable && HasStaticMethod(type, "BsonDeserialize"),
                    IsValueType = hasNetValue,
                };

                result.SerializableTypes.Add(info);
                result.SerializableTypeIndices[info.TypeFullName] = serializableIndex++;
            }

            // Find INetNode types
            foreach (var type in allTypes)
            {
                if (type.IsAbstract || type.TypeKind == TypeKind.Interface) continue;

                var interfaces = type.AllInterfaces;
                var isNetNode = interfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetNode");

                if (!isNetNode) continue;

                var scriptPath = GetScriptPath(type, projectRoot);
                if (string.IsNullOrEmpty(scriptPath)) continue;

                // Extract class-level [NetInterest] attribute
                var netInterestAttr = type.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "NetInterest" || 
                                        a.AttributeClass?.Name == "NetInterestAttribute");
                
                var interestAny = netInterestAttr != null ? GetNamedArgument(netInterestAttr, "Any", 0L) : 0L;
                var interestRequired = netInterestAttr != null ? GetNamedArgument(netInterestAttr, "Required", 0L) : 0L;

                var netTypeInfo = new NetworkTypeInfo
                {
                    ScriptPath = scriptPath,
                    TypeFullName = GetFullTypeName(type),
                    Properties = GetNetProperties(type).ToList(),
                    Functions = GetNetFunctions(type).ToList(),
                    InterestAny = interestAny,
                    InterestRequired = interestRequired,
                };

                result.NetNodesByScriptPath[scriptPath] = netTypeInfo;
            }

            return result;
        }

        private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in GetNestedTypes(type))
                    yield return nested;
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                foreach (var type in GetAllTypes(childNs))
                    yield return type;
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deepNested in GetNestedTypes(nested))
                    yield return deepNested;
            }
        }

        private static bool HasStaticMethod(INamedTypeSymbol type, string methodName)
        {
            var current = type;
            while (current != null)
            {
                var method = current.GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public);
                
                if (method != null) return true;
                current = current.BaseType;
            }
            return false;
        }

        private static string GetScriptPath(INamedTypeSymbol type, string projectRoot)
        {
            // First try ScriptPathAttribute (might be visible if in same partial file)
            var attr = type.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "ScriptPathAttribute");

            if (attr != null)
            {
                var pathArg = attr.ConstructorArguments.FirstOrDefault();
                if (pathArg.Value is string path)
                {
                    return path.Replace("\\", "/");
                }
            }

            // Fallback: derive from source file location using project root
            if (string.IsNullOrEmpty(projectRoot))
                return "";

            var normalizedRoot = projectRoot.Replace("\\", "/");
            if (normalizedRoot.EndsWith("/"))
                normalizedRoot = normalizedRoot.Substring(0, normalizedRoot.Length - 1);

            foreach (var location in type.Locations)
            {
                if (location.SourceTree?.FilePath is string filePath)
                {
                    var normalized = filePath.Replace("\\", "/");
                    
                    if (normalized.StartsWith(normalizedRoot))
                    {
                        var relativePath = normalized.Substring(normalizedRoot.Length);
                        if (relativePath.StartsWith("/"))
                            relativePath = relativePath.Substring(1);
                        return "res://" + relativePath;
                    }
                }
            }

            return "";
        }

        private static string GetFullTypeName(ITypeSymbol type)
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");
        }

        private static IEnumerable<NetPropertyInfo> GetNetProperties(INamedTypeSymbol type)
        {
            var visited = new HashSet<string>();
            var current = type;

            // First pass: collect all classes in the inheritance chain and count their properties
            // We need CUMULATIVE indices that match what NetPropertyGenerator creates:
            // Each class's indices start AFTER all base class property counts
            var classPropertyCounts = new Dictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var classBaseOffsets = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            
            // Build list of classes from derived to base
            var classChain = new List<INamedTypeSymbol>();
            var temp = type;
            while (temp != null)
            {
                var implementsNetNode = temp.AllInterfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetNode");
                if (!implementsNetNode) break;
                classChain.Add(temp);
                classPropertyCounts[temp] = 0;
                temp = temp.BaseType;
            }

            // Count properties in each class (needed to calculate offsets)
            foreach (var cls in classChain)
            {
                foreach (var member in cls.GetMembers())
                {
                    if (member is not IPropertySymbol prop) continue;
                    var hasNetProp = prop.GetAttributes()
                        .Any(a => a.AttributeClass?.Name == "NetProperty" || 
                                  a.AttributeClass?.Name == "NetPropertyAttribute");
                    if (hasNetProp)
                        classPropertyCounts[cls]++;
                }
            }

            // Calculate cumulative base offsets (sum of all base class properties)
            // Process from base to derived
            int cumulativeOffset = 0;
            for (int i = classChain.Count - 1; i >= 0; i--)
            {
                classBaseOffsets[classChain[i]] = cumulativeOffset;
                cumulativeOffset += classPropertyCounts[classChain[i]];
            }

            // Reset counts for per-class indexing during iteration
            foreach (var cls in classChain)
            {
                classPropertyCounts[cls] = 0;
            }

            while (current != null)
            {
                // Check if this type in chain implements INetNode
                var implementsNetNode = current.AllInterfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetNode");
                
                if (!implementsNetNode) break;

                foreach (var member in current.GetMembers())
                {
                    if (member is not IPropertySymbol prop) continue;
                    if (visited.Contains(prop.Name)) continue;

                    var netPropAttr = prop.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "NetProperty" || 
                                            a.AttributeClass?.Name == "NetPropertyAttribute");
                    
                    if (netPropAttr == null) continue;

                    visited.Add(prop.Name);

                    // Calculate cumulative index: baseOffset + classLocalIndex
                    var classLocalIndex = classPropertyCounts[current]++;
                    var cumulativeIndex = classBaseOffsets[current] + classLocalIndex;

                    // Get enum underlying type if this is an enum
                    var isEnum = prop.Type.TypeKind == TypeKind.Enum;
                    string? enumUnderlyingType = null;
                    if (isEnum && prop.Type is INamedTypeSymbol enumType)
                    {
                        enumUnderlyingType = enumType.EnumUnderlyingType?.ToDisplayString();
                    }

                    yield return new NetPropertyInfo
                    {
                        Name = prop.Name,
                        TypeFullName = GetFullTypeName(prop.Type),
                        InterestMask = GetNamedArgument(netPropAttr, "InterestMask", long.MaxValue),
                        InterestRequired = GetNamedArgument(netPropAttr, "InterestRequired", 0L),
                        NotifyOnChange = GetNamedArgument(netPropAttr, "NotifyOnChange", false),
                        Interpolate = GetNamedArgument(netPropAttr, "Interpolate", false),
                        InterpolateSpeed = GetNamedArgument(netPropAttr, "InterpolateSpeed", 15f),
                        IsEnum = isEnum,
                        EnumUnderlyingTypeName = enumUnderlyingType,
                        ClassLocalIndex = (byte)cumulativeIndex,
                        Predicted = GetNamedArgument(netPropAttr, "Predicted", false),
                    };
                }

                current = current.BaseType;
            }
        }

        private static IEnumerable<NetFunctionInfo> GetNetFunctions(INamedTypeSymbol type)
        {
            var visited = new HashSet<string>();
            var current = type;

            while (current != null)
            {
                var implementsNetNode = current.AllInterfaces.Any(i => 
                    i.IsGenericType && i.OriginalDefinition.Name == "INetNode");
                
                if (!implementsNetNode) break;

                foreach (var member in current.GetMembers())
                {
                    if (member is not IMethodSymbol method) continue;
                    if (method.MethodKind != MethodKind.Ordinary) continue;
                    if (visited.Contains(method.Name)) continue;

                    var netFuncAttr = method.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "NetFunction" || 
                                            a.AttributeClass?.Name == "NetFunctionAttribute");
                    
                    if (netFuncAttr == null) continue;

                    visited.Add(method.Name);

                    yield return new NetFunctionInfo
                    {
                        Name = method.Name,
                        Parameters = method.Parameters
                            .Select(p => new ParameterInfo { TypeFullName = GetFullTypeName(p.Type) })
                            .ToList(),
                        Sources = GetNamedArgument(netFuncAttr, "Source", 3), // Default All = 3
                    };
                }

                current = current.BaseType;
            }
        }

        private static T GetNamedArgument<T>(AttributeData attr, string name, T defaultValue = default!)
        {
            var arg = attr.NamedArguments.FirstOrDefault(a => a.Key == name);
            if (arg.Key == null) return defaultValue;
            
            if (arg.Value.Value is T value) return value;
            
            // Handle numeric type conversions (Roslyn may store values as different numeric types)
            if (arg.Value.Value != null)
            {
                if (typeof(T) == typeof(int))
                {
                    return (T)(object)System.Convert.ToInt32(arg.Value.Value);
                }
                if (typeof(T) == typeof(long))
                {
                    return (T)(object)System.Convert.ToInt64(arg.Value.Value);
                }
            }
            
            return defaultValue;
        }
    }
}