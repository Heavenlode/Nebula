using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Nebula.Generator;

[Generator]
public class NetPropertyGenerator : IIncrementalGenerator
{
    // Maps C# types to their PropertyCache field names
    private static readonly Dictionary<string, string> TypeToPropertyCacheField = new()
    {
        { "bool", "BoolValue" },
        { "System.Boolean", "BoolValue" },
        { "byte", "ByteValue" },
        { "System.Byte", "ByteValue" },
        { "int", "IntValue" },
        { "System.Int32", "IntValue" },
        { "long", "LongValue" },
        { "System.Int64", "LongValue" },
        { "ulong", "LongValue" },
        { "System.UInt64", "LongValue" },
        { "float", "FloatValue" },
        { "System.Single", "FloatValue" },
        { "double", "DoubleValue" },
        { "System.Double", "DoubleValue" },
        { "Godot.Vector2", "Vec2Value" },
        { "Vector2", "Vec2Value" },
        { "Godot.Vector3", "Vec3Value" },
        { "Vector3", "Vec3Value" },
        { "Godot.Quaternion", "QuatValue" },
        { "Quaternion", "QuatValue" },
        { "string", "StringValue" },
        { "System.String", "StringValue" },
    };

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

        // Extract attribute values
        bool notifyOnChange = false;
        bool interpolate = false;
        float interpolateSpeed = 15f;
        
