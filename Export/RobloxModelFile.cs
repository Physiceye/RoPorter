using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rbx2Source.Export
{
    // Reads just enough of a Roblox model file (.rbxm / .rbxmx) to find head meshes and accessory attachments.
    public static class RobloxModelFile
    {
        private static readonly byte[] BinaryMagic = Encoding.ASCII.GetBytes("<roblox!");

        private static readonly string[] MeshProperties = { "MeshId", "MeshContent" };
        private static readonly string[] TextureProperties = { "TextureID", "TextureId", "TextureContent", "ColorMap", "ColorMapContent" };

        public static long? FindHeadMeshId(byte[] data) => FindHeadAssets(data).Key;

        // Returns the head's mesh asset and its colour texture (SurfaceAppearance, MeshPart or SpecialMesh).
        public static KeyValuePair<long?, long?> FindHeadAssets(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("The head asset is empty.");
            var values = data.Take(8).SequenceEqual(BinaryMagic) ? ReadBinary(data) : ReadXml(data);
            long? mesh = values.Where(v => MeshProperties.Contains(v.Property))
                .OrderBy(v => v.Class == "MeshPart" ? 0 : 1).Select(v => ParseId(v.Value)).FirstOrDefault(id => id.HasValue);
            long? texture = values.Where(v => TextureProperties.Contains(v.Property))
                .OrderBy(v => v.Class == "SurfaceAppearance" ? 0 : v.Class == "MeshPart" ? 1 : 2)
                .Select(v => ParseId(v.Value)).FirstOrDefault(id => id.HasValue);
            return new KeyValuePair<long?, long?>(mesh, texture);
        }

        private sealed class PropertyValue
        {
            public string Class, Property, Value;
        }

        private const string ClassMarker = "__class__";

        public sealed class AccessoryModel
        {
            public bool HasWrapLayer;
            public List<string> Attachments = new List<string>();
            public long? MeshId;
            public long? TextureId;
        }

        // Describes an accessory: its attachment names (which decide the body part it is welded to), whether it
        // is layered clothing (it contains a WrapLayer), and its mesh.
        public static AccessoryModel ReadAccessory(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("The accessory asset is empty.");
            var values = data.Take(8).SequenceEqual(BinaryMagic) ? ReadBinary(data) : ReadXml(data);
            return new AccessoryModel
            {
                HasWrapLayer = values.Any(v => v.Property == ClassMarker && v.Class == "WrapLayer"),
                Attachments = values.Where(v => v.Class == "Attachment" && v.Property == "Name").Select(v => v.Value).ToList(),
                MeshId = values.Where(v => MeshProperties.Contains(v.Property))
                    .OrderBy(v => v.Class == "MeshPart" ? 0 : 1).Select(v => ParseId(v.Value)).FirstOrDefault(id => id.HasValue),
                TextureId = values.Where(v => TextureProperties.Contains(v.Property))
                    .OrderBy(v => v.Class == "SurfaceAppearance" ? 0 : v.Class == "MeshPart" ? 1 : 2)
                    .Select(v => ParseId(v.Value)).FirstOrDefault(id => id.HasValue)
            };
        }

        private static bool Wanted(string className, string property) =>
            MeshProperties.Contains(property) || TextureProperties.Contains(property) || (className == "Attachment" && property == "Name");

        public static long? ParseId(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;
            Match match = Regex.Match(content, @"(?:rbxassetid://|[?&]id=)(\d+)", RegexOptions.IgnoreCase);
            string digits = match.Success ? match.Groups[1].Value : Regex.IsMatch(content.Trim(), @"^\d+$") ? content.Trim() : null;
            return digits != null && long.TryParse(digits, out long id) && id > 0 ? id : (long?)null;
        }

        private static List<PropertyValue> ReadXml(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            if (!text.TrimStart().StartsWith("<roblox", StringComparison.Ordinal))
                throw new InvalidDataException("The asset is not a Roblox model.");
            var result = new List<PropertyValue>();
            foreach (Match item in Regex.Matches(text, @"<Item\s+class=""(\w+)""[^>]*>(.*?)</Properties>", RegexOptions.Singleline))
            {
                result.Add(new PropertyValue { Class = item.Groups[1].Value, Property = ClassMarker });
                foreach (Match property in Regex.Matches(item.Groups[2].Value,
                    @"<(?:Content|string|ContentId)\s+name=""(\w+)""[^>]*>\s*(?:<url>)?([^<]*)", RegexOptions.Singleline))
                    if (Wanted(item.Groups[1].Value, property.Groups[1].Value))
                        result.Add(new PropertyValue { Class = item.Groups[1].Value, Property = property.Groups[1].Value, Value = property.Groups[2].Value.Trim() });
            }
            return result;
        }

        private static List<PropertyValue> ReadBinary(byte[] data)
        {
            var result = new List<PropertyValue>();
            var classes = new Dictionary<int, string>();
            int position = 32;
            while (position + 16 <= data.Length)
            {
                string name = Encoding.ASCII.GetString(data, position, 4);
                int compressed = BitConverter.ToInt32(data, position + 4);
                int size = BitConverter.ToInt32(data, position + 8);
                position += 16;
                int stored = compressed > 0 ? compressed : size;
                if (stored < 0 || size < 0 || stored > data.Length - position)
                    throw new InvalidDataException("The model file is truncated.");
                byte[] chunk;
                if (compressed == 0)
                    chunk = data.Skip(position).Take(size).ToArray();
                else if (stored >= 4 && BitConverter.ToUInt32(data, position) == 0xFD2FB528)
                    throw new NotSupportedException("The model file uses Zstandard compression, which this exporter cannot read yet.");
                else
                    chunk = DecodeLz4(data, position, compressed, size);
                position += stored;

                if (name == "INST")
                {
                    int classIndex = BitConverter.ToInt32(chunk, 0);
                    classes[classIndex] = ReadString(chunk, 4, out _);
                    result.Add(new PropertyValue { Class = classes[classIndex], Property = ClassMarker });
                }
                else if (name == "PROP")
                {
                    int classIndex = BitConverter.ToInt32(chunk, 0);
                    string property = ReadString(chunk, 4, out int offset);
                    classes.TryGetValue(classIndex, out string className);
                    if (!Wanted(className, property))
                        continue;
                    byte type = chunk[offset++];
                    if (type == 0x01)
                    {
                        while (offset + 4 <= chunk.Length)
                            result.Add(new PropertyValue { Class = className, Property = property, Value = ReadString(chunk, offset, out offset) });
                    }
                    else
                    {
                        // Newer Content values embed the URI as text; extract any asset reference they contain.
                        string text = Encoding.UTF8.GetString(chunk, offset, chunk.Length - offset);
                        foreach (Match match in Regex.Matches(text, @"rbxassetid://\d+|[?&]id=\d+", RegexOptions.IgnoreCase))
                            result.Add(new PropertyValue { Class = className, Property = property, Value = match.Value });
                    }
                }
                else if (name == "END\0")
                {
                    break;
                }
            }
            return result;
        }

        private static string ReadString(byte[] data, int offset, out int next)
        {
            if (offset + 4 > data.Length)
                throw new InvalidDataException("The model file has a truncated string.");
            int length = BitConverter.ToInt32(data, offset);
            if (length < 0 || offset + 4 + length > data.Length)
                throw new InvalidDataException("The model file has an invalid string.");
            next = offset + 4 + length;
            return Encoding.UTF8.GetString(data, offset + 4, length);
        }

        public static byte[] DecodeLz4(byte[] source, int offset, int length, int outputLength)
        {
            var output = new byte[outputLength];
            int input = offset, end = offset + length, written = 0;
            while (input < end)
            {
                int token = source[input++];
                int literal = token >> 4;
                if (literal == 15)
                {
                    int extra;
                    do
                    {
                        if (input >= end)
                            throw new InvalidDataException("Invalid LZ4 data in the model file.");
                        extra = source[input++];
                        literal += extra;
                    } while (extra == 255);
                }
                if (input + literal > end || written + literal > outputLength)
                    throw new InvalidDataException("Invalid LZ4 data in the model file.");
                Buffer.BlockCopy(source, input, output, written, literal);
                input += literal;
                written += literal;
                if (input >= end)
                    break;
                if (input + 2 > end)
                    throw new InvalidDataException("Invalid LZ4 data in the model file.");
                int distance = source[input] | (source[input + 1] << 8);
                input += 2;
                int match = token & 15;
                if (match == 15)
                {
                    int extra;
                    do
                    {
                        if (input >= end)
                            throw new InvalidDataException("Invalid LZ4 data in the model file.");
                        extra = source[input++];
                        match += extra;
                    } while (extra == 255);
                }
                match += 4;
                if (distance == 0 || distance > written || written + match > outputLength)
                    throw new InvalidDataException("Invalid LZ4 data in the model file.");
                for (int i = 0; i < match; i++, written++)
                    output[written] = output[written - distance];
            }
            if (written != outputLength)
                throw new InvalidDataException("Invalid LZ4 data in the model file.");
            return output;
        }
    }
}
