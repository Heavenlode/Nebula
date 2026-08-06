using System;
using System.Diagnostics;
// System.Diagnostics also defines a Debugger; alias Nebula's so the two can't be confused here.
using NebulaDebugger = Nebula.Utility.Tools.Debugger;

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
