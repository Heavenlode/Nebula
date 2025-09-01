using System;
using FixedMathSharp;
using Godot;
using MongoDB.Bson;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
    /// <summary>
	/// Byte 0: Header
    /// Bit 0-1: Type of change
    /// (1 << 0) Delta
    /// (1 << 1) Keyframe
    /// 
    /// Remaining bits:
    /// (1 << 2) Position X Change Flag
    /// (1 << 3) Position Y Change Flag
    /// (1 << 4) Position Z Change Flag
    /// (1 << 5) Rotation X Change Flag
    /// (1 << 6) Rotation Y Change Flag
    /// (1 << 7) Rotation Z Change Flag
    /// </summary>
    public partial class NetPose3D : RefCounted, INetSerializable<NetPose3D>, IBsonSerializable<NetPose3D>
    {
        const int CHANGE_HEADER_LENGTH = 2;
        const int AXIS_COUNT = 3;

        static readonly Fixed64 POSITION_SCALE = new Fixed64(10);  // ±12.8 units/tick
        static readonly Fixed64 ROTATION_SCALE = new Fixed64(100); // 2 decimal precision (×100)

        // Allow X ticks worth of maximum movement before keyframe
        static readonly Fixed64 POSITION_KEYFRAME_THRESHOLD = new Fixed64(3276.7 * 5);  // 5 ticks of max movement
        static readonly Fixed64 ROTATION_KEYFRAME_THRESHOLD = new Fixed64(327.67 * 5);  // 5 ticks of max rotation (in degrees)

        Vector3d _position;
        Vector3d _rotation; // Still stored in radians internally
        Vector3d _positionDelta;
        Vector3d _rotationDelta; // Delta in radians, converted to degrees for network

        Vector3d _positionKeyframe;
        Vector3d _rotationKeyframe;

        public Vector3 Position => new Vector3(_position.x.ToFormattedFloat(), _position.y.ToFormattedFloat(), _position.z.ToFormattedFloat());
        public Vector3 Rotation => new Vector3(_rotation.x.ToFormattedFloat(), _rotation.y.ToFormattedFloat(), _rotation.z.ToFormattedFloat());

        [Signal]
        public delegate void OnChangeEventHandler();

        public enum ChangeType
        {
            Delta = 1 << 0,
            Keyframe = 1 << 1,
        }

        public ChangeType ClientState { get; private set; } = ChangeType.Delta;
        public int KeyframeFrequency = NetRunner.TPS;
        private int _keyframeOffset = 0;

        public NetPeer Owner;

        public NetPose3D()
        {
            _keyframeOffset = (int)(GD.Randi() % KeyframeFrequency);
        }

        /// <summary>
        /// Applies a position and rotation change based on Vector3 values.
        /// Position deltas are sent as shorts with 0.1 unit precision, allowing ±3276.7 units per tick.
        /// Rotation deltas are sent as shorts with 0.01 degree precision, allowing ±327.67 degrees per tick.
        /// </summary>
        /// <param name="newPosition"></param>
        /// <param name="newRotation"></param>
        public void ApplyDelta(Vector3 newPosition, Vector3 newRotation)
        {
            _positionKeyframe = new Vector3d(newPosition.X, newPosition.Y, newPosition.Z);
            _rotationKeyframe = new Vector3d(newRotation.X, newRotation.Y, newRotation.Z);
            _positionDelta += _positionKeyframe - _position;
            _rotationDelta += _rotationKeyframe - _rotation;

            // Clamp position and rotation deltas to short range
            for (byte i = 0; i < AXIS_COUNT; i++)
            {
                // Clamp position deltas to short range
                var maxPositionDelta = new Fixed64(short.MaxValue) / POSITION_SCALE; // ±3276.7 units
                if (_positionDelta[i] > maxPositionDelta)
                {
                    Debugger.Instance.Log($"Position delta is too high. Clamping. {_positionDelta[i]} to {maxPositionDelta}", Debugger.DebugLevel.WARN);
                    _positionDelta[i] = maxPositionDelta;
                }
                if (_positionDelta[i] < -maxPositionDelta)
                {
                    Debugger.Instance.Log($"Position delta is too low. Clamping. {_positionDelta[i]} to {-maxPositionDelta}", Debugger.DebugLevel.WARN);
                    _positionDelta[i] = -maxPositionDelta;
                }

                // Convert rotation delta to degrees and clamp to short range
                var rotationDeltaDegrees = _rotationDelta[i] * (new Fixed64(180.0) / FixedMath.PI);
                var maxRotationDeltaDegrees = new Fixed64(short.MaxValue) / ROTATION_SCALE; // ±327.67 degrees

                if (rotationDeltaDegrees > maxRotationDeltaDegrees)
                {
                    Debugger.Instance.Log($"Rotation delta is too high. Clamping. {rotationDeltaDegrees} to {maxRotationDeltaDegrees} degrees", Debugger.DebugLevel.WARN);
                    _rotationDelta[i] = maxRotationDeltaDegrees * (FixedMath.PI / new Fixed64(180.0));
                }
                if (rotationDeltaDegrees < -maxRotationDeltaDegrees)
                {
                    Debugger.Instance.Log($"Rotation delta is too low. Clamping. {rotationDeltaDegrees} to {-maxRotationDeltaDegrees} degrees", Debugger.DebugLevel.WARN);
                    _rotationDelta[i] = -maxRotationDeltaDegrees * (FixedMath.PI / new Fixed64(180.0));
                }
            }

            if (_positionDelta != Vector3d.Zero || _rotationDelta != Vector3d.Zero)
            {
                EmitSignal("OnChange");
            }
        }

        public void ApplyKeyframe(Vector3 position, Vector3 rotation)
        {
            _position = new Vector3d(position.X, position.Y, position.Z);
            _rotation = new Vector3d(rotation.X, rotation.Y, rotation.Z);
            _shouldSendKeyframe = true;
            EmitSignal("OnChange");
        }

        Vector3d _cumulativePositionDelta;
        Vector3d _cumulativeRotationDelta;
        private bool _shouldSendKeyframe = false;

        public void NetworkProcess(WorldRunner currentWorld)
        {
            if (!NetRunner.Instance.IsServer) return;

            // Determine if we need a keyframe
            _shouldSendKeyframe = false;

            // Regular interval check
            if (currentWorld.CurrentTick % KeyframeFrequency == _keyframeOffset)
            {
                _shouldSendKeyframe = true;
                EmitSignal("OnChange");
            }

            // Cumulative delta check
            if (!_shouldSendKeyframe)
            {
                for (int i = 0; i < AXIS_COUNT; i++)
                {
                    if (FixedMath.Abs(_cumulativePositionDelta[i]) > POSITION_KEYFRAME_THRESHOLD ||
                        FixedMath.Abs(_cumulativeRotationDelta[i]) > ROTATION_KEYFRAME_THRESHOLD)
                    {
                        Debugger.Instance.Log($"Cumulative position delta is too high. Sending keyframe. {_cumulativePositionDelta[i]} {_cumulativeRotationDelta[i]}", Debugger.DebugLevel.VERBOSE);
                        _shouldSendKeyframe = true;
                        EmitSignal("OnChange");
                        break;
                    }
                }
            }

            if (_shouldSendKeyframe)
            {
                // Keyframe: update _position to current actual position
                _position = _positionKeyframe;
                _rotation = _rotationKeyframe;
                _cumulativePositionDelta = Vector3d.Zero;
                _cumulativeRotationDelta = Vector3d.Zero;
                _positionDelta = Vector3d.Zero;
                _rotationDelta = Vector3d.Zero;
            }
            else
            {
                // Delta: Update position and rotation
                _position += _positionDelta;
                _rotation += _rotationDelta;
                _cumulativePositionDelta += _positionDelta;
                _cumulativeRotationDelta += _rotationDelta;
                _positionDelta = Vector3d.Zero;
                _rotationDelta = Vector3d.Zero;
            }
        }

        public static HLBuffer NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetPose3D obj)
        {
            var result = new HLBuffer();
            byte header = 0;

            // Use the flag set by NetworkProcess
            if (obj._shouldSendKeyframe || obj.Owner == peer)
            {
                header |= (byte)ChangeType.Keyframe;
                HLBytes.Pack(result, header);
                for (byte i = 0; i < AXIS_COUNT; i++)
                {
                    HLBytes.Pack(result, (int)(obj._position[i] * new Fixed64(100)));
                    HLBytes.Pack(result, (int)(obj._rotation[i] * new Fixed64(100)));

                }
                return result;
            }

            // Delta serialization
            var changeBuff = new HLBuffer();
            for (byte i = 0; i < AXIS_COUNT; i++)
            {
                if (obj._positionDelta[i] != Fixed64.Zero)
                {
                    header |= (byte)(1 << (i + CHANGE_HEADER_LENGTH));
                    var packedPos = (short)(obj._positionDelta[i] * POSITION_SCALE).FloorToInt();
                    HLBytes.Pack(changeBuff, packedPos);
                }

                if (obj._rotationDelta[i] != Fixed64.Zero)
                {
                    header |= (byte)(1 << (i + CHANGE_HEADER_LENGTH + AXIS_COUNT));
                    // Convert radians to degrees and pack with 2 decimal precision
                    var rotationDegrees = obj._rotationDelta[i] * (new Fixed64(180.0) / FixedMath.PI);
                    var packedRot = (short)(rotationDegrees * ROTATION_SCALE).FloorToInt();
                    HLBytes.Pack(changeBuff, packedRot);
                }
            }

            HLBytes.Pack(result, header);
            HLBytes.Pack(result, changeBuff);
            return result;
        }

        public static Variant GetDeserializeContext(NetPose3D obj)
        {
            Godot.Collections.Array result = [
                new Vector3(obj._position.x.ToPreciseFloat(), obj._position.y.ToPreciseFloat(), obj._position.z.ToPreciseFloat()),
                new Vector3(obj._rotation.x.ToPreciseFloat(), obj._rotation.y.ToPreciseFloat(), obj._rotation.z.ToPreciseFloat())
            ];
            return result;
        }

        public static NetPose3D NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, Variant ctx)
        {
            var header = HLBytes.UnpackByte(buffer);
            var positionDelta = new Vector3d();
            var rotationDelta = new Vector3d();
            var result = new NetPose3D();
            var position = ctx.As<Godot.Collections.Array>()[0].As<Vector3>();
            var rotation = ctx.As<Godot.Collections.Array>()[1].As<Vector3>();
            result._position = new Vector3d(new Fixed64(position.X), new Fixed64(position.Y), new Fixed64(position.Z));
            result._rotation = new Vector3d(new Fixed64(rotation.X), new Fixed64(rotation.Y), new Fixed64(rotation.Z));

            if ((header & (byte)ChangeType.Keyframe) != 0)
            {
                result.ClientState = ChangeType.Keyframe;
                for (byte i = 0; i < AXIS_COUNT; i++)
                {
                    result._position[i] = new Fixed64(HLBytes.UnpackInt32(buffer)) / new Fixed64(100);
                    result._rotation[i] = new Fixed64(HLBytes.UnpackInt32(buffer)) / new Fixed64(100);
                }
                return result;
            }

            // Delta deserialization
            result.ClientState = ChangeType.Delta;
            for (byte i = 0; i < AXIS_COUNT; i++)
            {
                if ((header & (1 << (i + CHANGE_HEADER_LENGTH))) != 0)
                {
                    var unpackedShort = HLBytes.UnpackInt16(buffer);
                    positionDelta[i] = new Fixed64(unpackedShort) / POSITION_SCALE;
                }
                if ((header & (1 << (i + CHANGE_HEADER_LENGTH + AXIS_COUNT))) != 0)
                {
                    var unpackedShort = HLBytes.UnpackInt16(buffer);
                    // Unpack degrees with 2 decimal precision and convert to radians
                    var rotationDegrees = new Fixed64(unpackedShort) / ROTATION_SCALE;
                    var rotationRadians = rotationDegrees * (FixedMath.PI / new Fixed64(180.0));
                    rotationDelta[i] = rotationRadians;
                }
            }

            result._position += positionDelta;
            result._rotation += rotationDelta;
            return result;
        }

        public static NetPose3D BsonDeserialize(Variant context, byte[] bson, NetPose3D initialObject)
        {
            var bsonValue = BsonTransformer.Instance.DeserializeBsonValue<BsonDocument>(bson);
            initialObject._position = new Vector3d(bsonValue["Position"].AsDouble, bsonValue["Position"].AsDouble, bsonValue["Position"].AsDouble);
            initialObject._rotation = new Vector3d(bsonValue["Rotation"].AsDouble, bsonValue["Rotation"].AsDouble, bsonValue["Rotation"].AsDouble);
            return initialObject;
        }

        public BsonValue BsonSerialize(Variant context)
        {
            var doc = new BsonDocument();
            doc["Position"] = new BsonArray { _position.x.ToFormattedFloat(), _position.y.ToFormattedFloat(), _position.z.ToFormattedFloat() };
            doc["Rotation"] = new BsonArray { _rotation.x.ToFormattedFloat(), _rotation.y.ToFormattedFloat(), _rotation.z.ToFormattedFloat() };
            return doc;
        }
    }
}