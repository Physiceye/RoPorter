using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Rbx2Source.Export
{
    public enum PictureFormat { Png, Jpeg }

    // An image in memory, read, drawn and saved with SkiaSharp so it works the same on Windows, Linux and macOS. Pixels are
    // handed out as ARGB integers (0xAARRGGBB, alpha not premultiplied), the layout of a 32-bit ARGB bitmap.
    public sealed class Picture : IDisposable
    {
        // Unpremultiplied, so a transparent pixel keeps its colour (texture edges filter towards it, not black). Drawing goes
        // through a premultiplied copy, as Skia draws.
        private readonly SKBitmap bitmap;
        private static readonly SKColorType Bgra = SKColorType.Bgra8888;

        public int Width => bitmap.Width;
        public int Height => bitmap.Height;
        // Whether the file it was read from can hold transparency (a PNG with an alpha channel; never a JPEG).
        public bool HasAlphaChannel { get; private set; } = true;

        // A transparent picture.
        public Picture(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "A picture must be at least one pixel wide and high.");
            bitmap = new SKBitmap(new SKImageInfo(width, height, Bgra, SKAlphaType.Unpremul));
            bitmap.Erase(SKColors.Transparent);
        }

        private Picture(SKBitmap bitmap, bool alpha)
        {
            this.bitmap = bitmap;
            HasAlphaChannel = alpha;
        }

        // Reads a PNG, JPEG, GIF, BMP or WebP file. Throws InvalidDataException for anything else.
        public static Picture Load(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException("The image is empty.");
            using (var stream = new SKMemoryStream(data))
            using (SKCodec codec = SKCodec.Create(stream))
            {
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                    throw new InvalidDataException("The file is not an image RoPorter can read.");
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, Bgra, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    bitmap.Dispose();
                    throw new InvalidDataException("The image could not be decoded (" + result + ").");
                }
                return new Picture(bitmap, codec.Info.AlphaType != SKAlphaType.Opaque);
            }
        }

        public static Picture Load(string path) => Load(File.ReadAllBytes(path));

        // Whether the data is an image that can be read.
        public static bool IsImage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;
            using (var stream = new SKMemoryStream(data))
            using (SKCodec codec = SKCodec.Create(stream))
                return codec != null && codec.Info.Width > 0 && codec.Info.Height > 0;
        }

        // The pixels, row by row, as ARGB integers.
        public int[] GetPixels()
        {
            var pixels = new int[Width * Height];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            return pixels;
        }

        public void SetPixels(int[] pixels)
        {
            if (pixels.Length != Width * Height)
                throw new ArgumentException("The pixel count does not match the picture's size.", nameof(pixels));
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            bitmap.NotifyPixelsChanged();
        }

        // One pixel as ARGB.
        public int GetPixel(int x, int y) => Marshal.ReadInt32(bitmap.GetPixels(), (y * Width + x) * 4);

        // Fills the picture with one ARGB colour.
        public void Clear(int argb)
        {
            var pixels = new int[Width * Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = argb;
            SetPixels(pixels);
        }

        // Fills the picture with a gradient from top to bottom (ARGB colours).
        public void FillVertical(int top, int bottom) => Paint(canvas =>
        {
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height),
                new[] { new SKColor((uint)top), new SKColor((uint)bottom) }, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = shader })
                canvas.DrawRect(0, 0, Width, Height, paint);
        });

        // Draws (part of) another picture scaled into a rectangle, smoothly. A tint multiplies each colour channel (red, green,
        // blue); replace overwrites the pixels instead of blending over them.
        public void Draw(Picture source, int x, int y, int width, int height, int sourceX = 0, int sourceY = 0, int sourceWidth = -1,
            int sourceHeight = -1, float[] tint = null, bool replace = false)
        {
            if (width <= 0 || height <= 0)
                return;
            var from = SKRect.Create(sourceX, sourceY, sourceWidth < 0 ? source.Width : sourceWidth, sourceHeight < 0 ? source.Height : sourceHeight);
            Paint(canvas =>
            {
                using (SKImage image = SKImage.FromBitmap(source.bitmap))
                using (var paint = new SKPaint { BlendMode = replace ? SKBlendMode.Src : SKBlendMode.SrcOver })
                {
                    if (tint != null)
                        paint.ColorFilter = SKColorFilter.CreateColorMatrix(new[]
                        {
                            tint[0], 0, 0, 0, 0,
                            0, tint[1], 0, 0, 0,
                            0, 0, tint[2], 0, 0,
                            0, 0, 0, 1, 0
                        });
                    canvas.DrawImage(image, from, SKRect.Create(x, y, width, height), Sampling(from.Width, from.Height, width, height), paint);
                }
            });
        }

        // The whole picture scaled to a new size, smoothly. The same size is a plain copy.
        public Picture Resized(int width, int height)
        {
            var result = new Picture(width, height) { HasAlphaChannel = HasAlphaChannel };
            if (width == Width && height == Height)
                result.SetPixels(GetPixels());
            else
                result.Draw(this, 0, 0, width, height, replace: true);
            return result;
        }

        // Draws on a premultiplied copy and stores the result.
        private void Paint(Action<SKCanvas> draw)
        {
            var premultiplied = new SKImageInfo(Width, Height, Bgra, SKAlphaType.Premul);
            using (var target = new SKBitmap(premultiplied))
            {
                if (!bitmap.PeekPixels().ReadPixels(premultiplied, target.GetPixels(), target.RowBytes, 0, 0))
                    throw new InvalidOperationException("The picture could not be prepared for drawing.");
                using (var canvas = new SKCanvas(target))
                    draw(canvas);
                if (!target.PeekPixels().ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
                    throw new InvalidOperationException("The drawing could not be stored.");
                bitmap.NotifyPixelsChanged();
            }
        }

        // Mipmaps (box filtering) when shrinking a lot, else a cubic filter, as a high-quality bicubic resize does.
        private static SKSamplingOptions Sampling(float fromWidth, float fromHeight, int toWidth, int toHeight) =>
            toWidth * 2 < fromWidth || toHeight * 2 < fromHeight
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);

        // PNG keeps transparency unless opaque (an RGB file without an alpha channel); JPEG (quality 0-100) never has it.
        public byte[] Encode(PictureFormat format, int quality = 92, bool opaque = false)
        {
            SKImageInfo info = opaque || format == PictureFormat.Jpeg ? bitmap.Info.WithAlphaType(SKAlphaType.Opaque) : bitmap.Info;
            using (var pixels = new SKPixmap(info, bitmap.GetPixels(), bitmap.RowBytes))
            using (SKImage image = SKImage.FromPixels(pixels))
            using (SKData data = image.Encode(format == PictureFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, quality))
                return data?.ToArray() ?? throw new InvalidOperationException("The picture could not be encoded as " + format + ".");
        }

        public void Save(string path, PictureFormat format, int quality = 92, bool opaque = false) => File.WriteAllBytes(path, Encode(format, quality, opaque));

        public void Dispose() => bitmap.Dispose();
    }
}
