using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Rbx2Source.Export
{
    // Writes a binary FBX 7.4 file (the version Unity and Blender read natively): the same skinned meshes, humanoid
    // skeleton, blend shapes and materials as the glTF export. Units are metres (UnitScaleFactor 100), Y is up and
    // the avatar faces +Z. Textures are embedded and also referenced by relative path.
    public static class FbxWriter
    {
        private sealed class Node
        {
            public readonly string Name;
            public readonly List<object> Properties = new List<object>();
            public readonly List<Node> Children = new List<Node>();

            public Node(string name, params object[] properties)
            {
                Name = name;
                Properties.AddRange(properties);
            }

            public Node Add(string name, params object[] properties)
            {
                var child = new Node(name, properties);
                Children.Add(child);
                return child;
            }
        }

        // Fixed FileId / CreationTime pair; FBX readers check the footer against them.
        private static readonly byte[] FileId = { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };
        private const string CreationTime = "1970-01-01 10:00:00:000";
        private static readonly byte[] FooterId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] FooterMagic = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
        private const int Version = 7400;

        private static string ObjectName(string name, string type) => name + "\0\u0001" + type;

        private static Node Property(string name, string type, string label, string flags, params object[] values)
        {
            return new Node("P", new object[] { name, type, label, flags }.Concat(values).ToArray());
        }

        private static double[] Translation(float[] t) => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, t[0], t[1], t[2], 1 };

        public static void Write(ExportScene scene, IReadOnlyDictionary<string, string> textures,
            IReadOnlyDictionary<string, MaterialMaps> maps, string path, CancellationToken cancellation = default)
        {
            WavefrontModel model = scene.Model;
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("The model contains no triangles.");
            maps = maps ?? new Dictionary<string, MaterialMaps>();
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            long nextId = 1000000;
            long Id() => nextId++;
            var objects = new Node("Objects");
            var connections = new Node("Connections");
            void Connect(long child, long parent) => connections.Add("C", "OO", child, parent);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            void Count(string type) => counts[type] = (counts.TryGetValue(type, out int n) ? n : 0) + 1;

            Node AddModel(long id, string name, string type, float[] translation)
            {
                Count("Model");
                Node node = objects.Add("Model", id, ObjectName(name, "Model"), type);
                node.Add("Version", 232);
                Node properties = node.Add("Properties70");
                if (translation != null)
                    properties.Children.Add(Property("Lcl Translation", "Lcl Translation", "", "A",
                        (double)translation[0], (double)translation[1], (double)translation[2]));
                properties.Children.Add(Property("DefaultAttributeIndex", "int", "Integer", "", 0));
                properties.Children.Add(Property("InheritType", "enum", "", "", 1));
                node.Add("MultiLayer", 0);
                node.Add("MultiTake", 0);
                node.Add("Shading", true);
                node.Add("Culling", "CullingOff");
                return node;
            }

            // Skeleton: an "Armature" null holding the bone hierarchy, as in the glTF export.
            long[] boneIds = null;
            float[][] boneWorld = null;
            long armatureId = 0;
            if (scene.Rig != null)
            {
                List<RigBone> bones = scene.Rig.Bones;
                armatureId = Id();
                AddModel(armatureId, "Armature", "Null", null);
                long armatureAttribute = Id();
                Count("NodeAttribute");
                objects.Add("NodeAttribute", armatureAttribute, ObjectName("Armature", "NodeAttribute"), "Null").Add("TypeFlags", "Null");
                Connect(armatureAttribute, armatureId);
                Connect(armatureId, 0);
                boneWorld = bones.Select(bone => scene.Point(bone.Position)).ToArray();
                boneIds = bones.Select(_ => Id()).ToArray();
                for (int i = 0; i < bones.Count; i++)
                {
                    float[] parent = bones[i].Parent >= 0 ? boneWorld[bones[i].Parent] : new float[3];
                    AddModel(boneIds[i], bones[i].Name, "LimbNode",
                        new[] { boneWorld[i][0] - parent[0], boneWorld[i][1] - parent[1], boneWorld[i][2] - parent[2] });
                    long attribute = Id();
                    Count("NodeAttribute");
                    Node node = objects.Add("NodeAttribute", attribute, ObjectName(bones[i].Name, "NodeAttribute"), "LimbNode");
                    node.Add("Properties70").Children.Add(Property("Size", "double", "Number", "", 10.0));
                    node.Add("TypeFlags", "Skeleton");
                    Connect(attribute, boneIds[i]);
                }
                for (int i = 0; i < bones.Count; i++)
                    Connect(boneIds[i], bones[i].Parent >= 0 ? boneIds[bones[i].Parent] : armatureId);
            }

            // Materials and their textures, shared by every mesh that uses them.
            var materialIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var textureIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long Texture(string relative)
            {
                if (textureIds.TryGetValue(relative, out long existing))
                    return existing;
                string full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                    throw new InvalidDataException("Missing texture file " + relative);
                string name = Path.GetFileNameWithoutExtension(relative);
                string fileName = relative.Replace('\\', '/');
                long video = Id(), texture = Id();
                Count("Video");
                Node clip = objects.Add("Video", video, ObjectName(name, "Video"), "Clip");
                clip.Add("Type", "Clip");
                clip.Add("Properties70").Children.Add(Property("Path", "KString", "XRefUrl", "", fileName));
                clip.Add("UseMipMap", 0);
                clip.Add("Filename", fileName);
                clip.Add("RelativeFilename", fileName);
                clip.Add("Content", File.ReadAllBytes(full));
                Count("Texture");
                Node node = objects.Add("Texture", texture, ObjectName(name, "Texture"), "");
                node.Add("Type", "TextureVideoClip");
                node.Add("Version", 202);
                node.Add("TextureName", ObjectName(name, "Texture"));
                Node properties = node.Add("Properties70");
                properties.Children.Add(Property("UVSet", "KString", "", "", "UVMap"));
                properties.Children.Add(Property("UseMaterial", "bool", "", "", 1));
                node.Add("Media", ObjectName(name, "Video"));
                node.Add("FileName", fileName);
                node.Add("RelativeFilename", fileName);
                node.Add("ModelUVTranslation", 0.0, 0.0);
                node.Add("ModelUVScaling", 1.0, 1.0);
                node.Add("Texture_Alpha_Source", "None");
                node.Add("Cropping", 0, 0, 0, 0);
                Connect(video, texture);
                textureIds[relative] = texture;
                return texture;
            }

            long Material(ObjMaterial material)
            {
                if (materialIds.TryGetValue(material.Name, out long existing))
                    return existing;
                long id = Id();
                Count("Material");
                Node node = objects.Add("Material", id, ObjectName(material.Name, "Material"), "");
                node.Add("Version", 102);
                node.Add("ShadingModel", "phong");
                node.Add("MultiLayer", 0);
                Node properties = node.Add("Properties70");
                float[] diffuse = material.Texture != null ? new float[] { 1, 1, 1 } : material.Diffuse;
                properties.Children.Add(Property("DiffuseColor", "Color", "", "A", (double)diffuse[0], (double)diffuse[1], (double)diffuse[2]));
                properties.Children.Add(Property("DiffuseFactor", "Number", "", "A", 1.0));
                properties.Children.Add(Property("SpecularColor", "Color", "", "A", 0.2, 0.2, 0.2));
                properties.Children.Add(Property("ShininessExponent", "Number", "", "A", 8.0));
                properties.Children.Add(Property("TransparencyFactor", "Number", "", "A", (double)(1 - material.Opacity)));
                properties.Children.Add(Property("Opacity", "double", "Number", "", (double)material.Opacity));
                maps.TryGetValue(material.Name, out MaterialMaps extra);
                if (extra?.Emissive != null)
                {
                    properties.Children.Add(Property("EmissiveColor", "Color", "", "A",
                        (double)extra.EmissiveFactor[0], (double)extra.EmissiveFactor[1], (double)extra.EmissiveFactor[2]));
                    properties.Children.Add(Property("EmissiveFactor", "Number", "", "A", (double)extra.EmissiveStrength));
                }
                materialIds[material.Name] = id;
                if (material.Texture != null)
                {
                    if (!textures.TryGetValue(material.Name, out string relative))
                        throw new InvalidDataException("Missing downloaded texture for " + material.Name);
                    long texture = Texture(relative);
                    connections.Add("C", "OP", texture, id, "DiffuseColor");
                    string alpha = GltfWriter.AlphaMode(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (alpha != "OPAQUE")
                        connections.Add("C", "OP", texture, id, "TransparentColor");
                }
                if (extra?.Normal != null)
                    connections.Add("C", "OP", Texture(extra.Normal), id, "NormalMap");
                if (extra?.Emissive != null)
                    connections.Add("C", "OP", Texture(extra.Emissive), id, "EmissiveColor");
                return id;
            }

            var parts = new List<KeyValuePair<string, List<ObjTriangle>>>();
            if (scene.Face != null)
            {
                parts.Add(new KeyValuePair<string, List<ObjTriangle>>("Body", model.Triangles.Where(t => !scene.Face.IsFace(t.Group)).ToList()));
                parts.Add(new KeyValuePair<string, List<ObjTriangle>>("Face", model.Triangles.Where(t => scene.Face.IsFace(t.Group)).ToList()));
            }
            else
            {
                parts.Add(new KeyValuePair<string, List<ObjTriangle>>(scene.Rig != null ? "Body" : "Model", model.Triangles));
            }
            parts.RemoveAll(part => part.Value.Count == 0);

            var pose = new List<KeyValuePair<long, double[]>>();
            foreach (var part in parts)
            {
                cancellation.ThrowIfCancellationRequested();
                List<ObjTriangle> triangles = part.Value;
                var local = new Dictionary<int, int>();
                var points = new List<int>();
                foreach (ObjCorner corner in triangles.SelectMany(t => t.Corners))
                    if (!local.ContainsKey(corner.Position))
                    {
                        local[corner.Position] = points.Count;
                        points.Add(corner.Position);
                    }
                var materialOrder = triangles.Select(t => t.Material).Distinct().ToList();

                long modelId = Id(), geometryId = Id();
                AddModel(modelId, part.Key, "Mesh", null);
                Connect(modelId, 0);
                pose.Add(new KeyValuePair<long, double[]>(modelId, Translation(new float[3])));
                Count("Geometry");
                Node geometry = objects.Add("Geometry", geometryId, ObjectName(part.Key, "Geometry"), "Mesh");
                geometry.Add("Properties70");
                geometry.Add("GeometryVersion", 124);
                geometry.Add("Vertices", points.SelectMany(p => scene.Point(model.Positions[p]).Select(v => (double)v)).ToArray());
                geometry.Add("PolygonVertexIndex", triangles.SelectMany(t => new[]
                {
                    local[t.Corners[0].Position], local[t.Corners[1].Position], -local[t.Corners[2].Position] - 1
                }).ToArray());
                Node normals = geometry.Add("LayerElementNormal", 0);
                normals.Add("Version", 101);
                normals.Add("Name", "");
                normals.Add("MappingInformationType", "ByPolygonVertex");
                normals.Add("ReferenceInformationType", "Direct");
                normals.Add("Normals", triangles.SelectMany(t => t.Corners).SelectMany(c =>
                {
                    float[] n = ExportScene.Direction(model.Normals[c.Normal]);
                    double length = Math.Sqrt(n.Sum(v => (double)v * v));
                    return n.Select(v => length > 0 ? v / length : 0);
                }).ToArray());
                int cornerCount = triangles.Count * 3;
                Node uvs = geometry.Add("LayerElementUV", 0);
                uvs.Add("Version", 101);
                uvs.Add("Name", "UVMap");
                uvs.Add("MappingInformationType", "ByPolygonVertex");
                uvs.Add("ReferenceInformationType", "IndexToDirect");
                uvs.Add("UV", triangles.SelectMany(t => t.Corners).SelectMany(c =>
                    c.Texture >= 0 ? new double[] { model.Texcoords[c.Texture][0], model.Texcoords[c.Texture][1] } : new double[2]).ToArray());
                uvs.Add("UVIndex", Enumerable.Range(0, cornerCount).ToArray());
                Node materials = geometry.Add("LayerElementMaterial", 0);
                materials.Add("Version", 101);
                materials.Add("Name", "");
                materials.Add("MappingInformationType", materialOrder.Count == 1 ? "AllSame" : "ByPolygon");
                materials.Add("ReferenceInformationType", "IndexToDirect");
                materials.Add("Materials", materialOrder.Count == 1 ? new[] { 0 } : triangles.Select(t => materialOrder.IndexOf(t.Material)).ToArray());
                Node layer = geometry.Add("Layer", 0);
                layer.Add("Version", 100);
                foreach (string element in new[] { "LayerElementNormal", "LayerElementUV", "LayerElementMaterial" })
                {
                    Node entry = layer.Add("LayerElement");
                    entry.Add("Type", element);
                    entry.Add("TypedIndex", 0);
                }
                Connect(geometryId, modelId);
                // Material connection order defines the material indices above.
                foreach (string material in materialOrder)
                    Connect(Material(model.Materials[material]), modelId);

                if (scene.Rig != null)
                {
                    long skin = Id();
                    Count("Deformer");
                    Node skinNode = objects.Add("Deformer", skin, ObjectName(part.Key, "Deformer"), "Skin");
                    skinNode.Add("Version", 101);
                    skinNode.Add("Link_DeformAcuracy", 50.0);
                    Connect(skin, geometryId);
                    var indexes = boneIds.Select(_ => new List<int>()).ToArray();
                    var weights = boneIds.Select(_ => new List<double>()).ToArray();
                    for (int i = 0; i < points.Count; i++)
                        for (int k = 0; k < 4; k++)
                        {
                            float w = scene.Rig.Weights[points[i]][k];
                            if (w <= 0)
                                continue;
                            int bone = scene.Rig.Joints[points[i]][k];
                            indexes[bone].Add(i);
                            weights[bone].Add(w);
                        }
                    for (int bone = 0; bone < boneIds.Length; bone++)
                    {
                        if (indexes[bone].Count == 0)
                            continue;
                        long cluster = Id();
                        Count("Deformer");
                        Node node = objects.Add("Deformer", cluster, ObjectName(scene.Rig.Bones[bone].Name, "SubDeformer"), "Cluster");
                        node.Add("Version", 100);
                        node.Add("UserData", "", "");
                        node.Add("Indexes", indexes[bone].ToArray());
                        node.Add("Weights", weights[bone].ToArray());
                        float[] p = boneWorld[bone];
                        node.Add("Transform", Translation(new[] { -p[0], -p[1], -p[2] }));
                        node.Add("TransformLink", Translation(p));
                        Connect(cluster, skin);
                        Connect(boneIds[bone], cluster);
                    }
                }

                if (scene.Face != null && part.Key == "Face")
                {
                    long blend = Id();
                    Count("Deformer");
                    objects.Add("Deformer", blend, ObjectName(part.Key, "Deformer"), "BlendShape").Add("Version", 100);
                    Connect(blend, geometryId);
                    foreach (FaceShape shape in scene.Face.Shapes)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var entries = shape.Deltas.Where(pair => local.ContainsKey(pair.Key)).OrderBy(pair => local[pair.Key]).ToList();
                        int[] index = entries.Select(pair => local[pair.Key]).ToArray();
                        double[] deltas = entries.SelectMany(pair => scene.Delta(pair.Value).Select(v => (double)v)).ToArray();
                        if (index.Length == 0)
                        {
                            // Keep empty shapes (such as vrc.v_sil) as one zero delta so importers don't drop them.
                            index = new[] { 0 };
                            deltas = new double[3];
                        }
                        long channel = Id(), target = Id();
                        Count("Deformer");
                        Node channelNode = objects.Add("Deformer", channel, ObjectName(shape.Name, "SubDeformer"), "BlendShapeChannel");
                        channelNode.Add("Version", 100);
                        channelNode.Add("DeformPercent", (double)shape.Weight * 100);
                        channelNode.Add("FullWeights", new double[] { 100 });
                        Count("Geometry");
                        Node targetNode = objects.Add("Geometry", target, ObjectName(shape.Name, "Geometry"), "Shape");
                        targetNode.Add("Version", 100);
                        targetNode.Add("Indexes", index);
                        targetNode.Add("Vertices", deltas);
                        targetNode.Add("Normals", new double[deltas.Length]);
                        Connect(target, channel);
                        Connect(channel, blend);
                    }
                }
            }

            if (scene.Rig != null)
            {
                for (int i = 0; i < boneIds.Length; i++)
                    pose.Add(new KeyValuePair<long, double[]>(boneIds[i], Translation(boneWorld[i])));
                long poseId = Id();
                Count("Pose");
                Node node = objects.Add("Pose", poseId, ObjectName("BindPose", "Pose"), "BindPose");
                node.Add("Type", "BindPose");
                node.Add("Version", 100);
                node.Add("NbPoseNodes", pose.Count);
                foreach (var entry in pose)
                {
                    Node poseNode = node.Add("PoseNode");
                    poseNode.Add("Node", entry.Key);
                    poseNode.Add("Matrix", entry.Value);
                }
            }

            var root = new List<Node>();
            Node header = new Node("FBXHeaderExtension");
            header.Add("FBXHeaderVersion", 1003);
            header.Add("FBXVersion", Version);
            header.Add("EncryptionType", 0);
            Node stamp = header.Add("CreationTimeStamp");
            foreach (var pair in new[] { ("Version", 1000), ("Year", 1970), ("Month", 1), ("Day", 1), ("Hour", 10), ("Minute", 0), ("Second", 0), ("Millisecond", 0) })
                stamp.Add(pair.Item1, pair.Item2);
            header.Add("Creator", "RoPorter");
            root.Add(header);
            root.Add(new Node("FileId", FileId));
            root.Add(new Node("CreationTime", CreationTime));
            root.Add(new Node("Creator", "RoPorter"));
            Node global = new Node("GlobalSettings");
            global.Add("Version", 1000);
            Node settings = global.Add("Properties70");
            foreach (var axis in new[] { ("UpAxis", 1), ("UpAxisSign", 1), ("FrontAxis", 2), ("FrontAxisSign", 1), ("CoordAxis", 0),
                ("CoordAxisSign", 1), ("OriginalUpAxis", 1), ("OriginalUpAxisSign", 1) })
                settings.Children.Add(Property(axis.Item1, "int", "Integer", "", axis.Item2));
            settings.Children.Add(Property("UnitScaleFactor", "double", "Number", "", 100.0));
            settings.Children.Add(Property("OriginalUnitScaleFactor", "double", "Number", "", 100.0));
            settings.Children.Add(Property("AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0));
            settings.Children.Add(Property("DefaultCamera", "KString", "", "", "Producer Perspective"));
            settings.Children.Add(Property("TimeMode", "enum", "", "", 11));
            settings.Children.Add(Property("TimeSpanStart", "KTime", "Time", "", 0L));
            settings.Children.Add(Property("TimeSpanStop", "KTime", "Time", "", 46186158000L));
            settings.Children.Add(Property("CustomFrameRate", "double", "Number", "", 24.0));
            root.Add(global);
            Node documents = new Node("Documents");
            documents.Add("Count", 1);
            Node document = documents.Add("Document", Id(), "Scene", "Scene");
            Node documentProperties = document.Add("Properties70");
            documentProperties.Children.Add(Property("SourceObject", "object", "", ""));
            documentProperties.Children.Add(Property("ActiveAnimStackName", "KString", "", "", ""));
            document.Add("RootNode", 0L);
            root.Add(documents);
            root.Add(new Node("References"));
            Node definitions = new Node("Definitions");
            definitions.Add("Version", 100);
            definitions.Add("Count", counts.Values.Sum() + 1);
            definitions.Add("ObjectType", "GlobalSettings").Add("Count", 1);
            foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                definitions.Add("ObjectType", pair.Key).Add("Count", pair.Value);
            root.Add(definitions);
            root.Add(objects);
            root.Add(connections);
            Node takes = new Node("Takes");
            takes.Add("Current", "");
            root.Add(takes);

            cancellation.ThrowIfCancellationRequested();
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0"));
                writer.Write((byte)0x1a);
                writer.Write((byte)0);
                writer.Write((uint)Version);
                foreach (Node node in root)
                    WriteNode(writer, node);
                writer.Write(new byte[13]);
                writer.Write(FooterId);
                writer.Write(new byte[4]);
                long offset = stream.Position;
                int padding = (int)(((offset + 15) & ~15L) - offset);
                writer.Write(new byte[padding == 0 ? 16 : padding]);
                writer.Write((uint)Version);
                writer.Write(new byte[120]);
                writer.Write(FooterMagic);
            }
        }

        // FBX 7.4 node record: end offset, property count and size (32-bit), name, properties, nested nodes, and a
        // 13-byte null record after nested nodes (or when the node is empty).
        private static void WriteNode(BinaryWriter writer, Node node)
        {
            Stream stream = writer.BaseStream;
            long start = stream.Position;
            writer.Write(0u);
            writer.Write((uint)node.Properties.Count);
            writer.Write(0u);
            byte[] name = Encoding.ASCII.GetBytes(node.Name);
            writer.Write((byte)name.Length);
            writer.Write(name);
            long propertiesStart = stream.Position;
            foreach (object property in node.Properties)
                WriteProperty(writer, property);
            long propertiesEnd = stream.Position;
            foreach (Node child in node.Children)
                WriteNode(writer, child);
            if (node.Children.Count > 0 || node.Properties.Count == 0)
                writer.Write(new byte[13]);
            long end = stream.Position;
            stream.Position = start;
            writer.Write(checked((uint)end));
            stream.Position = start + 8;
            writer.Write(checked((uint)(propertiesEnd - propertiesStart)));
            stream.Position = end;
        }

        private static void WriteProperty(BinaryWriter writer, object value)
        {
            switch (value)
            {
                case bool b: writer.Write((byte)'C'); writer.Write((byte)(b ? 1 : 0)); break;
                case short s: writer.Write((byte)'Y'); writer.Write(s); break;
                case int i: writer.Write((byte)'I'); writer.Write(i); break;
                case long l: writer.Write((byte)'L'); writer.Write(l); break;
                case float f: writer.Write((byte)'F'); writer.Write(f); break;
                case double d: writer.Write((byte)'D'); writer.Write(d); break;
                case string text:
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    writer.Write((byte)'S');
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    break;
                case byte[] raw: writer.Write((byte)'R'); writer.Write(raw.Length); writer.Write(raw); break;
                case int[] ints:
                    WriteArrayHeader(writer, 'i', ints.Length, ints.Length * 4);
                    foreach (int v in ints) writer.Write(v);
                    break;
                case double[] doubles:
                    WriteArrayHeader(writer, 'd', doubles.Length, doubles.Length * 8);
                    foreach (double v in doubles) writer.Write(v);
                    break;
                case long[] longs:
                    WriteArrayHeader(writer, 'l', longs.Length, longs.Length * 8);
                    foreach (long v in longs) writer.Write(v);
                    break;
                default:
                    throw new ArgumentException("Unsupported FBX property type " + value?.GetType());
            }
        }

        private static void WriteArrayHeader(BinaryWriter writer, char type, int length, int bytes)
        {
            writer.Write((byte)type);
            writer.Write(length);
            writer.Write(0);
            writer.Write(bytes);
        }
    }
}
