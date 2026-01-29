// ============================================================================
// SHARED TYPES - Link this file to both your game project and generator output
// These are pure C# types with no Godot dependencies
// ============================================================================

namespace Nebula.Serialization
{
    /// <summary>
    /// C# mirror of Godot's Variant.Type for network serialization.
    /// </summary>
    public enum SerialVariantType
    {
        Nil = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
        String = 4,
        Vector2 = 5,
        Vector2I = 6,
        Rect2 = 7,
        Rect2I = 8,
        Vector3 = 9,
        Vector3I = 10,
        Transform2D = 11,
        Vector4 = 12,
        Vector4I = 13,
        Plane = 14,
        Quaternion = 15,
        Aabb = 16,
        Basis = 17,
        Transform3D = 18,
        Projection = 19,
        Color = 20,
        StringName = 21,
        NodePath = 22,
        Rid = 23,
        Object = 24,
        Dictionary = 27,
        Array = 28,
        PackedByteArray = 29,
        PackedInt32Array = 30,
        PackedInt64Array = 31,
        PackedFloat32Array = 32,
        PackedFloat64Array = 33,
        PackedStringArray = 34,
        PackedVector2Array = 35,
        PackedVector3Array = 36,
        PackedColorArray = 37,
        PackedVector4Array = 38,
    }

    [System.Flags]
    public enum NetworkSources
    {
        None = 0,
        Server = 1 << 0,
        Client = 1 << 1,
        All = Server | Client,
    }

    [System.Flags]
    public enum StaticMethodType
    {
        None = 0,
        NetworkSerialize = 1 << 0,
        NetworkDeserialize = 1 << 1,
        BsonDeserialize = 1 << 2,
    }

    /// <summary>
    /// Metadata for serialization type identification.
    /// </summary>
    public readonly struct SerialMetadata
    {
        public readonly string TypeIdentifier;

        public SerialMetadata(string typeIdentifier)
        {
            TypeIdentifier = typeIdentifier ?? "None";
        }

        public static SerialMetadata None => new("None");
    }

    /// <summary>
    /// Argument descriptor for network functions.
    /// </summary>
    public readonly struct NetFunctionArgument
    {
        public readonly SerialVariantType VariantType;
        public readonly SerialMetadata Metadata;

        public NetFunctionArgument(SerialVariantType variantType, SerialMetadata metadata)
        {
            VariantType = variantType;
            Metadata = metadata;
        }
    }

    /// <summary>
    /// Compiled network property data.
    /// </summary>
    public readonly struct ProtocolNetProperty
    {
        public readonly string NodePath;
        public readonly string Name;
        public readonly SerialVariantType VariantType;
        public readonly SerialMetadata Metadata;
        /// <summary>
        /// Scene-global index used for dirty mask and network serialization.
        /// </summary>
        public readonly byte Index;
        /// <summary>
        /// Class-local index used for SetNetPropertyByIndex on the owning node.
        /// This matches the switch case index generated in the owning class.
        /// </summary>
        public readonly byte LocalIndex;
        public readonly long InterestMask;
        public readonly long InterestRequired;
        public readonly int ClassIndex;
        public readonly bool NotifyOnChange;
        public readonly bool Interpolate;
        public readonly float InterpolateSpeed;
        /// <summary>
        /// When true, this property participates in client-side prediction.
        /// Server state is not directly applied; reconciliation handles it.
        /// </summary>
        public readonly bool Predicted;
        /// <summary>
        /// Maximum bytes per tick for chunked initial sync of NetArray properties.
        /// </summary>
        public readonly int ChunkBudget;
        /// <summary>
        /// When true, this property type implements INetSerializable (reference type).
        /// Object properties are always called during serialization and self-filter.
        /// Primitive properties (INetValue) are only serialized when dirty.
        /// </summary>
        public readonly bool IsObjectProperty;

        public ProtocolNetProperty(
            string nodePath,
            string name,
            SerialVariantType variantType,
            SerialMetadata metadata,
            byte index,
            byte localIndex,
            long interestMask,
            long interestRequired,
            int classIndex,
            bool notifyOnChange = false,
            bool interpolate = false,
            float interpolateSpeed = 15f,
            bool predicted = false,
            int chunkBudget = 256,
            bool isObjectProperty = false)
        {
            NodePath = nodePath;
            Name = name;
            VariantType = variantType;
            Metadata = metadata;
            Index = index;
            LocalIndex = localIndex;
            InterestMask = interestMask;
            InterestRequired = interestRequired;
            ClassIndex = classIndex;
            NotifyOnChange = notifyOnChange;
            Interpolate = interpolate;
            InterpolateSpeed = interpolateSpeed;
            Predicted = predicted;
            ChunkBudget = chunkBudget;
            IsObjectProperty = isObjectProperty;
        }
    }

    /// <summary>
    /// Compiled network function data.
    /// </summary>
    public readonly struct ProtocolNetFunction
    {
        public readonly string NodePath;
        public readonly string Name;
        public readonly byte Index;
        public readonly NetFunctionArgument[] Arguments;
        public readonly NetworkSources Sources;

        public ProtocolNetFunction(
            string nodePath,
            string name,
            byte index,
            NetFunctionArgument[] arguments,
            NetworkSources sources)
        {
            NodePath = nodePath;
            Name = name;
            Index = index;
            Arguments = arguments;
            Sources = sources;
        }
    }

    /// <summary>
    /// Static method reference for serializable types.
    /// </summary>
    public readonly struct StaticMethodInfo
    {
        public readonly StaticMethodType MethodType;
        public readonly string TypeFullName;

        public StaticMethodInfo(StaticMethodType methodType, string typeFullName)
        {
            MethodType = methodType;
            TypeFullName = typeFullName;
        }
    }

    /// <summary>
    /// Scene-level interest requirements for spawn visibility.
    /// </summary>
    public readonly struct ProtocolSceneInterest
    {
        /// <summary>
        /// Peer must have ANY of these interest layers (OR logic). 0 = no check.
        /// </summary>
        public readonly long InterestAny;

        /// <summary>
        /// Peer must have ALL of these interest layers (AND logic). 0 = no check.
        /// </summary>
        public readonly long InterestRequired;

        public ProtocolSceneInterest(long interestAny, long interestRequired)
        {
            InterestAny = interestAny;
            InterestRequired = interestRequired;
        }
    }
}