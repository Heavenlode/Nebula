using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Nebula.Generator;

/// <summary>
/// Generates the PropertyCache union struct that can hold any NetProperty value type
/// without boxing. Discovers custom value types implementing INetValue&lt;T&gt;.
/// </summary>
[Generator]
public class PropertyCacheGenerator : IIncrementalGenerator
{
    // Known sizes of hardcoded primitive types (starting at offset 4)
    private static readonly Dictionary<string, int> HardcodedTypeSizes = new()
    {
        { "bool", 1 },
        { "byte", 1 },
        { "int", 4 },
        { "long", 8 },
        { "float", 4 },
        { "double", 8 },
        { "Vector2", 8 },   // 2 floats
        { "Vector3", 12 },  // 3 floats
        { "Quaternion", 16 } // 4 floats
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all struct types that implement INetValue<T>
        var netValueTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is StructDeclarationSyntax,
                transform: (ctx, _) => GetNetValueTypeInfo(ctx))
            .Where(t => t is not null)
            .Collect();

        context.RegisterSourceOutput(netValueTypes, GeneratePropertyCache!);
    }

    private static NetValueTypeInfo? GetNetValueTypeInfo(GeneratorSyntaxContext context)
    {
        var structDecl = (StructDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;
        
        if (symbol == null)
            return null;

        // Check if it implements INetValue<T>
        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.Name == "INetValue" && 
                iface.ContainingNamespace?.ToDisplayString() == "Nebula.Serialization" &&
                iface.TypeArguments.Length == 1)
            {
                var typeArg = iface.TypeArguments[0];
                // Verify the type argument is the struct itself (INetValue<Self>)
                if (SymbolEqualityComparer.Default.Equals(typeArg, symbol))
                {
                    // Look for NetValueLayoutAttribute to get the size
                    int? sizeInBytes = null;
                    foreach (var attr in symbol.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name == "NetValueLayoutAttribute" ||
                            attr.AttributeClass?.Name == "NetValueLayout")
                        {
                            if (attr.ConstructorArguments.Length > 0 &&
                                attr.ConstructorArguments[0].Value is int size)
                            {
                                sizeInBytes = size;
                            }
                            break;
                        }
                    }

                    return new NetValueTypeInfo(
                        symbol.Name,
                        symbol.ToDisplayString(),
                        symbol.ContainingNamespace?.ToDisplayString() ?? "",
                        sizeInBytes);
                }
            }
        }

        return null;
    }

    private static void GeneratePropertyCache(
        SourceProductionContext context,
        ImmutableArray<NetValueTypeInfo?> customTypes)
    {
        var sb = new StringBuilder();
        
        // Collect distinct custom types
        var distinctTypes = customTypes
            .Where(t => t is not null)
            .Select(t => t!)
            .DistinctBy(t => t.FullName)
            .ToList();

        // Calculate maximum size needed for value types
        // Start with the largest hardcoded type (Quaternion = 16)
        int maxValueTypeSize = HardcodedTypeSizes.Values.Max();
        
        // Check custom types
        var typesWithoutSize = new List<string>();
        foreach (var customType in distinctTypes)
        {
            if (customType.SizeInBytes.HasValue)
            {
                if (customType.SizeInBytes.Value > maxValueTypeSize)
                    maxValueTypeSize = customType.SizeInBytes.Value;
            }
            else
            {
                typesWithoutSize.Add(customType.FullName);
            }
        }

        // Report diagnostics for types missing the size attribute
        foreach (var typeName in typesWithoutSize)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "NEBULA001",
                    "Missing NetValueLayout attribute",
                    "Type '{0}' implements INetValue<T> but is missing [NetValueLayout(sizeInBytes)] attribute. Using default offset calculation.",
                    "Nebula.Generator",
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true),
                Location.None,
                typeName));
        }

        // Calculate reference type offset with 8-byte alignment
        // Offset 0-3: Type discriminator (4 bytes)
        // Offset 4 to (4 + maxValueTypeSize - 1): Value types
        int valueTypesEndOffset = 4 + maxValueTypeSize;
        int refTypeOffset = ((valueTypesEndOffset + 7) / 8) * 8; // Round up to 8-byte boundary
        int objectRefOffset = refTypeOffset + 8; // string is 8 bytes (pointer), then object

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using Godot;");
        sb.AppendLine("using Nebula.Serialization;");
        sb.AppendLine();
        sb.AppendLine("namespace Nebula");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Union struct for caching property values without boxing.");
        sb.AppendLine("    /// Generated to include all discovered INetValue&lt;T&gt; types.");
        sb.AppendLine($"    /// Max value type size: {maxValueTypeSize} bytes. Reference types start at offset {refTypeOffset}.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [StructLayout(LayoutKind.Explicit)]");
        sb.AppendLine("    public struct PropertyCache");
        sb.AppendLine("    {");
        
        // Type discriminator at offset 0
        sb.AppendLine("        /// <summary>Type tag indicating which field is valid</summary>");
        sb.AppendLine("        [FieldOffset(0)] public SerialVariantType Type;");
        sb.AppendLine();
        
        // Hardcoded primitives - all overlapping at offset 4
        sb.AppendLine("        // Primitives (hardcoded)");
        sb.AppendLine("        [FieldOffset(4)] public bool BoolValue;");
        sb.AppendLine("        [FieldOffset(4)] public byte ByteValue;");
        sb.AppendLine("        [FieldOffset(4)] public int IntValue;");
        sb.AppendLine("        [FieldOffset(4)] public long LongValue;");
        sb.AppendLine("        [FieldOffset(4)] public float FloatValue;");
        sb.AppendLine("        [FieldOffset(4)] public double DoubleValue;");
        sb.AppendLine("        [FieldOffset(4)] public Vector2 Vec2Value;");
        sb.AppendLine("        [FieldOffset(4)] public Vector3 Vec3Value;");
        sb.AppendLine("        [FieldOffset(4)] public Quaternion QuatValue;");
        sb.AppendLine();
        
        // Custom value types discovered from INetValue<T>
        if (distinctTypes.Any())
        {
            sb.AppendLine("        // Custom value types (discovered from INetValue<T>)");
            foreach (var customType in distinctTypes)
            {
                var sizeComment = customType.SizeInBytes.HasValue 
                    ? $" // {customType.SizeInBytes.Value} bytes"
                    : " // size unknown - add [NetValueLayout] attribute";
                sb.AppendLine($"        [FieldOffset(4)] public {customType.FullName} {customType.Name}Value;{sizeComment}");
            }
            sb.AppendLine();
        }
        
        // Reference types at calculated safe offset
        sb.AppendLine("        // Reference types (separate offset for GC safety)");
        sb.AppendLine($"        [FieldOffset({refTypeOffset})] public string? StringValue;");
        sb.AppendLine($"        [FieldOffset({objectRefOffset})] public object? RefValue;");
        
        sb.AppendLine("    }");
        sb.AppendLine();
        
        // Generate helper enum for custom type identification if needed
        if (distinctTypes.Any())
        {
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Extended type identifiers for custom INetValue types.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public enum CustomPropertyType : byte");
            sb.AppendLine("    {");
            sb.AppendLine("        None = 0,");
            byte idx = 1;
            foreach (var customType in distinctTypes)
            {
                sb.AppendLine($"        {customType.Name} = {idx++},");
            }
            sb.AppendLine("    }");
        }
        
        sb.AppendLine("}");
        
        context.AddSource("PropertyCache.g.cs", sb.ToString());
    }

    private record NetValueTypeInfo(
        string Name,
        string FullName,
        string Namespace,
        int? SizeInBytes);
}

// Extension for netstandard2.0 compatibility
internal static class EnumerableExtensions
{
    public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, System.Func<T, TKey> keySelector)
    {
        var seen = new HashSet<TKey>();
        foreach (var item in source)
        {
            if (seen.Add(keySelector(item)))
                yield return item;
        }
    }
}
