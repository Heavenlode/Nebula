using System;
using Godot;
using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Wire encoding of a quantized property (<c>[NetProperty(Quantize = step)]</c>), shared by
    /// the server writer and the client reader so the two can never disagree.
    ///
    /// <para>A quantized value is N signed integer step counts ("codes"): float N=1, Vector2
    /// N=2, Vector3 N=3, unit-vector Vector3 N=2 (octahedral u, w). Codes travel either
    /// absolute (N magnitude forms, see BitCodec), as a SMALL delta (a 2-bit
    /// width class + N x width bits), or as a FULL delta (N magnitude forms). Quaternions are
    /// the exception: smallest-three at <see cref="ResolveQuatBits"/> bits per component,
    /// absolute only.</para>
    ///
    /// <para><b>Exactness contract.</b> Deltas are exact because both sides compute
    /// <c>Quantize(baseline)</c> from bit-identical floats with identical code: the server's
    /// delta ring holds <see cref="Canonicalize"/>d values, the client holds the
    /// <see cref="Decode"/> of the codes it received, and both are the same
    /// <c>Dequantize(codes)</c>. That does NOT require <c>Quantize(Dequantize(q)) == q</c>;
    /// on the octahedral fold seams it is legitimately false (the mirrored code comes back),
    /// which merely turns that tick's delta into the full form. What it does require is that
    /// <see cref="Dequantize"/> and <see cref="OctDecode"/> are deterministic across
    /// machines: they use only IEEE + - * / and sqrt, never a transcendental.</para>
    ///
    /// <para><b>Magnitude limit.</b> <c>v / step</c> is round-robust in float32 only while
    /// <c>|v| &lt; ~0.5 * step * 2^24</c> (about 84,000 units at a 0.01 step). Beyond that the
    /// grid is coarser than the step, still exact on the wire but no longer the declared
    /// resolution. Codes are clamped to int range rather than left to the platform's
    /// out-of-range float-to-int conversion, which differs between x86 and ARM.</para>
    /// </summary>
    internal static class QuantizedCodec
    {
        /// <summary>
        /// Most bits per smallest-three component a QUANTIZED quaternion may use: 2 + 3 * 10
        /// keeps the packed word inside 32 bits so it can serve as the dead-band's grid code.
        /// </summary>
        public const int MaxQuatBits = 10;
        public const int MinQuatBits = 2;
        private const int QuatIndexBits = 2;

        /// <summary>Largest component count of any quantized type (Vector3).</summary>
        public const int MaxComponents = 3;


        /// <summary>Smallest-three components live in [-1/sqrt2, 1/sqrt2].</summary>
        private const float QuatHalfRange = 0.70710678f;

        /// <summary>
        /// Inputs whose squared length is below this are not directions (a Vector3.Zero
        /// sentinel, an uninitialised value) and encode as +Y rather than as NaN.
        /// </summary>
        private const float UnitMinLengthSquared = 0.5f;
        private static bool _sentinelWarned;

        // ───────────────────────────── metadata ─────────────────────────────

        public static int ComponentCount(SerialVariantType type, bool unitVector)
        {
            return type switch
            {
                SerialVariantType.Float => 1,
                SerialVariantType.Vector2 => 2,
                SerialVariantType.Vector3 => unitVector ? 2 : 3,
                _ => 0,
            };
        }

        /// <summary>True for the types <c>Quantize</c> is allowed on (NEBULA010).</summary>
        public static bool IsQuantizable(SerialVariantType type)
        {
            return type is SerialVariantType.Float or SerialVariantType.Vector2
                or SerialVariantType.Vector3 or SerialVariantType.Quaternion;
        }

        /// <summary>
        /// Bits per smallest-three component for a declared step: the fewest that make the
        /// component quantum no larger than the step, capped at <see cref="MaxQuatBits"/>.
        /// </summary>
        public static byte ResolveQuatBits(float step)
        {
            if (step <= 0f) return 0;
            int bits = MinQuatBits;
            while (bits < MaxQuatBits && QuatQuantum(bits) > step) bits++;
            return (byte)bits;
        }

        /// <summary>
        /// Code range at a bit width: one level fewer than the width allows, so the level
        /// count is odd and ZERO is a code. A resting rotation (identity, or any axis-aligned
        /// quarter turn) then replicates exactly instead of as a half-quantum wobble.
        /// </summary>
        private static uint QuatMaxCode(int bits) => (1u << bits) - 2;

        /// <summary>The component quantum actually used at a bit width.</summary>
        public static float QuatQuantum(int bits) => 2f * QuatHalfRange / QuatMaxCode(bits);

        /// <summary>
        /// Worst-case error the encoding introduces, in the units the prediction tolerance of
        /// that type is authored in: Euclidean distance for float/Vector2/Vector3 and for unit
        /// vectors (chord length, ~radians), radians for a quaternion (AngleTo). Bounds verified
        /// by brute force in QuantizedCodecTests.
        /// </summary>
        public static float MaxError(SerialVariantType type, bool unitVector, float step)
        {
            switch (type)
            {
                case SerialVariantType.Float: return step * 0.5f;
                case SerialVariantType.Vector2: return step * 0.5f * 1.41421356f;
                case SerialVariantType.Vector3:
                    // Octahedral: the square-to-sphere map's largest singular value is 3, at
                    // the octant centres (1,1,1)/sqrt3, along the (u, w) diagonal. The worst
                    // rounding error in the square is half a step on both axes, sqrt2 * step/2
                    // along that diagonal, so 3 * sqrt2 / 2 = 2.12 steps on the sphere; 2.2
                    // leaves room for float rounding. The fold is an isometry of the square,
                    // so the lower hemisphere has the same bound.
                    return unitVector ? step * 2.2f : step * 0.5f * 1.73205081f;
                case SerialVariantType.Quaternion:
                    // Three components each off by up to half a quantum, plus the
                    // reconstructed largest component's second-order error; angle ~ 2|dq|.
                    return 4f * QuatQuantum(ResolveQuatBits(step));
                default: return 0f;
            }
        }

        // ───────────────────────────── grid ─────────────────────────────

        public static int Quantize(float v, float step)
        {
            float r = MathF.Round(v / step);
            // Clamp in double: (float)int.MaxValue rounds up to 2^31, which is itself out of
            // range, and an out-of-range cast is platform-defined.
            if (r >= 2147483647.0) return int.MaxValue;
            if (r <= -2147483648.0) return int.MinValue;
            if (float.IsNaN(r)) return 0;
            return (int)r;
        }

        public static float Dequantize(int q, float step) => q * step;

        // ───────────────────────────── octahedral ─────────────────────────────

        private static float Sgn(float f) => f >= 0f ? 1f : -1f;

        /// <summary>
        /// Octahedral projection with +Y as the fold axis, so the seams (where a direction has
        /// two equivalent codes) lie in the -Y hemisphere, away from the "up" every direction
        /// property rests at. Non-directions (see <see cref="UnitMinLengthSquared"/>) encode
        /// as +Y with a one-time warning.
        /// </summary>
        public static void OctEncode(Vector3 v, out float u, out float w)
        {
            float lengthSq = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            if (!(lengthSq >= UnitMinLengthSquared))
            {
                if (!_sentinelWarned)
                {
                    _sentinelWarned = true;
                    GD.PushWarning($"QuantizedCodec: a UnitVector property was written with a non-direction {v}; sent as +Y. Properties that use Vector3.Zero as a sentinel must not set UnitVector.");
                }
                u = 0f; w = 0f;
                return;
            }
            float l1 = MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z);
            float px = v.X / l1;
            float pz = v.Z / l1;
            if (v.Y < 0f)
            {
                float fx = (1f - MathF.Abs(pz)) * Sgn(px);
                float fz = (1f - MathF.Abs(px)) * Sgn(pz);
                px = fx; pz = fz;
            }
            u = px; w = pz;
        }

        /// <summary>Inverse of <see cref="OctEncode"/>; the result is renormalised.</summary>
        public static Vector3 OctDecode(float u, float w)
        {
            float y = 1f - MathF.Abs(u) - MathF.Abs(w);
            float x = u;
            float z = w;
            if (y < 0f)
            {
                float fx = (1f - MathF.Abs(w)) * Sgn(u);
                float fz = (1f - MathF.Abs(u)) * Sgn(w);
                x = fx; z = fz;
            }
            float len = MathF.Sqrt(x * x + y * y + z * z);
            return new Vector3(x / len, y / len, z / len);
        }

        // ───────────────────────────── value <-> codes ─────────────────────────────

        /// <summary>Fills <paramref name="codes"/> (length &gt;= ComponentCount) from a value.</summary>
        public static void Encode(in PropertyCache value, SerialVariantType type, bool unitVector, float step, Span<int> codes)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    codes[0] = Quantize(value.FloatValue, step);
                    break;
                case SerialVariantType.Vector2:
                    codes[0] = Quantize(value.Vec2Value.X, step);
                    codes[1] = Quantize(value.Vec2Value.Y, step);
                    break;
                case SerialVariantType.Vector3:
                    if (unitVector)
                    {
                        OctEncode(value.Vec3Value, out float u, out float w);
                        codes[0] = Quantize(u, step);
                        codes[1] = Quantize(w, step);
                    }
                    else
                    {
                        codes[0] = Quantize(value.Vec3Value.X, step);
                        codes[1] = Quantize(value.Vec3Value.Y, step);
                        codes[2] = Quantize(value.Vec3Value.Z, step);
                    }
                    break;
                default:
                    throw new NotSupportedException($"QuantizedCodec.Encode: {type} is not a grid type");
            }
        }

        /// <summary>Writes the value the codes denote into the cache (type-tagged).</summary>
        public static void Decode(ReadOnlySpan<int> codes, SerialVariantType type, bool unitVector, float step, ref PropertyCache cache)
        {
            cache.Type = type;
            switch (type)
            {
                case SerialVariantType.Float:
                    cache.FloatValue = Dequantize(codes[0], step);
                    break;
                case SerialVariantType.Vector2:
                    cache.Vec2Value = new Vector2(Dequantize(codes[0], step), Dequantize(codes[1], step));
                    break;
                case SerialVariantType.Vector3:
                    cache.Vec3Value = unitVector
                        ? OctDecode(Dequantize(codes[0], step), Dequantize(codes[1], step))
                        : new Vector3(Dequantize(codes[0], step), Dequantize(codes[1], step), Dequantize(codes[2], step));
                    break;
                default:
                    throw new NotSupportedException($"QuantizedCodec.Decode: {type} is not a grid type");
            }
        }

        /// <summary>
        /// Replaces the value with what a client holds after receiving it: the exact float the
        /// server's delta ring must carry for deltas against it to be exact.
        /// </summary>
        public static void Canonicalize(ref PropertyCache cache, SerialVariantType type, bool unitVector, float step)
        {
            Span<int> codes = stackalloc int[MaxComponents];
            Encode(in cache, type, unitVector, step, codes);
            Decode(codes, type, unitVector, step, ref cache);
        }

        // ───────────────────────────── quaternion ─────────────────────────────

        /// <summary>
        /// Smallest-three at <paramref name="bits"/> per component, largest forced positive,
        /// components rounded (not truncated) to the grid: [index:2][a][b][c] in a uint32.
        /// </summary>
        public static ulong PackQuat(Quaternion q, int bits)
        {
            float absX = MathF.Abs(q.X), absY = MathF.Abs(q.Y), absZ = MathF.Abs(q.Z), absW = MathF.Abs(q.W);
            int maxIndex = 0;
            float maxVal = absX;
            if (absY > maxVal) { maxIndex = 1; maxVal = absY; }
            if (absZ > maxVal) { maxIndex = 2; maxVal = absZ; }
            if (absW > maxVal) { maxIndex = 3; }

            float sign = maxIndex switch
            {
                0 => Sgn(q.X),
                1 => Sgn(q.Y),
                2 => Sgn(q.Z),
                _ => Sgn(q.W),
            };
            float a, b, c;
            switch (maxIndex)
            {
                case 0: a = q.Y * sign; b = q.Z * sign; c = q.W * sign; break;
                case 1: a = q.X * sign; b = q.Z * sign; c = q.W * sign; break;
                case 2: a = q.X * sign; b = q.Y * sign; c = q.W * sign; break;
                default: a = q.X * sign; b = q.Y * sign; c = q.Z * sign; break;
            }

            uint maxCode = QuatMaxCode(bits);
            uint ua = QuatComponentCode(a, maxCode);
            uint ub = QuatComponentCode(b, maxCode);
            uint uc = QuatComponentCode(c, maxCode);
            return (ulong)maxIndex | ((ulong)ua << QuatIndexBits) | ((ulong)ub << (QuatIndexBits + bits)) | ((ulong)uc << (QuatIndexBits + 2 * bits));
        }

        private static uint QuatComponentCode(float component, uint maxCode)
        {
            float t = (component + QuatHalfRange) / (2f * QuatHalfRange) * maxCode;
            float r = MathF.Round(t);
            if (r <= 0f) return 0;
            if (r >= maxCode) return maxCode;
            return (uint)r;
        }

        public static Quaternion UnpackQuat(ulong packed, int bits)
        {
            ulong mask = (1UL << bits) - 1;
            uint maxCode = QuatMaxCode(bits);
            int maxIndex = (int)(packed & ((1UL << QuatIndexBits) - 1));
            uint ua = Math.Min((uint)((packed >> QuatIndexBits) & mask), maxCode);
            uint ub = Math.Min((uint)((packed >> (QuatIndexBits + bits)) & mask), maxCode);
            uint uc = Math.Min((uint)((packed >> (QuatIndexBits + 2 * bits)) & mask), maxCode);
            float scale = 2f * QuatHalfRange / maxCode;
            float a = ua * scale - QuatHalfRange;
            float b = ub * scale - QuatHalfRange;
            float c = uc * scale - QuatHalfRange;
            // Rounding can push the three components' sum of squares past 1 (the largest is
            // then reconstructed as 0 and the result is short); renormalise so the client
            // never holds a non-unit rotation. Deterministic: sqrt and division only.
            float sumSq = a * a + b * b + c * c;
            float largest = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));
            float invLen = 1f / MathF.Sqrt(sumSq + largest * largest);
            a *= invLen; b *= invLen; c *= invLen; largest *= invLen;
            return maxIndex switch
            {
                0 => new Quaternion(largest, a, b, c),
                1 => new Quaternion(a, largest, b, c),
                2 => new Quaternion(a, b, largest, c),
                _ => new Quaternion(a, b, c, largest),
            };
        }

        // ───────────────────────────── wire forms ─────────────────────────────
        //
        // All bit-level. A quantized property's codes travel absolute as N magnitude forms
        // (BitCodec.WriteMagnitude), as a SMALL delta with a 2-bit width class selecting one
        // fixed signed width for every component, or as a FULL delta of N magnitude forms.

        /// <summary>Bits of the width-class selector that prefixes a small delta.</summary>
        public const int WidthClassBits = 2;

        // Per component count, the four signed widths a small delta may use (index = class).
        private static readonly byte[] Widths1 = { 4, 6, 9, 16 };
        private static readonly byte[] Widths2 = { 5, 7, 9, 12 };
        private static readonly byte[] Widths3 = { 4, 6, 8, 10 };

        private static byte[] Widths(int count) => count switch
        {
            1 => Widths1,
            2 => Widths2,
            3 => Widths3,
            _ => throw new NotSupportedException($"QuantizedCodec: {count} components"),
        };

        /// <summary>The signed range of a width: [-2^(w-1), 2^(w-1) - 1], stored biased.</summary>
        private static int WidthMin(int width) => -(1 << (width - 1));

        /// <summary>N magnitude forms.</summary>
        public static void WriteCodes(NetBuffer buffer, ReadOnlySpan<int> codes, int count)
        {
            for (int i = 0; i < count; i++) BitCodec.WriteMagnitude(buffer, codes[i]);
        }

        public static void ReadCodes(NetBuffer buffer, Span<int> codes, int count)
        {
            for (int i = 0; i < count; i++) codes[i] = BitCodec.ReadMagnitude(buffer);
        }

        /// <summary>
        /// Writes the deltas as [class: 2 bits][N x width bits] using the smallest width class
        /// every component fits; writes nothing and returns false when even the widest class
        /// overflows (the caller then takes the full form).
        /// </summary>
        public static bool TryWriteSmallDelta(NetBuffer buffer, ReadOnlySpan<int> deltas, int count)
        {
            var widths = Widths(count);
            for (int cls = 0; cls < widths.Length; cls++)
            {
                int width = widths[cls];
                int min = WidthMin(width);
                int max = -min - 1;
                bool fits = true;
                for (int k = 0; k < count; k++)
                {
                    if (deltas[k] < min || deltas[k] > max) { fits = false; break; }
                }
                if (!fits) continue;

                buffer.WriteBits((ulong)cls, WidthClassBits);
                for (int k = 0; k < count; k++) buffer.WriteBits((ulong)(uint)(deltas[k] - min), width);
                return true;
            }
            return false;
        }

        public static void ReadSmallDelta(NetBuffer buffer, Span<int> deltas, int count)
        {
            int cls = (int)buffer.ReadBits(WidthClassBits);
            int width = Widths(count)[cls];
            int min = WidthMin(width);
            for (int k = 0; k < count; k++) deltas[k] = (int)buffer.ReadBits(width) + min;
        }

        /// <summary>Bits a small delta of this component count costs at a width class.</summary>
        public static int SmallDeltaBits(int count, int widthClass) => WidthClassBits + count * Widths(count)[widthClass];

        /// <summary>The widest small-delta range for a component count (per component).</summary>
        public static int SmallDeltaMaxMagnitude(int count) => -WidthMin(Widths(count)[^1]) - 1;

        /// <summary>Smallest-three bits per component for a quaternion that declared no step.</summary>
        public const int UnquantizedQuatBits = 14;

        /// <summary>Wire bits of a smallest-three quaternion at a component width.</summary>
        public static int QuatWireBits(int bits) => QuatIndexBits + 3 * bits;

        public static void WriteQuat(NetBuffer buffer, Quaternion q, int bits)
            => buffer.WriteBits(PackQuat(q, bits), QuatWireBits(bits));

        public static Quaternion ReadQuat(NetBuffer buffer, int bits)
            => UnpackQuat(buffer.ReadBits(QuatWireBits(bits)), bits);
    }
}
