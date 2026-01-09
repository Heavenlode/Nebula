using System;
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
        public NetworkController Network { get; }
        public IStateSerializer[] Serializers { get; }
        public void SetupSerializers();

        /// <summary>
        /// When the node is first spawned, and when players first connect to the world, this method is called to determine the initial interest layers for the node.
        /// </summary>
        /// <returns>The initial interest layers for the node.</returns>
        public long InitializeInterest(NetPeer peer);
    }

    /// <summary>
    /// Interface for nodes that support typed network input.
    /// Provides zero-allocation input handling using unmanaged structs.
    /// </summary>
    public interface INetInputNode
    {
        /// <summary>
        /// Returns true if the input has changed since the last network tick.
        /// </summary>
        bool HasInputChanged { get; }

        /// <summary>
        /// The size in bytes of the input struct.
        /// </summary>
        int InputSize { get; }

        /// <summary>
        /// Gets the current input as a byte span for serialization.
        /// </summary>
        ReadOnlySpan<byte> GetInputBytes();

        /// <summary>
        /// Sets the current input from a byte span received from the network.
        /// </summary>
        void SetInputBytes(ReadOnlySpan<byte> bytes);

        /// <summary>
        /// Clears the input changed flag after the input has been sent.
        /// </summary>
        void ClearInputChanged();
    }

    public interface INetNode<T> : INetNodeBase, INetSerializable<T>, IBsonSerializable<T> where T : Godot.Node { }
}