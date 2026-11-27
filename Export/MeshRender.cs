using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rbx2Source.Export
{
    // A picture of a single mesh drawn the way Roblox draws its mesh thumbnails: on a transparent background, seen through a
    // wide perspective camera (as in Roblox, near parts look bigger) from the front and a little to the side and above,
    // smoothly shaded by a light from the upper left, and centred with a margin.
    // Its texture is drawn on it when it has one; an untextured mesh is its part's grey. A mesh is prepared once (Prepare)
    // and can then be drawn from any angle (Draw), so the preview can be turned by dragging it.
    public sealed class MeshRender
    {
        // The view Roblox's thumbnails use: turned this far round from the front and looking down this much (degrees).
        public const double DefaultYaw = 30, DefaultPitch = 30;
        // How much of the picture the mesh fills, and how many samples each pixel takes across (for smooth edges).
        private const double Fill = 0.8;
        // The camera's field of view (degrees), about Roblox's default camera's.
        private const double FieldOfView = 70;
        public const int Samples = 3;

        private sealed class Texel
        {
            public int Width, Height;
            public int[] Pixels;

            // Texture coordinates count V up from the image's bottom (as in meshes and OBJ files), so the row is 1 - V.
            public int Sample(double u, double v)
            {
                int x = (int)Math.Floor((u - Math.Floor(u)) * Width), y = (int)Math.Floor((1 - (v - Math.Floor(v))) * Height);
                return Pixels[Math.Min(Height - 1, Math.Max(0, y)) * Width + Math.Min(Width - 1, Math.Max(0, x))];
            }
        }

        private readonly WavefrontModel model;
        private readonly IReadOnlyDictionary<string, byte[]> textures;
        private readonly List<int> used;
        private readonly double[] middle;
        private readonly double radius;
        // Decoded textures, kept for every later drawing.
        private readonly Dictionary<string, Texel> loaded = new Dictionary<string, Texel>(StringComparer.Ordinal);

        private MeshRender(WavefrontModel model, IReadOnlyDictionary<string, byte[]> textures, List<int> used, double[] middle, double radius)
        {
            this.model = model;
            this.textures = textures;
            this.used = used;
            this.middle = middle;
            this.radius = radius;
        }

        // The mesh ready to draw, or null when it has nothing to draw. The model must not change while it is drawn.
        public static MeshRender Prepare(WavefrontModel model, IReadOnlyDictionary<string, byte[]> textures)
        {
            var used = model.Triangles.Where(t => t.Corners.Length == 3).SelectMany(t => t.Corners).Select(c => c.Position).Distinct().ToList();
            if (used.Count == 0)
                return null;
            var low = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            var high = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (int index in used)
                for (int axis = 0; axis < 3; axis++)
                {
                    low[axis] = Math.Min(low[axis], model.Positions[index][axis]);
                    high[axis] = Math.Max(high[axis], model.Positions[index][axis]);
                }
            double[] middle = { (low[0] + high[0]) / 2, (low[1] + high[1]) / 2, (low[2] + high[2]) / 2 };
            double radius = Math.Max(1e-6, used.Max(index => Length(Subtract(Vector(model.Positions[index]), middle))));
            return new MeshRender(model, textures, used, middle, radius);
        }

        // The mesh as Roblox's thumbnail shows it.
        public static Picture Render(WavefrontModel model, IReadOnlyDictionary<string, byte[]> textures, int size) =>
            Prepare(model, textures)?.Draw(size, DefaultYaw, DefaultPitch);

        // The camera's three directions for a view turned yaw degrees round from the front and looking down pitch degrees.
        private static (double[] View, double[] Right, double[] Up) Camera(double yawDegrees, double pitchDegrees)
        {
            double yaw = yawDegrees * Math.PI / 180, pitch = pitchDegrees * Math.PI / 180;
            double[] view = Normalize(new[] { -Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch) });
            double[] right = Normalize(Cross(new double[] { 0, 1, 0 }, view));
            return (view, right, Cross(view, right));
        }

        // The light, from the upper left as Roblox's thumbnails are lit, given relative to the camera so it stays there however
        // the mesh is turned: its parts along the camera's right, up and view at the thumbnail's own angle.
        private static readonly double[] CameraLight = LightRelativeToCamera();

        private static double[] LightRelativeToCamera()
        {
            var (view, right, up) = Camera(DefaultYaw, DefaultPitch);
            double[] light = Normalize(new[] { -0.45, 0.8, -0.55 });
            return new[] { Dot(light, right), Dot(light, up), Dot(light, view) };
        }

        // The mesh seen from a view turned yaw degrees round from the front (towards +X) and looking down pitch degrees, on a
        // size by size picture; samples across each pixel smooth the edges (1 is quickest, for drawing while it is turned).
        public Picture Draw(int size, double yawDegrees, double pitchDegrees, int samples = Samples)
        {
            if (size < 1)
                return null;
            samples = Math.Max(1, samples);
            pitchDegrees = Math.Max(-85, Math.Min(85, pitchDegrees));
            // Roblox's front is -Z: at no turn the camera sits in front. Right, up and the view direction span the picture.
            var (view, right, up) = Camera(yawDegrees, pitchDegrees);
            double[] toLight = Normalize(new[]
            {
                CameraLight[0] * right[0] + CameraLight[1] * up[0] + CameraLight[2] * view[0],
                CameraLight[0] * right[1] + CameraLight[1] * up[1] + CameraLight[2] * view[1],
                CameraLight[0] * right[2] + CameraLight[1] * up[2] + CameraLight[2] * view[2]
            });
            // The camera stands back from the middle of the mesh far enough that its bounding sphere fits the field of view.
            double distance = radius / Math.Sin(FieldOfView * Math.PI / 360);
            double[] camera = Subtract(middle, Scale(view, distance));
            // A point's place across and up the picture plane (before scaling), and its depth along the view.
            double[] Project(double[] p)
            {
                double[] relative = Subtract(p, camera);
                double z = Math.Max(1e-6, Dot(relative, view));
                return new[] { Dot(relative, right) / z, Dot(relative, up) / z, z };
            }
            double left = double.MaxValue, rightmost = double.MinValue, bottom = double.MaxValue, top = double.MinValue;
            foreach (int index in used)
            {
                double[] projected = Project(Vector(model.Positions[index]));
                left = Math.Min(left, projected[0]); rightmost = Math.Max(rightmost, projected[0]);
                bottom = Math.Min(bottom, projected[1]); top = Math.Max(top, projected[1]);
            }
            int full = size * samples;
            double scale = full * Fill / Math.Max(1e-12, Math.Max(rightmost - left, top - bottom));
            double middleAcross = (left + rightmost) / 2, middleUp = (bottom + top) / 2;

            Texel TextureOf(ObjMaterial material)
            {
                if (material?.Texture == null || textures == null || !textures.TryGetValue(material.Texture, out byte[] image))
                    return null;
                lock (loaded)
                if (!loaded.TryGetValue(material.Texture, out Texel texel))
                {
                    try
                    {
                        using (Picture picture = Picture.Load(image))
                        using (Picture small = picture.Width > 1024 || picture.Height > 1024 ? picture.Resized(Math.Min(1024, picture.Width), Math.Min(1024, picture.Height)) : null)
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
                    return texel;
                }
                else
                    return texel;
            }

            var color = new int[full * full];
            var depth = new double[full * full];
            for (int i = 0; i < depth.Length; i++)
                depth[i] = double.MaxValue;
            foreach (ObjTriangle triangle in model.Triangles)
            {
                if (triangle.Corners.Length != 3)
                    continue;
                model.Materials.TryGetValue(triangle.Material ?? "", out ObjMaterial material);
                if (material != null && material.Opacity < 0.05f)
                    continue;
                float[] diffuse = material?.Diffuse ?? new float[] { 163 / 255f, 162 / 255f, 165 / 255f };
                Texel texture = TextureOf(material);
                ObjCorner[] corners = triangle.Corners;
                double[][] points = corners.Select(c => Vector(model.Positions[c.Position])).ToArray();
                double[] face = Cross(Subtract(points[1], points[0]), Subtract(points[2], points[0]));
                if (Dot(face, face) < 1e-18)
                    continue;
                face = Normalize(face);
                // Each corner's own normal (smooth shading), or the face's where the mesh has none.
                double[][] normals = corners.Select(c => c.Normal >= 0 && c.Normal < model.Normals.Count ? Normalize(Vector(model.Normals[c.Normal])) : face).ToArray();
                float[][] uvs = texture != null && corners.All(c => c.Texture >= 0 && c.Texture < model.Texcoords.Count)
                    ? corners.Select(c => model.Texcoords[c.Texture]).ToArray() : null;
                double[][] q = points.Select(p =>
                {
                    double[] projected = Project(p);
                    return new[] { full / 2.0 + (projected[0] - middleAcross) * scale, full / 2.0 - (projected[1] - middleUp) * scale, projected[2] };
                }).ToArray();
                double area = (q[1][0] - q[0][0]) * (q[2][1] - q[0][1]) - (q[2][0] - q[0][0]) * (q[1][1] - q[0][1]);
                if (Math.Abs(area) < 1e-12)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor(q.Min(p => p[0]))), x1 = Math.Min(full - 1, (int)Math.Ceiling(q.Max(p => p[0])));
                int y0 = Math.Max(0, (int)Math.Floor(q.Min(p => p[1]))), y1 = Math.Min(full - 1, (int)Math.Ceiling(q.Max(p => p[1])));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double s0 = ((q[1][0] - px) * (q[2][1] - py) - (q[2][0] - px) * (q[1][1] - py)) / area;
                        double s1 = ((q[2][0] - px) * (q[0][1] - py) - (q[0][0] - px) * (q[2][1] - py)) / area;
                        double s2 = 1 - s0 - s1;
                        if (s0 < 0 || s1 < 0 || s2 < 0)
                            continue;
                        // Weights for the corners' normals and texture coordinates, corrected for perspective.
                        double inverse = s0 / q[0][2] + s1 / q[1][2] + s2 / q[2][2];
                        double z = 1 / inverse;
                        double w0 = s0 / q[0][2] * z, w1 = s1 / q[1][2] * z, w2 = s2 / q[2][2] * z;
                        int index = y * full + x;
                        if (z >= depth[index])
                            continue;
                        depth[index] = z;
                        double[] n = Normalize(new[]
                        {
                            w0 * normals[0][0] + w1 * normals[1][0] + w2 * normals[2][0],
                            w0 * normals[0][1] + w1 * normals[1][1] + w2 * normals[2][1],
                            w0 * normals[0][2] + w1 * normals[1][2] + w2 * normals[2][2]
                        });
                        // The side facing the camera is lit, also for the inside of an open mesh (a hat's brim, hair cards).
                        if (Dot(n, view) > 0)
                            n = n.Select(v => -v).ToArray();
                        double light = Math.Max(0, Dot(n, toLight));
                        double rim = Math.Max(0, -Dot(n, view));
                        double shade = 0.45 + 0.55 * light + 0.1 * rim;
                        double r = diffuse[0], g = diffuse[1], b = diffuse[2];
                        if (uvs != null)
                        {
                            int texel = texture.Sample(w0 * uvs[0][0] + w1 * uvs[1][0] + w2 * uvs[2][0], w0 * uvs[0][1] + w1 * uvs[1][1] + w2 * uvs[2][1]);
                            double a = ((texel >> 24) & 255) / 255.0;
                            // A texture shows over white, unless it is an overlay on the part's colour.
                            double br = material?.Overlay == true ? r : 1, bg = material?.Overlay == true ? g : 1, bb = material?.Overlay == true ? b : 1;
                            r = ((texel >> 16) & 255) / 255.0 * a + br * (1 - a);
                            g = ((texel >> 8) & 255) / 255.0 * a + bg * (1 - a);
                            b = (texel & 255) / 255.0 * a + bb * (1 - a);
                        }
                        color[index] = unchecked((int)0xFF000000) | (Channel(r * shade) << 16) | (Channel(g * shade) << 8) | Channel(b * shade);
                    }
            }

            // Each pixel is the average of its samples; uncovered samples are transparent, so the edges blend smoothly into
            // whatever the picture is shown on.
            var pixels = new int[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double r = 0, g = 0, b = 0, covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                        {
                            int index = (y * samples + sy) * full + x * samples + sx;
                            if (depth[index] == double.MaxValue)
                                continue;
                            int c = color[index];
                            r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                            covered++;
                        }
                    if (covered == 0)
                        continue;
                    int alpha = (int)Math.Round(255 * covered / (samples * samples));
                    pixels[y * size + x] = (alpha << 24) | ((int)Math.Round(r / covered) << 16) | ((int)Math.Round(g / covered) << 8) | (int)Math.Round(b / covered);
                }
            var result = new Picture(size, size);
            result.SetPixels(pixels);
            return result;
        }

        private static int Channel(double value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
        private static double[] Vector(float[] v) => new double[] { v[0], v[1], v[2] };
        private static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static double Length(double[] a) => Math.Sqrt(Dot(a, a));
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] u, double[] v) => new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(Dot(v, v));
            return length > 1e-12 ? v.Select(x => x / length).ToArray() : new double[] { 0, 0, -1 };
        }
    }
}
