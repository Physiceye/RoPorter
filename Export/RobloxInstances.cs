using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Rbx2Source.Export
{
    // One instance of a Roblox model file, with the property values the Model export reads: strings, bools, numbers,
    // enums (ints), and Vector3 / Color3 / CFrame values (float arrays; a CFrame is X Y Z then R00..R22, row by row).
    public sealed class RobloxInstance
    {
        public string ClassName;
        public RobloxInstance Parent;
        public List<RobloxInstance> Children { get; } = new List<RobloxInstance>();
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // Binary data kept byte for byte (RobloxModelFile.BinaryProperties), such as Terrain's voxel grid.
        public Dictionary<string, byte[]> Binary { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public string Name => Properties.TryGetValue("Name", out object name) ? name as string ?? ClassName : ClassName;

        public T Get<T>(string property) where T : class => Properties.TryGetValue(property, out object value) ? value as T : null;

        public float? Number(string property) =>
            Properties.TryGetValue(property, out object value) && value is float number ? number : (float?)null;

        public int? Enum(string property) =>
            Properties.TryGetValue(property, out object value) && value is int number ? number : (int?)null;

        public bool Flag(string property) => Properties.TryGetValue(property, out object value) && value is bool flag && flag;

        public IEnumerable<RobloxInstance> Descendants() => Children.SelectMany(child => new[] { child }.Concat(child.Descendants()));
    }

    public static partial class RobloxModelFile
    {
        // Properties holding binary data rather than text: Terrain's voxels, and a union's mesh and parts.
        private static readonly HashSet<string> BinaryProperties = new HashSet<string>(new[] { "SmoothGrid", "MaterialColors", "MeshData", "ChildData", "PhysicsData" },
            StringComparer.OrdinalIgnoreCase);

        // Reads every instance of a .rbxm or .rbxl (binary) or .rbxmx or .rbxlx (XML) file, returning the top-level ones: for
        // a place, its services (Workspace, Lighting...).
        public static List<RobloxInstance> ReadInstances(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("The model file is empty.");
            return data.Take(8).SequenceEqual(BinaryMagic) ? ReadBinaryInstances(data) : ReadXmlInstances(data);
        }

        private static List<RobloxInstance> ReadXmlInstances(byte[] data)
        {
            var document = new XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
            }
            catch (XmlException error)
            {
                throw new InvalidDataException("The model file is not a Roblox model (" + error.Message + ").");
            }
            if (document.DocumentElement?.Name != "roblox")
                throw new InvalidDataException("The model file is not a Roblox model.");
            var roots = new List<RobloxInstance>();
            foreach (XmlElement item in document.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "Item"))
                roots.Add(ReadXmlItem(item, null));
            return roots;
        }

        private static RobloxInstance ReadXmlItem(XmlElement item, RobloxInstance parent)
        {
            var instance = new RobloxInstance { ClassName = item.GetAttribute("class"), Parent = parent };
            parent?.Children.Add(instance);
            foreach (XmlElement child in item.ChildNodes.OfType<XmlElement>())
            {
                if (child.Name == "Item")
                    ReadXmlItem(child, instance);
                else if (child.Name == "Properties")
                    foreach (XmlElement property in child.ChildNodes.OfType<XmlElement>())
                    {
                        string name = property.GetAttribute("name");
                        if (property.Name == "BinaryString" && BinaryProperties.Contains(name))
                            try
                            {
                                instance.Binary[name] = Convert.FromBase64String(property.InnerText.Trim());
                            }
                            catch (FormatException) { }
                        object value = XmlValue(property);
                        if (name.Length > 0 && value != null)
                            instance.Properties[name] = value;
                    }
            }
            return instance;
        }

        private static float XmlFloat(XmlElement element, string child)
        {
            string text = element[child]?.InnerText?.Trim();
            if (text == null)
                return 0;
            if (text == "INF")
                return float.PositiveInfinity;
            if (text == "-INF")
                return float.NegativeInfinity;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0;
        }

        private static object XmlValue(XmlElement property)
        {
            string text = property.InnerText.Trim();
            switch (property.Name)
            {
                case "string":
                case "ProtectedString":
                case "BinaryString":
                    return property.Name == "BinaryString" ? null : property.InnerText;
                case "Content":
                case "ContentId":
                    // <Content><url>rbxassetid://1</url></Content>, <Content><null/></Content>, or the URI as text.
                    return property["url"]?.InnerText.Trim() ?? (property["uri"]?.InnerText.Trim() ?? text);
                case "bool":
                    return text.Equals("true", StringComparison.OrdinalIgnoreCase);
                case "float":
                case "double":
                    return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number : (object)null;
                case "int":
                case "token":
                    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) ? integer : (object)null;
                case "Vector3":
                    return new[] { XmlFloat(property, "X"), XmlFloat(property, "Y"), XmlFloat(property, "Z") };
                case "Color3":
                    return new[] { XmlFloat(property, "R"), XmlFloat(property, "G"), XmlFloat(property, "B") };
                case "Color3uint8":
                    // 0xAARRGGBB as a decimal number.
                    if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint argb))
                        return null;
                    return new[] { ((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f };
                case "CoordinateFrame":
                    return new[] { "X", "Y", "Z", "R00", "R01", "R02", "R10", "R11", "R12", "R20", "R21", "R22" }.Select(c => XmlFloat(property, c)).ToArray();
                default:
                    return null;
            }
        }

        private static List<RobloxInstance> ReadBinaryInstances(byte[] data)
        {
            var classNames = new Dictionary<int, string>();
            var classInstances = new Dictionary<int, int[]>();
            var byReferent = new Dictionary<int, RobloxInstance>();
            var order = new List<RobloxInstance>();
            var parents = new List<KeyValuePair<int, int>>();
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
                byte[] chunk = ChunkData(data, position, compressed, size);
                position += stored;

                if (name == "INST")
                {
                    int classIndex = BitConverter.ToInt32(chunk, 0);
                    string className = ReadString(chunk, 4, out int offset);
                    offset++; // object format (1 for services)
                    int count = BitConverter.ToInt32(chunk, offset);
                    offset += 4;
                    int[] referents = ReadReferents(chunk, ref offset, count);
                    classNames[classIndex] = className;
                    classInstances[classIndex] = referents;
                    foreach (int referent in referents)
                    {
                        var instance = new RobloxInstance { ClassName = className };
                        byReferent[referent] = instance;
                        order.Add(instance);
                    }
                }
                else if (name == "PROP")
                {
                    int classIndex = BitConverter.ToInt32(chunk, 0);
                    string property = ReadString(chunk, 4, out int offset);
                    if (!classInstances.TryGetValue(classIndex, out int[] referents) || offset >= chunk.Length)
                        continue;
                    byte type = chunk[offset++];
                    if (type == 0x01 && BinaryProperties.Contains(property))
                    {
                        for (int i = 0; i < referents.Length && offset + 4 <= chunk.Length; i++)
                        {
                            int length = BitConverter.ToInt32(chunk, offset);
                            if (length < 0 || offset + 4 + length > chunk.Length)
                                throw new InvalidDataException("The model file has an invalid string.");
                            var bytes = new byte[length];
                            Buffer.BlockCopy(chunk, offset + 4, bytes, 0, length);
                            byReferent[referents[i]].Binary[property] = bytes;
                            offset += 4 + length;
                        }
                        continue;
                    }
                    object[] values = ReadPropertyValues(chunk, offset, type, referents.Length);
                    if (values == null)
                        continue;
                    for (int i = 0; i < referents.Length; i++)
                        if (values[i] != null)
                            byReferent[referents[i]].Properties[property] = values[i];
                }
                else if (name == "PRNT")
                {
                    int offset = 1; // version
                    int count = BitConverter.ToInt32(chunk, offset);
                    offset += 4;
                    int[] children = ReadReferents(chunk, ref offset, count);
                    int[] parentReferents = ReadReferents(chunk, ref offset, count);
                    for (int i = 0; i < count; i++)
                        parents.Add(new KeyValuePair<int, int>(children[i], parentReferents[i]));
                }
                else if (name == "END\0")
                {
                    break;
                }
            }
            var roots = new List<RobloxInstance>();
            var placed = new HashSet<RobloxInstance>();
            foreach (var link in parents)
            {
                if (!byReferent.TryGetValue(link.Key, out RobloxInstance child) || !placed.Add(child))
                    continue;
                if (byReferent.TryGetValue(link.Value, out RobloxInstance parent) && parent != child)
                {
                    child.Parent = parent;
                    parent.Children.Add(child);
                }
                else
                    roots.Add(child);
            }
            roots.AddRange(order.Where(instance => !placed.Contains(instance)));
            return roots;
        }

        // Referents are interleaved, zigzag-encoded, and each one is stored as the difference from the one before.
        private static int[] ReadReferents(byte[] chunk, ref int offset, int count)
        {
            if (count < 0 || offset + 4L * count > chunk.Length)
                throw new InvalidDataException("The model file has a truncated instance list.");
            var result = new int[count];
            int last = 0;
            for (int i = 0; i < count; i++)
            {
                uint raw = Interleaved(chunk, offset, count, i);
                last += (int)(raw >> 1) ^ -(int)(raw & 1);
                result[i] = last;
            }
            offset += 4 * count;
            return result;
        }

        // The i-th of count big-endian 32-bit values stored byte plane by byte plane.
        private static uint Interleaved(byte[] chunk, int offset, int count, int i) =>
            ((uint)chunk[offset + i] << 24) | ((uint)chunk[offset + count + i] << 16) | ((uint)chunk[offset + 2 * count + i] << 8) | chunk[offset + 3 * count + i];

        // Floats are interleaved too, with the sign bit moved to the lowest bit.
        private static float RotatedFloat(byte[] chunk, int offset, int count, int i)
        {
            uint raw = Interleaved(chunk, offset, count, i);
            return BitConverter.ToSingle(BitConverter.GetBytes((raw >> 1) | (raw << 31)), 0);
        }

        private static object[] ReadPropertyValues(byte[] chunk, int offset, byte type, int count)
        {
            var values = new object[count];
            int remaining = chunk.Length - offset;
            switch (type)
            {
                case 0x01: // String
                    for (int i = 0; i < count && offset + 4 <= chunk.Length; i++)
                        values[i] = ReadString(chunk, offset, out offset);
                    return values;
                case 0x02: // Bool
                    for (int i = 0; i < count && i < remaining; i++)
                        values[i] = chunk[offset + i] != 0;
                    return values;
                case 0x03: // Int32
                case 0x12: // Enum (token)
                    if (remaining < 4 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                    {
                        uint raw = Interleaved(chunk, offset, count, i);
                        values[i] = type == 0x03 ? (int)(raw >> 1) ^ -(int)(raw & 1) : (int)raw;
                    }
                    return values;
                case 0x04: // Float
                    if (remaining < 4 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = RotatedFloat(chunk, offset, count, i);
                    return values;
                case 0x05: // Double
                    if (remaining < 8 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = (float)BitConverter.ToDouble(chunk, offset + 8 * i);
                    return values;
                case 0x0C: // Color3
                case 0x0E: // Vector3
                    if (remaining < 12 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = new[] { RotatedFloat(chunk, offset, count, i), RotatedFloat(chunk, offset + 4 * count, count, i), RotatedFloat(chunk, offset + 8 * count, count, i) };
                    return values;
                case 0x1A: // Color3uint8: all reds, then greens, then blues
                    if (remaining < 3 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = new[] { chunk[offset + i] / 255f, chunk[offset + count + i] / 255f, chunk[offset + 2 * count + i] / 255f };
                    return values;
                case 0x10: // CFrame: every rotation (an orientation ID, or 0 and nine floats), then every position as a Vector3
                    var rotations = new float[count][];
                    for (int i = 0; i < count; i++)
                    {
                        if (offset >= chunk.Length)
                            return null;
                        byte id = chunk[offset++];
                        if (id == 0)
                        {
                            if (offset + 36 > chunk.Length)
                                return null;
                            rotations[i] = Enumerable.Range(0, 9).Select(k => BitConverter.ToSingle(chunk, offset + 4 * k)).ToArray();
                            offset += 36;
                        }
                        else
                            rotations[i] = BasicRotation(id);
                    }
                    if (chunk.Length - offset < 12 * count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = new[] { RotatedFloat(chunk, offset, count, i), RotatedFloat(chunk, offset + 4 * count, count, i), RotatedFloat(chunk, offset + 8 * count, count, i) }
                            .Concat(rotations[i]).ToArray();
                    return values;
                default:
                    // Newer Content values: when every instance has one asset reference, they are matched up in order.
                    string text = Encoding.UTF8.GetString(chunk, offset, remaining);
                    var references = Regex.Matches(text, @"rbxassetid://\d+|[?&]id=\d+", RegexOptions.IgnoreCase);
                    if (references.Count != count)
                        return null;
                    for (int i = 0; i < count; i++)
                        values[i] = references[i].Value;
                    return values;
            }
        }

        // An axis-aligned orientation stored as one byte: (ID - 1) is 6 * right + up, each a normal (+X, +Y, +Z, -X, -Y, -Z),
        // and the third column is right × up. Unknown IDs read as no rotation.
        private static float[] BasicRotation(byte id)
        {
            int index = id - 1, right = index / 6, up = index % 6;
            if (index < 0 || right > 5 || right % 3 == up % 3)
                return new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            float[] x = Normal(right), y = Normal(up);
            float[] z = { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
            return new[] { x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2] };
        }

        private static float[] Normal(int normal)
        {
            var vector = new float[3];
            vector[normal % 3] = normal < 3 ? 1 : -1;
            return vector;
        }
    }
}
