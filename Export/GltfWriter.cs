using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Rbx2Source.Export
{
    public enum ExportFormat { Glb, Gltf, Fbx, GarrysMod, SourceFilmmaker }

    // Extra PBR textures for one material, as paths relative to the export folder.
    public sealed class MaterialMaps
    {
        public string Normal;
        // glTF packing: G = roughness, B = metalness.
        public string MetallicRoughness;
        public string Emissive;
        public float[] EmissiveFactor = { 1, 1, 1 };
        // Above 1, written with KHR_materials_emissive_strength.
        public float EmissiveStrength = 1;
    }

    public static class GltfWriter
    {
        private const int ArrayBuffer = 34962;

        private sealed class Buffers
        {
            public readonly MemoryStream Binary = new MemoryStream();
            public readonly BinaryWriter Writer;
            public readonly JArray Views = new JArray();
            public readonly JArray Accessors = new JArray();

            public Buffers()
            {
                Writer = new BinaryWriter(Binary, Encoding.UTF8, true);
            }

            public int View(long offset, int? target)
            {
                var view = new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = Binary.Position - offset };
                if (target.HasValue)
                    view["target"] = target.Value;
                Views.Add(view);
                return Views.Count - 1;
            }

            public int Floats(float[][] vectors, string type, bool bounds, int? target = ArrayBuffer)
            {
                Align(Writer);
                long offset = Binary.Position;
                foreach (float[] vector in vectors)
                    foreach (float value in vector)
                    {
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            throw new InvalidDataException("The model contains a non-finite attribute.");
                        Writer.Write(value);
                    }
                var accessor = new JObject
                {
                    ["bufferView"] = View(offset, target), ["componentType"] = 5126,
                    ["count"] = vectors.Length, ["type"] = type
                };
                if (bounds)
                {
                    int size = vectors[0].Length;
                    accessor["min"] = new JArray(Enumerable.Range(0, size).Select(axis => vectors.Min(vector => vector[axis])));
                    accessor["max"] = new JArray(Enumerable.Range(0, size).Select(axis => vectors.Max(vector => vector[axis])));
                }
                Accessors.Add(accessor);
                return Accessors.Count - 1;
            }

            public int Joints(int[][] joints)
            {
                Align(Writer);
                long offset = Binary.Position;
                foreach (int[] set in joints)
                    foreach (int joint in set)
                        Writer.Write((byte)joint);
                Accessors.Add(new JObject
                {
                    ["bufferView"] = View(offset, ArrayBuffer), ["componentType"] = 5121,
                    ["count"] = joints.Length, ["type"] = "VEC4"
                });
                return Accessors.Count - 1;
            }
        }

        private static void Align(BinaryWriter writer, byte padding = 0)
        {
            while (writer.BaseStream.Position % 4 != 0)
                writer.Write(padding);
        }

        // Picks OPAQUE, MASK (cut-out) or BLEND from the texture's actual alpha values.
        internal static string AlphaMode(string path)
        {
            using (Picture image = Picture.Load(path))
            {
                if (!image.HasAlphaChannel)
                    return "OPAQUE";
                long transparent = 0, partial = 0;
                foreach (int pixel in image.GetPixels())
                {
                    int alpha = (pixel >> 24) & 255;
                    if (alpha <= 5)
                        transparent++;
                    else if (alpha < 250)
                        partial++;
                }
                long total = (long)image.Width * image.Height;
                if (transparent + partial == 0)
                    return "OPAQUE";
                return partial <= 0.05 * total ? "MASK" : "BLEND";
            }
        }

        // Per-corner tangents for normal mapping, accumulated over the triangles that share a vertex (same position,
        // normal and UV) and orthogonalised against its normal. UVs are used V-up, matching OpenGL-style normal maps,
        // so the glTF bitangent (normal x tangent * w) points towards the top of the texture.
        private static float[][] Tangents(WavefrontModel model, ObjCorner[] corners, float[][] positions, float[][] normals)
        {
            var sums = new Dictionary<(int, int, int), double[]>();
            (int, int, int) Key(ObjCorner corner) => (corner.Position, corner.Normal, corner.Texture);
            for (int i = 0; i + 2 < corners.Length; i += 3)
            {
                float[] p0 = positions[i], p1 = positions[i + 1], p2 = positions[i + 2];
                float[] t0 = corners[i].Texture >= 0 ? model.Texcoords[corners[i].Texture] : new float[2];
                float[] t1 = corners[i + 1].Texture >= 0 ? model.Texcoords[corners[i + 1].Texture] : new float[2];
                float[] t2 = corners[i + 2].Texture >= 0 ? model.Texcoords[corners[i + 2].Texture] : new float[2];
                double du1 = t1[0] - t0[0], dv1 = t1[1] - t0[1], du2 = t2[0] - t0[0], dv2 = t2[1] - t0[1];
                double r = du1 * dv2 - du2 * dv1;
                if (Math.Abs(r) < 1e-12)
                    continue;
                var tangent = new double[3];
                var bitangent = new double[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    double e1 = p1[axis] - p0[axis], e2 = p2[axis] - p0[axis];
                    tangent[axis] = (e1 * dv2 - e2 * dv1) / r;
                    bitangent[axis] = (e2 * du1 - e1 * du2) / r;
                }
                for (int k = 0; k < 3; k++)
                {
                    var key = Key(corners[i + k]);
                    if (!sums.TryGetValue(key, out double[] sum))
                        sums[key] = sum = new double[6];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        sum[axis] += tangent[axis];
                        sum[3 + axis] += bitangent[axis];
                    }
                }
            }
            var result = new float[corners.Length][];
            for (int i = 0; i < corners.Length; i++)
            {
                float[] n = normals[i];
                double[] sum = sums.TryGetValue(Key(corners[i]), out double[] found) ? found : new double[6];
                double dot = n[0] * sum[0] + n[1] * sum[1] + n[2] * sum[2];
                double[] t = { sum[0] - n[0] * dot, sum[1] - n[1] * dot, sum[2] - n[2] * dot };
                double length = Math.Sqrt(t[0] * t[0] + t[1] * t[1] + t[2] * t[2]);
                if (length < 1e-9)
                {
                    // Any unit vector perpendicular to the normal.
                    t = Math.Abs(n[0]) < 0.9 ? new double[] { 0, -n[2], n[1] } : new double[] { -n[2], 0, n[0] };
                    length = Math.Sqrt(t[0] * t[0] + t[1] * t[1] + t[2] * t[2]);
                }
                double[] cross = { n[1] * t[2] - n[2] * t[1], n[2] * t[0] - n[0] * t[2], n[0] * t[1] - n[1] * t[0] };
                double handedness = cross[0] * sum[3] + cross[1] * sum[4] + cross[2] * sum[5] < 0 ? -1 : 1;
                result[i] = new[] { (float)(t[0] / length), (float)(t[1] / length), (float)(t[2] / length), (float)handedness };
            }
            return result;
        }

        public static void Write(WavefrontModel model, IReadOnlyDictionary<string, string> textures, string path,
            ExportFormat format, CancellationToken cancellation = default)
        {
            Write(new ExportScene(model), textures, path, format, cancellation);
        }

        public static void Write(ExportScene scene, IReadOnlyDictionary<string, string> textures, string path,
            ExportFormat format, CancellationToken cancellation = default)
        {
            Write(scene, textures, null, path, format, cancellation);
        }

        public static void Write(ExportScene scene, IReadOnlyDictionary<string, string> textures,
            IReadOnlyDictionary<string, MaterialMaps> maps, string path, ExportFormat format, CancellationToken cancellation = default)
        {
            if (format != ExportFormat.Glb && format != ExportFormat.Gltf)
                throw new ArgumentOutOfRangeException(nameof(format));
            WavefrontModel model = scene.Model;
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("The model contains no triangles.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            var buffers = new Buffers();
            var materials = new JArray();
            var images = new JArray();
            var textureObjects = new JArray();
            var meshes = new JArray();
            var materialIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            var imageIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            var extensionsUsed = new HashSet<string>(StringComparer.Ordinal);

            string FullPath(string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

            int TextureIndex(string relative)
            {
                if (imageIndices.TryGetValue(relative, out int existing))
                    return existing;
                string imagePath = FullPath(relative);
                if (!File.Exists(imagePath))
                    throw new InvalidDataException("Missing texture file " + relative);
                int index = images.Count;
                imageIndices.Add(relative, index);
                if (format == ExportFormat.Glb)
                {
                    Align(buffers.Writer);
                    long offset = buffers.Binary.Position;
                    buffers.Writer.Write(File.ReadAllBytes(imagePath));
                    images.Add(new JObject
                    {
                        ["bufferView"] = buffers.View(offset, null),
                        ["mimeType"] = Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                            ? "image/png" : "image/jpeg"
                    });
                }
                else
                {
                    images.Add(new JObject { ["uri"] = relative.Replace('\\', '/') });
                }
                textureObjects.Add(new JObject { ["source"] = index });
                return index;
            }

            int MaterialIndex(ObjMaterial material)
            {
                if (materialIndices.TryGetValue(material.Name, out int existing))
                    return existing;
                var pbr = new JObject
                {
                    ["baseColorFactor"] = material.Texture == null
                        ? new JArray(material.Diffuse[0], material.Diffuse[1], material.Diffuse[2], material.Opacity)
                        : new JArray(1, 1, 1, material.Opacity),
                    ["metallicFactor"] = 0,
                    ["roughnessFactor"] = 1
                };
                string alphaMode = material.Opacity < 1 ? "BLEND" : "OPAQUE";
                if (material.Texture != null)
                {
                    if (!textures.TryGetValue(material.Name, out string relative))
                        throw new InvalidDataException("Missing downloaded texture for " + material.Name);
                    string textureAlpha = AlphaMode(FullPath(relative));
                    if (alphaMode == "OPAQUE")
                        alphaMode = textureAlpha;
                    pbr["baseColorTexture"] = new JObject { ["index"] = TextureIndex(relative) };
                }
                var json = new JObject
                {
                    ["name"] = material.Name, ["pbrMetallicRoughness"] = pbr,
                    // Back faces only for DoubleSided accessories, as in Roblox (outline shells must keep their front faces hidden).
                    ["alphaMode"] = alphaMode, ["doubleSided"] = material.DoubleSided
                };
                if (maps != null && maps.TryGetValue(material.Name, out MaterialMaps extra) && extra != null)
                {
                    if (extra.MetallicRoughness != null)
                    {
                        pbr["metallicFactor"] = 1;
                        pbr["metallicRoughnessTexture"] = new JObject { ["index"] = TextureIndex(extra.MetallicRoughness) };
                    }
                    if (extra.Normal != null)
                        json["normalTexture"] = new JObject { ["index"] = TextureIndex(extra.Normal) };
                    if (extra.Emissive != null)
                    {
                        json["emissiveTexture"] = new JObject { ["index"] = TextureIndex(extra.Emissive) };
                        json["emissiveFactor"] = new JArray(extra.EmissiveFactor.Select(value => Math.Max(0, Math.Min(1, value))));
                        if (extra.EmissiveStrength > 1)
                        {
                            json["extensions"] = new JObject
                            {
                                ["KHR_materials_emissive_strength"] = new JObject { ["emissiveStrength"] = extra.EmissiveStrength }
                            };
                            extensionsUsed.Add("KHR_materials_emissive_strength");
                        }
                    }
                }
                if (alphaMode == "MASK")
                    json["alphaCutoff"] = 0.5;
                materials.Add(json);
                materialIndices[material.Name] = materials.Count - 1;
                return materials.Count - 1;
            }

            // The face gets its own mesh so its shape keys do not have to cover every body vertex.
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

            foreach (var part in parts)
            {
                bool morph = scene.Face != null && part.Key == "Face";
                var primitives = new JArray();
                foreach (var group in part.Value.GroupBy(triangle => triangle.Material))
                {
                    cancellation.ThrowIfCancellationRequested();
                    ObjMaterial material = model.Materials[group.Key];

                    // OBJ uses independent position, normal and UV indices. Flatten corners to preserve seams.
                    ObjCorner[] corners = group.SelectMany(triangle => triangle.Corners).ToArray();
                    var positions = new float[corners.Length][];
                    var normals = new float[corners.Length][];
                    var uvs = new float[corners.Length][];
                    bool hasUvs = corners.Any(corner => corner.Texture >= 0);
                    for (int i = 0; i < corners.Length; i++)
                    {
                        ObjCorner corner = corners[i];
                        positions[i] = scene.Point(model.Positions[corner.Position]);
                        float[] normal = ExportScene.Direction(model.Normals[corner.Normal]);
                        double length = Math.Sqrt(normal.Sum(value => (double)value * value));
                        if (length == 0)
                            throw new InvalidDataException("The model contains a zero-length normal.");
                        normals[i] = normal.Select(value => (float)(value / length)).ToArray();
                        float[] uv = corner.Texture >= 0 ? model.Texcoords[corner.Texture] : new float[] { 0, 0 };
                        uvs[i] = new[] { uv[0], 1 - uv[1] };
                    }
                    var attributes = new JObject
                    {
                        ["POSITION"] = buffers.Floats(positions, "VEC3", true),
                        ["NORMAL"] = buffers.Floats(normals, "VEC3", false)
                    };
                    if (hasUvs)
                        attributes["TEXCOORD_0"] = buffers.Floats(uvs, "VEC2", false);
                    if (hasUvs && maps != null && maps.TryGetValue(material.Name, out MaterialMaps normalMaps) && normalMaps?.Normal != null)
                        attributes["TANGENT"] = buffers.Floats(Tangents(model, corners, positions, normals), "VEC4", false);
                    if (scene.Rig != null)
                    {
                        attributes["JOINTS_0"] = buffers.Joints(corners.Select(corner => scene.Rig.Joints[corner.Position]).ToArray());
                        attributes["WEIGHTS_0"] = buffers.Floats(corners.Select(corner => scene.Rig.Weights[corner.Position]).ToArray(), "VEC4", false);
                    }
                    var primitive = new JObject { ["attributes"] = attributes, ["material"] = MaterialIndex(material), ["mode"] = 4 };
                    if (morph)
                    {
                        var targets = new JArray();
                        foreach (FaceShape shape in scene.Face.Shapes)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var deltas = corners.Select(corner => shape.Deltas.TryGetValue(corner.Position, out float[] d)
                                ? scene.Delta(d) : new float[3]).ToArray();
                            targets.Add(new JObject { ["POSITION"] = buffers.Floats(deltas, "VEC3", true) });
                        }
                        primitive["targets"] = targets;
                    }
                    primitives.Add(primitive);
                }
                var mesh = new JObject { ["name"] = part.Key, ["primitives"] = primitives };
                if (morph)
                {
                    mesh["weights"] = new JArray(scene.Face.Shapes.Select(shape => shape.Weight));
                    mesh["extras"] = new JObject { ["targetNames"] = new JArray(scene.Face.Shapes.Select(shape => shape.Name)) };
                }
                meshes.Add(mesh);
            }

            var nodes = new JArray();
            var sceneNodes = new JArray();
            JArray skins = null;
            if (scene.Rig != null)
            {
                List<RigBone> bones = scene.Rig.Bones;
                var world = bones.Select(bone => scene.Point(bone.Position)).ToArray();
                int armature = nodes.Count;
                nodes.Add(new JObject { ["name"] = "Armature", ["children"] = new JArray() });
                sceneNodes.Add(armature);
                int first = nodes.Count;
                for (int i = 0; i < bones.Count; i++)
                {
                    float[] parent = bones[i].Parent >= 0 ? world[bones[i].Parent] : new float[3];
                    var node = new JObject
                    {
                        ["name"] = bones[i].Name,
                        ["translation"] = new JArray(world[i][0] - parent[0], world[i][1] - parent[1], world[i][2] - parent[2])
                    };
                    var children = Enumerable.Range(0, bones.Count).Where(child => bones[child].Parent == i).Select(child => first + child).ToList();
                    if (children.Count > 0)
                        node["children"] = new JArray(children);
                    nodes.Add(node);
                }
                ((JArray)nodes[armature]["children"]).Add(first);
                var inverseBind = world.Select(p => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -p[0], -p[1], -p[2], 1 }).ToArray();
                skins = new JArray(new JObject
                {
                    ["name"] = "Armature",
                    ["joints"] = new JArray(Enumerable.Range(first, bones.Count)),
                    ["skeleton"] = first,
                    ["inverseBindMatrices"] = buffers.Floats(inverseBind, "MAT4", false, null)
                });
            }
            for (int i = 0; i < meshes.Count; i++)
            {
                var node = new JObject { ["name"] = meshes[i]["name"], ["mesh"] = i };
                if (skins != null)
                    node["skin"] = 0;
                sceneNodes.Add(nodes.Count);
                nodes.Add(node);
            }

            Align(buffers.Writer);
            buffers.Writer.Flush();
            var buffer = new JObject { ["byteLength"] = buffers.Binary.Length };
            if (format == ExportFormat.Gltf)
                buffer["uri"] = Path.GetFileNameWithoutExtension(path) + ".bin";
            var document = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "RoPorter" },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = sceneNodes }),
                ["nodes"] = nodes,
                ["meshes"] = meshes,
                ["materials"] = materials, ["accessors"] = buffers.Accessors,
                ["bufferViews"] = buffers.Views, ["buffers"] = new JArray(buffer)
            };
            if (skins != null)
                document["skins"] = skins;
            if (extensionsUsed.Count > 0)
                document["extensionsUsed"] = new JArray(extensionsUsed.OrderBy(name => name, StringComparer.Ordinal));
            if (images.Count > 0)
            {
                document["images"] = images;
                document["textures"] = textureObjects;
            }
            cancellation.ThrowIfCancellationRequested();
            byte[] binary = buffers.Binary.ToArray();
            buffers.Writer.Dispose();
            buffers.Binary.Dispose();
            if (format == ExportFormat.Gltf)
            {
                File.WriteAllBytes(Path.ChangeExtension(path, ".bin"), binary);
                File.WriteAllText(path, document.ToString(Formatting.Indented), new UTF8Encoding(false));
                return;
            }
            using (var json = new MemoryStream())
            using (var jsonWriter = new BinaryWriter(json, Encoding.UTF8, true))
            {
                jsonWriter.Write(Encoding.UTF8.GetBytes(document.ToString(Formatting.None)));
                Align(jsonWriter, 0x20);
                jsonWriter.Flush();
                using (var output = new BinaryWriter(File.Create(path)))
                {
                    output.Write(0x46546c67u);
                    output.Write(2u);
                    output.Write(checked((uint)(12 + 8 + json.Length + 8 + binary.Length)));
                    output.Write(checked((uint)json.Length));
                    output.Write(0x4e4f534au);
                    output.Write(json.ToArray());
                    output.Write(checked((uint)binary.Length));
                    output.Write(0x004e4942u);
                    output.Write(binary);
                }
            }
        }
    }
}
