using System;
using Godot;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Quantized properties end to end through the props serializer: a server fixture exports
/// and a client fixture (same metadata, Protocol-free ctor) deserializes, so every claim
/// the encoding makes - section sizes, exact deltas, no lossy settle, the server-side
/// dead-band, the 30-delta refresh - is asserted on real bytes and real applied values.
///
/// Delta encoding needs an acked baseline and a send the previous tick, so the scripts
/// here run the full per-tick cycle: Begin, Export, client Deserialize, CommitExport,
/// Acknowledge.
/// </summary>
[NebulaUnitTest]
public class PropsQuantizedWireTests
{
    private const int UnitDir = 0;     // Vector3, UnitVector, step 2e-5 (LocalSurfaceDirection-like)
    private const int Height = 1;      // Float, step 0.01 (TerrainHeight-like)
    private const int Position = 2;    // Vector3, step 0.01 (NetPosition-like)
    private const int Rotation = 3;    // Quaternion, step 0.002 (NetRotation-like)

    private const float UnitStep = 0.00002f;
    private const float GridStep = 0.01f;
    private const float QuatStep = 0.002f;

    private const int PropertyCount = 4;
    private const int MaskModeBits = 1;
    private const int AgeBits = 5;
    private const int EncodingBits = 2;

    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;
        public UUID PeerId;
        public NetNode ServerNode;
        public NetPropertiesSerializer Server;
        public NetNode ClientNode;
        public NetPropertiesSerializer Client;

        public Fixture()
        {
            var types = new[] { SerialVariantType.Vector3, SerialVariantType.Float, SerialVariantType.Vector3, SerialVariantType.Quaternion };
            var steps = new[] { UnitStep, GridStep, GridStep, QuatStep };
            var units = new[] { true, false, false, false };

            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            ServerNode = new NetNode();
            ServerNode.Network.InterestLayers[PeerId] = 1;
            ServerNode.Network.CurrentWorld = World;
            ServerNode.Network.CachedProperties[UnitDir] = new PropertyCache { Type = SerialVariantType.Vector3, Vec3Value = Vector3.Up };
            ServerNode.Network.CachedProperties[Height] = new PropertyCache { Type = SerialVariantType.Float, FloatValue = 601.23f };
            ServerNode.Network.CachedProperties[Position] = new PropertyCache { Type = SerialVariantType.Vector3, Vec3Value = new Vector3(1200f, -35.5f, 4000f) };
            ServerNode.Network.CachedProperties[Rotation] = new PropertyCache { Type = SerialVariantType.Quaternion, QuatValue = Quaternion.Identity };
            Server = new NetPropertiesSerializer(ServerNode.Network, types, null, steps, units)
            {
                ForceRingCaptureForTests = true,
            };
            World.SetClientSpawnState(ServerNode.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);

            ClientNode = new NetNode();
            Client = new NetPropertiesSerializer(ClientNode.Network, types, null, steps, units);
        }

        public void Set(int prop, Vector3 v)
        {
            var c = ServerNode.Network.CachedProperties[prop]; c.Vec3Value = v; ServerNode.Network.CachedProperties[prop] = c;
        }
        public void Set(int prop, float f)
        {
            var c = ServerNode.Network.CachedProperties[prop]; c.FloatValue = f; ServerNode.Network.CachedProperties[prop] = c;
        }
        public void Set(int prop, Quaternion q)
        {
            var c = ServerNode.Network.CachedProperties[prop]; c.QuatValue = q; ServerNode.Network.CachedProperties[prop] = c;
        }

