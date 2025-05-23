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
        public int ClassIndex = -1;
    }
}