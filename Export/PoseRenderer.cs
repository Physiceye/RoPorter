using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Rbx2Source.Export
{
    // Draws exported reference meshes (.smd) posed by an animation frame, for pictures such as Left 4 Dead 2's HUD portraits:
    // linear-blend skinning, an orthographic camera fitted around chosen bones, a depth buffer, textures with their alpha
    // cut out, and soft lighting, supersampled.
    internal sealed class PoseRenderer
    {
        // A material's look: its texture (ARGB pixels) or, without one, its colour.
        public sealed class Look
        {
            public int[] Pixels;
            public int Width, Height;
            public float[] Color = { 1, 1, 1 };
            public float Opacity = 1;
        }

        private sealed class Corner
        {
            public double[] Position, Normal;
            public double U, V;
            public int[] Bones;
            public double[] Weights;
        }

        private readonly List<string> names = new List<string>();
        private readonly List<int> parents = new List<int>();
        private readonly List<(double[] Position, double[] Rotation)> bind = new List<(double[], double[])>();
        private readonly List<(string Material, Corner[] Corners)> triangles = new List<(string, Corner[])>();

        public IReadOnlyList<string> Bones => names;

        // An animation frame as the game plays it on the export: each animated bone takes the frame's transform plus the
        // model's own difference from the animation model's rest pose (the "proportions" delta every exported model plays).
        public Dictionary<string, (double[] Position, double[] Rotation)> Animate(IEnumerable<ValvePlayer.Bone> frame, IEnumerable<ValvePlayer.Bone> rest)
        {
            Dictionary<string, ValvePlayer.Bone> restByName = rest.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
            var pose = new Dictionary<string, (double[], double[])>(StringComparer.Ordinal);
            foreach (ValvePlayer.Bone bone in frame)
            {
                int index = names.IndexOf(bone.Name);
                if (index < 0 || !restByName.TryGetValue(bone.Name, out ValvePlayer.Bone original))
                    continue;
                var (position, rotation) = bind[index];
                pose[bone.Name] = (Matrix.Add(bone.LocalPosition, Matrix.Subtract(position, original.LocalPosition)),
                    Matrix.Multiply(bone.LocalRotation, Matrix.Multiply(Matrix.Transpose(original.LocalRotation), rotation)));
            }
            return pose;
        }

        // Reads meshes written on one skeleton (the first file's nodes and pose).
        public static PoseRenderer Read(IEnumerable<string> files)
        {
            var renderer = new PoseRenderer();
            foreach (string file in files.Where(File.Exists))
                renderer.Add(File.ReadAllLines(file));
            return renderer;
        }

        private static double D(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        private void Add(string[] lines)
        {
            bool first = names.Count == 0;
            var map = new Dictionary<int, int>();
            string section = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("version", StringComparison.Ordinal))
                    continue;
                if (section == null)
                {
                    section = line;
                    continue;
                }
                if (line == "end")
                {
                    section = null;
                    continue;
                }
                if (section == "nodes")
                {
                    int quote = line.IndexOf('"'), close = line.LastIndexOf('"');
                    int index = int.Parse(line.Substring(0, quote).Trim(), CultureInfo.InvariantCulture);
                    string name = line.Substring(quote + 1, close - quote - 1);
                    int parent = int.Parse(line.Substring(close + 1).Trim(), CultureInfo.InvariantCulture);
                    if (first)
                    {
                        names.Add(name);
                        parents.Add(parent);
                        bind.Add((new double[3], Matrix.Identity));
                    }
                    map[index] = names.IndexOf(name);
                }
                else if (section == "skeleton")
                {
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (!first || parts[0] == "time")
                        continue;
                    int index = map[int.Parse(parts[0], CultureInfo.InvariantCulture)];
                    if (index >= 0)
                        bind[index] = (new[] { D(parts[1]), D(parts[2]), D(parts[3]) }, Matrix.FromEuler(D(parts[4]), D(parts[5]), D(parts[6])));
                }
                else if (section == "triangles" && i + 3 < lines.Length)
                {
                    var corners = new Corner[3];
                    for (int k = 0; k < 3; k++)
                    {
                        string[] parts = lines[i + 1 + k].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        var corner = new Corner
                        {
                            Position = new[] { D(parts[1]), D(parts[2]), D(parts[3]) },
                            Normal = new[] { D(parts[4]), D(parts[5]), D(parts[6]) },
                            U = D(parts[7]),
                            V = D(parts[8])
                        };
                        int links = parts.Length > 9 ? int.Parse(parts[9], CultureInfo.InvariantCulture) : 0;
                        if (links == 0)
                        {
                            corner.Bones = new[] { Math.Max(0, map[int.Parse(parts[0], CultureInfo.InvariantCulture)]) };
                            corner.Weights = new[] { 1.0 };
                        }
                        else
                        {
                            corner.Bones = Enumerable.Range(0, links).Select(l => Math.Max(0, map[int.Parse(parts[10 + 2 * l], CultureInfo.InvariantCulture)])).ToArray();
                            corner.Weights = Enumerable.Range(0, links).Select(l => D(parts[11 + 2 * l])).ToArray();
                        }
                        corners[k] = corner;
                    }
                    triangles.Add((line, corners));
                    i += 3;
                }
            }
        }

        private List<(double[] Position, double[] Rotation)> World(Func<int, (double[] Position, double[] Rotation)> local)
        {
            var world = new List<(double[] Position, double[] Rotation)>();
            for (int i = 0; i < names.Count; i++)
            {
                var (p, r) = local(i);
                int parent = parents[i];
                world.Add(parent < 0 || parent >= i ? (p, r)
                    : (Matrix.Add(world[parent].Position, Matrix.Apply(world[parent].Rotation, p)), Matrix.Multiply(world[parent].Rotation, r)));
            }
            return world;
        }

        // Renders the meshes with some bones' local transforms replaced (by name; others keep the mesh's own pose).
        // The camera looks along the view's yaw (degrees about Z, 0 = from +X toward -X) tilted down by pitch, and fits the
        // corners mainly carried by the focus bones (all of them for null), with a margin (a fraction of the picture). A figure
        // narrower than the picture sits by align: 0 at the left, 0.5 in the middle, 1 at the right.
        public Picture Render(IReadOnlyDictionary<string, (double[] Position, double[] Rotation)> pose, IReadOnlyDictionary<string, Look> looks,
            double yaw, double pitch, Func<string, bool> focus, double margin, int width, int height, int supersample = 3, double align = 0.5)
        {
            List<(double[] Position, double[] Rotation)> rest = World(i => bind[i]);
            List<(double[] Position, double[] Rotation)> posed = World(i => pose != null && pose.TryGetValue(names[i], out var p) ? p : bind[i]);
            var skin = new (double[] Rotation, double[] Offset)[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                double[] rotation = Matrix.Multiply(posed[i].Rotation, Matrix.Transpose(rest[i].Rotation));
                skin[i] = (rotation, Matrix.Subtract(posed[i].Position, Matrix.Apply(rotation, rest[i].Position)));
            }

            // View: x right, y up, z away from the camera.
            double a = yaw * Math.PI / 180, b = pitch * Math.PI / 180;
            double[] right = { -Math.Sin(a), Math.Cos(a), 0 };
            double[] forward = { -Math.Cos(a) * Math.Cos(b), -Math.Sin(a) * Math.Cos(b), -Math.Sin(b) };
            double[] up = Cross(right, forward);
            double[] View(double[] p) => new[] { Matrix.Dot(p, right), Matrix.Dot(p, up), Matrix.Dot(p, forward) };

            var screen = new List<(Look Look, double[][] Points, double[][] Normals, double[][] Uvs)>();
            double left = double.MaxValue, top = double.MinValue, rightEdge = double.MinValue, bottom = double.MaxValue;
            foreach (var (material, corners) in triangles)
            {
                if (looks == null || !looks.TryGetValue(material, out Look look))
                    look = new Look();
                if (look.Opacity < 0.25f)
                    continue;
                var points = new double[3][];
                var normals = new double[3][];
                for (int k = 0; k < 3; k++)
                {
                    Corner c = corners[k];
                    double[] p = new double[3], n = new double[3];
                    for (int l = 0; l < c.Bones.Length; l++)
                    {
                        var (rotation, offset) = skin[c.Bones[l]];
                        p = Matrix.Add(p, Matrix.Scale(Matrix.Add(Matrix.Apply(rotation, c.Position), offset), c.Weights[l]));
                        n = Matrix.Add(n, Matrix.Scale(Matrix.Apply(rotation, c.Normal), c.Weights[l]));
                    }
                    points[k] = View(p);
                    double length = Matrix.Length(n);
                    normals[k] = View(length > 0 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 });
                    int main = c.Bones[Array.IndexOf(c.Weights, c.Weights.Max())];
                    if (focus == null || focus(names[main]))
                    {
                        left = Math.Min(left, points[k][0]);
                        rightEdge = Math.Max(rightEdge, points[k][0]);
                        bottom = Math.Min(bottom, points[k][1]);
                        top = Math.Max(top, points[k][1]);
                    }
                }
                screen.Add((look, points, normals, corners.Select(c => new[] { c.U, c.V }).ToArray()));
            }
            if (left > rightEdge)
                throw new InvalidDataException("Nothing to draw in the picture.");

            int w = width * supersample, h = height * supersample;
            double usable = 1 - 2 * margin;
            double scale = Math.Min(w * usable / Math.Max(1e-6, rightEdge - left), h * usable / Math.Max(1e-6, top - bottom));
            double startX = w * margin + (w * usable - (rightEdge - left) * scale) * align, centerY = (top + bottom) / 2;
            var color = new int[w * h];
            var depth = new double[w * h];
            for (int i = 0; i < depth.Length; i++)
                depth[i] = double.MaxValue;
            double[] light = Normalize(new[] { 0.45, -0.6, 0.65 });
            foreach (var (look, points, normals, uvs) in screen)
            {
                var sx = new double[3];
                var sy = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    sx[k] = startX + (points[k][0] - left) * scale;
                    sy[k] = h / 2.0 - (points[k][1] - centerY) * scale;
                }
                double area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
                if (Math.Abs(area) < 1e-9)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor(sx.Min())), x1 = Math.Min(w - 1, (int)Math.Ceiling(sx.Max()));
                int y0 = Math.Max(0, (int)Math.Floor(sy.Min())), y1 = Math.Min(h - 1, (int)Math.Ceiling(sy.Max()));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double w0 = ((sx[1] - px) * (sy[2] - py) - (sx[2] - px) * (sy[1] - py)) / area;
                        double w1 = ((sx[2] - px) * (sy[0] - py) - (sx[0] - px) * (sy[2] - py)) / area;
                        double w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0)
                            continue;
                        double z = w0 * points[0][2] + w1 * points[1][2] + w2 * points[2][2];
                        int index = y * w + x;
                        // Decals (a classic face) lie on the surface under them and are written after it, so near-ties go to them.
                        if (z > depth[index] + 0.02)
                            continue;
                        double[] rgb = { look.Color[0], look.Color[1], look.Color[2] };
                        if (look.Pixels != null)
                        {
                            double u = w0 * uvs[0][0] + w1 * uvs[1][0] + w2 * uvs[2][0], v = w0 * uvs[0][1] + w1 * uvs[1][1] + w2 * uvs[2][1];
                            int texel = Sample(look, u, 1 - v);
                            if (((texel >> 24) & 255) < 128)
                                continue;
                            rgb = new[] { ((texel >> 16) & 255) / 255.0, ((texel >> 8) & 255) / 255.0, (texel & 255) / 255.0 };
                        }
                        double[] n = Normalize(new[]
                        {
                            w0 * normals[0][0] + w1 * normals[1][0] + w2 * normals[2][0],
                            w0 * normals[0][1] + w1 * normals[1][1] + w2 * normals[2][1],
                            w0 * normals[0][2] + w1 * normals[1][2] + w2 * normals[2][2]
                        });
                        if (n[2] > 0)
                            n = Matrix.Scale(n, -1);
                        double shade = 0.5 + 0.6 * Math.Max(0, -Matrix.Dot(n, light));
                        depth[index] = Math.Min(z, depth[index]);
                        color[index] = unchecked((int)0xFF000000) | (Byte(rgb[0] * shade) << 16) | (Byte(rgb[1] * shade) << 8) | Byte(rgb[2] * shade);
                    }
            }

            // Averages each block of samples (weighting colours by coverage) into one pixel.
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double r = 0, g = 0, bl = 0, alpha = 0;
                    for (int j = 0; j < supersample; j++)
                        for (int i = 0; i < supersample; i++)
                        {
                            int c = color[(y * supersample + j) * w + x * supersample + i];
                            if (c == 0)
                                continue;
                            alpha++;
                            r += (c >> 16) & 255;
                            g += (c >> 8) & 255;
                            bl += c & 255;
                        }
                    if (alpha > 0)
                        pixels[y * width + x] = ((int)Math.Round(255 * alpha / (supersample * supersample)) << 24) |
                            ((int)(r / alpha) << 16) | ((int)(g / alpha) << 8) | (int)(bl / alpha);
                }
            var picture = new Picture(width, height);
            picture.SetPixels(pixels);
            return picture;
        }

        private static int Sample(Look look, double u, double v)
        {
            u -= Math.Floor(u);
            v -= Math.Floor(v);
            int x = Math.Min(look.Width - 1, (int)(u * look.Width)), y = Math.Min(look.Height - 1, (int)(v * look.Height));
            return look.Pixels[y * look.Width + x];
        }

        private static int Byte(double value) => (int)Math.Max(0, Math.Min(255, Math.Round(value * 255)));

        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return length > 0 ? new[] { v[0] / length, v[1] / length, v[2] / length } : new double[] { 0, 0, 1 };
        }
    }
}
