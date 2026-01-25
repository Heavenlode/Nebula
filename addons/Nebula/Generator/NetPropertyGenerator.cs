#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

// Suppress analyzer release tracking - this is an internal analyzer, not a public NuGet package
#pragma warning disable RS2008

namespace Nebula.Generator;

[Generator]
public class NetPropertyGenerator : IIncrementalGenerator
{
    // Diagnostic for missing NotifyOnChange handler implementation
    private static readonly DiagnosticDescriptor MissingChangeHandlerDiagnostic = new(
        id: "NEBULA001",
        title: "Missing network change handler implementation",
        messageFormat: "Property '{0}' has NotifyOnChange=true but 'OnNetChange{0}' is not implemented",
        category: "Nebula",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Diagnostic for missing prediction tolerance property
    private static readonly DiagnosticDescriptor MissingTolerancePropertyDiagnostic = new(
        id: "NEBULA002",
        title: "Missing prediction tolerance property",
        messageFormat: "Property '{0}' has Predicted=true but '{0}PredictionTolerance' property is not defined",
        category: "Nebula",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

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
        bool predicted = false;

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
                        case "Predicted" when namedArg.Value.Value is bool b3:
                            predicted = b3;
                            break;
                    }
                }
                break;
            }
        }

        // Check if the property type implements IBsonValue<T> or IBsonSerializable<T>
        bool isBsonSerializable = false;
        if (propertySymbol.Type is INamedTypeSymbol namedType)
        {
            isBsonSerializable = namedType.AllInterfaces.Any(i =>
                i.IsGenericType && (i.OriginalDefinition.Name == "IBsonValue" ||
                                    i.OriginalDefinition.Name == "IBsonSerializable"));
        }

        // Simple name for internal lookups (PropertyCache field mapping)
        var simpleTypeName = propertySymbol.Type.ToDisplayString();

        // Fully qualified name for method signatures (Fody matching)
        var fullyQualifiedTypeName = propertySymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Count [NetProperty] properties in all base classes
        int baseClassPropertyCount = CountBaseClassNetProperties(containingType);

        // Check if the user implemented the OnNetChange partial method
        bool hasChangeHandlerImpl = false;
        if (notifyOnChange)
        {
            var expectedMethodName = $"OnNetChange{propertySymbol.Name}";
            hasChangeHandlerImpl = containingType.GetMembers(expectedMethodName)
                .OfType<IMethodSymbol>()
                .Any(m => m.Parameters.Length == 3 && 
                          !m.DeclaringSyntaxReferences.All(r => 
                              r.GetSyntax() is MethodDeclarationSyntax mds && mds.Body == null && mds.ExpressionBody == null));
        }

        // Check if the user defined the {PropertyName}PredictionTolerance property
        bool hasToleranceProperty = false;
        if (predicted)
        {
            var expectedPropertyName = $"{propertySymbol.Name}PredictionTolerance";
            hasToleranceProperty = containingType.GetMembers(expectedPropertyName)
                .OfType<IPropertySymbol>()
                .Any(p => p.Type.SpecialType == SpecialType.System_Single);
        }

        // Get location for diagnostic reporting
        var propertyLocation = propertySymbol.Locations.FirstOrDefault();

        return new PropertyInfo(
            propertySymbol.Name,
            simpleTypeName,
            fullyQualifiedTypeName,  // Add this new field
            propertySymbol.Type.IsValueType,
            propertySymbol.Type.TypeKind == TypeKind.Enum,
            containingType.Name,
            containingType.ContainingNamespace.IsGlobalNamespace
                ? null
                : containingType.ContainingNamespace.ToDisplayString(),
            notifyOnChange,
            interpolate,
            interpolateSpeed,
            isBsonSerializable,
            baseClassPropertyCount,
            hasChangeHandlerImpl,
            propertyLocation,
            predicted,
            hasToleranceProperty);
    }

    /// <summary>
    /// Counts the number of [NetProperty] properties in all base classes of the given type.
    /// </summary>
    private static int CountBaseClassNetProperties(INamedTypeSymbol type)
    {
        int count = 0;
        var baseType = type.BaseType;

        while (baseType != null)
        {
            // Check if this type in chain implements INetNode (stop if it doesn't)
            var implementsNetNode = baseType.AllInterfaces.Any(i =>
                i.IsGenericType && i.OriginalDefinition.Name == "INetNode");

            if (!implementsNetNode)
                break;

            // Count [NetProperty] attributes on properties in this base class
            foreach (var member in baseType.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;

                var hasNetProperty = prop.GetAttributes()
                    .Any(a => a.AttributeClass?.Name == "NetProperty" ||
                              a.AttributeClass?.Name == "NetPropertyAttribute");

                if (hasNetProperty)
                    count++;
            }

            baseType = baseType.BaseType;
        }

        return count;
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
    /// Generates the expression to serialize a property to BSON using BsonTypeHelper.
    /// </summary>
    private static string GetBsonSerializeExpression(PropertyInfo prop)
    {
        var normalizedType = prop.PropertyType.Replace("Godot.", "");
        
        if (prop.IsEnum)
        {
            return $"Nebula.Serialization.BsonTypeHelper.ToBsonEnum({prop.PropertyName})";
        }
        
        if (prop.IsBsonSerializable)
        {
            // Check if it's a value type implementing IBsonValue or reference type implementing IBsonSerializable
            if (prop.IsValueType)
            {
                return $"Nebula.Serialization.BsonTypeHelper.ToBsonValue({prop.PropertyName})";
            }
            else
            {
                return $"Nebula.Serialization.BsonTypeHelper.ToBson({prop.PropertyName}, context)";
            }
        }
        
        // Standard types handled by BsonTypeHelper
        return $"Nebula.Serialization.BsonTypeHelper.ToBson({prop.PropertyName})";
    }

    /// <summary>
    /// Generates the expression to deserialize a property from BSON using BsonTypeHelper.
    /// </summary>
    private static string? GetBsonDeserializeExpression(PropertyInfo prop, string bsonValueExpr)
    {
        var normalizedType = prop.PropertyType.Replace("Godot.", "");
        
        if (prop.IsEnum)
        {
            return $"Nebula.Serialization.BsonTypeHelper.ToEnum<{prop.PropertyType}>({bsonValueExpr})";
        }
        
        if (prop.IsBsonSerializable)
        {
            if (prop.IsValueType)
            {
                return $"Nebula.Serialization.BsonTypeHelper.FromBsonValue<{prop.PropertyType}>({bsonValueExpr})";
            }
            else
            {
                // Reference types need async deserialization - skip for now, handled separately
                return null;
            }
        }
        
        // Map standard types to their BsonTypeHelper methods
        return normalizedType switch
        {
            "string" or "String" or "System.String" => $"Nebula.Serialization.BsonTypeHelper.ToString({bsonValueExpr})",
            "bool" or "Boolean" or "System.Boolean" => $"Nebula.Serialization.BsonTypeHelper.ToBool({bsonValueExpr})",
            "byte" or "Byte" or "System.Byte" => $"Nebula.Serialization.BsonTypeHelper.ToByte({bsonValueExpr})",
            "short" or "Int16" or "System.Int16" => $"Nebula.Serialization.BsonTypeHelper.ToShort({bsonValueExpr})",
            "int" or "Int32" or "System.Int32" => $"Nebula.Serialization.BsonTypeHelper.ToInt({bsonValueExpr})",
            "long" or "Int64" or "System.Int64" => $"Nebula.Serialization.BsonTypeHelper.ToLong({bsonValueExpr})",
            "ulong" or "UInt64" or "System.UInt64" => $"Nebula.Serialization.BsonTypeHelper.ToULong({bsonValueExpr})",
            "float" or "Single" or "System.Single" => $"Nebula.Serialization.BsonTypeHelper.ToFloat({bsonValueExpr})",
            "double" or "Double" or "System.Double" => $"Nebula.Serialization.BsonTypeHelper.ToDouble({bsonValueExpr})",
            "Vector2" => $"Nebula.Serialization.BsonTypeHelper.ToVector2({bsonValueExpr})",
            "Vector2I" => $"Nebula.Serialization.BsonTypeHelper.ToVector2I({bsonValueExpr})",
            "Vector3" => $"Nebula.Serialization.BsonTypeHelper.ToVector3({bsonValueExpr})",
            "Vector3I" => $"Nebula.Serialization.BsonTypeHelper.ToVector3I({bsonValueExpr})",
            "Vector4" => $"Nebula.Serialization.BsonTypeHelper.ToVector4({bsonValueExpr})",
            "Quaternion" => $"Nebula.Serialization.BsonTypeHelper.ToQuaternion({bsonValueExpr})",
            "Color" => $"Nebula.Serialization.BsonTypeHelper.ToColor({bsonValueExpr})",
            "byte[]" or "Byte[]" or "System.Byte[]" => $"Nebula.Serialization.BsonTypeHelper.ToByteArray({bsonValueExpr})",
            "int[]" or "Int32[]" or "System.Int32[]" => $"Nebula.Serialization.BsonTypeHelper.ToInt32Array({bsonValueExpr})",
            "long[]" or "Int64[]" or "System.Int64[]" => $"Nebula.Serialization.BsonTypeHelper.ToInt64Array({bsonValueExpr})",
            _ => null // Unknown type, will be handled specially
        };
    }

    /// <summary>
    /// Generates the comparison expression for prediction tolerance checking.
    /// Returns code that evaluates to true if values are within tolerance.
    /// Uses {PropertyName}PredictionTolerance property for tolerance value.
    /// </summary>
    private static string GetPredictionCompareExpression(PropertyInfo prop, string predictedVar, string confirmedVar, string toleranceVar)
    {
        var normalizedType = prop.PropertyType.Replace("Godot.", "");

        return normalizedType switch
        {
            "Vector3" => $"({predictedVar} - {confirmedVar}).LengthSquared() <= {toleranceVar} * {toleranceVar}",
            "Vector2" => $"({predictedVar} - {confirmedVar}).LengthSquared() <= {toleranceVar} * {toleranceVar}",
            "Quaternion" => $"Godot.Mathf.Abs({predictedVar}.Dot({confirmedVar})) >= 1.0f - {toleranceVar}",
            "float" or "System.Single" => $"Godot.Mathf.Abs({predictedVar} - {confirmedVar}) <= {toleranceVar}",
            "double" or "System.Double" => $"System.Math.Abs({predictedVar} - {confirmedVar}) <= {toleranceVar}",
            "int" or "System.Int32" or "long" or "System.Int64" or "byte" or "System.Byte" => $"{predictedVar} == {confirmedVar}",
            "bool" or "System.Boolean" => $"{predictedVar} == {confirmedVar}",
            _ when prop.IsEnum => $"{predictedVar} == {confirmedVar}",
            _ => $"{predictedVar}.Equals({confirmedVar})" // Fallback for unknown types
        };
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
            "Quaternion" => $@"// Guard against uninitialized (zero) quaternions
        if (target.LengthSquared() < 0.0001f) return current.LengthSquared() < 0.0001f ? Godot.Quaternion.Identity : current;
        if (current.LengthSquared() < 0.0001f) current = Godot.Quaternion.Identity;
        float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta);
        var normalizedTarget = target.Normalized();
        if (current.Dot(normalizedTarget) < 0) normalizedTarget = -normalizedTarget;
        return current.Slerp(normalizedTarget, t);",
            "float" or "System.Single" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return Godot.Mathf.Lerp(current, target, t);",
            "double" or "System.Double" => $"float t = 1f - Godot.Mathf.Exp(-{speedStr} * delta); return Godot.Mathf.Lerp((float)current, (float)target, t);",
            _ => "return target ?? current; // No interpolation for this type - snap to target, but preserve current if target is null"
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

            // Report diagnostics for missing change handler implementations
            foreach (var prop in propList)
            {
                if (prop!.NotifyOnChange && !prop.HasChangeHandlerImpl && prop.PropertyLocation != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MissingChangeHandlerDiagnostic,
                        prop.PropertyLocation,
                        prop.PropertyName,
                        prop.PropertyType));
                }
            }

            // Report diagnostics for missing prediction tolerance properties
            foreach (var prop in propList)
            {
                if (prop!.Predicted && !prop.HasToleranceProperty && prop.PropertyLocation != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MissingTolerancePropertyDiagnostic,
                        prop.PropertyLocation,
                        prop.PropertyName));
                }
            }

            // Get the base class property count offset - all props in this class have the same value
            var baseOffset = propList.FirstOrDefault()?.BaseClassPropertyCount ?? 0;

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("#pragma warning disable CS0109 // Member does not hide an inherited member; new keyword is not required");
            sb.AppendLine();

            if (ns is not null)
                sb.AppendLine($"namespace {ns};");

            sb.AppendLine("using Godot;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine($"partial class {className}");
            sb.AppendLine("{");

            // Generate On{PropertyName}Changed methods (existing functionality)
            foreach (var prop in propList)
            {
                var markDirtyMethod = prop!.IsValueType ? "MarkDirty" : "MarkDirtyRef";

                sb.AppendLine($"    public void On{prop.PropertyName}Changed({prop.FullyQualifiedPropertyType} oldVal, {prop.FullyQualifiedPropertyType} newVal)");
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
                    sb.AppendLine($"    /// Implement this partial method to handle the change.");
                    sb.AppendLine($"    /// </summary>");
                    sb.AppendLine($"    /// <param name=\"tick\">The network tick when the change occurred</param>");
                    sb.AppendLine($"    /// <param name=\"oldVal\">The previous value</param>");
                    sb.AppendLine($"    /// <param name=\"newVal\">The new value</param>");
                    sb.AppendLine($"    partial void OnNetChange{prop.PropertyName}(int tick, {prop.PropertyType} oldVal, {prop.PropertyType} newVal);");
                    sb.AppendLine($"    public event System.Action<int, {prop.PropertyType}, {prop.PropertyType}> NetChangeListener{prop.PropertyName};");
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
                sb.AppendLine($"        {{ \"{prop.PropertyName}\", {baseOffset + i} }},");
            }
            sb.AppendLine("    };");
            sb.AppendLine();

            // Generate method to get property index
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Gets the property index for the given property name, or -1 if not found.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static new int GetNetPropertyIndex(string propertyName)");
            sb.AppendLine("    {");
            sb.AppendLine("        return _propertyNameToIndex.TryGetValue(propertyName, out var index) ? index : -1;");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate the dispatcher method
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Invokes the property change handler for the given property index.");
            sb.AppendLine("    /// Called by the serializer when a network property changes.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    [EditorBrowsable(EditorBrowsableState.Never)]");
            sb.AppendLine("    internal override void InvokePropertyChangeHandler(int propIndex, int tick, ref Nebula.PropertyCache oldVal, ref Nebula.PropertyCache newVal)");
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
                        sb.AppendLine($"            case {baseOffset + i}: {{");
                        sb.AppendLine($"                OnNetChange{prop.PropertyName}(tick, {oldExpr}, {newExpr});");
                        sb.AppendLine($"                NetChangeListener{prop.PropertyName}?.Invoke(tick, {oldExpr}, {newExpr});");
                        sb.AppendLine($"                break;");
                        sb.AppendLine($"            }}");
                    }
                }

                sb.AppendLine("            default: base.InvokePropertyChangeHandler(propIndex, tick, ref oldVal, ref newVal); break;");
                sb.AppendLine("        }");
            }
            else
            {
                // No NotifyOnChange properties in this class, but might be in base class
                sb.AppendLine("        base.InvokePropertyChangeHandler(propIndex, tick, ref oldVal, ref newVal);");
            }

            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate method to check if property has change handler
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Returns true if the property at the given index has a change handler.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static new bool HasPropertyChangeHandler(int propIndex)");
            sb.AppendLine("    {");

            if (notifyProps.Count > 0)
            {
                var notifyIndices = new List<int>();
                for (int i = 0; i < propList.Count; i++)
                {
                    if (propList[i]!.NotifyOnChange)
                    {
                        notifyIndices.Add(baseOffset + i);
                    }
                }

                if (notifyIndices.Count == 1)
                {
                    sb.AppendLine($"        return propIndex == {notifyIndices[0]};");
                }
                else
                {
                    var indicesStr = string.Join(" || ", notifyIndices.Select(idx => $"propIndex == {idx}"));
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
            sb.AppendLine("    [EditorBrowsable(EditorBrowsableState.Never)]");
            sb.AppendLine("    internal override void SetNetPropertyByIndex(int propIndex, ref Nebula.PropertyCache value)");
            sb.AppendLine("    {");
            sb.AppendLine("        switch (propIndex)");
            sb.AppendLine("        {");

            for (int i = 0; i < propList.Count; i++)
            {
                var prop = propList[i]!;
                var valueExpr = GetCacheReadExpression(prop, "value");
                sb.AppendLine($"            case {baseOffset + i}: {prop.PropertyName} = {valueExpr}; break;");
            }

            // Forward unhandled indices to base class for inherited properties
            sb.AppendLine("            default: base.SetNetPropertyByIndex(propIndex, ref value); break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate SetBsonPropertyByName - sets BSON-serializable properties by name
            var bsonProps = propList.Where(p => p!.IsBsonSerializable).ToList();
            if (bsonProps.Count > 0)
            {
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Sets a BSON-serializable property by name from a deserialized object.");
                sb.AppendLine("    /// Used during BSON deserialization to bypass Godot's property system.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    /// <returns>True if the property was found and set, false otherwise.</returns>");
                sb.AppendLine("    public new bool SetBsonPropertyByName(string propName, object value)");
                sb.AppendLine("    {");
                sb.AppendLine("        switch (propName)");
                sb.AppendLine("        {");

                foreach (var prop in bsonProps)
                {
                    sb.AppendLine($"            case \"{prop!.PropertyName}\": {prop.PropertyName} = ({prop.PropertyType})value; return true;");
                }

                sb.AppendLine("            default: return false;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
            else
            {
                // Generate a stub method that always returns false
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Sets a BSON-serializable property by name. This class has no BSON-serializable properties.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    public new bool SetBsonPropertyByName(string propName, object value) => false;");
                sb.AppendLine();
            }

            sb.AppendLine("    #endregion");
            sb.AppendLine();

            // Generate BSON serialization helpers
            sb.AppendLine("    #region BSON Serialization");
            sb.AppendLine();

            // Generate WriteBsonProperties - writes all [NetProperty] values to a BsonDocument
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Writes all [NetProperty] values to a BSON document.");
            sb.AppendLine("    /// Called by BsonSerialize implementations to avoid Godot's property system.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal override void WriteBsonProperties(MongoDB.Bson.BsonDocument doc, Nebula.Serialization.NetBsonContext context = default)");
            sb.AppendLine("    {");
            
            foreach (var prop in propList)
            {
                var serializeExpr = GetBsonSerializeExpression(prop!);
                sb.AppendLine($"        doc[\"{prop!.PropertyName}\"] = {serializeExpr};");
            }
            
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate ReadBsonProperties - reads [NetProperty] values from a BsonDocument
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Reads [NetProperty] values from a BSON document.");
            sb.AppendLine("    /// Called by BSON deserialization to avoid Godot's property system.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal override void ReadBsonProperties(MongoDB.Bson.BsonDocument doc)");
            sb.AppendLine("    {");
            
            foreach (var prop in propList)
            {
                var deserializeExpr = GetBsonDeserializeExpression(prop!, $"doc[\"{prop!.PropertyName}\"]");
                if (deserializeExpr != null)
                {
                    sb.AppendLine($"        if (doc.Contains(\"{prop!.PropertyName}\")) {prop.PropertyName} = {deserializeExpr};");
                }
                else if (prop.IsBsonSerializable && !prop.IsValueType)
                {
                    // Reference types implementing IBsonSerializable need special handling
                    // They use static BsonDeserialize method which may be async
                    sb.AppendLine($"        // {prop.PropertyName}: IBsonSerializable reference type - requires async deserialization, handle in OnBsonDeserialize");
                }
                else
                {
                    sb.AppendLine($"        // {prop.PropertyName}: Unknown type {prop.PropertyType} - requires manual handling");
                }
            }
            
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

                // Generate cached global index fields for each interpolated property
                foreach (var prop in interpolatedProps)
                {
                    sb.AppendLine($"    private int _interpolate_{prop!.PropertyName}_GlobalIndex = -1;");
                }
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
                sb.AppendLine("    /// Processes all interpolated properties. Called each frame.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    internal override void ProcessInterpolation(float delta)");
                sb.AppendLine("    {");
                
                // If any property has both Interpolate and Predicted, skip interpolation for owned entities
                // (prediction handles owned entities, interpolation handles non-owned)
                var hasPredictedInterpolated = interpolatedProps.Any(p => p!.Predicted);
                if (hasPredictedInterpolated)
                {
                    sb.AppendLine("        // Skip interpolation for owned entities - prediction handles them");
                    sb.AppendLine("        if (Network.IsCurrentOwner) return;");
                    sb.AppendLine();
                }
                
                sb.AppendLine("        var parentNetwork = Network.IsNetScene() ? Network : Network.NetParent;");
                sb.AppendLine("        var scenePath = parentNetwork.NetSceneFilePath;");
                sb.AppendLine("        var staticChildId = Network.StaticChildId;");
                sb.AppendLine();

                for (int i = 0; i < propList.Count; i++)
                {
                    var prop = propList[i]!;
                    if (!prop.Interpolate) continue;

                    var targetExpr = GetCacheReadExpression(prop, $"parentNetwork.CachedProperties[_interpolate_{prop.PropertyName}_GlobalIndex]");

                    sb.AppendLine($"        if (_interpolate_{prop.PropertyName}_GlobalIndex < 0)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            if (Nebula.Serialization.Protocol.LookupPropertyByStaticChildId(scenePath, staticChildId, \"{prop.PropertyName}\", out var prop_{prop.PropertyName}))");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                _interpolate_{prop.PropertyName}_GlobalIndex = prop_{prop.PropertyName}.Index;");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var current = {prop.PropertyName};");
                    sb.AppendLine($"            var target = {targetExpr};");
                    sb.AppendLine($"            {prop.PropertyName} = Interpolate{prop.PropertyName}(delta, current, target);");
                    sb.AppendLine("        }");
                }

                sb.AppendLine("    }");
                sb.AppendLine();

                sb.AppendLine("    #endregion");
            }
            else
            {
                // No interpolated properties - still generate override with empty body
                sb.AppendLine("    internal override void ProcessInterpolation(float delta) { }");
            }

            // Generate prediction methods for properties with Predicted = true
            var predictedProps = propList.Where(p => p!.Predicted).ToList();
            if (predictedProps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    #region Client-Side Prediction");
                sb.AppendLine();
                sb.AppendLine("    // ============================================================");
                sb.AppendLine("    // HOT PATH OPTIMIZATION: Circular buffers, no per-tick allocation");
                sb.AppendLine("    // ============================================================");
                sb.AppendLine();
                sb.AppendLine("    private const int PREDICTION_BUFFER_SIZE = 64; // Power of 2 for fast modulo");
                sb.AppendLine();

                // Generate per-property storage (simplified - no render/simulation/mispredicted fields)
                foreach (var prop in predictedProps)
                {
                    sb.AppendLine($"    // Prediction state for {prop!.PropertyName}");
                    sb.AppendLine($"    private {prop.PropertyType}[] _predicted_{prop.PropertyName} = new {prop.PropertyType}[PREDICTION_BUFFER_SIZE];");
                    sb.AppendLine($"    private {prop.PropertyType} _confirmed_{prop.PropertyName};");
                    sb.AppendLine($"    private int _confirmed_{prop.PropertyName}_GlobalIndex = -1;");
                    sb.AppendLine();
                }

                // Generate StorePredictedState
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Stores current predicted state for all predicted properties.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    internal override void StorePredictedState(int tick)");
                sb.AppendLine("    {");
                sb.AppendLine("        int slot = tick & (PREDICTION_BUFFER_SIZE - 1);");
                foreach (var prop in predictedProps)
                {
                    sb.AppendLine($"        _predicted_{prop!.PropertyName}[slot] = {prop.PropertyName};");
                }
                sb.AppendLine("    }");
                sb.AppendLine();

                // Generate StoreConfirmedState
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Stores confirmed server state for all predicted properties.");
                sb.AppendLine("    /// Reads from CachedProperties (where server values are stored during import)");
                sb.AppendLine("    /// rather than from the properties themselves (which have client-predicted values).");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    internal override void StoreConfirmedState()");
                sb.AppendLine("    {");
                sb.AppendLine("        var parentNetwork = Network.IsNetScene() ? Network : Network.NetParent;");
                sb.AppendLine("        var scenePath = parentNetwork.NetSceneFilePath;");
                sb.AppendLine("        var staticChildId = Network.StaticChildId;");
                sb.AppendLine();
                foreach (var prop in predictedProps)
                {
                    var cacheReadExpr = GetCacheReadExpression(prop!, $"parentNetwork.CachedProperties[_confirmed_{prop!.PropertyName}_GlobalIndex]");
                    sb.AppendLine($"        if (_confirmed_{prop.PropertyName}_GlobalIndex < 0)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            if (Nebula.Serialization.Protocol.LookupPropertyByStaticChildId(scenePath, staticChildId, \"{prop.PropertyName}\", out var prop_{prop.PropertyName}))");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                _confirmed_{prop.PropertyName}_GlobalIndex = prop_{prop.PropertyName}.Index;");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                    sb.AppendLine($"        if (_confirmed_{prop.PropertyName}_GlobalIndex >= 0)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            _confirmed_{prop.PropertyName} = {cacheReadExpr};");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
                sb.AppendLine("    }");
                sb.AppendLine();

                // Generate Reconcile - combines compare + selective restore
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Compares predicted state against confirmed server state and restores mispredicted properties.");
                sb.AppendLine("    /// Returns true if any property was mispredicted (rollback needed), false if all predictions correct.");
                sb.AppendLine("    /// If forceRestoreAll is true, skips comparison and restores all properties to confirmed state.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    internal override bool Reconcile(int tick, bool forceRestoreAll = false)");
                sb.AppendLine("    {");
                sb.AppendLine("        if (forceRestoreAll)");
                sb.AppendLine("        {");
                foreach (var prop in predictedProps)
                {
                    sb.AppendLine($"            {prop!.PropertyName} = _confirmed_{prop.PropertyName};");
                }
                sb.AppendLine("            OnConfirmedStateRestored();");
                sb.AppendLine("            return true;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        int slot = tick & (PREDICTION_BUFFER_SIZE - 1);");
                sb.AppendLine("        bool anyMispredicted = false;");
                sb.AppendLine();
                foreach (var prop in predictedProps)
                {
                    var compareExpr = GetPredictionCompareExpression(prop!, "predicted", "confirmed", "tolerance");
                    sb.AppendLine($"        // Check {prop!.PropertyName}");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var tolerance = {prop.PropertyName}PredictionTolerance;");
                    sb.AppendLine($"            var predicted = _predicted_{prop.PropertyName}[slot];");
                    sb.AppendLine($"            var confirmed = _confirmed_{prop.PropertyName};");
                    sb.AppendLine($"            if (!({compareExpr}))");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                {prop.PropertyName} = confirmed;");
                    sb.AppendLine("                anyMispredicted = true;");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                }
                sb.AppendLine();
                sb.AppendLine("        if (anyMispredicted) OnConfirmedStateRestored();");
                sb.AppendLine("        return anyMispredicted;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Called after mispredicted properties are restored to confirmed state.");
                sb.AppendLine("    /// Implement this partial method to perform additional actions after rollback.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    partial void OnConfirmedStateRestored();");
                sb.AppendLine();

                // Generate RestoreToPredictedState
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Restores all predicted properties from the prediction buffer for a given tick.");
                sb.AppendLine("    /// Used when prediction was correct and we need to continue with predicted values after import.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    internal override void RestoreToPredictedState(int tick)");
                sb.AppendLine("    {");
                sb.AppendLine("        int slot = tick & (PREDICTION_BUFFER_SIZE - 1);");
                foreach (var prop in predictedProps)
                {
                    sb.AppendLine($"        {prop!.PropertyName} = _predicted_{prop.PropertyName}[slot];");
                }
                sb.AppendLine("        OnPredictedStateRestored();");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Called after predicted properties are restored from prediction buffer.");
                sb.AppendLine("    /// Implement this partial method to perform additional actions after restoration.");
                sb.AppendLine("    /// </summary>");
                sb.AppendLine("    partial void OnPredictedStateRestored();");
                sb.AppendLine();

                sb.AppendLine("    #endregion");
            }
            else
            {
                // No predicted properties - generate stubs
                sb.AppendLine();
                sb.AppendLine("    internal override void StorePredictedState(int tick) { }");
                sb.AppendLine("    internal override void StoreConfirmedState() { }");
                sb.AppendLine("    internal override bool Reconcile(int tick, bool forceRestoreAll = false) => false;");
                sb.AppendLine("    internal override void RestoreToPredictedState(int tick) { }");
            }

            sb.AppendLine("}");

            context.AddSource($"{className}.NetProperties.g.cs", sb.ToString());
        }
    }

    private record PropertyInfo(
        string PropertyName,
        string PropertyType,
        string FullyQualifiedPropertyType,
        bool IsValueType,
        bool IsEnum,
        string ClassName,
        string? Namespace,
        bool NotifyOnChange,
        bool Interpolate,
        float InterpolateSpeed,
        bool IsBsonSerializable,
        int BaseClassPropertyCount,
        bool HasChangeHandlerImpl,
        Location? PropertyLocation,
        // Prediction fields
        bool Predicted,
        bool HasToleranceProperty);
}
