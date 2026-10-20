using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Rbx2Source.Export
{
    // Merges every material into one material with one texture set: {name}_color.png, plus {name}_normal.png,
    // {name}_metalness.png, {name}_roughness.png and {name}_emissive.png when any material has those maps. Each
    // material gets a region of the atlas (tiled to cover its UV range) and its UVs are remapped into it.
    public static class TextureAtlas
    {
        public const int MaxSize = 4096;
        private const int Padding = 4;
        private const int SolidSize = 8;

        public sealed class Result
        {
            public string MaterialName;
            public Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, MaterialMaps> Maps = new Dictionary<string, MaterialMaps>(StringComparer.Ordinal);
            // Files only a separate-file glTF needs (the glTF-packed metallic-roughness map).
            public List<string> GltfOnlyFiles = new List<string>();
            public List<string> Files = new List<string>();
            public int Width, Height, Materials;
        }

        private sealed class Pixels
        {
            public int Width, Height;
            public int[] Argb;
        }

        private sealed class Region
        {
            public List<ObjMaterial> Materials = new List<ObjMaterial>();
            public ObjMaterial Material => Materials[0];
            public MaterialMaps Maps;
            public string Color;
            public int SourceWidth, SourceHeight;
            public double U0, V0;
            public int TilesU = 1, TilesV = 1;
            public bool Solid;
            // Inner rectangle in atlas pixels (top-left origin); the padding around it lies inside the region's slot.
            public int X, Y, Width, Height;

            // Materials that would render identical pixels share one region.
            public string Signature => string.Join("|", Color, Maps?.Normal, Maps?.MetallicRoughness, Maps?.Emissive,
                Maps?.EmissiveFactor == null ? "" : string.Join(",", Maps.EmissiveFactor), Maps?.EmissiveStrength, Material.Opacity,
                Solid || Color == null ? string.Join(",", Material.Diffuse) : "", Solid, U0, V0, TilesU, TilesV);
        }

        public static Result Build(WavefrontModel model, string folder, IReadOnlyDictionary<string, string> textures,
            IReadOnlyDictionary<string, MaterialMaps> maps, string name, CancellationToken cancellation = default)
        {
            var cache = new Dictionary<string, Pixels>(StringComparer.OrdinalIgnoreCase);
            Pixels Load(string relative)
            {
                if (!cache.TryGetValue(relative, out Pixels pixels))
                    cache[relative] = pixels = Read(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                return pixels;
            }

            var regions = new List<Region>();
            var bySignature = new Dictionary<string, Region>(StringComparer.Ordinal);
            foreach (var group in model.Triangles.GroupBy(t => t.Material))
            {
                ObjMaterial material = model.Materials[group.Key];
                var region = new Region();
                region.Materials.Add(material);
                if (maps != null && maps.TryGetValue(material.Name, out MaterialMaps materialMaps))
                    region.Maps = materialMaps;
                if (material.Texture != null && textures.TryGetValue(material.Name, out string color))
                    region.Color = color;
                var sources = new[] { region.Color, region.Maps?.Normal, region.Maps?.MetallicRoughness, region.Maps?.Emissive }
                    .Where(path => path != null).ToList();
                var uvs = group.SelectMany(t => t.Corners).Where(c => c.Texture >= 0).Select(c => model.Texcoords[c.Texture]).ToList();
                region.Solid = sources.Count == 0 || uvs.Count == 0;
                if (!region.Solid)
                {
                    region.SourceWidth = sources.Max(path => Load(path).Width);
                    region.SourceHeight = sources.Max(path => Load(path).Height);
                    // Tiled UVs keep tiling inside their region, up to four repeats per axis.
                    region.U0 = Math.Floor(uvs.Min(t => t[0]) + 1e-4);
                    region.V0 = Math.Floor(uvs.Min(t => t[1]) + 1e-4);
                    region.TilesU = Math.Max(1, Math.Min(4, (int)Math.Ceiling(uvs.Max(t => t[0]) - 1e-4 - region.U0)));
                    region.TilesV = Math.Max(1, Math.Min(4, (int)Math.Ceiling(uvs.Max(t => t[1]) - 1e-4 - region.V0)));
                }
                if (bySignature.TryGetValue(region.Signature, out Region same))
                {
                    same.Materials.Add(material);
                    continue;
                }
                bySignature[region.Signature] = region;
                regions.Add(region);
            }

            int width, height;
            double scale = 1;
            while (!Pack(regions, scale, out width, out height))
                scale *= 0.85;

            var result = new Result { MaterialName = name + "_material", Width = width, Height = height, Materials = bySignature.Values.Sum(r => r.Materials.Count) };
            bool hasNormal = regions.Any(r => r.Maps?.Normal != null);
            bool hasMetalRough = regions.Any(r => r.Maps?.MetallicRoughness != null);
            bool hasEmissive = regions.Any(r => r.Maps?.Emissive != null);
            double emissiveScale = Math.Max(1, regions.Where(r => r.Maps?.Emissive != null)
                .Select(r => r.Maps.EmissiveFactor.Max() * r.Maps.EmissiveStrength).DefaultIfEmpty(1).Max());

            string Save(string suffix, int[] argb, bool alpha)
            {
                string file = name + "_" + suffix + ".png";
                Write(Path.Combine(folder, file), width, height, argb, alpha);
                result.Files.Add(file);
                return file;
            }

            // Colour: the texture (with material opacity) or the material's flat colour.
            int[] colorPixels = Render(regions, width, height, Gray(128), r => r.Color != null ? Load(r.Color) : null,
                (r, argb) =>
                {
                    if (r.Color == null)
                        return Argb(r.Material.Opacity, r.Material.Diffuse[0], r.Material.Diffuse[1], r.Material.Diffuse[2]);
                    int a = (int)Math.Round(((argb >> 24) & 255) * r.Material.Opacity);
                    return (a << 24) | (argb & 0xFFFFFF);
                }, cancellation);
            string colorFile = Save("color", colorPixels, colorPixels.Any(p => ((p >> 24) & 255) < 255));
            colorPixels = null;
            // One material for everything: drawn from both sides if any item it replaces was (hair cards stay visible).
            var merged = new ObjMaterial
            {
                Name = result.MaterialName, Texture = colorFile, DoubleSided = regions.Any(r => r.Materials.Any(m => m.DoubleSided))
            };
            var mergedMaps = new MaterialMaps();

            if (hasNormal)
                mergedMaps.Normal = Save("normal", Render(regions, width, height, unchecked((int)0xFF8080FF),
                    r => r.Maps?.Normal != null ? Load(r.Maps.Normal) : null, (r, argb) => argb | unchecked((int)0xFF000000), cancellation), false);
            if (hasMetalRough)
            {
                // glTF packs roughness in G and metalness in B; untextured materials are fully rough, non-metal.
                int[] packed = Render(regions, width, height, unchecked((int)0xFFFFFF00),
                    r => r.Maps?.MetallicRoughness != null ? Load(r.Maps.MetallicRoughness) : null,
                    (r, argb) => argb | unchecked((int)0xFF000000), cancellation);
                Save("metalness", packed.Select(p => Gray(p & 255)).ToArray(), false);
                Save("roughness", packed.Select(p => Gray((p >> 8) & 255)).ToArray(), false);
                mergedMaps.MetallicRoughness = Save("metallicRoughness", packed.Select(p => unchecked((int)0xFFFF0000) | (p & 0xFFFF)).ToArray(), false);
                result.GltfOnlyFiles.Add(mergedMaps.MetallicRoughness);
            }
            if (hasEmissive)
            {
                mergedMaps.Emissive = Save("emissive", Render(regions, width, height, unchecked((int)0xFF000000),
                    r => r.Maps?.Emissive != null ? Load(r.Maps.Emissive) : null, (r, argb) =>
                    {
                        if (r.Maps?.Emissive == null)
                            return unchecked((int)0xFF000000);
                        double k = r.Maps.EmissiveStrength / emissiveScale;
                        return Argb(1, ((argb >> 16) & 255) / 255.0 * r.Maps.EmissiveFactor[0] * k,
                            ((argb >> 8) & 255) / 255.0 * r.Maps.EmissiveFactor[1] * k, (argb & 255) / 255.0 * r.Maps.EmissiveFactor[2] * k);
                    }, cancellation), false);
                mergedMaps.EmissiveFactor = new float[] { 1, 1, 1 };
                mergedMaps.EmissiveStrength = (float)emissiveScale;
            }

            Remap(model, regions, width, height);
            model.Materials.Clear();
            model.Materials[merged.Name] = merged;
            foreach (ObjTriangle triangle in model.Triangles)
                triangle.Material = merged.Name;
            result.Textures[merged.Name] = colorFile;
            if (hasNormal || hasMetalRough || hasEmissive)
                result.Maps[merged.Name] = mergedMaps;
            return result;
        }

        // Shelf-packs the regions into the smallest power-of-two atlas (no larger than MaxSize) among several widths.
        private static bool Pack(List<Region> regions, double scale, out int width, out int height)
        {
            int Outer(int size, int tiles) => Math.Max(SolidSize + 2 * Padding, (int)Math.Round(size * tiles * scale));
            var slots = regions.Select(r => r.Solid
                ? new { Region = r, W = SolidSize + 2 * Padding, H = SolidSize + 2 * Padding }
                : new { Region = r, W = Outer(r.SourceWidth, r.TilesU), H = Outer(r.SourceHeight, r.TilesV) })
                .OrderByDescending(s => s.H).ThenByDescending(s => s.W).ToList();
            width = height = 0;
            long bestArea = long.MaxValue;
            for (int candidate = NextPowerOfTwo(slots.Max(s => s.W)); candidate <= MaxSize; candidate <<= 1)
            {
                int x = 0, y = 0, shelf = 0;
                foreach (var slot in slots)
                {
                    if (x + slot.W > candidate)
                    {
                        y += shelf;
                        x = 0;
                        shelf = 0;
                    }
                    x += slot.W;
                    shelf = Math.Max(shelf, slot.H);
                }
                int used = NextPowerOfTwo(y + shelf);
                long area = (long)candidate * used;
                if (used > MaxSize || area > bestArea || (area == bestArea && Math.Abs(candidate - used) >= Math.Abs(width - height)))
                    continue;
                bestArea = area;
                width = candidate;
                height = used;
            }
            if (width == 0)
                return false;
            {
                int x = 0, y = 0, shelf = 0;
                foreach (var slot in slots)
                {
                    if (x + slot.W > width)
                    {
                        y += shelf;
                        x = 0;
                        shelf = 0;
                    }
                    slot.Region.X = x + Padding;
                    slot.Region.Y = y + Padding;
                    slot.Region.Width = slot.W - 2 * Padding;
                    slot.Region.Height = slot.H - 2 * Padding;
                    x += slot.W;
                    shelf = Math.Max(shelf, slot.H);
                }
            }
            return true;
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }

        // Fills each region (and its padding, by clamping to the region's edge) from its source image or a flat value.
        private static int[] Render(List<Region> regions, int width, int height, int background, Func<Region, Pixels> source,
            Func<Region, int, int> shade, CancellationToken cancellation)
        {
            var output = new int[width * height];
            for (int i = 0; i < output.Length; i++)
                output[i] = background;
            foreach (Region r in regions)
            {
                cancellation.ThrowIfCancellationRequested();
                Pixels image = source(r);
                int flat = image == null ? shade(r, background) : 0;
                for (int y = r.Y - Padding; y < r.Y + r.Height + Padding; y++)
                {
                    double vn = Math.Max(0, Math.Min(1, (y - r.Y + 0.5) / r.Height));
                    for (int x = r.X - Padding; x < r.X + r.Width + Padding; x++)
                    {
                        if (image == null)
                        {
                            output[y * width + x] = flat;
                            continue;
                        }
                        double un = Math.Max(0, Math.Min(1, (x - r.X + 0.5) / r.Width));
                        // Region top is the highest V (OBJ UVs start at the bottom-left; images at the top-left).
                        double u = r.U0 + un * r.TilesU, v = r.V0 + (1 - vn) * r.TilesV;
                        output[y * width + x] = shade(r, Sample(image, u, v));
                    }
                }
            }
            return output;
        }

        private static void Remap(WavefrontModel model, List<Region> regions, int width, int height)
        {
            var byMaterial = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < regions.Count; i++)
                foreach (ObjMaterial material in regions[i].Materials)
                    byMaterial[material.Name] = i;
            var remapped = new Dictionary<long, int>();
            foreach (ObjTriangle triangle in model.Triangles)
            {
                int regionIndex = byMaterial[triangle.Material];
                Region r = regions[regionIndex];
                triangle.Corners = triangle.Corners.Select(corner =>
                {
                    long key = ((long)regionIndex << 32) | (uint)corner.Texture;
                    if (!remapped.TryGetValue(key, out int index))
                    {
                        double x, y;
                        if (r.Solid || corner.Texture < 0)
                        {
                            x = r.X + r.Width / 2.0;
                            y = r.Y + r.Height / 2.0;
                        }
                        else
                        {
                            float[] t = model.Texcoords[corner.Texture];
                            double un = Math.Max(0, Math.Min(1, (t[0] - r.U0) / r.TilesU));
                            double vn = Math.Max(0, Math.Min(1, (t[1] - r.V0) / r.TilesV));
                            x = r.X + un * r.Width;
                            y = r.Y + (1 - vn) * r.Height;
                        }
                        index = model.Texcoords.Count;
                        model.Texcoords.Add(new[] { (float)(x / width), (float)(1 - y / height) });
                        remapped[key] = index;
                    }
                    return new ObjCorner { Position = corner.Position, Normal = corner.Normal, Texture = index };
                }).ToArray();
            }
        }

        // Bilinear sample with wrapping; (u, v) in OBJ convention.
        private static int Sample(Pixels image, double u, double v)
        {
            double fx = (u - Math.Floor(u)) * image.Width - 0.5, fy = (1 - (v - Math.Floor(v))) * image.Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double ax = fx - x0, ay = fy - y0;
            int Pixel(int x, int y) => image.Argb[(((y % image.Height) + image.Height) % image.Height) * image.Width +
                (((x % image.Width) + image.Width) % image.Width)];
            int p00 = Pixel(x0, y0), p10 = Pixel(x0 + 1, y0), p01 = Pixel(x0, y0 + 1), p11 = Pixel(x0 + 1, y0 + 1);
            int result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                double c = (1 - ay) * ((1 - ax) * ((p00 >> shift) & 255) + ax * ((p10 >> shift) & 255)) +
                    ay * ((1 - ax) * ((p01 >> shift) & 255) + ax * ((p11 >> shift) & 255));
                result |= Math.Max(0, Math.Min(255, (int)Math.Round(c))) << shift;
            }
            return result;
        }

        private static int Gray(int value) => unchecked((int)0xFF000000) | (value << 16) | (value << 8) | value;

        private static int Argb(double a, double r, double g, double b)
        {
            int C(double value) => Math.Max(0, Math.Min(255, (int)Math.Round(value * 255)));
            return (C(a) << 24) | (C(r) << 16) | (C(g) << 8) | C(b);
        }

        private static Pixels Read(string path)
        {
            using (Picture image = Picture.Load(path))
                return new Pixels { Width = image.Width, Height = image.Height, Argb = image.GetPixels() };
        }

        private static void Write(string path, int width, int height, int[] argb, bool alpha)
        {
            using (var image = new Picture(width, height))
            {
                image.SetPixels(argb);
                image.Save(path, PictureFormat.Png, opaque: !alpha);
            }
        }
    }
}
