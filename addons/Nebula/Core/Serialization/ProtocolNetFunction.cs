using Godot;

namespace Nebula.Serialization
{
    [Tool]
    public partial class ProtocolNetFunction : Resource
    {
        [Export]
        public string NodePath;
        [Export]
        public string Name;
        [Export]
        public byte Index;
        [Export]
        public NetFunctionArgument[] Arguments;
        [Export]
        public NetFunction.NetworkSources Sources;
    }

}