#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Nebula.Generator;

/// <summary>
/// Shared utilities for Nebula source generators.
/// </summary>
internal static class GeneratorUtils
{
    /// <summary>
    /// Maps C# types to their PropertyCache field names.
    /// </summary>
    public static readonly Dictionary<string, string> TypeToPropertyCacheField = new()
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

    /// <summary>
    /// Gets the PropertyCache field name for a given type.
    /// </summary>
    public static string GetPropertyCacheFieldName(string propertyType)
    {
        if (TypeToPropertyCacheField.TryGetValue(propertyType, out var fieldName))
        {
            return fieldName;
        }

        var simpleName = propertyType.Split('.').Last();
        if (simpleName.EndsWith("?"))
        {
            simpleName = simpleName.TrimEnd('?');
        }

        return $"{simpleName}Value";
    }

    /// <summary>
    /// Generates the expression to read a value from a PropertyCache variable.
    /// Handles enums by casting from IntValue, and reference types by casting from RefValue.
    /// </summary>
    /// <param name="typeName">The C# type name (e.g., "Vector3", "int", "MyEnum")</param>
    /// <param name="isEnum">True if the type is an enum</param>
    /// <param name="isValueType">True if the type is a value type</param>
    /// <param name="cacheVar">The name of the PropertyCache variable</param>
    /// <returns>A C# expression to read the value from the cache</returns>
    public static string GetCacheReadExpression(string typeName, bool isEnum, bool isValueType, string cacheVar)
    {
        var cacheField = GetPropertyCacheFieldName(typeName);

        if (isEnum)
        {
            return $"({typeName}){cacheVar}.IntValue";
        }
        else if (typeName is "ulong" or "System.UInt64")
        {
            return $"(ulong){cacheVar}.LongValue";
        }
        else if (!isValueType && cacheField != "StringValue")
        {
            return $"({typeName}){cacheVar}.RefValue!";
        }
        else
        {
            return $"{cacheVar}.{cacheField}";
        }
    }
}
