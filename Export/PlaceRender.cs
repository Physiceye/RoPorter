using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rbx2Source.Export
{
    // A picture of a map for Garry's Mod's map list and the Workshop: its triangles (Source coordinates, flat colours) seen from
    // above at an angle, lit from the sun's direction.
    internal sealed class PlaceRender
    {
        private readonly List<(double[] A, double[] B, double[] C, int Color)> triangles = new List<(double[], double[], double[], int)>();
        // Textures of textured triangles (by triangle index): the texture's pixels and each corner's texture coordinates.
        private readonly Dictionary<int, (Texel Texture, float[][] Uvs)> textured = new Dictionary<int, (Texel, float[][])>();

        private sealed class Texel
        {
            public int Width, Height;
            public int[] Pixels;

            // The texture's colour at a texture coordinate (repeating, V up), over the colour behind it by the texel's alpha.
            public int Sample(double u, double v, int behind)
            {
                int x = (int)Math.Floor((u - Math.Floor(u)) * Width), y = (int)Math.Floor((1 - (v - Math.Floor(v))) * Height);
                int pixel = Pixels[Math.Min(Height - 1, Math.Max(0, y)) * Width + Math.Min(Width - 1, Math.Max(0, x))];
                double a = ((pixel >> 24) & 255) / 255.0;
                int Mix(int shift) => (int)Math.Round(((pixel >> shift) & 255) * a + ((behind >> shift) & 255) * (1 - a));
                return unchecked((int)0xFF000000) | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
            }
        }

        // A picture of a built model (a model file with its meshes and textures downloaded): its triangles with their
        // textures, seen from above at an angle. Null for a model with nothing to draw.
        public static Picture Render(WavefrontModel model, IReadOnlyDictionary<string, byte[]> textures, int size)
        {
            var render = new PlaceRender();
            var loaded = new Dictionary<string, Texel>(StringComparer.Ordinal);
            Texel TextureOf(ObjMaterial material)
            {
                if (material?.Texture == null || textures == null || !textures.TryGetValue(material.Texture, out byte[] image))
                    return null;
                if (!loaded.TryGetValue(material.Texture, out Texel texel))
                {
                    try
                    {
                        using (Picture picture = Picture.Load(image))
                        using (Picture small = picture.Width > 256 || picture.Height > 256 ? picture.Resized(Math.Min(256, picture.Width), Math.Min(256, picture.Height)) : null)
                        {
                            Picture source = small ?? picture;
                            texel = new Texel { Width = source.Width, Height = source.Height, Pixels = source.GetPixels() };
                        }
                    }
                    catch (InvalidDataException)
                    {
                        texel = null;
                    }
                    loaded[material.Texture] = texel;
                }
                return texel;
            }
            double[] Axes(float[] p) => new double[] { -p[2], -p[0], p[1] };
            foreach (ObjTriangle triangle in model.Triangles)
            {
                if (triangle.Corners.Length != 3)
                    continue;
                model.Materials.TryGetValue(triangle.Material ?? "", out ObjMaterial material);
                if (material != null && material.Opacity < 0.05f)
                    continue;
                float[] color = material?.Diffuse ?? new float[] { 0.65f, 0.65f, 0.65f };
                Texel texture = TextureOf(material);
                double[][] points = triangle.Corners.Select(c => Axes(model.Positions[c.Position])).ToArray();
                if (texture != null && triangle.Corners.All(c => c.Texture >= 0 && c.Texture < model.Texcoords.Count))
                    render.textured[render.triangles.Count] = (texture, triangle.Corners.Select(c => model.Texcoords[c.Texture]).ToArray());
                // A texture shows over white, unless it is an overlay on the part's colour.
                render.AddTriangle(points[0], points[1], points[2], texture != null && !(material?.Overlay ?? false) ? new float[] { 1, 1, 1 } : color);
                for (int axis = 0; axis < 3; axis++)
                {
                    render.low[axis] = Math.Min(render.low[axis], points.Min(p => p[axis]));
                    render.high[axis] = Math.Max(render.high[axis], points.Max(p => p[axis]));
                }
            }
            if (render.triangles.Count == 0)
                return null;
            render.AddPart(render.low, render.high);
            return render.Draw(size, new[] { 0.35, 0.25, -1 });
        }

        private readonly double[] low = { double.MaxValue, double.MaxValue, double.MaxValue }, high = { double.MinValue, double.MinValue, double.MinValue };
        // What the picture frames: the parts, leaving out ones so wide they are the ground (a baseplate) unless nothing else is.
        private readonly List<(double[] Min, double[] Max)> parts = new List<(double[], double[])>();

        private static int Argb(float[] color) =>
            unchecked((int)0xFF000000) | (Channel(color[0]) << 16) | (Channel(color[1]) << 8) | Channel(color[2]);

        private static int Channel(float value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);

        public void AddPolygon(IReadOnlyList<double[]> points, float[] color)
        {
            int argb = Argb(color);
            for (int i = 1; i + 1 < points.Count; i++)
                triangles.Add((points[0], points[i], points[i + 1], argb));
        }

        public void AddTriangle(double[] a, double[] b, double[] c, float[] color) => triangles.Add((a, b, c, Argb(color)));

        public void AddPart(double[] min, double[] max) => parts.Add((min, max));

        // A quick picture of a model or place file, for the preview: its blocks, wedges, spheres and cylinders in their colours,
        // its meshes and unions as boxes their size, and its terrain's ground. Nothing is downloaded.
        public static Picture Preview(ModelContents contents, int size)
        {
            var render = new PlaceRender();
            // Roblox's axes to the picture's (Z up), as for maps.
            double[] Axes(double x, double y, double z) => new[] { -z, -x, y };
            foreach (PlacedPart part in contents.Parts.Where(p => p.Visible))
            {
                bool boxed = part.Shape == PartShape.Mesh || part.Shape == PartShape.Union;
                PartShape shape = boxed ? PartShape.Box : part.Shape;
                float[] scale = boxed ? part.Size : part.Scale, offset = boxed ? new float[3] : part.Offset, c = part.CFrame;
                bool mirrored = scale[0] * scale[1] * scale[2] < 0;
                var low = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                var high = new[] { double.MinValue, double.MinValue, double.MinValue };
                foreach (float[][] polygon in ModelFileBuilder.Polygons(shape, 8, out _))
                {
                    var points = polygon.Select(v =>
                    {
                        double x = v[0] * scale[0] + offset[0], y = v[1] * scale[1] + offset[1], z = v[2] * scale[2] + offset[2];
                        double[] p = Axes(c[0] + c[3] * x + c[4] * y + c[5] * z, c[1] + c[6] * x + c[7] * y + c[8] * z, c[2] + c[9] * x + c[10] * y + c[11] * z);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            low[axis] = Math.Min(low[axis], p[axis]);
                            high[axis] = Math.Max(high[axis], p[axis]);
                        }
                        return p;
                    }).ToList();
                    if (mirrored)
                        points.Reverse();
                    render.AddPolygon(points, part.Color);
                }
                render.AddPart(low, high);
            }
            TerrainSurface ground = contents.Ground;
            if (ground != null)
            {
                const float voxel = RobloxTerrain.VoxelSize;
                // The ground in blocks of columns, about 160 across, each in its columns' average colour: a quad per column was
                // far smaller than a pixel on a big place, and its bumps and mixed materials came out as speckles.
                int step = Math.Max(1, (int)Math.Ceiling(Math.Max(ground.Width, ground.Depth) / 160.0));
                for (int bz = 0; bz < ground.Depth; bz += step)
                    for (int bx = 0; bx < ground.Width; bx += step)
                    {
                        int x1 = Math.Min(bx + step, ground.Width), z1 = Math.Min(bz + step, ground.Depth);
                        var color = new float[3];
                        float height = 0;
                        int count = 0;
                        for (int z = bz; z < z1; z++)
                            for (int x = bx; x < x1; x++)
                                if (ground.Has(x, z))
                                {
                                    float[] column = contents.Terrain.Color(ground.Materials[x + ground.Width * z]);
                                    for (int channel = 0; channel < 3; channel++)
                                        color[channel] += column[channel];
                                    height += ground.Heights[x + ground.Width * z];
                                    count++;
                                }
                        if (count == 0)
                            continue;
                        for (int channel = 0; channel < 3; channel++)
                            color[channel] /= count;
                        height /= count;
                        // Half a stud up, as the map's terrain is lifted: ground level with a part's top (a baseplate under the
                        // terrain) otherwise fought it pixel by pixel.
                        double[] Corner(int cx, int cz)
                        {
                            float y = ground.CornerHeight(cx, cz, out bool any);
                            return Axes((ground.MinX + cx) * voxel, (any ? y : height) + 0.5, (ground.MinZ + cz) * voxel);
                        }
                        // Counter-clockwise seen from above, in the picture's axes.
                        render.AddPolygon(new[] { Corner(bx, bz), Corner(bx, z1), Corner(x1, z1), Corner(x1, bz) }, color);
                    }
            }
            return render.triangles.Count == 0 ? null : render.Draw(size, new[] { 0.35, 0.25, -1 });
        }

        public Picture Draw(int size, double[] light)
        {
            const double yaw = 225 * Math.PI / 180, pitch = 35 * Math.PI / 180;
            // The camera looks down at the map from the south-east; right and up span the picture.
            double[] view = { Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), -Math.Sin(pitch) };
            double[] right = Normalize(Cross(view, new double[] { 0, 0, 1 })), up = Cross(right, view);
            double[] toLight = Normalize(light.Select(v => -v).ToArray());

            double footprint(double[] min, double[] max) => (max[0] - min[0]) * (max[1] - min[1]);
            var framed = parts;
            if (parts.Count > 1)
            {
                double all = footprint(new[] { parts.Min(p => p.Min[0]), parts.Min(p => p.Min[1]) }, new[] { parts.Max(p => p.Max[0]), parts.Max(p => p.Max[1]) });
                var smaller = parts.Where(p => footprint(p.Min, p.Max) < 0.25 * all).ToList();
                if (smaller.Count > 0)
                    framed = smaller;
            }
            // Each part's extent across and up the picture; the picture spans most of them, so a few far-off parts don't
            // shrink the rest to a speck.
            var spans = framed.Select(p =>
            {
                var corners = Enumerable.Range(0, 8).Select(c => new[] { (c & 1) != 0 ? p.Max[0] : p.Min[0], (c & 2) != 0 ? p.Max[1] : p.Min[1], (c & 4) != 0 ? p.Max[2] : p.Min[2] }).ToList();
                return (Left: corners.Min(c => Dot(c, right)), Right: corners.Max(c => Dot(c, right)), Bottom: corners.Min(c => Dot(c, up)), Top: corners.Max(c => Dot(c, up)));
            }).ToList();
            if (spans.Count == 0)
                spans = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Select(p => (Dot(p, right), Dot(p, right), Dot(p, up), Dot(p, up))).ToList();
            double Percentile(IEnumerable<double> values, double fraction)
            {
                var sorted = values.OrderBy(v => v).ToList();
                return sorted.Count == 0 ? 0 : sorted[(int)(fraction * (sorted.Count - 1))];
            }
            double left = Percentile(spans.Select(s => s.Left), 0.03), rightmost = Percentile(spans.Select(s => s.Right), 0.97);
            double bottom = Percentile(spans.Select(s => s.Bottom), 0.03), top = Percentile(spans.Select(s => s.Top), 0.97);
            double middleAcross = (left + rightmost) / 2, middleUp = (bottom + top) / 2;
            double reach = Math.Max(rightmost - left, top - bottom) / 2 * 1.12;
            double scale = size / 2.0 / Math.Max(1, reach);

            var pixels = new int[size * size];
            var depth = new double[size * size];
            for (int y = 0; y < size; y++)
            {
                // Sky: lighter towards the horizon.
                float t = (float)y / size;
                int sky = Argb(new[] { 0.45f + 0.35f * t, 0.65f + 0.25f * t, 0.95f });
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = sky;
                    depth[y * size + x] = double.MaxValue;
                }
            }
            for (int t = 0; t < triangles.Count; t++)
            {
                var (a, b, c, color) = triangles[t];
                textured.TryGetValue(t, out var texture);
                double[] normal = Cross(Subtract(b, a), Subtract(c, a));
                double length = Math.Sqrt(Dot(normal, normal));
                if (length < 1e-9 || Dot(normal, view) >= 0)
                    continue;
                double shade = 0.5 + 0.5 * Math.Max(0, Dot(normal, toLight) / length);
                int shaded = unchecked((int)0xFF000000) | ((int)(((color >> 16) & 255) * shade) << 16) | ((int)(((color >> 8) & 255) * shade) << 8) | (int)((color & 255) * shade);
                var q = new[] { a, b, c }.Select(p =>
                    new[] { size / 2.0 + (Dot(p, right) - middleAcross) * scale, size / 2.0 - (Dot(p, up) - middleUp) * scale, Dot(p, view) }).ToArray();
                double area = (q[1][0] - q[0][0]) * (q[2][1] - q[0][1]) - (q[2][0] - q[0][0]) * (q[1][1] - q[0][1]);
                if (Math.Abs(area) < 1e-12)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor(q.Min(p => p[0]))), x1 = Math.Min(size - 1, (int)Math.Ceiling(q.Max(p => p[0])));
                int y0 = Math.Max(0, (int)Math.Floor(q.Min(p => p[1]))), y1 = Math.Min(size - 1, (int)Math.Ceiling(q.Max(p => p[1])));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double w0 = ((q[1][0] - px) * (q[2][1] - py) - (q[2][0] - px) * (q[1][1] - py)) / area;
                        double w1 = ((q[2][0] - px) * (q[0][1] - py) - (q[0][0] - px) * (q[2][1] - py)) / area;
                        double w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0)
                            continue;
                        double z = w0 * q[0][2] + w1 * q[1][2] + w2 * q[2][2];
                        int index = y * size + x;
                        if (z < depth[index])
                        {
                            depth[index] = z;
                            if (texture.Texture == null)
                                pixels[index] = shaded;
                            else
                            {
                                float[][] uv = texture.Uvs;
                                int texel = texture.Texture.Sample(w0 * uv[0][0] + w1 * uv[1][0] + w2 * uv[2][0], w0 * uv[0][1] + w1 * uv[1][1] + w2 * uv[2][1], color);
                                pixels[index] = unchecked((int)0xFF000000) | ((int)(((texel >> 16) & 255) * shade) << 16) |
                                    ((int)(((texel >> 8) & 255) * shade) << 8) | (int)((texel & 255) * shade);
                            }
                        }
                    }
            }
            var picture = new Picture(size, size);
            picture.SetPixels(pixels);
            return picture;
        }

        private static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] u, double[] v) => new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(Dot(v, v));
            return length > 1e-12 ? v.Select(x => x / length).ToArray() : new double[] { 0, 0, 1 };
        }
    }
}
