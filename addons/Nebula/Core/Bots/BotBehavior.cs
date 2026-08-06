using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Nebula.Bots
{
    /// <summary>
    /// Base class for a scripted bot client. Games subclass this outside Nebula and point a
    /// play configuration at the script; <see cref="BotRunner"/> instantiates one per bot
    /// process and drives it.
    ///
    /// <para>A bot is a <em>real</em> client — full spawn stream, prediction, reconciliation.
    /// The only difference from a human is where its input comes from. The intended way to
    /// act is <see cref="Godot.Input.ActionPress(StringName, float)"/> /
    /// <see cref="Godot.Input.ActionRelease(StringName)"/> from <see cref="NetworkProcess"/>:
    /// the game's own input-sampling code then reads those actions exactly as it reads a
    /// keyboard, so nothing downstream can drift from the path a player takes. (Verified to
    /// work under <c>--headless</c>; the action state is engine-internal, not display-server
    /// backed.)</para>
    ///
    /// <para>Behaviors that need to do more than move — calling a NetFunction, say — can reach
    /// their own nodes through <see cref="OwnedNodes"/>.</para>
    /// </summary>
    public abstract partial class BotBehavior : Node
    {
        /// <summary>
        /// Zero-based index of this bot within the launched set, from <c>--botId</c>. Stable for
        /// the life of the process, so it is safe to derive an identity (account, spawn point,
        /// role) from it.
        /// </summary>
        public int BotId { get; internal set; }

        /// <summary>
        /// The client's world. Null until <see cref="NetRunner.StartClient"/> has run, so
        /// <see cref="BotStartup"/> must not assume it.
        /// </summary>
        public WorldRunner World => WorldRunner.CurrentWorld;

        /// <summary>
        /// The nodes this bot owns — its character, its ship. Empty until the server has spawned
        /// them and ownership has replicated, which is what <see cref="BotReady"/> waits for.
        /// </summary>
        public IReadOnlyList<NetworkController> OwnedNodes =>
            World?.OwnedNodes ?? (IReadOnlyList<NetworkController>)Array.Empty<NetworkController>();

        /// <summary>
        /// Runs before the client connects, and is awaited — the game's entry flow can block on
        /// <see cref="BotRunner.StartupTask"/> so a bot establishes its session before anything
        /// tries to use it. Override to log in, pick a character, or otherwise assume an identity.
        /// </summary>
        public virtual Task BotStartup() => Task.CompletedTask;

        /// <summary>
        /// Runs once, on the first tick where <see cref="OwnedNodes"/> is non-empty. This is the
        /// earliest point at which the bot has a body to act on.
        /// </summary>
        public virtual void BotReady() { }

        /// <summary>
        /// Runs once per network tick, before the prediction pass, which is where a human's input
        /// would have been sampled. Press and release input actions here.
        /// </summary>
        /// <param name="tick">The client's confirmed tick.</param>
        /// <param name="delta">Seconds since the previous network tick.</param>
        public abstract void NetworkProcess(Tick tick, double delta);
    }
}
