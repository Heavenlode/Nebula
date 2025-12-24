using Nebula.Serialization;
using Nebula.Serialization.Serializers;

namespace Nebula {

    /// <summary>
    /// This provides a common interface for NetNode, NetNode2D, and NetNode3D.
    /// This is necessary because they don't share an inheritance chain.
    /// For example, NetNode2D inherits from Node2D, while NetNode ineherits from Node.
    /// NetNode2D cannot inherit from NetNode because it needs Node2D functionality and C# does not support multiple inheritance.
    /// </summary>
    public interface INetNodeBase {

        /// <summary>
        /// This allows us to access the Node without having to manually cast it every time we want to access it.
        /// </summary>
        public Godot.Node Node { get; }
        public NetworkController Network { get; }
        public IStateSerializer[] Serializers { get; }
        public void SetupSerializers();
    }
    public interface INetNode<T> : INetNodeBase, INetSerializable<T>, IBsonSerializable<T> where T : Godot.Node { }
}