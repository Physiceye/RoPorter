using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace Rbx2Source.Export
{
    // A picture of a map for Garry's Mod's map list and the Workshop: its triangles (Source coordinates, flat colours) seen from
    // above at an angle, lit from the sun's direction.
    internal sealed class PlaceRender
    {
        private readonly List<(double[] A, double[] B, double[] C, int Color)> triangles = new List<(double[], double[], double[], int)>();
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
        public static Bitmap Preview(ModelContents contents, int size)
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
                for (int z = 0; z < ground.Depth; z++)
                    for (int x = 0; x < ground.Width; x++)
                        if (ground.Has(x, z))
                        {
                            double[] Corner(int cx, int cz) => Axes((ground.MinX + cx) * voxel, ground.CornerHeight(cx, cz, out _), (ground.MinZ + cz) * voxel);
                            // Counter-clockwise seen from above, in the picture's axes.
                            render.AddPolygon(new[] { Corner(x, z), Corner(x, z + 1), Corner(x + 1, z + 1), Corner(x + 1, z) },
                                contents.Terrain.Color(ground.Materials[x + ground.Width * z]));
                        }
            }
            return render.triangles.Count == 0 ? null : render.Draw(size, new[] { 0.35, 0.25, -1 });
        }

        public Bitmap Draw(int size, double[] light)
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
            foreach (var (a, b, c, color) in triangles)
            {
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
                            pixels[index] = shaded;
                        }
                    }
            }
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < size; y++)
                    Marshal.Copy(pixels, y * size, data.Scan0 + y * data.Stride, size);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
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
