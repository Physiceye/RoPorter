using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Rbx2Source.Export
{
    public sealed class ObjCorner
    {
        public int Position;
        public int Texture = -1;
        public int Normal = -1;
    }

    public sealed class ObjTriangle
    {
        public string Material;
        public string Group = "";
        public ObjCorner[] Corners;
    }

    public sealed class ObjMaterial
    {
        public string Name;
        public float[] Diffuse = { 1, 1, 1 };
        public float Opacity = 1;
        public string Texture;

        // Roblox's "overlay" alpha mode blends the texture over the part colour instead of making it see-through.
        public bool Overlay;
        // The material named an alpha mode (rbx_alphamode); without one, the texture's own alpha decides.
        public bool AlphaModeGiven;
        // Drawn from both sides (an accessory whose MeshPart is DoubleSided); Roblox culls back faces otherwise.
        public bool DoubleSided;

        // PBR maps from Roblox SurfaceAppearances: a tangent-space normal map (map_Bump), a packed map (map_Ns:
        // R = metalness, G = roughness, B = emissive mask), and the emissive colour (map_Ke) scaled by Ke.
        public string NormalTexture;
        public string PackedTexture;
        public string EmissiveTexture;
        public float[] Emissive;
    }

    public sealed class WavefrontModel
    {
        public List<float[]> Positions { get; } = new List<float[]>();
        public List<float[]> Normals { get; } = new List<float[]>();
        public List<float[]> Texcoords { get; } = new List<float[]>();
        public List<ObjTriangle> Triangles { get; } = new List<ObjTriangle>();
        public Dictionary<string, ObjMaterial> Materials { get; } = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);

        internal static IEnumerable<KeyValuePair<string, string>> Lines(string text)
        {
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int comment = line.IndexOf('#');
                    if (comment >= 0)
                        line = line.Substring(0, comment);
                    line = line.Trim();
                    if (line.Length == 0)
                        continue;
                    int separator = line.IndexOfAny(new[] { ' ', '\t' });
                    yield return separator < 0
                        ? new KeyValuePair<string, string>(line, "")
                        : new KeyValuePair<string, string>(line.Substring(0, separator), line.Substring(separator + 1).Trim());
                }
            }
        }

        private static string[] Words(string value)
        {
            return value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        // Map statements may carry options (e.g. "-bm 1 file"); the file is the last word.
        private static string MapFile(string value)
        {
            string[] words = Words(value);
            return words.Length == 0 ? null : words[words.Length - 1].Trim('"');
        }

        private static float Number(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                || float.IsNaN(number) || float.IsInfinity(number))
                throw new InvalidDataException("Invalid OBJ/MTL number: " + value);
            return number;
        }

        private static float[] Vector(string value, int size)
        {
            string[] words = Words(value);
            if (words.Length < size)
                throw new InvalidDataException("Incomplete OBJ/MTL vector: " + value);
            return words.Take(size).Select(Number).ToArray();
        }

        private static int Index(string text, int count)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
                throw new InvalidDataException("Invalid OBJ index: " + text);
            int index = value < 0 ? count + value : value - 1;
            if (index < 0 || index >= count)
                throw new InvalidDataException("OBJ index is out of range: " + text);
            return index;
        }

        public static WavefrontModel Parse(string obj, string mtl, CancellationToken cancellation = default)
        {
            var model = new WavefrontModel();
            ObjMaterial material = null;
            foreach (var line in Lines(mtl))
            {
                cancellation.ThrowIfCancellationRequested();
                if (line.Key == "newmtl")
                {
                    if (line.Value.Length == 0 || model.Materials.ContainsKey(line.Value))
                        throw new InvalidDataException("Missing or duplicate MTL material name.");
                    material = new ObjMaterial { Name = line.Value };
                    model.Materials.Add(material.Name, material);
                }
                else if (material != null)
                {
                    switch (line.Key)
                    {
                        case "Kd":
                            material.Diffuse = Vector(line.Value, 3);
                            if (material.Diffuse.Any(component => component < 0 || component > 1))
                                throw new InvalidDataException("MTL diffuse color must be between zero and one.");
                            break;
                        case "d": material.Opacity = Number(line.Value); break;
                        case "Tr": material.Opacity = 1 - Number(line.Value); break;
                        case "map_Kd":
                            if (line.Value.StartsWith("-", StringComparison.Ordinal))
                                throw new InvalidDataException("MTL texture options are not supported.");
                            material.Texture = line.Value.Trim('"');
                            break;
                        case "rbx_alphamode":
                            material.Overlay = line.Value.Equals("overlay", StringComparison.OrdinalIgnoreCase);
                            material.AlphaModeGiven = true;
                            break;
                        case "map_Bump": case "map_bump": case "bump": case "norm":
                            material.NormalTexture = MapFile(line.Value);
                            break;
                        case "map_Ns":
                            material.PackedTexture = MapFile(line.Value);
                            break;
                        case "map_Ke":
                            material.EmissiveTexture = MapFile(line.Value);
                            break;
                        case "Ke":
                            material.Emissive = Vector(line.Value, 3);
                            if (material.Emissive.Any(component => component < 0))
                                throw new InvalidDataException("MTL emissive color cannot be negative.");
                            break;
                    }
                    if (material.Opacity < 0 || material.Opacity > 1)
                        throw new InvalidDataException("MTL opacity must be between zero and one.");
                }
            }

            string currentMaterial = null;
            string currentGroup = "";
            foreach (var line in Lines(obj))
            {
                cancellation.ThrowIfCancellationRequested();
                switch (line.Key)
                {
                    case "g":
                    case "o":
                        currentGroup = line.Value;
                        break;
                    case "v": model.Positions.Add(Vector(line.Value, 3)); break;
                    case "vn": model.Normals.Add(Vector(line.Value, 3)); break;
                    case "vt": model.Texcoords.Add(Vector(line.Value, 2)); break;
                    case "usemtl": currentMaterial = line.Value; break;
                    case "f":
                        var corners = Words(line.Value).Select(word =>
                        {
                            string[] indices = word.Split('/');
                            if (indices.Length > 3)
                                throw new InvalidDataException("Invalid OBJ face corner: " + word);
                            return new ObjCorner
                            {
                                Position = Index(indices[0], model.Positions.Count),
                                Texture = indices.Length > 1 && indices[1].Length > 0 ? Index(indices[1], model.Texcoords.Count) : -1,
                                Normal = indices.Length > 2 && indices[2].Length > 0 ? Index(indices[2], model.Normals.Count) : -1
                            };
                        }).ToArray();
                        if (corners.Length < 3)
                            throw new InvalidDataException("An OBJ face needs at least three corners.");
                        if (corners.Length > 3)
                            ValidateConvexPolygon(corners, model.Positions);
                        string name = currentMaterial ?? "__default";
                        if (!model.Materials.ContainsKey(name))
                        {
                            if (currentMaterial != null)
                                throw new InvalidDataException("OBJ refers to an undefined material: " + name);
                            model.Materials.Add(name, new ObjMaterial { Name = name });
                        }
                        // Roblox's renderer emits triangles. Convex polygons are accepted for imported OBJ data.
                        for (int i = 1; i < corners.Length - 1; i++)
                        {
                            var triangle = new ObjTriangle
                            {
                                Material = name,
                                Group = currentGroup,
                                Corners = new[] { corners[0], corners[i], corners[i + 1] }
                            };
                            if (triangle.Corners.Any(corner => corner.Normal < 0))
                            {
                                float[] a = model.Positions[triangle.Corners[0].Position];
                                float[] b = model.Positions[triangle.Corners[1].Position];
                                float[] c = model.Positions[triangle.Corners[2].Position];
                                double x = (b[1] - a[1]) * (double)(c[2] - a[2]) - (b[2] - a[2]) * (double)(c[1] - a[1]);
                                double y = (b[2] - a[2]) * (double)(c[0] - a[0]) - (b[0] - a[0]) * (double)(c[2] - a[2]);
                                double z = (b[0] - a[0]) * (double)(c[1] - a[1]) - (b[1] - a[1]) * (double)(c[0] - a[0]);
                                double length = Math.Sqrt(x * x + y * y + z * z);
                                if (length == 0)
                                    throw new InvalidDataException("OBJ contains a degenerate face with no normal.");
                                int normal = model.Normals.Count;
                                model.Normals.Add(new[] { (float)(x / length), (float)(y / length), (float)(z / length) });
                                triangle.Corners = triangle.Corners.Select(corner => new ObjCorner
                                {
                                    Position = corner.Position, Texture = corner.Texture,
                                    Normal = corner.Normal < 0 ? normal : corner.Normal
                                }).ToArray();
                            }
                            model.Triangles.Add(triangle);
                        }
                        break;
                }
            }
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("The Roblox model contains no faces.");
            foreach (var triangle in model.Triangles)
                if (model.Materials[triangle.Material].Texture != null && triangle.Corners.Any(corner => corner.Texture < 0))
                    throw new InvalidDataException("A textured OBJ face has no UV coordinates.");
            return model;
        }

        private static void ValidateConvexPolygon(ObjCorner[] corners, List<float[]> positions)
        {
            var normal = new double[3];
            for (int i = 0; i < corners.Length; i++)
            {
                float[] a = positions[corners[i].Position];
                float[] b = positions[corners[(i + 1) % corners.Length].Position];
                normal[0] += (a[1] - (double)b[1]) * (a[2] + (double)b[2]);
                normal[1] += (a[2] - (double)b[2]) * (a[0] + (double)b[0]);
                normal[2] += (a[0] - (double)b[0]) * (a[1] + (double)b[1]);
            }
            int axis = Array.IndexOf(normal.Select(Math.Abs).ToArray(), normal.Max(value => Math.Abs(value)));
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;
            double direction = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                float[] a = positions[corners[i].Position];
                float[] b = positions[corners[(i + 1) % corners.Length].Position];
                float[] c = positions[corners[(i + 2) % corners.Length].Position];
                double cross = (b[u] - (double)a[u]) * (c[v] - (double)b[v]) -
                    (b[v] - (double)a[v]) * (c[u] - (double)b[u]);
                if (cross == 0)
                    continue;
                if (direction != 0 && Math.Sign(cross) != Math.Sign(direction))
                    throw new InvalidDataException("Concave OBJ polygons are not supported. Triangulate the model first.");
                direction = cross;
            }
            if (direction == 0)
                throw new InvalidDataException("OBJ contains a degenerate polygon.");
        }
    }
}
