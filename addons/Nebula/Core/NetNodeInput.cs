using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nebula
{
    /// <summary>
    /// Generic variant of NetNode that supports typed network input.
    /// Use this when your node needs to send input to the server without boxing.
    /// Define your input as a struct with the unmanaged constraint.
    /// </summary>
    /// <typeparam name="TInput">The unmanaged struct type for network input.</typeparam>
    public partial class NetNode<TInput> : NetNode, INetInputNode
        where TInput : unmanaged
    {
        private TInput _currentInput;
        private TInput _previousInput;
        private bool _inputChanged;

        /// <inheritdoc/>
        public bool HasInputChanged => _inputChanged;

        /// <inheritdoc/>
        public int InputSize => Marshal.SizeOf<TInput>();

        /// <summary>
        /// Sets the current input for this network tick.
        /// Only call this on the client that owns this node.
        /// </summary>
        /// <param name="input">The input struct to send to the server.</param>
        public void SetInput(in TInput input)
        {
            _previousInput = _currentInput;
            _currentInput = input;
            _inputChanged = !EqualityComparer<TInput>.Default.Equals(_currentInput, _previousInput);
        }

        /// <summary>
        /// Gets the current input. Use this on the server to read client input.
        /// </summary>
        /// <returns>A readonly reference to the current input.</returns>
        public ref readonly TInput GetInput() => ref _currentInput;

        /// <inheritdoc/>
        public ReadOnlySpan<byte> GetInputBytes()
        {
            return MemoryMarshal.AsBytes(new ReadOnlySpan<TInput>(in _currentInput));
        }

        /// <inheritdoc/>
        public void SetInputBytes(ReadOnlySpan<byte> bytes)
        {
            _currentInput = MemoryMarshal.Read<TInput>(bytes);
        }

        /// <inheritdoc/>
        public void ClearInputChanged()
        {
            _inputChanged = false;
        }
    }
}
