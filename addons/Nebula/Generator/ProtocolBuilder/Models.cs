#nullable enable
using System.Collections.Generic;

namespace Nebula.Generators
{
    /// <summary>
    /// Intermediate representation of a scene's network data.
    /// </summary>
    internal sealed class SceneBytecode
    {
        public bool IsNetScene { get; set; }
        public long InterestAny { get; set; }
        public long InterestRequired { get; set; }
        public List<StaticNetNode> StaticNetNodes { get; } = new();
        public Dictionary<string, Dictionary<string, PropertyData>> Properties { get; } = new();
        public Dictionary<string, Dictionary<string, FunctionData>> Functions { get; } = new();
    }

    internal sealed class StaticNetNode
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
    }

    internal sealed class PropertyData
    {
        public string NodePath { get; set; } = "";
        public string Name { get; set; } = "";
        public string TypeFullName { get; set; } = "";
        public string? SubtypeIdentifier { get; set; }
        /// <summary>
        /// Scene-global index used for dirty mask and network serialization.
        /// </summary>
        public byte Index { get; set; }
        /// <summary>
        /// Class-local index used for SetNetPropertyByIndex on the owning node.
        /// </summary>
        public byte LocalIndex { get; set; }
        public long InterestMask { get; set; }
        public long InterestRequired { get; set; }
        public int ClassIndex { get; set; } = -1;
        public bool NotifyOnChange { get; set; } = false;
        public bool Interpolate { get; set; } = false;
        public float InterpolateSpeed { get; set; } = 15f;
        public bool IsEnum { get; set; } = false;
        /// <summary>
        /// When true, this property participates in client-side prediction.
        /// </summary>
        public bool Predicted { get; set; } = false;
    }

    internal sealed class FunctionData
    {
        public string NodePath { get; set; } = "";
        public string Name { get; set; } = "";
        public byte Index { get; set; }
        public List<ArgumentData> Arguments { get; } = new();
        public int Sources { get; set; } = 3;
    }

    internal sealed class ArgumentData
    {
        public string TypeFullName { get; set; } = "";
        public string? SubtypeIdentifier { get; set; }
    }

    /// <summary>
    /// Aggregated protocol data for all scenes.
    /// </summary>
    internal sealed class ProtocolData
    {
        public Dictionary<int, SerializableMethodData> StaticMethods { get; } = new();
        public Dictionary<byte, string> ScenesMap { get; } = new();
        public Dictionary<string, byte> ScenesPack { get; } = new();
        public Dictionary<string, Dictionary<byte, string>> StaticNetworkNodePathsMap { get; } = new();
        public Dictionary<string, Dictionary<string, byte>> StaticNetworkNodePathsPack { get; } = new();
        public Dictionary<string, Dictionary<string, Dictionary<string, PropertyData>>> PropertiesMap { get; } = new();
        public Dictionary<string, Dictionary<string, Dictionary<string, FunctionData>>> FunctionsMap { get; } = new();
        public Dictionary<string, Dictionary<int, PropertyData>> PropertiesLookup { get; } = new();
        public Dictionary<string, Dictionary<int, FunctionData>> FunctionsLookup { get; } = new();
        public Dictionary<string, int> SerialTypePack { get; } = new();
        /// <summary>
        /// Direct lookup: scenePath -> staticChildId -> propertyName -> property
        /// Avoids intermediate nodePath string lookup at runtime.
        /// </summary>
        public Dictionary<string, Dictionary<byte, Dictionary<string, PropertyData>>> PropertiesByStaticChildId { get; } = new();
        /// <summary>
        /// Scene-level interest requirements: scenePath -> (InterestAny, InterestRequired)
        /// </summary>
        public Dictionary<string, SceneInterestData> SceneInterestMap { get; } = new();
    }

    internal sealed class SceneInterestData
    {
        public long InterestAny { get; set; }
        public long InterestRequired { get; set; }
    }

    internal sealed class SerializableMethodData
    {
        public int MethodType { get; set; }
        public string TypeFullName { get; set; } = "";
        /// <summary>
        /// True if this type implements INetValue (value type), false if INetSerializable (reference type).
        /// Used by CodeEmitter to generate correct PropertyCache field access.
        /// </summary>
        public bool IsValueType { get; set; }
    }
}
