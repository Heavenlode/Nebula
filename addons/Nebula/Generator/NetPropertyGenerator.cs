using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Nebula.Generator;

[Generator]
public class NetPropertyGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var properties = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Nebula.NetProperty",
                predicate: (node, _) => node is PropertyDeclarationSyntax,
                transform: (ctx, _) => GetPropertyInfo(ctx))
            .Where(p => p is not null)
            .Collect();

        context.RegisterSourceOutput(properties, GenerateSource!);
    }

    private static PropertyInfo? GetPropertyInfo(GeneratorAttributeSyntaxContext context)
    {
        var propertySymbol = (IPropertySymbol)context.TargetSymbol;
        var containingType = propertySymbol.ContainingType;

        return new PropertyInfo(
            propertySymbol.Name,
            propertySymbol.Type.ToDisplayString(),
            propertySymbol.Type.IsValueType,  // add this
            containingType.Name,
            containingType.ContainingNamespace.IsGlobalNamespace
                ? null
                : containingType.ContainingNamespace.ToDisplayString());
    }

    private static void GenerateSource(
        SourceProductionContext context,
        ImmutableArray<PropertyInfo?> properties)
    {
        var grouped = properties
            .Where(p => p is not null)
            .GroupBy(p => (p!.Namespace, p.ClassName));

        foreach (var group in grouped)
        {
            var (ns, className) = group.Key;
            var sb = new StringBuilder();

            if (ns is not null)
                sb.AppendLine($"namespace {ns};");

            sb.AppendLine();
            sb.AppendLine($"partial class {className}");
            sb.AppendLine("{");

            foreach (var prop in group)
            {
                var markDirtyMethod = prop.IsValueType ? "MarkDirty" : "MarkDirtyRef";

                sb.AppendLine($"    public void On{prop!.PropertyName}Changed({prop.PropertyType} newVal)");
                sb.AppendLine("    {");
                sb.AppendLine($"        Network.{markDirtyMethod}(this, \"{prop.PropertyName}\", newVal);");
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");

            context.AddSource($"{className}.NetProperties.g.cs", sb.ToString());
        }
    }

    private record PropertyInfo(
        string PropertyName,
        string PropertyType,
        bool IsValueType,
        string ClassName,
        string? Namespace);
}