using Godot;

namespace Nebula.Serialization
{
    [Tool]
    public partial class ProtocolNetProperty : Resource
    {
        [Export]
        public string NodePath;
        [Export]
        public string Name;
        [Export]
        public Variant.Type VariantType;
        [Export]
        public SerialMetadata Metadata;
        [Export]
        public byte Index;
        [Export]
        public long InterestMask;
        [Export]
        public NetLerpMode LerpMode = NetLerpMode.None;
        [Export]
        public float LerpParam = 15f;
        [Export]
        public int ClassIndex = -1;
    }
}