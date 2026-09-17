using System;

namespace Nebula.Testing.Load
{
    /// <summary>
    /// The default input source when <c>--loadBehavior</c> names none: an all-zero payload with one
    /// bit toggled periodically.
    ///
    /// <para>Game-agnostic on purpose — Nebula must not reference a game's input struct — which is
    /// exactly why it is a weak load. All-zero is a valid "no keys held" for the boolean structs
    /// these usually are, so the peer occupies the server, replicates, and acknowledges, but never
    /// moves. A still player leaves its interest set static and lets the props-settled skip
    /// suppress most export work, so a run driven by this measures the floor, not the game.</para>
    ///
    /// <para>The periodic toggle is not there to simulate play. It exists so that BOTH cadence
    /// paths are exercised — the change path and the <c>&amp; 3</c> keepalive — because a source
    /// that never changed would silently test only one of them.</para>
    ///
    /// <para>For a load run that means anything, write a game-side source that builds the real
    /// input struct.</para>
    /// </summary>
    public sealed class ZeroInputSource : SyntheticInputSource
    {
        /// <summary>Prediction ticks between toggles. ~1s at 30 TPS.</summary>
        public const int DefaultChangePeriodTicks = 30;

        /// <summary>Bit flipped in byte 0. Byte 0 is the first field of any sequential struct.</summary>
        private const byte ToggleBit = 0x01;

        public int ChangePeriodTicks = DefaultChangePeriodTicks;

        private int _nextSlot;
        private readonly int[] _sizeBySlot = new int[MaxSlots];
        private const int MaxSlots = 16;

        public override bool TryBind(string scenePath, string staticChildPath, int inputSize, out int slot)
        {
            slot = -1;
            if (_nextSlot >= MaxSlots) return false;
            if (inputSize <= 0) return false;

            slot = _nextSlot++;
            _sizeBySlot[slot] = inputSize;
            return true;
        }

        public override bool WriteInput(int slot, int tick, Span<byte> destination)
        {
            destination.Clear();

            // Offset by peer so the fleet does not toggle in lockstep, which would give the server
            // one synchronised spike per period instead of a spread.
            int period = Math.Max(1, ChangePeriodTicks);
            int phase = (tick + PeerIndex) % (period * 2);
            bool on = phase >= period;

            if (on && destination.Length > 0) destination[0] = ToggleBit;

            // "Changed" exactly on the two ticks where the value actually flips, so the keepalive
            // path carries every other tick — which is the point of the default source.
            return phase == 0 || phase == period;
        }
    }
}
