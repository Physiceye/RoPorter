using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rbx2Source.Export
{
    // Reads an avatar's Mood: a Mood asset holds an Animation whose AnimationId points to a KeyframeSequence. Its
    // FaceControls NumberPoses name FACS controls (JawDrop, LeftLipCornerPuller, ...) and hold their values.
    public static class MoodAnimation
    {
        private static readonly byte[] BinaryMagic = Encoding.ASCII.GetBytes("<roblox!");

        public static long? ReadAnimationId(byte[] data)
        {
            Model model = Read(data);
            return model.Instances.Where(i => i.Class == "Animation")
                .Select(i => i.Strings.TryGetValue("AnimationId", out string value) ? RobloxModelFile.ParseId(value) : null)
                .FirstOrDefault(id => id.HasValue);
        }

        // FACS control values at the first keyframe. Later keyframes (moods can animate, for example to blink) are ignored.
        public static Dictionary<string, float> ReadFacePose(byte[] data)
        {
            Model model = Read(data);
            var byReferent = model.Instances.ToDictionary(i => i.Referent);
            double TimeOf(Instance pose)
            {
                for (Instance current = pose; current != null; current = current.Parent >= 0 && byReferent.TryGetValue(current.Parent, out Instance p) ? p : null)
                    if (current.Class == "Keyframe")
                        return current.Numbers.TryGetValue("Time", out double time) ? time : 0;
                return 0;
            }
            var poses = model.Instances.Where(i => i.Class == "NumberPose" && i.Strings.ContainsKey("Name") && i.Numbers.ContainsKey("Value")).ToList();
            if (poses.Count == 0)
                return new Dictionary<string, float>(StringComparer.Ordinal);
            double first = poses.Min(TimeOf);
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (Instance pose in poses.Where(p => TimeOf(p) == first))
                result[pose.Strings["Name"]] = (float)pose.Numbers["Value"];
            return result;
        }

        private sealed class Instance
        {
            public int Referent, Parent = -1;
            public string Class;
            public readonly Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> Numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        private sealed class Model
        {
            public readonly List<Instance> Instances = new List<Instance>();
        }

        private static Model Read(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("The mood asset is empty.");
            return data.Take(8).SequenceEqual(BinaryMagic) ? ReadBinary(data) : ReadXml(data);
        }

        private static Model ReadXml(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            if (!text.TrimStart().StartsWith("<roblox", StringComparison.Ordinal))
                throw new InvalidDataException("The mood asset is not a Roblox model.");
            var model = new Model();
            var stack = new Stack<Instance>();
            int referent = 0;
            foreach (Match tag in Regex.Matches(text, @"<Item\s+class=""(\w+)""[^>]*>|</Item>|<(string|Content|ContentId|float|double)\s+name=""(\w+)""[^>]*>\s*(?:<url>)?([^<]*)"))
            {
                if (tag.Value.StartsWith("<Item", StringComparison.Ordinal))
                {
                    var instance = new Instance { Referent = referent++, Class = tag.Groups[1].Value, Parent = stack.Count > 0 ? stack.Peek().Referent : -1 };
                    model.Instances.Add(instance);
                    stack.Push(instance);
                }
                else if (tag.Value == "</Item>")
                {
                    if (stack.Count > 0)
                        stack.Pop();
                }
                else if (stack.Count > 0)
                {
                    string type = tag.Groups[2].Value, name = tag.Groups[3].Value, value = tag.Groups[4].Value.Trim();
                    if ((type == "float" || type == "double") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                        stack.Peek().Numbers[name] = number;
                    else if (type != "float" && type != "double")
                        stack.Peek().Strings[name] = System.Net.WebUtility.HtmlDecode(value);
                }
            }
            return model;
        }

        private static Model ReadBinary(byte[] data)
        {
            var model = new Model();
            var classes = new Dictionary<int, string>();
            var classInstances = new Dictionary<int, List<Instance>>();
            var byReferent = new Dictionary<int, Instance>();
            int position = 32;
            while (position + 16 <= data.Length)
            {
                string name = Encoding.ASCII.GetString(data, position, 4);
                int compressed = BitConverter.ToInt32(data, position + 4), size = BitConverter.ToInt32(data, position + 8);
                position += 16;
                int stored = compressed > 0 ? compressed : size;
                if (stored < 0 || size < 0 || stored > data.Length - position)
                    throw new InvalidDataException("The mood asset is truncated.");
                byte[] chunk;
                if (compressed == 0)
                    chunk = data.Skip(position).Take(size).ToArray();
                else if (stored >= 4 && BitConverter.ToUInt32(data, position) == 0xFD2FB528)
                    throw new NotSupportedException("The mood asset uses Zstandard compression, which this exporter cannot read yet.");
                else
                    chunk = RobloxModelFile.DecodeLz4(data, position, compressed, size);
                position += stored;
                var reader = new BinaryReader(new MemoryStream(chunk));
                if (name == "INST")
                {
                    int classIndex = reader.ReadInt32();
                    string className = ReadString(reader);
                    reader.ReadByte();
                    int count = reader.ReadInt32();
                    int[] referents = Referents(reader, count);
                    classes[classIndex] = className;
                    classInstances[classIndex] = referents.Select(r =>
                    {
                        var instance = new Instance { Referent = r, Class = className };
                        model.Instances.Add(instance);
                        byReferent[r] = instance;
                        return instance;
                    }).ToList();
                }
                else if (name == "PROP")
                {
                    int classIndex = reader.ReadInt32();
                    string property = ReadString(reader);
                    if (!classInstances.TryGetValue(classIndex, out List<Instance> instances))
                        continue;
                    byte type = reader.ReadByte();
                    int count = instances.Count;
                    if (type == 0x01)
                    {
                        for (int i = 0; i < count; i++)
                            instances[i].Strings[property] = ReadString(reader);
                    }
                    else if (type == 0x04)
                    {
                        // Float32 values are byte-interleaved, big-endian, with the sign bit rotated to the end.
                        uint[] raw = Interleaved(reader, count);
                        for (int i = 0; i < count; i++)
                        {
                            uint bits = (raw[i] >> 1) | (raw[i] << 31);
                            instances[i].Numbers[property] = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                        }
                    }
                    else if (type == 0x05)
                    {
                        for (int i = 0; i < count; i++)
                            instances[i].Numbers[property] = reader.ReadDouble();
                    }
                }
                else if (name == "PRNT")
                {
                    reader.ReadByte();
                    int count = reader.ReadInt32();
                    int[] children = Referents(reader, count), parents = Referents(reader, count);
                    for (int i = 0; i < count; i++)
                        if (byReferent.TryGetValue(children[i], out Instance child))
                            child.Parent = parents[i];
                }
                else if (name == "END\0")
                {
                    break;
                }
            }
            return model;
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("The mood asset has an invalid string.");
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static uint[] Interleaved(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count * 4);
            if (bytes.Length < count * 4)
                throw new InvalidDataException("The mood asset is truncated.");
            var values = new uint[count];
            for (int i = 0; i < count; i++)
                values[i] = (uint)(bytes[i] << 24 | bytes[count + i] << 16 | bytes[2 * count + i] << 8 | bytes[3 * count + i]);
            return values;
        }

        // Referents are interleaved, zigzag-encoded, and stored as differences from the previous one.
        private static int[] Referents(BinaryReader reader, int count)
        {
            uint[] raw = Interleaved(reader, count);
            var result = new int[count];
            int last = 0;
            for (int i = 0; i < count; i++)
            {
                int value = (int)(raw[i] >> 1) ^ -(int)(raw[i] & 1);
                last += value;
                result[i] = last;
            }
            return result;
        }
    }
}
