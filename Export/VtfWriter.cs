using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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
        private const int FormatBgr888 = 3, FormatBgra8888 = 12;
        public const int MaxSize = 2048;

        // Returns whether the texture kept an alpha channel.
        public static bool Write(string imagePath, string vtfPath, Kind kind)
        {
            using (var source = new Bitmap(imagePath))
                return Write(source, vtfPath, kind);
        }

        public static bool Write(Bitmap source, string vtfPath, Kind kind)
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
                writer.Write((kind == Kind.Normal ? FlagNormal : 0) | (alpha ? FlagEightBitAlpha : 0));
                writer.Write((ushort)1);
                writer.Write((ushort)0);
                writer.Write(new byte[4]);
                foreach (double r in reflectivity)
                    writer.Write((float)r);
                writer.Write(new byte[4]);
                writer.Write(1f);
                writer.Write(alpha ? FormatBgra8888 : FormatBgr888);
                writer.Write((byte)mips);
                writer.Write(-1);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write(new byte[80 - 65]);
                // Mipmaps are stored smallest first.
                for (int level = mips - 1; level >= 0; level--)
                    foreach (int p in levels[level])
                    {
                        writer.Write((byte)(p & 255));
                        writer.Write((byte)((p >> 8) & 255));
                        writer.Write((byte)((p >> 16) & 255));
                        if (alpha)
                            writer.Write((byte)((p >> 24) & 255));
                    }
            }
            return alpha;
        }

        // Solid-colour texture for untextured materials.
        public static void WriteSolid(string vtfPath, float[] color, float opacity)
        {
            using (var image = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(image))
                    graphics.Clear(Color.FromArgb((int)Math.Round(opacity * 255), (int)Math.Round(color[0] * 255),
                        (int)Math.Round(color[1] * 255), (int)Math.Round(color[2] * 255)));
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

        private static int[] Resize(Bitmap source, int width, int height)
        {
            using (var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
                var pixels = new int[width * height];
                BitmapData data = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
                }
                finally
                {
                    resized.UnlockBits(data);
                }
                return pixels;
            }
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
