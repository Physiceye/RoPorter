using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Rbx2Source.Export
{
    // Reads the skeleton of a binary FBX file (another game's character rig, such as s&box's Citizen): each bone's name, parent
    // and bind-pose world matrix, in the file's own axes and units.
    public static class FbxReader
    {
        public sealed class Node
        {
            public string Name;
            public List<object> Properties = new List<object>();
            public List<Node> Children = new List<Node>();

            public Node Child(string name) => Children.FirstOrDefault(c => c.Name == name);
            public IEnumerable<Node> All(string name) => Children.Where(c => c.Name == name);
        }

        public sealed class Bone
        {
            public string Name;
            public int Parent = -1;
            // Column-major 4x4 world matrix (FBX's own order: translation in elements 12-14).
            public double[] World;
        }

        public static List<Node> Parse(byte[] data)
        {
            if (data.Length < 27 || Encoding.ASCII.GetString(data, 0, 20) != "Kaydara FBX Binary  ")
                throw new InvalidDataException("Not a binary FBX file.");
            int version = BitConverter.ToInt32(data, 23);
            bool wide = version >= 7500;
            var nodes = new List<Node>();
            int position = 27;
            while (true)
            {
                Node node = ReadNode(data, ref position, wide);
                if (node == null)
                    break;
                nodes.Add(node);
            }
            return nodes;
        }

        private static Node ReadNode(byte[] data, ref int position, bool wide)
        {
            long end = wide ? BitConverter.ToInt64(data, position) : BitConverter.ToUInt32(data, position);
            long count = wide ? BitConverter.ToInt64(data, position + 8) : BitConverter.ToUInt32(data, position + 4);
            position += wide ? 24 : 12;
            int nameLength = data[position++];
            if (end == 0)
                return null;
            var node = new Node { Name = Encoding.ASCII.GetString(data, position, nameLength) };
            position += nameLength;
            for (long i = 0; i < count; i++)
                node.Properties.Add(ReadProperty(data, ref position));
            while (position < end)
            {
                Node child = ReadNode(data, ref position, wide);
                if (child == null)
                    break;
                node.Children.Add(child);
            }
            position = (int)end;
            return node;
        }

        private static object ReadProperty(byte[] data, ref int position)
        {
            char type = (char)data[position++];
            switch (type)
            {
                case 'C': return data[position++] != 0;
                case 'Y': position += 2; return BitConverter.ToInt16(data, position - 2);
                case 'I': position += 4; return BitConverter.ToInt32(data, position - 4);
                case 'F': position += 4; return BitConverter.ToSingle(data, position - 4);
                case 'D': position += 8; return BitConverter.ToDouble(data, position - 8);
                case 'L': position += 8; return BitConverter.ToInt64(data, position - 8);
                case 'S':
                case 'R':
                    int length = BitConverter.ToInt32(data, position);
                    position += 4;
                    object value = type == 'S' ? Encoding.UTF8.GetString(data, position, length) : (object)data.Skip(position).Take(length).ToArray();
                    position += length;
                    return value;
                case 'f': case 'd': case 'l': case 'i': case 'b':
                    int elements = BitConverter.ToInt32(data, position), encoding = BitConverter.ToInt32(data, position + 4),
                        compressed = BitConverter.ToInt32(data, position + 8);
                    position += 12;
                    byte[] raw;
                    if (encoding == 1)
                        using (var input = new ZLibStream(new MemoryStream(data, position, compressed), CompressionMode.Decompress))
                        using (var output = new MemoryStream())
                        {
                            input.CopyTo(output);
                            raw = output.ToArray();
                        }
                    else
                        raw = data.Skip(position).Take(compressed).ToArray();
                    position += compressed;
                    switch (type)
                    {
                        case 'f': return Enumerable.Range(0, elements).Select(i => (double)BitConverter.ToSingle(raw, 4 * i)).ToArray();
                        case 'd': return Enumerable.Range(0, elements).Select(i => BitConverter.ToDouble(raw, 8 * i)).ToArray();
                        case 'l': return Enumerable.Range(0, elements).Select(i => BitConverter.ToInt64(raw, 8 * i)).ToArray();
                        case 'i': return Enumerable.Range(0, elements).Select(i => BitConverter.ToInt32(raw, 4 * i)).ToArray();
                        default: return raw;
                    }
                default:
                    throw new InvalidDataException("Unknown FBX property type '" + type + "'.");
            }
        }

        // The bones (LimbNode models) with their parents and bind-pose world matrices. Bones missing from the bind pose get
        // theirs from their local translation and rotation (Lcl Translation, PreRotation and Lcl Rotation, XYZ order).
        public static List<Bone> ReadSkeleton(byte[] data)
        {
            List<Node> root = Parse(data);
            Node objects = root.First(n => n.Name == "Objects");
            var models = objects.All("Model").Where(m => m.Properties.Count >= 3 && (string)m.Properties[2] == "LimbNode")
                .ToDictionary(m => (long)m.Properties[0]);
            // A bone is also connected (as a link) to each skin cluster that uses it; only its connection to another model is
            // its parent.
            var anyModel = new HashSet<long>(objects.All("Model").Select(m => (long)m.Properties[0]));
            var parents = new Dictionary<long, long>();
            foreach (Node c in root.First(n => n.Name == "Connections").All("C"))
                if ((string)c.Properties[0] == "OO" && models.ContainsKey((long)c.Properties[1]) && anyModel.Contains((long)c.Properties[2]))
                    parents[(long)c.Properties[1]] = (long)c.Properties[2];
            var pose = new Dictionary<long, double[]>();
            foreach (Node bindPose in objects.All("Pose"))
                foreach (Node poseNode in bindPose.All("PoseNode"))
                    pose[(long)poseNode.Child("Node").Properties[0]] = (double[])poseNode.Child("Matrix").Properties[0];

            var order = new List<long>();
            var visited = new HashSet<long>();
            void Visit(long id)
            {
                if (!visited.Add(id))
                    return;
                if (parents.TryGetValue(id, out long parent) && models.ContainsKey(parent))
                    Visit(parent);
                order.Add(id);
            }
            foreach (long id in models.Keys)
                Visit(id);

            var bones = new List<Bone>();
            var index = new Dictionary<long, int>();
            foreach (long id in order)
            {
                string name = ((string)models[id].Properties[1]).Split('\0')[0];
                int parent = parents.TryGetValue(id, out long p) && index.TryGetValue(p, out int found) ? found : -1;
                double[] world = pose.TryGetValue(id, out double[] matrix) ? matrix
                    : Multiply(parent >= 0 ? bones[parent].World : Identity(), Local(models[id]));
                index[id] = bones.Count;
                bones.Add(new Bone { Name = name, Parent = parent, World = world });
            }
            return bones;
        }

        private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        private static double[] Local(Node model)
        {
            double[] Vector(string property)
            {
                Node p = model.Child("Properties70")?.All("P").FirstOrDefault(n => (string)n.Properties[0] == property);
                return p == null ? new double[3] : new[] { Convert.ToDouble(p.Properties[4]), Convert.ToDouble(p.Properties[5]), Convert.ToDouble(p.Properties[6]) };
            }
            double[] t = Vector("Lcl Translation");
            double[] rotation = Multiply(Euler(Vector("PreRotation")), Euler(Vector("Lcl Rotation")));
            rotation[12] = t[0];
            rotation[13] = t[1];
            rotation[14] = t[2];
            return rotation;
        }

        // FBX XYZ Euler order (degrees): X applied first, so the matrix is Rz * Ry * Rx.
        private static double[] Euler(double[] degrees)
        {
            double Rad(double d) => d * Math.PI / 180;
            double cx = Math.Cos(Rad(degrees[0])), sx = Math.Sin(Rad(degrees[0])), cy = Math.Cos(Rad(degrees[1])), sy = Math.Sin(Rad(degrees[1])),
                cz = Math.Cos(Rad(degrees[2])), sz = Math.Sin(Rad(degrees[2]));
            double[] x = { 1, 0, 0, 0, 0, cx, sx, 0, 0, -sx, cx, 0, 0, 0, 0, 1 };
            double[] y = { cy, 0, -sy, 0, 0, 1, 0, 0, sy, 0, cy, 0, 0, 0, 0, 1 };
            double[] z = { cz, sz, 0, 0, -sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            return Multiply(z, Multiply(y, x));
        }

        // Column-major 4x4 product a * b.
        public static double[] Multiply(double[] a, double[] b)
        {
            var result = new double[16];
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += a[k * 4 + row] * b[column * 4 + k];
                    result[column * 4 + row] = sum;
                }
            return result;
        }
    }
}