        foreach (var attr in propertySymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "NetProperty" || 
                attr.AttributeClass?.Name == "NetPropertyAttribute")
            {
                foreach (var namedArg in attr.NamedArguments)
                {
                    switch (namedArg.Key)
                    {
                        case "NotifyOnChange" when namedArg.Value.Value is bool b1:
                            notifyOnChange = b1;
                            break;
                        case "Interpolate" when namedArg.Value.Value is bool b2:
                            interpolate = b2;
                            break;
                        case "InterpolateSpeed" when namedArg.Value.Value is float f:
                            interpolateSpeed = f;
                            break;
                    }
                }
                break;
            }
        }

        return new PropertyInfo(
            propertySymbol.Name,
            propertySymbol.Type.ToDisplayString(),
            propertySymbol.Type.IsValueType,
            propertySymbol.Type.TypeKind == TypeKind.Enum,
            containingType.Name,
            containingType.ContainingNamespace.IsGlobalNamespace
                ? null
                : containingType.ContainingNamespace.ToDisplayString(),
            notifyOnChange,
            interpolate,
            interpolateSpeed);
    }

    private static string GetPropertyCacheFieldName(string propertyType)
    {
        // Check direct mapping
        if (TypeToPropertyCacheField.TryGetValue(propertyType, out var fieldName))
        {
            return fieldName;
        }

        // For custom value types (INetValue<T>), use {TypeName}Value
        // Extract the simple type name from fully qualified name
        var simpleName = propertyType.Split('.').Last();
        if (simpleName.EndsWith("?"))
        {
            simpleName = simpleName.TrimEnd('?');
        }
        
        // Reference types use RefValue
        return $"{simpleName}Value";
    }

    /// <summary>
    /// Generates the expression to read a property value from a PropertyCache variable.
    /// Handles enums by casting from IntValue, and reference types by casting from RefValue.
    /// </summary>
    private static string GetCacheReadExpression(PropertyInfo prop, string cacheVar)
    {
        var cacheField = GetPropertyCacheFieldName(prop.PropertyType);
        
        if (prop.IsEnum)
        {
            // Enums are stored as IntValue, need to cast
            return $"({prop.PropertyType}){cacheVar}.IntValue";
        }
        else if (prop.PropertyType is "ulong" or "System.UInt64")
        {
            // ulong is stored in LongValue but needs explicit cast
            return $"(ulong){cacheVar}.LongValue";
        }
        else if (!prop.IsValueType && cacheField != "StringValue")
        {
            // Reference types (except string) use RefValue with cast
            return $"({prop.PropertyType}){cacheVar}.RefValue!";
        }
        else
        {
            // Direct field access for known types
            return $"{cacheVar}.{cacheField}";
        }
    }

    /// <summary>
    /// Generates the expression to write a property value to a PropertyCache.
    /// Handles enums by casting to int.
    /// </summary>
    private static string GetCacheWriteField(PropertyInfo prop)
    {
        if (prop.IsEnum)
        {
            return "IntValue";
        }
        
        var cacheField = GetPropertyCacheFieldName(prop.PropertyType);
        if (!prop.IsValueType && cacheField != "StringValue")
        {
            return "RefValue";
        }
        return cacheField;
    }

    /// <summary>
    /// Generates the default interpolation implementation based on property type.
    /// </summary>
    private static string GetDefaultInterpolationImpl(string propertyType, float speed)
    {
        var speedStr = speed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f";
        
        // Normalize type name for comparison
        var normalizedType = propertyType.Replace("Godot.", "");
        
        return normalizedType switch
        {
            "Vector3" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return current.Lerp(target, t);",
            "Vector2" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return current.Lerp(target, t);",
            "Quaternion" => $@"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta);
        var normalizedTarget = target.Normalized();
        if (current.Dot(normalizedTarget) < 0) normalizedTarget = -normalizedTarget;
        return current.Slerp(normalizedTarget, t);",
            "float" or "System.Single" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return Godot.Mathf.Lerp(current, target, t);",
            "double" or "System.Double" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return Godot.Mathf.Lerp((float)current, (float)target, t);",
            _ => "return target; // No interpolation for this type - snap to target"
        };
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
            var propList = group.ToList();

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine();

            if (ns is not null)
                sb.AppendLine($"namespace {ns};");

            sb.AppendLine();
            sb.AppendLine($"partial class {className}");
            sb.AppendLine("{");

            // Generate On{PropertyName}Changed methods (existing functionality)
            foreach (var prop in propList)
            {
                var markDirtyMethod = prop!.IsValueType ? "MarkDirty" : "MarkDirtyRef";

                sb.AppendLine($"    public void On{prop.PropertyName}Changed({prop.PropertyType} newVal)");
                sb.AppendLine("    {");
                sb.AppendLine($"        Network.{markDirtyMethod}(this, \"{prop.PropertyName}\", newVal);");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // Generate virtual OnNetworkChange{PropertyName} methods for properties with NotifyOnChange = true
            var notifyProps = propList.Where(p => p!.NotifyOnChange).ToList();
            if (notifyProps.Count > 0)
            {
                sb.AppendLine("    #region Network Change Handlers");
                sb.AppendLine();

                foreach (var prop in notifyProps)
                {
                    sb.AppendLine($"    /// <summary>");
                    sb.AppendLine($"    /// Called when the {prop!.PropertyName} property changes over the network.");
                    sb.AppendLine($"    /// Override this method to handle the change.");
                    sb.AppendLine($"    /// </summary>");
                    sb.AppendLine($"    /// <param name=\"tick\">The network tick when the change occurred</param>");
                    sb.AppendLine($"    /// <param name=\"oldVal\">The previous value</param>");
                    sb.AppendLine($"    /// <param name=\"newVal\">The new value</param>");
                    sb.AppendLine($"    protected virtual void OnNetworkChange{prop.PropertyName}(int tick, {prop.PropertyType} oldVal, {prop.PropertyType} newVal) {{ }}");
                    sb.AppendLine();
                }

                sb.AppendLine("    #endregion");
                sb.AppendLine();
            }

            // Generate the property change dispatcher
            // This creates a mapping from property name to index for efficient lookup
            sb.AppendLine("    #region Property Change Dispatcher");
            sb.AppendLine();
            
            // Generate static property name to index mapping
            sb.AppendLine("    private static readonly System.Collections.Generic.Dictionary<string, int> _propertyNameToIndex = new()");
            sb.AppendLine("    {");
            for (int i = 0; i < propList.Count; i++)
            {
                var prop = propList[i]!;
                sb.AppendLine($"        {{ \"{prop.PropertyName}\", {i} }},");
            }
            sb.AppendLine("    };");
            sb.AppendLine();

            // Generate method to get property index
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Gets the property index for the given property name, or -1 if not found.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static int GetNetPropertyIndex(string propertyName)");
            sb.AppendLine("    {");
            sb.AppendLine("        return _propertyNameToIndex.TryGetValue(propertyName, out var index) ? index : -1;");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate the dispatcher method
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Invokes the property change handler for the given property index.");
            sb.AppendLine("    /// Called by the serializer when a network property changes.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public void InvokePropertyChangeHandler(int propIndex, int tick, ref Nebula.PropertyCache oldVal, ref Nebula.PropertyCache newVal)");
            sb.AppendLine("    {");
            
            if (notifyProps.Count > 0)
            {
                sb.AppendLine("        switch (propIndex)");
                sb.AppendLine("        {");
                
                for (int i = 0; i < propList.Count; i++)
                {
                    var prop = propList[i]!;
                    if (prop.NotifyOnChange)
                    {
                        var oldExpr = GetCacheReadExpression(prop, "oldVal");
                        var newExpr = GetCacheReadExpression(prop, "newVal");
                        sb.AppendLine($"            case {i}: OnNetworkChange{prop.PropertyName}(tick, {oldExpr}, {newExpr}); break;");
                    }
                }
                
                sb.AppendLine("        }");
            }
            
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate method to check if property has change handler
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Returns true if the property at the given index has a change handler.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static bool HasPropertyChangeHandler(int propIndex)");
            sb.AppendLine("    {");
            
            if (notifyProps.Count > 0)
            {
                var notifyIndices = new List<int>();
                for (int i = 0; i < propList.Count; i++)
                {
                    if (propList[i]!.NotifyOnChange)
                    {
                        notifyIndices.Add(i);
                    }
                }
                
                if (notifyIndices.Count == 1)
                {
                    sb.AppendLine($"        return propIndex == {notifyIndices[0]};");
                }
                else
                {
                    var indicesStr = string.Join(" or ", notifyIndices.Select(i => $"propIndex == {i}"));
                    sb.AppendLine($"        return {indicesStr};");
                }
            }
            else
            {
                sb.AppendLine("        return false;");
            }
            
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate SetNetPropertyByIndex - sets property value without crossing Godot boundary
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Sets a network property by its index from a PropertyCache.");
            sb.AppendLine("    /// Avoids Godot boundary crossing by setting the C# property directly.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public void SetNetPropertyByIndex(int propIndex, ref Nebula.PropertyCache value)");
            sb.AppendLine("    {");
            sb.AppendLine("        switch (propIndex)");
            sb.AppendLine("        {");
            
            for (int i = 0; i < propList.Count; i++)
            {
                var prop = propList[i]!;
                var valueExpr = GetCacheReadExpression(prop, "value");
                sb.AppendLine($"            case {i}: {prop.PropertyName} = {valueExpr}; break;");
            }
            
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    #endregion");
            sb.AppendLine();

            // Generate interpolation methods for properties with Interpolate = true
            var interpolatedProps = propList.Where(p => p!.Interpolate).ToList();
            if (interpolatedProps.Count > 0)
            {
                sb.AppendLine("    #region Interpolation");
                sb.AppendLine();

                // Generate Interpolate{PropertyName} virtual methods with default implementations
                foreach (var prop in interpolatedProps)
                {
                    var defaultImpl = GetDefaultInterpolationImpl(prop!.PropertyType, prop.InterpolateSpeed);
                    
                    sb.AppendLine($"    /// <summary>");
                    sb.AppendLine($"    /// Interpolates {prop.PropertyName} toward the network target value.");
                    sb.AppendLine($"    /// Override to customize interpolation behavior.");
                    sb.AppendLine($"    /// </summary>");
                    sb.AppendLine($"    /// <param name=\"delta\">Frame delta time in seconds</param>");
                    sb.AppendLine($"    /// <param name=\"current\">Current property value</param>");
                    sb.AppendLine($"    /// <param name=\"target\">Target value from network</param>");
                    sb.AppendLine($"    /// <returns>The interpolated value to set</returns>");
                    sb.AppendLine($"    protected virtual {prop.PropertyType} Interpolate{prop.PropertyName}(float delta, {prop.PropertyType} current, {prop.PropertyType} target)");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        {defaultImpl}");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                }

                // Generate ProcessInterpolation method
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Processes all interpolated properties. Called each frame by the serializer.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    public void ProcessInterpolation(float delta)");
                sb.AppendLine("    {");
                
                for (int i = 0; i < propList.Count; i++)
                {
                    var prop = propList[i]!;
                    if (!prop.Interpolate) continue;
                    
                    var targetExpr = GetCacheReadExpression(prop, $"Network.CachedProperties[{i}]");
                    
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var current = {prop.PropertyName};");
                    sb.AppendLine($"            var target = {targetExpr};");
                    sb.AppendLine($"            {prop.PropertyName} = Interpolate{prop.PropertyName}(delta, current, target);");
                    sb.AppendLine("        }");
                }
                
                sb.AppendLine("    }");
                sb.AppendLine();

                // Generate HasInterpolatedProperties property
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Returns true if this class has any interpolated properties.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    public bool HasInterpolatedProperties => true;");
                sb.AppendLine();

                sb.AppendLine("    #endregion");
            }
            else
            {
                // No interpolated properties - still generate the interface members with defaults
                sb.AppendLine("    public void ProcessInterpolation(float delta) { }");
                sb.AppendLine("    public bool HasInterpolatedProperties => false;");
            }

            sb.AppendLine("}");

            context.AddSource($"{className}.NetProperties.g.cs", sb.ToString());
        }
    }

    private record PropertyInfo(
        string PropertyName,
        string PropertyType,
        bool IsValueType,
        bool IsEnum,
        string ClassName,
        string? Namespace,
        bool NotifyOnChange,
        bool Interpolate,
        float InterpolateSpeed);
}