        /// <summary>
        /// One full tick: mark the given props dirty, export, deliver to the client, commit
        /// and ack. Returns the section bytes (empty when nothing was exported).
        /// </summary>
        public NetBuffer Tick(int tick, params int[] dirty)
        {
            long mask = 0;
            foreach (var i in dirty) mask |= 1L << i;
            World.CurrentTick = tick;
            ServerNode.Network.DirtyMask = mask;
            Server.Begin();
            var buf = new NetBuffer(256, usePool: false);
            var result = Server.Export(World, Peer, buf, int.MaxValue);
            if (result != ExportResult.None)
            {
                buf.ResetRead();
                Assert.True(Client.DeserializeForTests(buf, tick), $"tick {tick}: client discarded the payload");
                Server.CommitExport(World, Peer, tick);
                Server.Acknowledge(World, Peer, tick);
            }
            return buf;
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            ServerNode.Free();
            ClientNode.Free();
            World.Free();
        }
    }

    /// <summary>Parses the header and returns the first property's encoding flag plus the header's bit cost.</summary>
    private static DeltaEncodingFlags Flag(NetBuffer buf) => Flag(buf, out _);

    private static DeltaEncodingFlags Flag(NetBuffer buf, out int headerBits)
    {
        buf.ResetRead();
        bool reuse = buf.ReadBool();
        buf.ReadBits(AgeBits);
        if (!reuse) buf.ReadBits(PropertyCount);          // flat mask: one bit per property
        headerBits = buf.ReadBitPosition;
        return (DeltaEncodingFlags)buf.ReadBits(EncodingBits);
    }

    /// <summary>Section bytes for a header of <paramref name="headerBits"/> plus a value of <paramref name="valueBits"/>.</summary>
    private static int Bytes(int headerBits, int valueBits) => (headerBits + EncodingBits + valueBits + 7) / 8;

    private static bool BitEqual(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // 1. Section sizes match the plan's table, absolute then delta, per type. The header
    //    is [reuse:1][age:5][mask:4 bits] on the first send (10 bits) and 6 bits when the
    //    mask equals the acked baseline's; every value pays a 2-bit encoding selector.
    [NebulaUnitTest]
    public void SectionSizes_MatchTheTable()
    {
        using var f = new Fixture();
        const int FullHeader = MaskModeBits + AgeBits + PropertyCount;
        const int ReuseHeader = MaskModeBits + AgeBits;

        // Unit vector at rest (+Y): octahedral (0, 0) -> two 1-bit magnitude forms.
        var b = f.Tick(1, UnitDir);
        Assert.Equal(DeltaEncodingFlags.Absolute, Flag(b, out int h));
        Assert.Equal(FullHeader, h);
        Assert.Equal(Bytes(FullHeader, 1 + 1), b.Length);
        // One walking tick (0.0017 rad): 2x9-bit small delta with its 2-bit class, mask reused.
        f.Set(UnitDir, new Vector3(MathF.Sin(0.0017f), MathF.Cos(0.0017f), 0f));
        b = f.Tick(2, UnitDir);
        Assert.Equal(DeltaEncodingFlags.DeltaSmall, Flag(b, out h));
        Assert.Equal(ReuseHeader, h);
        Assert.Equal(Bytes(ReuseHeader, 2 + 2 * 9), b.Length);

        // Float 601.23 at 0.01: code 60123 -> 22-bit magnitude form; +0.05 -> 5 steps -> 4-bit class.
        b = f.Tick(3, Height);
        Assert.Equal(DeltaEncodingFlags.Absolute, Flag(b, out h));
        Assert.Equal(FullHeader, h);
        Assert.Equal(Bytes(FullHeader, 22), b.Length);
        f.Set(Height, 601.28f);
        b = f.Tick(4, Height);
        Assert.Equal(DeltaEncodingFlags.DeltaSmall, Flag(b, out h));
        Assert.Equal(ReuseHeader, h);
        Assert.Equal(Bytes(ReuseHeader, 2 + 4), b.Length);

        // Position (1200, -35.5, 4000) at 0.01: codes 120000, -3550, 400000 -> magnitude
        // forms of 23, 18 and 25 bits (bit length + 6); a ship tick at 100 u/s (333 steps) is the 10-bit
        // class; at 200 u/s (667 steps) the small form declines and it falls back to three
        // magnitude forms.
        b = f.Tick(5, Position);
        Assert.Equal(DeltaEncodingFlags.Absolute, Flag(b, out h));
        Assert.Equal(FullHeader, h);
        Assert.Equal(Bytes(FullHeader, 23 + 18 + 25), b.Length);
        f.Set(Position, new Vector3(1203.33f, -35.5f, 4000f));
        b = f.Tick(6, Position);
        Assert.Equal(DeltaEncodingFlags.DeltaSmall, Flag(b, out h));
        Assert.Equal(Bytes(ReuseHeader, 2 + 3 * 10), b.Length);
        f.Set(Position, new Vector3(1210f, -35.5f, 4000f));
        b = f.Tick(7, Position);
        Assert.Equal(DeltaEncodingFlags.DeltaFull, Flag(b, out h));
        Assert.Equal(Bytes(ReuseHeader, 16 + 1 + 1), b.Length);   // 667 steps: 16 bits; two zero components: 1 bit each

        // Quaternion: smallest-three at 10 bits, absolute only: 2 + 30 bits.
        f.Set(Rotation, new Quaternion(Vector3.Up, 0.3f));
        b = f.Tick(8, Rotation);
        Assert.Equal(DeltaEncodingFlags.Absolute, Flag(b, out h));
        Assert.Equal(Bytes(FullHeader, 2 + 3 * 10), b.Length);
        f.Set(Rotation, new Quaternion(Vector3.Up, 0.35f));
        b = f.Tick(9, Rotation);
        Assert.Equal(DeltaEncodingFlags.Absolute, Flag(b, out h));
        Assert.Equal(Bytes(ReuseHeader, 2 + 3 * 10), b.Length);
    }

    // 2. The walking script: 60 ticks of a direction advancing 0.0017 rad and a height
    //    creeping, with acks. The client's applied value equals the server's canonical
    //    ring value BIT FOR BIT every tick, no lossy bit is ever set, the absolutes are
    //    exactly the initial one and the 30-delta refresh, and the node settles the moment
    //    the walk stops - no settle absolute.
    [NebulaUnitTest]
    public void Walk_ClientMatchesServerCanonicalExactly_NoLossy_RefreshFires_NoSettle()
    {
        using var f = new Fixture();
        int absolutes = 0, smalls = 0;
        for (int tick = 1; tick <= 60; tick++)
        {
            float theta = 0.0017f * tick;
            f.Set(UnitDir, new Vector3(MathF.Sin(theta) * 0.8f, MathF.Cos(theta), MathF.Sin(theta) * 0.6f).Normalized());
            f.Set(Height, 601.23f + 0.031f * tick);
            var b = f.Tick(tick, UnitDir, Height);
            Assert.True(b.Length > 0, $"tick {tick}: nothing exported");

            // Flag of the first written prop (UnitDir, index 0).
            var flag = Flag(b);
            if (flag == DeltaEncodingFlags.Absolute) absolutes++;
            else if (flag == DeltaEncodingFlags.DeltaSmall) smalls++;
            else Assert.Fail($"tick {tick}: unexpected flag {flag}");

            Assert.Equal(0, f.Server.LossyByteForTests(f.PeerId, 0));

            var serverDir = f.Server.RingValueForTests(tick, UnitDir).Vec3Value;
            var clientDir = f.Client.AppliedValueForTests(tick, UnitDir).Vec3Value;
            Assert.True(BitEqual(serverDir, clientDir), $"tick {tick}: client {clientDir} != server canonical {serverDir}");
            float serverH = f.Server.RingValueForTests(tick, Height).FloatValue;
            float clientH = f.Client.AppliedValueForTests(tick, Height).FloatValue;
            Assert.Equal(BitConverter.SingleToInt32Bits(serverH), BitConverter.SingleToInt32Bits(clientH));

            // The canonical value is within the declared resolution of the true one.
            Assert.True((serverDir - f.ServerNode.Network.CachedProperties[UnitDir].Vec3Value).Length()
                <= QuantizedCodec.MaxError(SerialVariantType.Vector3, true, UnitStep));
        }
        // Tick 1 absolute, ticks 2..31 deltas (chain 30), tick 32 refresh absolute, then deltas.
        Assert.Equal(2, absolutes);
        Assert.Equal(58, smalls);

        // Walk stops: nothing dirty -> nothing exported, and the peer is settled with no
        // lossy residue to settle.
        var quiet = f.Tick(61);
        Assert.Equal(0, quiet.Length);
        Assert.Equal(0, f.Server.LossyByteForTests(f.PeerId, 0));
        Assert.True(f.Server.SettledForTests(f.PeerId), "peer should be settled: exact deltas leave nothing owed");
    }

    // 3. The dead-band. Sub-step jitter never ships; a creep of 0.15 steps per tick is
    //    filtered until the accumulated drift crosses a cell boundary (tick 4), and the
    //    filter compares against the last SHIPPED cell, not the previous tick.
    [NebulaUnitTest]
    public void DirtyFilter_JitterNeverShips_CreepShipsOnCellCross()
    {
        using var f = new Fixture();
        f.Set(Height, 1.0f);
        Assert.True(f.Tick(1, Height).Length > 0);

        // Jitter within +-0.2 step around the shipped cell.
        float[] jitter = { 1.002f, 0.998f, 1.001f, 0.9985f, 1.0f };
        for (int i = 0; i < jitter.Length; i++)
        {
            f.Set(Height, jitter[i]);
            Assert.Equal(0, f.Tick(2 + i, Height).Length);
            Assert.False(f.Server.SettledForTests(f.PeerId) == false && f.Server.PendingDirtyByteForTests(f.PeerId, 0) != 0,
                "a filtered change must leave nothing pending");
        }

        // Creep: 1.0015, 1.003, 1.0045 stay in cell 100; 1.006 rounds to 101 and ships.
        float[] creep = { 1.0015f, 1.003f, 1.0045f, 1.006f };
        for (int i = 0; i < creep.Length; i++)
        {
            f.Set(Height, creep[i]);
            var b = f.Tick(10 + i, Height);
            if (i < 3) Assert.Equal(0, b.Length);
            else
            {
                Assert.True(b.Length > 0, "creep must ship once it crosses a cell");
                Assert.Equal(1.01f, f.Client.AppliedValueForTests(13, Height).FloatValue, 5);
            }
        }

        // The filter is server-only state; the same value written again after a ship is
        // filtered, but a real change is not.
        f.Set(Height, 1.0102f);
        Assert.Equal(0, f.Tick(20, Height).Length);
        f.Set(Height, 1.02f);
        Assert.True(f.Tick(21, Height).Length > 0);
    }

    // 4. A seam-crossing direction (the -Y hemisphere fold) through the real serializer:
    //    the full-varint delta escape lands the client on the canonical value exactly.
    [NebulaUnitTest]
    public void SeamCrossing_ThroughSerializer_IsExact()
    {
        using var f = new Fixture();
        bool sawFull = false;
        for (int tick = 1; tick <= 200; tick++)
        {
            float t = tick / 200f * MathF.PI * 2f;
            f.Set(UnitDir, new Vector3(MathF.Cos(t), -0.6f + 0.3f * MathF.Sin(3 * t), MathF.Sin(t)).Normalized());
            var b = f.Tick(tick, UnitDir);
            Assert.True(b.Length > 0);
            if (Flag(b) == DeltaEncodingFlags.DeltaFull) sawFull = true;
            Assert.True(BitEqual(f.Server.RingValueForTests(tick, UnitDir).Vec3Value, f.Client.AppliedValueForTests(tick, UnitDir).Vec3Value), $"tick {tick}");
            Assert.Equal(0, f.Server.LossyByteForTests(f.PeerId, 0));
        }
        Assert.True(sawFull, "the sweep never took the full-delta seam escape");
    }

    // 5. Position deltas are exact too, including the small->full fallback at speed, and
    //    a quaternion round-trips within its declared error.
    [NebulaUnitTest]
    public void PositionAndRotation_ClientValuesExactAndBounded()
    {
        using var f = new Fixture();
        var pos = new Vector3(1200f, -35.5f, 4000f);
        for (int tick = 1; tick <= 40; tick++)
        {
            pos += new Vector3(tick < 20 ? 3.33f : 6.67f, 0.01f * tick, -1.5f);
            f.Set(Position, pos);
            f.Set(Rotation, new Quaternion(new Vector3(0.3f, 1f, 0.2f).Normalized(), 0.05f * tick));
            var b = f.Tick(tick, Position, Rotation);
            Assert.True(b.Length > 0);

            var sp = f.Server.RingValueForTests(tick, Position).Vec3Value;
            var cp = f.Client.AppliedValueForTests(tick, Position).Vec3Value;
            Assert.True(BitEqual(sp, cp), $"tick {tick}: {cp} != {sp}");
            Assert.True((sp - pos).Length() <= QuantizedCodec.MaxError(SerialVariantType.Vector3, false, GridStep) * 1.001f);

            var cq = f.Client.AppliedValueForTests(tick, Rotation).QuatValue;
            var sq = f.ServerNode.Network.CachedProperties[Rotation].QuatValue;
            Assert.True(sq.AngleTo(cq) <= QuantizedCodec.MaxError(SerialVariantType.Quaternion, false, QuatStep), $"tick {tick}: rotation error {sq.AngleTo(cq)}");
        }
        Assert.Equal(0, f.Server.LossyByteForTests(f.PeerId, 0));
    }
}
