using System;
using System.Diagnostics;
// System.Diagnostics also defines a Debugger; alias Nebula's so the two can't be confused here.
using NebulaDebugger = Nebula.Utility.Tools.Debugger;
// Aliased rather than `using Godot`, which would make every Environment in this file ambiguous.
using DisplayServer = Godot.DisplayServer;

namespace Nebula
{
    /// <summary>
    /// Thread-affinity assertions for the netcode.
    ///
    /// Nebula was written when everything ran on Godot's main thread, and a good deal of code
    /// depends on that without saying so -- shared scratch buffers, the ENet host, the peer
    /// registries. Once worlds tick on their own threads (see the
    /// <c>Nebula/config/threading/per_world_thread_group</c> setting) breaking one of those
    /// assumptions does not crash; it corrupts state and surfaces later as a desync, which is the
    /// most expensive bug class in this codebase to chase.
    ///
    /// These assertions exist to turn that silent corruption into a loud, located failure. They
    /// compile out entirely in release builds, so annotate freely: any method that mutates
    /// cross-world state, touches the SceneTree, or assumes serial execution is a candidate.
    /// </summary>
    /// <summary>
    /// Something a long-lived object can hand to <c>NetRunner.RunOnMainThread</c> without allocating.
    ///
    /// The point is that the caller IS the work item: it passes <c>this</c>, so there is no delegate to
    /// construct and no closure to capture. A lambda would allocate on every deferral, which is fine at
    /// join/leave frequency and is not fine for anything a world tick can reach repeatedly.
    ///
    /// Implement it on the object that owns the state being changed, so what runs on the main thread
    /// stays next to what it touches.
    /// </summary>
    public interface IMainThreadWork
    {
        /// <param name="tag">Which job, for an implementer that defers more than one kind of work.
        /// Handed back exactly as it was passed, so no per-job state has to be stored to tell them
        /// apart.</param>
        void OnMainThread(int tag);
    }

    public static class NebulaThread
    {
        /// <summary>
        /// Managed id of Godot's main thread, captured in <see cref="NetRunner._EnterTree"/>.
        /// Zero until then, which is what <see cref="IsMain"/> treats as "unknown, assume fine" --
        /// static constructors and early autoload wiring must not trip an assertion.
        /// </summary>
        private static int _mainThreadId;

        internal static void CaptureMainThread()
        {
            _mainThreadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>True on Godot's main thread, or before the main thread has been identified.</summary>
        public static bool IsMain => _mainThreadId == 0 || Environment.CurrentManagedThreadId == _mainThreadId;

        /// <summary>
        /// Whether this process may build RenderingServer-backed resources -- meshes, textures,
        /// instantiated scenes -- off the main thread.
        /// </summary>
        ///
        /// <remarks>
        /// The RENDERER decides this, not the network role, and the difference matters: a headless
        /// process's dummy renderer uses the non-thread-safe RID owners where a real renderer's are
        /// safe, so an off-main allocation racing any other thread's corrupts the RID freelist. See
        /// the long note in <c>NetNodeCommon.DeserializeInstance</c>, which is where that was paid for
        /// once already.
        ///
        /// True for every client and for a windowed dev server; false for a dedicated server AND for
        /// the headless test runner -- which is exactly why asking "am I a client?" is the wrong
        /// question. A process that answers false is not thereby slow: what it loses is the ability to
        /// move the work off a thread nobody is watching draw.
        /// </remarks>
        public static bool CanBuildResourcesOffMain => DisplayServer.GetName() != HeadlessDisplayDriver;

        /// <summary>Godot's name for the no-op display server, i.e. the dummy renderer.</summary>
        private const string HeadlessDisplayDriver = "headless";

        /// <summary>
        /// Asserts the caller is on Godot's main thread. Use on anything that mutates state shared
        /// between worlds (the peer registries, <see cref="NetRunner.Worlds"/>) or touches the
        /// SceneTree -- <c>AddChild</c>, <c>GetTree()</c>, reparenting. Work reached from a world
        /// tick must be marshalled rather than called directly.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertMain(string context)
        {
            if (IsMain) return;
            Report($"{context} must run on the main thread, but ran on thread {Environment.CurrentManagedThreadId}.");
        }

        /// <summary>
        /// Asserts the caller is NOT on the main thread -- for work deliberately moved off it, so a
        /// silent re-marshal back onto main (the classic captured-SynchronizationContext mistake,
        /// which reintroduces the stall it was meant to remove) fails loudly instead of just
        /// getting slow again.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertOffMain(string context)
        {
            if (!IsMain) return;
            Report($"{context} is expected to run off the main thread, but ran on it.");
        }

        private static void Report(string message)
        {
            // Stack trace included deliberately: the useful information is the call path that got
            // here, not the assertion site itself.
            NebulaDebugger.Instance?.Log(
                $"[NebulaThread] {message}\n{new StackTrace(true)}",
                NebulaDebugger.DebugLevel.ERROR);
        }
    }
}
