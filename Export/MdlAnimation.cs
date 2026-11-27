using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Rbx2Source.Export
{
    // Reads one frame of a compiled model's (.mdl v44-49) sequence: each bone's local position and rotation, from the
    // model's own animation data or the .ani file its animation blocks are in.
    internal static class MdlAnimation
    {
        private const int SequenceSize = 212, AnimationSize = 100, BoneSize = 216;
        private const int RawPosition = 0x01, RawRotation = 0x02, AnimatedPosition = 0x04, AnimatedRotation = 0x08, RawRotation64 = 0x20;
        private const int DeltaAnimation = 0x04, FrameAnimation = 0x40;

        private static int Int(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
        private static short Short(byte[] data, int offset) => BitConverter.ToInt16(data, offset);

        private static string Text(byte[] data, int offset)
        {
            int end = Array.IndexOf(data, (byte)0, offset);
            return Encoding.ASCII.GetString(data, offset, (end < 0 ? data.Length : end) - offset);
        }

        private static double[] Vector(byte[] data, int offset) =>
            new double[] { BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8) };

        // The sequences' names, in order.
        public static List<string> Sequences(byte[] data)
        {
            int count = Int(data, 188), index = Int(data, 192);
            return Enumerable.Range(0, count).Select(i => index + SequenceSize * i).Select(o => Text(data, o + Int(data, o + 4))).ToList();
        }

        // The model's bones (by name and parent) posed at a point of a sequence (cycle 0-1), or null when it has no such
        // sequence. A sequence that blends several animations (aiming) takes the middle one. read loads the model's .ani file.
        public static List<ValvePlayer.Bone> Pose(byte[] data, string sequence, Func<string, byte[]> read, double cycle = 0)
        {
            List<string> names = Sequences(data);
            int found = names.FindIndex(n => n.Equals(sequence, StringComparison.OrdinalIgnoreCase));
            if (found < 0)
                return null;
            int seq = Int(data, 192) + SequenceSize * found;
            int across = Math.Max(1, Int(data, seq + 68)), down = Math.Max(1, Int(data, seq + 72));
            int animation = Short(data, seq + Int(data, seq + 60) + 2 * ((down / 2) * across + across / 2));
            int desc = Int(data, 184) + AnimationSize * animation;
            int frames = Math.Max(1, Int(data, desc + 16));
            bool delta = (Int(data, desc + 12) & DeltaAnimation) != 0;
            int frame = (int)Math.Round(Math.Max(0, Math.Min(1, cycle)) * (frames - 1));
            int block = Int(data, desc + 52), offset = Int(data, desc + 56);
            int sectionFrames = Int(data, desc + 84);
            if (sectionFrames > 0)
            {
                int section = frame / sectionFrames;
                frame -= section * sectionFrames;
                int entry = desc + Int(data, desc + 80) + 8 * section;
                block = Int(data, entry);
                offset = Int(data, entry + 4);
            }
            byte[] source = data;
            int start = desc + offset;
            if (block > 0)
            {
                string aniName = Text(data, Int(data, 348));
                source = read(aniName) ?? throw new FileNotFoundException("The model's animation file was not found.", aniName);
                start = Int(data, Int(data, 356) + 8 * block) + offset;
            }

            List<ValvePlayer.Bone> bones = Mdl.Bones(data).ToList();
            if ((Int(data, desc + 12) & FrameAnimation) != 0)
            {
                FramePose(source, start, frame, bones, delta);
                return bones;
            }
            int boneIndex = Int(data, 160);
            for (int p = start; p >= 0 && p + 4 <= source.Length;)
            {
                int bone = source[p], flags = source[p + 1], next = Short(source, p + 2);
                if (bone < bones.Count)
                {
                    int b = boneIndex + BoneSize * bone, d = p + 4;
                    ValvePlayer.Bone target = bones[bone];
                    if ((flags & RawRotation) != 0)
                        target.LocalRotation = Quaternion48(source, d);
                    else if ((flags & RawRotation64) != 0)
                        target.LocalRotation = Quaternion64(source, d);
                    else if ((flags & AnimatedRotation) != 0)
                    {
                        double[] rest = delta ? new double[3] : Vector(data, b + 60), scale = Vector(data, b + 84);
                        var euler = Enumerable.Range(0, 3).Select(j => Value(source, d, j, frame) * scale[j] + rest[j]).ToArray();
                        target.LocalRotation = Matrix.FromEuler(euler[0], euler[1], euler[2]);
                    }
                    else if (delta)
                        target.LocalRotation = Matrix.Identity;

                    if ((flags & RawPosition) != 0)
                    {
                        int v = d + ((flags & RawRotation) != 0 ? 6 : 0) + ((flags & RawRotation64) != 0 ? 8 : 0);
                        target.LocalPosition = Enumerable.Range(0, 3).Select(j => (double)BitConverter.Int16BitsToHalf(Short(source, v + 2 * j))).ToArray();
                    }
                    else if ((flags & AnimatedPosition) != 0)
                    {
                        int v = d + ((flags & AnimatedRotation) != 0 ? 6 : 0);
                        double[] rest = delta ? new double[3] : Vector(data, b + 32), scale = Vector(data, b + 72);
                        target.LocalPosition = Enumerable.Range(0, 3).Select(j => Value(source, v, j, frame) * scale[j] + rest[j]).ToArray();
                    }
                    else if (delta)
                        target.LocalPosition = new double[3];
                }
                if (next <= 0)
                    break;
                p += next;
            }
            return bones;
        }

        // The newer (Left 4 Dead+) uncompressed layout: a header (constants offset, frames offset, bytes per frame), one
        // flags byte per bone, then each bone's constant values and, per frame, each bone's animated ones - rotation first.
        private static void FramePose(byte[] source, int start, int frame, List<ValvePlayer.Bone> bones, bool delta)
        {
            int constants = start + Int(source, start), frames = start + Int(source, start + 4) + frame * Int(source, start + 8);
            for (int i = 0; i < bones.Count; i++)
            {
                int flags = source[start + 24 + i];
                ValvePlayer.Bone bone = bones[i];
                if ((flags & 0x02) != 0) { bone.LocalRotation = Quaternion48(source, constants); constants += 6; }
                else if ((flags & 0x40) != 0) { bone.LocalRotation = Quaternion48S(source, constants); constants += 6; }
                else if ((flags & 0x08) != 0) { bone.LocalRotation = Quaternion48(source, frames); frames += 6; }
                else if ((flags & 0x80) != 0) { bone.LocalRotation = Quaternion48S(source, frames); frames += 6; }
                else if (delta) bone.LocalRotation = Matrix.Identity;

                if ((flags & 0x01) != 0) { bone.LocalPosition = Vector48(source, constants); constants += 6; }
                else if ((flags & 0x20) != 0) { bone.LocalPosition = Vector(source, constants); constants += 12; }
                else if ((flags & 0x04) != 0) { bone.LocalPosition = Vector48(source, frames); frames += 6; }
                else if ((flags & 0x10) != 0) { bone.LocalPosition = Vector(source, frames); frames += 12; }
                else if (delta) bone.LocalPosition = new double[3];
            }
        }

        private static double[] Vector48(byte[] data, int offset) =>
            Enumerable.Range(0, 3).Select(j => (double)BitConverter.Int16BitsToHalf(Short(data, offset + 2 * j))).ToArray();

        // "Smallest three": the top bits of the first two words say which component was dropped (the largest), the
        // third's its sign; the other three follow it in order (wrapping), each 15 bits over ±1/√2.
        private static double[] Quaternion48S(byte[] data, int offset)
        {
            int a = BitConverter.ToUInt16(data, offset), b = BitConverter.ToUInt16(data, offset + 2), c = BitConverter.ToUInt16(data, offset + 4);
            int missing = ((((a >> 15) << 1) | (b >> 15)) + 3) & 3;
            var q = new double[4];
            double sum = 0;
            int[] stored = { a, b, c };
            for (int k = 0; k < 3; k++)
            {
                double v = ((stored[k] & 0x7FFF) - 16384) / 23170.475;
                q[(missing + 1 + k) & 3] = v;
                sum += v * v;
            }
            q[missing] = Math.Sqrt(Math.Max(0, 1 - sum)) * ((c & 0x8000) != 0 ? -1 : 1);
            return Matrix.FromQuaternion(q[0], q[1], q[2], q[3]);
        }

        // A compressed animation channel's value at a frame: runs of (valid, total) headers, each followed by its valid
        // values; frames past a run's valid ones repeat its last.
        private static double Value(byte[] data, int pointer, int channel, int frame)
        {
            int offset = Short(data, pointer + 2 * channel);
            if (offset == 0)
                return 0;
            int v = pointer + offset;
            int k = frame;
            for (int guard = 0; guard < 100000; guard++)
            {
                int valid = data[v], total = data[v + 1];
                if (total == 0)
                    return 0;
                if (total > k)
                    return Short(data, v + 2 * (valid > k ? k + 1 : valid));
                k -= total;
                v += 2 * (valid + 1);
            }
            return 0;
        }

        private static double[] Quaternion48(byte[] data, int offset)
        {
            int x = BitConverter.ToUInt16(data, offset), y = BitConverter.ToUInt16(data, offset + 2), zw = BitConverter.ToUInt16(data, offset + 4);
            double qx = (x - 32768) / 32768.0, qy = (y - 32768) / 32768.0, qz = ((zw & 0x7FFF) - 16384) / 16384.0;
            double qw = Math.Sqrt(Math.Max(0, 1 - qx * qx - qy * qy - qz * qz));
            return Matrix.FromQuaternion(qx, qy, qz, (zw & 0x8000) != 0 ? -qw : qw);
        }

        private static double[] Quaternion64(byte[] data, int offset)
        {
            ulong v = BitConverter.ToUInt64(data, offset);
            double qx = ((long)(v & 0x1FFFFF) - 1048576) / 1048576.5, qy = ((long)((v >> 21) & 0x1FFFFF) - 1048576) / 1048576.5,
                qz = ((long)((v >> 42) & 0x1FFFFF) - 1048576) / 1048576.5;
            double qw = Math.Sqrt(Math.Max(0, 1 - qx * qx - qy * qy - qz * qz));
            return Matrix.FromQuaternion(qx, qy, qz, (v >> 63) != 0 ? -qw : qw);
        }
    }
}
