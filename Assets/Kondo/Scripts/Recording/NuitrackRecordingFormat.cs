using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kondo.Recording
{
    /// <summary>
    /// Binary format shared by <see cref="NuitrackRecorder"/> (writer) and
    /// <see cref="NuitrackRecordingPlayer"/> (reader): everything Nuitrack contributes to
    /// driving the experience — user skeletons + IDs, the floor plane, and the calibrated
    /// sensor pose — as a stream of timestamped records.
    ///
    /// Layout (little-endian): a 20-byte header (magic "KNUIREC\0", int32 version,
    /// int64 UTC start ticks), then records until EOF. Each record is a byte tag +
    /// double time (seconds since recording start), followed by a tag-specific payload:
    ///   SkeletonFrame — byte userCount, per user: int32 id, byte jointCount, per joint in
    ///     nuitrack.JointType enum order: float confidence, float realX/Y/Z (raw millimeters).
    ///   Floor — plane normal xyz + point xyz (floats, meters, sensor space).
    ///   CalibrationPose — position xyz, rotation quaternion xyzw (floats), byte source.
    /// Only Confidence and Real are recorded per joint — they are the only joint fields the
    /// pointing pipeline consumes (Position = Real*0.001, IsGoodDepth = Real.Z &gt; 0).
    /// </summary>
    public static class NuitrackRecordingFormat
    {
        public const int Version = 1;

        /// <summary>Number of nuitrack.JointType values; joints are stored in enum order (None..RightFoot).</summary>
        public const int JointCount = 25;

        public const byte TagSkeletonFrame = 1;
        public const byte TagFloor = 2;
        public const byte TagCalibrationPose = 3;

        public const string DirectoryName = "NuitrackRecordings";
        public const string Extension = ".knrec";

        static readonly byte[] Magic = { (byte)'K', (byte)'N', (byte)'U', (byte)'I', (byte)'R', (byte)'E', (byte)'C', 0 };

        public static string DefaultDirectory => Path.Combine(Application.persistentDataPath, DirectoryName);

        /// <summary>Absolute paths pass through; anything else resolves against the default recordings directory.</summary>
        public static string ResolvePath(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName))
                return null;
            return Path.IsPathRooted(pathOrName) ? pathOrName : Path.Combine(DefaultDirectory, pathOrName);
        }

        // ---- Writing ----

        public static void WriteHeader(BinaryWriter w)
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(DateTime.UtcNow.Ticks);
        }

        public static void BeginSkeletonFrame(BinaryWriter w, double time, int userCount)
        {
            w.Write(TagSkeletonFrame);
            w.Write(time);
            w.Write((byte)userCount);
        }

        /// <summary>One user of a skeleton frame: arrays must hold <see cref="JointCount"/> entries in enum order.</summary>
        public static void WriteSkeletonFrameUser(BinaryWriter w, int id, float[] confidences, Vector3[] realMm)
        {
            w.Write(id);
            w.Write((byte)JointCount);
            for (int i = 0; i < JointCount; i++)
            {
                w.Write(confidences[i]);
                w.Write(realMm[i].x);
                w.Write(realMm[i].y);
                w.Write(realMm[i].z);
            }
        }

        public static void WriteFloor(BinaryWriter w, double time, Vector3 normal, Vector3 point)
        {
            w.Write(TagFloor);
            w.Write(time);
            w.Write(normal.x); w.Write(normal.y); w.Write(normal.z);
            w.Write(point.x); w.Write(point.y); w.Write(point.z);
        }

        public static void WriteCalibrationPose(BinaryWriter w, double time, Vector3 position, Quaternion rotation, byte source)
        {
            w.Write(TagCalibrationPose);
            w.Write(time);
            w.Write(position.x); w.Write(position.y); w.Write(position.z);
            w.Write(rotation.x); w.Write(rotation.y); w.Write(rotation.z); w.Write(rotation.w);
            w.Write(source);
        }

        // ---- Reconstruction ----

        /// <summary>
        /// Build a raw nuitrack skeleton from recorded joint data. The Joints array covers every
        /// JointType in enum order with Type set on each slot, so GetJoint works whether the
        /// native wrapper indexes by (int)type or searches by Type.
        /// </summary>
        public static nuitrack.Skeleton BuildSkeleton(int id, float[] confidences, Vector3[] realMm)
        {
            var joints = new nuitrack.Joint[JointCount];
            for (int i = 0; i < JointCount; i++)
            {
                joints[i] = new nuitrack.Joint
                {
                    Type = (nuitrack.JointType)i,
                    Confidence = confidences[i],
                    Real = new nuitrack.Vector3(realMm[i].x, realMm[i].y, realMm[i].z),
                };
            }
            return new nuitrack.Skeleton(id, joints);
        }

        // ---- Reading ----

        public enum RecordKind : byte
        {
            SkeletonFrame = TagSkeletonFrame,
            Floor = TagFloor,
            CalibrationPose = TagCalibrationPose,
        }

        public class RecordedUser
        {
            public int Id;
            public float[] Confidences;   // JointCount entries, enum order
            public Vector3[] RealMm;      // JointCount entries, enum order
        }

        /// <summary>One parsed record; only the fields for its <see cref="Kind"/> are meaningful.</summary>
        public class Record
        {
            public RecordKind Kind;
            public double Time;
            public RecordedUser[] Users;        // SkeletonFrame
            public Vector3 FloorNormal;         // Floor
            public Vector3 FloorPoint;
            public Vector3 PosePosition;        // CalibrationPose
            public Quaternion PoseRotation;
            public byte PoseSource;
        }

        public class RecordingData
        {
            public List<Record> Records = new List<Record>();
            public DateTime StartUtc;
            public double Duration;             // time of the last record
            public bool Truncated;              // file ended mid-record (crash mid-write); parsed prefix kept
            public bool HasCalibrationPose;
        }

        /// <summary>
        /// Parse an entire recording. Throws on a missing file, bad magic, or unsupported
        /// version (with a cause-specific message); a truncated final record is tolerated
        /// and flagged via <see cref="RecordingData.Truncated"/>.
        /// </summary>
        public static RecordingData Load(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var r = new BinaryReader(stream);

            byte[] magic = r.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length)
                throw new IOException($"File too short to be a Nuitrack recording: {path}");
            for (int i = 0; i < Magic.Length; i++)
                if (magic[i] != Magic[i])
                    throw new IOException($"Not a Nuitrack recording (bad magic): {path}");

            int version = r.ReadInt32();
            if (version != Version)
                throw new IOException($"Unsupported Nuitrack recording version {version} (expected {Version}): {path}");

            var data = new RecordingData { StartUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc) };

            try
            {
                while (stream.Position < stream.Length)
                {
                    byte tag = r.ReadByte();
                    double time = r.ReadDouble();
                    var record = new Record { Time = time };
                    switch (tag)
                    {
                        case TagSkeletonFrame:
                            record.Kind = RecordKind.SkeletonFrame;
                            int userCount = r.ReadByte();
                            record.Users = new RecordedUser[userCount];
                            for (int u = 0; u < userCount; u++)
                            {
                                var user = new RecordedUser
                                {
                                    Id = r.ReadInt32(),
                                    Confidences = new float[JointCount],
                                    RealMm = new Vector3[JointCount],
                                };
                                int jointCount = r.ReadByte();
                                for (int j = 0; j < jointCount; j++)
                                {
                                    float confidence = r.ReadSingle();
                                    var real = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                    if (j < JointCount)
                                    {
                                        user.Confidences[j] = confidence;
                                        user.RealMm[j] = real;
                                    }
                                }
                                record.Users[u] = user;
                            }
                            break;

                        case TagFloor:
                            record.Kind = RecordKind.Floor;
                            record.FloorNormal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            record.FloorPoint = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            break;

                        case TagCalibrationPose:
                            record.Kind = RecordKind.CalibrationPose;
                            record.PosePosition = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            record.PoseRotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            record.PoseSource = r.ReadByte();
                            data.HasCalibrationPose = true;
                            break;

                        default:
                            throw new IOException($"Unknown record tag {tag} at byte {stream.Position} — file corrupt: {path}");
                    }
                    data.Records.Add(record);
                    data.Duration = time;
                }
            }
            catch (EndOfStreamException)
            {
                // A crash mid-write leaves a partial final record; keep the parsed prefix.
                data.Truncated = true;
            }

            return data;
        }
    }
}
