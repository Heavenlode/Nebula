using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FixedMathSharp;
using Godot;
using MongoDB.Bson;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula
{
    public partial class NetPose3D : RefCounted, INetSerializable<NetPose3D>, IBsonSerializable<NetPose3D>
    {
        const int CHANGE_HEADER_LENGTH = 2;
        const int AXIS_COUNT = 3;

        // Standard resolution: 10 bits per quaternion component (4 bytes total)
        // High resolution: 14 bits per quaternion component (6 bytes total)
        const int STANDARD_QUAT_BITS = 10;
        const int HIGH_RES_QUAT_BITS = 14;
        const int STANDARD_QUAT_MAX = (1 << STANDARD_QUAT_BITS) - 1;
        const int HIGH_RES_QUAT_MAX = (1 << HIGH_RES_QUAT_BITS) - 1;

        // Standard: 0.1 unit precision, High: 0.05 unit precision
        static readonly Fixed64 STANDARD_POSITION_SCALE = new Fixed64(10);
        static readonly Fixed64 HIGH_RES_POSITION_SCALE = new Fixed64(20);

        // Allow X ticks worth of maximum movement before keyframe
        static readonly Fixed64 POSITION_KEYFRAME_THRESHOLD = new Fixed64(3276.7 * 5);
        static readonly float ROTATION_KEYFRAME_THRESHOLD_DOT = 0.95f;

        Vector3d _position;
        Quaternion _rotation = Quaternion.Identity;
        Vector3d _positionDelta;
        Quaternion _rotationDelta = Quaternion.Identity;

        Vector3d _positionKeyframe;
        Quaternion _rotationKeyframe = Quaternion.Identity;

        public Vector3 Position => new Vector3(_position.x.ToFormattedFloat(), _position.y.ToFormattedFloat(), _position.z.ToFormattedFloat());
        public Vector3 Rotation => _rotation.GetEuler();
        public Quaternion RotationQuat => _rotation;

        public event Action OnChange;

        public enum ChangeType
        {
            Delta = 1 << 0,
            Keyframe = 1 << 1,
            HighResolution = 1 << 6,  // Flag for high-res quaternion encoding
        }

        public ChangeType ClientState { get; private set; } = ChangeType.Delta;
        public int KeyframeFrequency = NetRunner.TPS;
        private int _keyframeOffset = 0;

        /// <summary>
        /// The owner peer receives high resolution updates.
        /// </summary>
        public NetPeer Owner;

        public NetPose3D()
        {
            _keyframeOffset = (int)(GD.Randi() % KeyframeFrequency);
        }

        /// <summary>
        /// Applies a position and rotation change.
        /// </summary>
        public void ApplyDelta(Vector3 newPosition, Vector3 newRotation)
        {
            _positionKeyframe = new Vector3d(newPosition.X, newPosition.Y, newPosition.Z);
            _rotationKeyframe = Quaternion.FromEuler(newRotation).Normalized();
            _positionDelta += _positionKeyframe - _position;

            var currentRotation = _rotation.Normalized();
            _rotationDelta = (_rotationKeyframe * currentRotation.Inverse()).Normalized();

            // Use standard scale for clamping (server-side, resolution doesn't matter for clamping)
            var maxPositionDelta = new Fixed64(short.MaxValue) / STANDARD_POSITION_SCALE;
            for (byte i = 0; i < AXIS_COUNT; i++)
            {
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
            }

            bool hasChange = _positionDelta != Vector3d.Zero || !IsQuaternionIdentity(_rotationDelta);
            if (hasChange)
            {
                OnChange?.Invoke();
            }
        }

        /// <summary>
        /// Applies a network delta received from the server to this pose.
        /// Used on the client to accumulate deltas into the cached pose.
        /// </summary>
        public void ApplyNetworkDelta(NetPose3D delta)
        {
            _position += delta._position;
            _rotation = (delta._rotation * _rotation).Normalized();
        }

        public void ApplyKeyframe(Vector3 position, Vector3 rotation)
        {
            _position = new Vector3d(position.X, position.Y, position.Z);
            _rotation = Quaternion.FromEuler(rotation).Normalized();
            _shouldSendKeyframe = true;
            OnChange?.Invoke();
        }

        public void ApplyKeyframe(Vector3 position, Quaternion rotation)
        {
            _position = new Vector3d(position.X, position.Y, position.Z);
            _rotation = rotation.Normalized();
            _shouldSendKeyframe = true;
            OnChange?.Invoke();
        }

        Vector3d _cumulativePositionDelta;
        Quaternion _cumulativeRotationDelta = Quaternion.Identity;
        private bool _shouldSendKeyframe = false;

        private static bool IsQuaternionIdentity(Quaternion q, float threshold = 0.0001f)
        {
            return Mathf.Abs(q.W) > (1f - threshold) &&
                   Mathf.Abs(q.X) < threshold &&
                   Mathf.Abs(q.Y) < threshold &&
                   Mathf.Abs(q.Z) < threshold;
        }

        public void NetworkProcess(WorldRunner currentWorld)
        {
            if (!NetRunner.Instance.IsServer) return;

            _shouldSendKeyframe = false;

            if (currentWorld.CurrentTick % KeyframeFrequency == _keyframeOffset)
            {
                _shouldSendKeyframe = true;
                OnChange?.Invoke();
            }

            if (!_shouldSendKeyframe)
            {
                for (int i = 0; i < AXIS_COUNT; i++)
                {
                    if (FixedMath.Abs(_cumulativePositionDelta[i]) > POSITION_KEYFRAME_THRESHOLD)
                    {
                        Debugger.Instance.Log($"Cumulative position delta is too high. Sending keyframe. {_cumulativePositionDelta[i]}", Debugger.DebugLevel.VERBOSE);
                        _shouldSendKeyframe = true;
                        OnChange?.Invoke();
                        break;
                    }
                }

                if (!_shouldSendKeyframe && Mathf.Abs(_cumulativeRotationDelta.Dot(Quaternion.Identity)) < ROTATION_KEYFRAME_THRESHOLD_DOT)
                {
                    Debugger.Instance.Log($"Cumulative rotation delta is too high. Sending keyframe.", Debugger.DebugLevel.VERBOSE);
                    _shouldSendKeyframe = true;
                    OnChange?.Invoke();
                }
            }

            if (_shouldSendKeyframe)
            {
                _position = _positionKeyframe;
                _rotation = _rotationKeyframe.Normalized();
                _cumulativePositionDelta = Vector3d.Zero;
                _cumulativeRotationDelta = Quaternion.Identity;
                _positionDelta = Vector3d.Zero;
                _rotationDelta = Quaternion.Identity;
            }
            else
            {
                _position += _positionDelta;
                _rotation = (_rotationDelta * _rotation).Normalized();
                _cumulativePositionDelta += _positionDelta;
                _cumulativeRotationDelta = (_rotationDelta * _cumulativeRotationDelta).Normalized();
                _positionDelta = Vector3d.Zero;
                _rotationDelta = Quaternion.Identity;
            }
        }

        public Dictionary<NetPeer, Tick> LastKeyframeSent = [];

        #region Quaternion Smallest-Three Encoding

        /// <summary>
        /// Packs a quaternion using smallest-three encoding.
        /// </summary>
        private static void PackQuaternion(NetBuffer buffer, Quaternion q, bool highRes)
        {
            q = q.Normalized();
            int quatMax = highRes ? HIGH_RES_QUAT_MAX : STANDARD_QUAT_MAX;

            float[] components = { q.X, q.Y, q.Z, q.W };
            int largestIndex = 0;
            float largestValue = Mathf.Abs(components[0]);

            for (int i = 1; i < 4; i++)
            {
                float absVal = Mathf.Abs(components[i]);
                if (absVal > largestValue)
                {
                    largestValue = absVal;
                    largestIndex = i;
                }
            }

            if (components[largestIndex] < 0)
            {
                q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                components[0] = q.X;
                components[1] = q.Y;
                components[2] = q.Z;
                components[3] = q.W;
            }

            int[] smallestIndices = new int[3];
            int idx = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i != largestIndex)
                {
                    smallestIndices[idx++] = i;
                }
            }

            const float maxComponentValue = 0.70710678118f;

            uint[] quantized = new uint[3];
            for (int i = 0; i < 3; i++)
            {
                float normalized = (components[smallestIndices[i]] / maxComponentValue + 1f) * 0.5f;
                normalized = Mathf.Clamp(normalized, 0f, 1f);
                quantized[i] = (uint)(normalized * quatMax);
            }

            if (highRes)
            {
                // 14-bit: pack into 44 bits (6 bytes)
                ulong packed = ((ulong)largestIndex << 42) |
                               ((ulong)quantized[0] << 28) |
                               ((ulong)quantized[1] << 14) |
                               (ulong)quantized[2];

                NetWriter.WriteByte(buffer, (byte)(packed >> 40));
                NetWriter.WriteByte(buffer, (byte)(packed >> 32));
                NetWriter.WriteByte(buffer, (byte)(packed >> 24));
                NetWriter.WriteByte(buffer, (byte)(packed >> 16));
                NetWriter.WriteByte(buffer, (byte)(packed >> 8));
                NetWriter.WriteByte(buffer, (byte)(packed));
            }
            else
            {
                // 10-bit: pack into 32 bits (4 bytes)
                uint packed = ((uint)largestIndex << 30) |
                              (quantized[0] << 20) |
                              (quantized[1] << 10) |
                              quantized[2];
                NetWriter.WriteUInt32(buffer, packed);
            }
        }

        /// <summary>
        /// Unpacks a quaternion using smallest-three encoding.
        /// </summary>
        private static Quaternion UnpackQuaternion(NetBuffer buffer, bool highRes)
        {
            int largestIndex;
            uint q0, q1, q2;
            int quatMax = highRes ? HIGH_RES_QUAT_MAX : STANDARD_QUAT_MAX;

            if (highRes)
            {
                // Unpack 6 bytes
                byte b0 = NetReader.ReadByte(buffer);
                byte b1 = NetReader.ReadByte(buffer);
                byte b2 = NetReader.ReadByte(buffer);
                byte b3 = NetReader.ReadByte(buffer);
                byte b4 = NetReader.ReadByte(buffer);
                byte b5 = NetReader.ReadByte(buffer);

                ulong packed = ((ulong)b0 << 40) |
                               ((ulong)b1 << 32) |
                               ((ulong)b2 << 24) |
                               ((ulong)b3 << 16) |
                               ((ulong)b4 << 8) |
                               (ulong)b5;

                largestIndex = (int)(packed >> 42);
                q0 = (uint)((packed >> 28) & 0x3FFF);
                q1 = (uint)((packed >> 14) & 0x3FFF);
                q2 = (uint)(packed & 0x3FFF);
            }
            else
            {
                uint packed32 = NetReader.ReadUInt32(buffer);
                largestIndex = (int)(packed32 >> 30);
                q0 = (packed32 >> 20) & 0x3FF;
                q1 = (packed32 >> 10) & 0x3FF;
                q2 = packed32 & 0x3FF;
            }

            const float maxComponentValue = 0.70710678118f;

            float[] smallComponents = new float[3];
            smallComponents[0] = ((q0 / (float)quatMax) * 2f - 1f) * maxComponentValue;
            smallComponents[1] = ((q1 / (float)quatMax) * 2f - 1f) * maxComponentValue;
            smallComponents[2] = ((q2 / (float)quatMax) * 2f - 1f) * maxComponentValue;

            float sumSquares = smallComponents[0] * smallComponents[0] +
                               smallComponents[1] * smallComponents[1] +
                               smallComponents[2] * smallComponents[2];
            float largest = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSquares));

            float[] components = new float[4];
            int smallIdx = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largestIndex)
                {
                    components[i] = largest;
                }
                else
                {
                    components[i] = smallComponents[smallIdx++];
                }
            }

            return new Quaternion(components[0], components[1], components[2], components[3]).Normalized();
        }

        #endregion

        public static void NetworkSerialize(WorldRunner currentWorld, NetPeer peer, NetPose3D obj, NetBuffer buffer)
        {
            byte header = 0;
            bool isOwner = obj.Owner.ID == peer.ID;

            if (obj._shouldSendKeyframe || isOwner || !obj.LastKeyframeSent.ContainsKey(peer))
            {
                header |= (byte)ChangeType.Keyframe;
                if (isOwner)
                {
                    header |= (byte)ChangeType.HighResolution;
                }
                NetWriter.WriteByte(buffer, header);

                for (byte i = 0; i < AXIS_COUNT; i++)
                {
                    NetWriter.WriteInt32(buffer, (int)(obj._position[i] * new Fixed64(100)));
                }

                PackQuaternion(buffer, obj._rotation, isOwner);

                obj.LastKeyframeSent[peer] = currentWorld.CurrentTick;
                return;
            }

            // Delta - non-owners only (owners always get keyframes)
            using var changeBuff = new NetBuffer();
            var positionScale = STANDARD_POSITION_SCALE;

            for (byte i = 0; i < AXIS_COUNT; i++)
            {
                if (obj._positionDelta[i] != Fixed64.Zero)
                {
                    header |= (byte)(1 << (i + CHANGE_HEADER_LENGTH));
                    var packedPos = (short)(obj._positionDelta[i] * positionScale).FloorToInt();
                    NetWriter.WriteInt16(changeBuff, packedPos);
                }
            }

            if (!IsQuaternionIdentity(obj._rotationDelta))
            {
                header |= (byte)(1 << (CHANGE_HEADER_LENGTH + AXIS_COUNT));
                PackQuaternion(changeBuff, obj._rotationDelta, false); // Deltas always standard res
            }

            NetWriter.WriteByte(buffer, header);
            NetWriter.WriteBytes(buffer, changeBuff.WrittenSpan);
        }

        public static NetPose3D NetworkDeserialize(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, NetPose3D existing = null)
        {
            var header = NetReader.ReadByte(buffer);

            if ((header & (byte)ChangeType.Keyframe) != 0)
            {
                // Keyframe: use existing instance or create new
                var result = existing ?? new NetPose3D();
                result.ClientState = ChangeType.Keyframe;

                for (byte i = 0; i < AXIS_COUNT; i++)
                {
                    result._position[i] = new Fixed64(NetReader.ReadInt32(buffer)) / new Fixed64(100);
                }

                // Read high resolution quaternion if the flag is set
                bool highRes = (header & (byte)ChangeType.HighResolution) != 0;
                result._rotation = UnpackQuaternion(buffer, highRes);
                return result;
            }

            // Delta encoding
            var positionDelta = new Vector3d();
            var positionScale = STANDARD_POSITION_SCALE;

            for (byte i = 0; i < AXIS_COUNT; i++)
            {
                if ((header & (1 << (i + CHANGE_HEADER_LENGTH))) != 0)
                {
                    var unpackedShort = NetReader.ReadInt16(buffer);
                    positionDelta[i] = new Fixed64(unpackedShort) / positionScale;
                }
            }

            Quaternion rotationDelta = Quaternion.Identity;
            if ((header & (1 << (CHANGE_HEADER_LENGTH + AXIS_COUNT))) != 0)
            {
                rotationDelta = UnpackQuaternion(buffer, false);
            }

            if (existing != null)
            {
                // Apply delta to existing instance
                existing._position += positionDelta;
                existing._rotation = (rotationDelta * existing._rotation).Normalized();
                existing.ClientState = ChangeType.Delta;
                return existing;
            }
            else
            {
                // No existing instance - create new with delta values
                var result = new NetPose3D();
                result.ClientState = ChangeType.Delta;
                result._position = positionDelta;
                result._rotation = rotationDelta;
                return result;
            }
        }

        public async Task OnBsonDeserialize(NetBsonContext context, BsonDocument doc)
        {
            await Task.CompletedTask;
        }

        public async Task<NetPose3D> BsonDeserialize(NetBsonContext context, byte[] bson)
        {
            return await BsonDeserialize(context, bson, this);
        }

        public static async Task<NetPose3D> BsonDeserialize(NetBsonContext context, byte[] bson, NetPose3D initialObject)
        {
            var bsonValue = BsonTransformer.Instance.DeserializeBsonValue<BsonDocument>(bson);
            var result = initialObject ?? new NetPose3D();
            var position = bsonValue["Position"].AsBsonArray;

            result._position = new Vector3d(position[0].AsDouble, position[1].AsDouble, position[2].AsDouble);

            if (bsonValue.Contains("RotationQuat"))
            {
                var rotQuat = bsonValue["RotationQuat"].AsBsonArray;
                result._rotation = new Quaternion(
                    (float)rotQuat[0].AsDouble,
                    (float)rotQuat[1].AsDouble,
                    (float)rotQuat[2].AsDouble,
                    (float)rotQuat[3].AsDouble
                ).Normalized();
            }
            else if (bsonValue.Contains("Rotation"))
            {
                var rotation = bsonValue["Rotation"].AsBsonArray;
                result._rotation = Quaternion.FromEuler(new Vector3(
                    (float)rotation[0].AsDouble,
                    (float)rotation[1].AsDouble,
                    (float)rotation[2].AsDouble
                )).Normalized();
            }

            await result.OnBsonDeserialize(context, bsonValue);
            return result;
        }

        public BsonValue BsonSerialize(NetBsonContext context)
        {
            var doc = new BsonDocument();
            doc["Position"] = new BsonArray { _position.x.ToFormattedFloat(), _position.y.ToFormattedFloat(), _position.z.ToFormattedFloat() };
            doc["RotationQuat"] = new BsonArray { _rotation.X, _rotation.Y, _rotation.Z, _rotation.W };
            return doc;
        }
    }
}
