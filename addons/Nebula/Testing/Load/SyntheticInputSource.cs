using System;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// Where a synthetic peer's input bytes come from. The load client's analogue of
    /// <c>BotBehavior</c>, and the one seam a game implements.
    ///
    /// <para>A PLAIN CLASS, not a <c>Node</c> — unlike <c>BotBehavior</c>. Instances run on shard
    /// threads, which are not Godot's main thread, and making this a Node would invite exactly the
    /// engine calls that are unsafe from there.</para>
    ///
    /// <para>It also cannot work the way <c>BotBehavior</c> does. A bot presses input ACTIONS and
    /// lets the real <c>PlayerShip</c> sample them into an input struct, so nothing about a bot is
    /// bot-aware. A synthetic peer has no node to do that sampling, so it must write the struct
    /// itself. That is a second input route alongside the one the bot README deliberately avoided
    /// creating, and it is the real cost of the synthetic approach — the mitigation is that
    /// everything downstream (the packet header, the record encoding) is the code a real client
    /// runs, so only the bytes themselves are written here.</para>
    ///
    /// <para>Implement one in your game project and name it with <c>--loadBehavior=TypeName</c>. It
    /// is found by reflection on the type name, the same way and for the same reason as a
    /// <c>BotBehavior</c>: a C# script resource cannot be instantiated through Godot's script API.
    /// It needs a parameterless constructor.</para>
    /// </summary>
    public abstract class SyntheticInputSource
    {
        /// <summary>Zero-based index of the peer this instance drives. Stable for the run.</summary>
        public int PeerIndex { get; internal set; }

        /// <summary>
        /// Called once per entry in the server's input map, before any input is asked for.
        ///
        /// <para>Bind the node to a slot index this source will recognise later, or return false to
        /// send it nothing. <paramref name="scenePath"/> and <paramref name="staticChildPath"/>
        /// identify the node at protocol level, so a source never has to guess from a byte count.
        /// </para>
        /// </summary>
        /// <param name="scenePath">e.g. <c>res://player/player.tscn</c>.</param>
        /// <param name="staticChildPath">Path within that scene, or "." for the scene root.</param>
        /// <param name="inputSize">Bytes the server expects. It rejects a packet that disagrees.</param>
        public abstract bool TryBind(string scenePath, string staticChildPath, int inputSize, out int slot);

        /// <summary>
        /// Called once per prediction tick per bound slot. Write exactly the bound
        /// <c>inputSize</c> bytes into <paramref name="destination"/>.
        /// </summary>
        /// <returns>
        /// False to leave the input unchanged since last tick. That is not a detail: it is what
        /// drives the keepalive suppression, so a source that always returns true disables the
        /// <c>&amp; 3</c> path and inflates the inbound packet rate roughly fourfold against what a
        /// real client produces.
        /// </returns>
        public abstract bool WriteInput(int slot, int tick, Span<byte> destination);
    }
}
