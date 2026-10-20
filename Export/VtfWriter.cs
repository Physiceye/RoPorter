using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Rbx2Source.Export
{
    // Writes Valve Texture Format 7.2 files: uncompressed BGR888 or BGRA8888 with a full mipmap chain, resized to
    // power-of-two dimensions (Source's texture requirement).
    public static class VtfWriter
    {
        public enum Kind { Color, Normal, Mask }

        private const uint FlagNormal = 0x80, FlagEightBitAlpha = 0x2000;
        // Clamped at the edges and always at full detail: for sky faces, which must not blend into the opposite edge.
        public const uint FlagClampS = 0x4, FlagClampT = 0x8, FlagNoLod = 0x200;
        private const int FormatBgr888 = 3, FormatBgra8888 = 12, FormatDxt1 = 13, FormatDxt5 = 15;
        public const int MaxSize = 2048;

        // Returns whether the texture kept an alpha channel.
        public static bool Write(string imagePath, string vtfPath, Kind kind)
        {
            using (Picture source = Picture.Load(imagePath))
                return Write(source, vtfPath, kind);
        }

        // compress stores the texture DXT-compressed (DXT1, or DXT5 with alpha): a quarter to a sixth of the size, for a
        // little detail.
        public static bool Write(Picture source, string vtfPath, Kind kind, uint flags = 0, bool compress = false)
        {
            (int width, int height) = FitSize(source.Width, source.Height);
            int[] pixels = Resize(source, width, height);
            bool alpha = kind == Kind.Color && Array.Exists(pixels, p => ((p >> 24) & 255) < 255);
            for (int i = 0; i < pixels.Length; i++)
            {
                int p = pixels[i];
                if (kind == Kind.Normal)
                {
                    // Roblox normal maps are OpenGL-style (green up); Source expects green down.
                    int g = 255 - ((p >> 8) & 255);
                    p = (p & unchecked((int)0xFFFF00FF)) | (g << 8);
                }
                else if (kind == Kind.Mask)
                {
                    int l = (int)Math.Round(0.299 * ((p >> 16) & 255) + 0.587 * ((p >> 8) & 255) + 0.114 * (p & 255));
                    p = unchecked((int)0xFF000000) | (l << 16) | (l << 8) | l;
                }
                pixels[i] = p;
            }

            int mips = 1;
            for (int size = Math.Max(width, height); size > 1; size >>= 1)
                mips++;
            var levels = new int[mips][];
            var sizes = new (int W, int H)[mips];
            levels[0] = pixels;
            sizes[0] = (width, height);
            for (int level = 1; level < mips; level++)
            {
                (int w, int h) = sizes[level - 1];
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                levels[level] = Downsample(levels[level - 1], w, h, nw, nh);
                sizes[level] = (nw, nh);
            }

            double[] reflectivity = new double[3];
            foreach (int p in levels[mips - 1])
                for (int c = 0; c < 3; c++)
                    reflectivity[c] += Math.Pow(((p >> (16 - 8 * c)) & 255) / 255.0, 2.2) / levels[mips - 1].Length;

            using (var writer = new BinaryWriter(File.Create(vtfPath)))
            {
                writer.Write(Encoding.ASCII.GetBytes("VTF\0"));
                writer.Write(7u);
                writer.Write(2u);
                writer.Write(80u);
                writer.Write((ushort)width);
                writer.Write((ushort)height);
                writer.Write((kind == Kind.Normal ? FlagNormal : 0) | (alpha ? FlagEightBitAlpha : 0) | flags);
                writer.Write((ushort)1);
                writer.Write((ushort)0);
                writer.Write(new byte[4]);
                foreach (double r in reflectivity)
                    writer.Write((float)r);
                writer.Write(new byte[4]);
                writer.Write(1f);
                writer.Write(compress ? (alpha ? FormatDxt5 : FormatDxt1) : alpha ? FormatBgra8888 : FormatBgr888);
                writer.Write((byte)mips);
                writer.Write(-1);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write(new byte[80 - 65]);
                // Mipmaps are stored smallest first.
                for (int level = mips - 1; level >= 0; level--)
                {
                    if (compress)
                    {
                        writer.Write(EncodeDxt(levels[level], sizes[level].W, sizes[level].H, alpha));
                        continue;
                    }
                    foreach (int p in levels[level])
                    {
                        writer.Write((byte)(p & 255));
                        writer.Write((byte)((p >> 8) & 255));
                        writer.Write((byte)((p >> 16) & 255));
                        if (alpha)
                            writer.Write((byte)((p >> 24) & 255));
                    }
                }
            }
            return alpha;
        }

        // DXT1 (or DXT5, with an alpha block before each colour block) in 4 x 4 blocks; blocks past a small mip level's edge
        // repeat its last pixels.
        private static byte[] EncodeDxt(int[] pixels, int width, int height, bool alpha)
        {
            int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
            var output = new byte[blocksX * blocksY * (alpha ? 16 : 8)];
            var block = new int[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    for (int i = 0; i < 16; i++)
                        block[i] = pixels[Math.Min(height - 1, by * 4 + i / 4) * width + Math.Min(width - 1, bx * 4 + i % 4)];
                    if (alpha)
                    {
                        EncodeAlpha(block, output, offset);
                        offset += 8;
                    }
                    EncodeColor(block, output, offset);
                    offset += 8;
                }
            return output;
        }

        // A colour block: two endpoint colours (the block's colour range, inset a little) and a 2-bit choice per pixel among
        // them and the two colours between.
        private static void EncodeColor(int[] block, byte[] output, int offset)
        {
            int[] low = { 255, 255, 255 }, high = { 0, 0, 0 };
            foreach (int p in block)
                for (int c = 0; c < 3; c++)
                {
                    int value = (p >> (16 - 8 * c)) & 255;
                    low[c] = Math.Min(low[c], value);
                    high[c] = Math.Max(high[c], value);
                }
            for (int c = 0; c < 3; c++)
            {
                int inset = (high[c] - low[c]) / 16;
                low[c] += inset;
                high[c] -= inset;
            }
            int To565(int[] rgb) => ((rgb[0] * 31 + 127) / 255 << 11) | ((rgb[1] * 63 + 127) / 255 << 5) | ((rgb[2] * 31 + 127) / 255);
            int c0 = To565(high), c1 = To565(low);
            if (c0 < c1)
            {
                int swap = c0;
                c0 = c1;
                c1 = swap;
            }
            uint indices = 0;
            if (c0 != c1)
            {
                int[] From565(int c) => new[] { ((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31 };
                int[] p0 = From565(c0), p1 = From565(c1);
                var palette = new[] { p0, p1, Enumerable.Range(0, 3).Select(c => (2 * p0[c] + p1[c]) / 3).ToArray(), Enumerable.Range(0, 3).Select(c => (p0[c] + 2 * p1[c]) / 3).ToArray() };
                for (int i = 0; i < 16; i++)
                {
                    int r = (block[i] >> 16) & 255, g = (block[i] >> 8) & 255, b = block[i] & 255;
                    int best = 0, bestDistance = int.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        int dr = r - palette[k][0], dg = g - palette[k][1], db = b - palette[k][2];
                        int distance = dr * dr + dg * dg + db * db;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = k;
                        }
                    }
                    indices |= (uint)best << (2 * i);
                }
            }
            output[offset] = (byte)c0;
            output[offset + 1] = (byte)(c0 >> 8);
            output[offset + 2] = (byte)c1;
            output[offset + 3] = (byte)(c1 >> 8);
            for (int i = 0; i < 4; i++)
                output[offset + 4 + i] = (byte)(indices >> (8 * i));
        }

        // A DXT5 alpha block: the block's highest and lowest alpha, and a 3-bit choice per pixel among them and six between.
        private static void EncodeAlpha(int[] block, byte[] output, int offset)
        {
            int high = 0, low = 255;
            foreach (int p in block)
            {
                int a = (p >> 24) & 255;
                high = Math.Max(high, a);
                low = Math.Min(low, a);
            }
            output[offset] = (byte)high;
            output[offset + 1] = (byte)low;
            ulong indices = 0;
            if (high != low)
            {
                var palette = new int[8];
                palette[0] = high;
                palette[1] = low;
                for (int k = 1; k <= 6; k++)
                    palette[k + 1] = ((7 - k) * high + k * low) / 7;
                for (int i = 0; i < 16; i++)
                {
                    int a = (block[i] >> 24) & 255, best = 0;
                    for (int k = 1; k < 8; k++)
                        if (Math.Abs(a - palette[k]) < Math.Abs(a - palette[best]))
                            best = k;
                    indices |= (ulong)best << (3 * i);
                }
            }
            for (int i = 0; i < 6; i++)
                output[offset + 2 + i] = (byte)(indices >> (8 * i));
        }

        // Solid-colour texture for untextured materials.
        public static void WriteSolid(string vtfPath, float[] color, float opacity)
        {
            using (var image = new Picture(4, 4))
            {
                image.Clear(((int)Math.Round(opacity * 255) << 24) | ((int)Math.Round(color[0] * 255) << 16) |
                    ((int)Math.Round(color[1] * 255) << 8) | (int)Math.Round(color[2] * 255));
                Write(image, vtfPath, Kind.Color);
            }
        }

        public static int PowerOfTwo(int size)
        {
            int result = 4;
            while (result < size)
                result <<= 1;
            // Round to the nearer power of two rather than always up.
            if (result > 4 && result / 2 >= size * 0.75)
                result /= 2;
            return result;
        }

        // Power-of-two size for an image, halving both sides together past MaxSize so the aspect ratio is kept.
        public static (int Width, int Height) FitSize(int width, int height)
        {
            int w = PowerOfTwo(width), h = PowerOfTwo(height);
            while (Math.Max(w, h) > MaxSize)
            {
                w = Math.Max(4, w / 2);
                h = Math.Max(4, h / 2);
            }
            return (w, h);
        }

        private static int[] Resize(Picture source, int width, int height)
        {
            using (Picture resized = source.Resized(width, height))
                return resized.GetPixels();
        }

        private static int[] Downsample(int[] pixels, int w, int h, int nw, int nh)
        {
            var result = new int[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    var sum = new int[4];
                    int count = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy);
                            int p = pixels[sy * w + sx];
                            for (int c = 0; c < 4; c++)
                                sum[c] += (p >> (8 * c)) & 255;
                            count++;
                        }
                    int value = 0;
                    for (int c = 0; c < 4; c++)
                        value |= ((sum[c] + count / 2) / count) << (8 * c);
                    result[y * nw + x] = value;
                }
            return result;
        }
    }
}
