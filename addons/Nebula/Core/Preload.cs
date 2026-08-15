using System;

namespace Nebula
{
    /// <summary>
    /// Marks a NetScene to be built once, early, before anyone is waiting for it.
    ///
    /// WHY THIS EXISTS. The first <c>PackedScene.Instantiate()</c> of a scene in a process is far more
    /// expensive than every one after it -- measured on this engine at ~100ms against 0.2ms, whatever
    /// the scene contains. The gap is too large to be resource loading; it is the per-process
    /// first-time cost of the scene's script classes, their generated marshalling code and their static
    /// initialisers. It recurs every session, never twice, and it lands on whichever frame first needs
    /// the scene -- which for a networked game is the moment a player arrives somewhere.
    ///
    /// The cost cannot be removed, only moved. Mark the scene's ROOT NetNode class with this, then call
    /// <see cref="Serialization.Protocol.PreloadScenes"/> somewhere the player is already waiting -- a
    /// menu, a hub, a loading screen -- and the arrival costs a normal frame instead.
    ///
    /// <code>
    /// [Preload]
    /// public partial class IntroExpedition : NetNode3D { }
    /// </code>
    ///
    /// Applies to the class attached to a scene's root node; a scene whose root carries a marked class
    /// is preloaded whole. Marking a class that is not a scene root does nothing, because there is no
    /// scene for the protocol to attribute it to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class Preload : Attribute
    {
    }
}
