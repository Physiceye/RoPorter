using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rbx2Source.Export
{
    // Uploads an exported Garry's Mod addon to the Steam Workshop with the game's own tools: gmad packs the folder into a
    // .gma and gmpublish uploads it with a 512x512 JPEG icon. Steam must be running, signed in to an account that owns
    // Garry's Mod.
    public static class Workshop
    {
        // Kept in the addon folder but left out of the .gma (addon.json ignores it).
        public const string IconFile = "workshop_icon.jpg";

        // Where earlier versions kept the Workshop item from an upload (now in addon.json); still read for those addons.
        public const string IdFile = "src/workshop_id.txt";

        private const int IconSize = 512;

        // The icon: the avatar's headshot (with the background the API gives it) filling the icon, or the item's thumbnail
        // centred. Anything transparent shows a light grey gradient behind it, which is also the icon without a thumbnail.
        public static void WriteIcon(byte[] thumbnail, string path, bool headshot = false)
        {
            using (var icon = new Bitmap(IconSize, IconSize, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(icon))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    var bounds = new Rectangle(0, 0, IconSize, IconSize);
                    using (var backdrop = new LinearGradientBrush(bounds, Color.FromArgb(236, 237, 239), Color.FromArgb(196, 199, 204), LinearGradientMode.Vertical))
                        graphics.FillRectangle(backdrop, bounds);
                    if (thumbnail != null)
                        using (var stream = new MemoryStream(thumbnail))
                        using (Image image = Image.FromStream(stream))
                        {
                            // Headshots fill the icon; item thumbnails fill most of it. Both keep their proportions.
                            double fit = IconSize * (headshot ? 1 : 0.875) / Math.Max(image.Width, image.Height);
                            int width = (int)Math.Round(image.Width * fit), height = (int)Math.Round(image.Height * fit);
                            graphics.DrawImage(image, (IconSize - width) / 2, (IconSize - height) / 2, width, height);
                        }
                }
                SaveJpeg(icon, path);
            }
        }

        // The Workshop page image (637x358, the size the Workshop shows it at): the user's profile background, or its colour,
        // or the icon's grey gradient, with the avatar's full-body render in the centre. gmpublish cannot upload it, so it is
        // left beside the addon to add on the Workshop page.
        public const string PreviewFile = "workshop_preview.jpg";
        private const int PreviewWidth = 637, PreviewHeight = 358;

        public static void WritePreview(byte[] background, Color? color, byte[] avatar, string path)
        {
            using (var preview = new Bitmap(PreviewWidth, PreviewHeight, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(preview))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    var bounds = new Rectangle(0, 0, PreviewWidth, PreviewHeight);
                    if (color.HasValue)
                        graphics.Clear(color.Value);
                    else
                        using (var backdrop = new LinearGradientBrush(bounds, Color.FromArgb(236, 237, 239), Color.FromArgb(196, 199, 204), LinearGradientMode.Vertical))
                            graphics.FillRectangle(backdrop, bounds);
                    if (background != null)
                        using (var stream = new MemoryStream(background))
                        using (Image image = Image.FromStream(stream))
                        {
                            // Cover: scaled to fill the frame, cropped evenly on the long side.
                            double fit = Math.Max((double)PreviewWidth / image.Width, (double)PreviewHeight / image.Height);
                            int width = (int)Math.Ceiling(image.Width * fit), height = (int)Math.Ceiling(image.Height * fit);
                            graphics.DrawImage(image, (PreviewWidth - width) / 2, (PreviewHeight - height) / 2, width, height);
                        }
                    if (avatar != null)
                        using (var stream = new MemoryStream(avatar))
                        using (var image = new Bitmap(stream))
                        {
                            // The render has uneven empty space around the avatar, so only its visible pixels are placed:
                            // centred in the frame, filling 90% of its height (or width).
                            Rectangle visible = VisibleBounds(image);
                            double fit = Math.Min(PreviewHeight * 0.9 / visible.Height, PreviewWidth * 0.9 / visible.Width);
                            int width = (int)Math.Round(visible.Width * fit), height = (int)Math.Round(visible.Height * fit);
                            graphics.DrawImage(image, new Rectangle((PreviewWidth - width) / 2, (PreviewHeight - height) / 2, width, height),
                                visible, GraphicsUnit.Pixel);
                        }
                }
                SaveJpeg(preview, path);
            }
        }

        // The smallest rectangle holding every pixel that isn't (nearly) transparent; the whole image when none is.
        private static Rectangle VisibleBounds(Bitmap image)
        {
            var rectangle = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData data = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int left = image.Width, top = image.Height, right = -1, bottom = -1;
                var row = new byte[image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int x = 0; x < image.Width; x++)
                        if (row[x * 4 + 3] > 16)
                        {
                            left = Math.Min(left, x);
                            right = Math.Max(right, x);
                            top = Math.Min(top, y);
                            bottom = y;
                        }
                }
                return right < 0 ? rectangle : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static void SaveJpeg(Image image, string path)
        {
            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                image.Save(path, jpeg, parameters);
            }
        }

        public const string DetailsArgument = "--workshop-details";

        // gmpublish sets the item's title but not its description, and cannot add page images. When an item is created, a
        // separate RoPorter process (see SteamPreview) sets the description from addon.json and adds the page image, since
        // signing in to Steam as Garry's Mod lasts until the process exits.
        private static async Task UpdateDetailsAsync(string folder, long id, IProgress<string> progress, CancellationToken cancellation)
        {
            progress?.Report("Setting the Workshop description and page image...");
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var result = await RunAsync(self, DetailsArgument + " " + id.ToString(CultureInfo.InvariantCulture) + " " + Quote(folder), cancellation,
                new[] { ("SteamAppId", "4000"), ("SteamGameId", "4000") }).ConfigureAwait(false);
            // The helper's last line says what it did, or why it failed; steam_api prints its own start-up lines before it.
            string last = (Tail(result.Value).Split('\n').LastOrDefault() ?? "").Trim().TrimEnd('.');
            if (result.Key != 0)
                progress?.Report("The Workshop description and page image could not be set (" + (last.Length > 0 ? last : "no reason given") +
                    "). Add them on the item's Workshop page instead.");
            else if (last.Length > 0)
                progress?.Report(last + ".");
        }

        public static bool IsAddon(string folder) =>
            !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "addon.json")) && Directory.Exists(Path.Combine(folder, "models"));

        // The Workshop item is kept in addon.json ("workshopid"), so it stays with the addon wherever it is copied; gmad
        // ignores the extra key. Addons uploaded before that have it in src/workshop_id.txt instead.
        private const string IdKey = "workshopid";

        public static long? PublishedId(string folder)
        {
            try
            {
                string json = Path.Combine(folder, "addon.json");
                if (File.Exists(json) && Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json))[IdKey] is Newtonsoft.Json.Linq.JToken token &&
                    long.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long stored) && stored > 0)
                    return stored;
            }
            catch (Exception error) when (error is IOException || error is Newtonsoft.Json.JsonException)
            {
            }
            string file = Path.Combine(folder, IdFile.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
                ? id : (long?)null;
        }

        public static void SetPublishedId(string folder, long id)
        {
            string json = Path.Combine(folder, "addon.json");
            var addon = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json));
            addon[IdKey] = id;
            string text = addon.ToString(Newtonsoft.Json.Formatting.Indented).Replace("  ", "\t");
            File.WriteAllText(json, text + "\n", new UTF8Encoding(false)); // gmad cannot parse addon.json with a byte order mark
        }

        // Packs and uploads the addon; returns its Workshop item ID. New items start hidden on the Workshop.
        public static async Task<long> PublishAsync(string folder, IProgress<string> progress, CancellationToken cancellation)
        {
            if (!IsAddon(folder))
                throw new InvalidDataException("The export folder is not a Garry's Mod addon: " + folder);
            GarrysModInstall install = GarrysModInstall.Find() ?? throw new InvalidDataException(
                "Garry's Mod was not found. Its gmad and gmpublish tools are needed to upload to the Workshop.");
            string gmad = Path.Combine(install.Folder, "bin", "gmad.exe"), gmpublish = Path.Combine(install.Folder, "bin", "gmpublish.exe");
            if (!File.Exists(gmad) || !File.Exists(gmpublish))
                throw new InvalidDataException("Garry's Mod's gmad.exe or gmpublish.exe is missing from " + Path.Combine(install.Folder, "bin") + ".");
            string icon = Path.Combine(folder, IconFile);
            if (!File.Exists(icon))
                WriteIcon(null, icon);

            string gma = Path.Combine(Path.GetTempPath(), "roporter-" + Guid.NewGuid().ToString("N") + ".gma");
            try
            {
                progress?.Report("Packing the addon with gmad...");
                var packed = await RunAsync(gmad, "create -folder " + Quote(folder) + " -out " + Quote(gma), cancellation).ConfigureAwait(false);
                if (packed.Key != 0 || !File.Exists(gma))
                    throw new InvalidDataException("gmad could not pack the addon:\n" + Tail(packed.Value));

                long? existing = PublishedId(folder);
                progress?.Report(existing.HasValue ? "Updating Workshop item " + existing.Value + "..." : "Uploading to the Steam Workshop...");
                string arguments = existing.HasValue
                    ? "update -id " + existing.Value.ToString(CultureInfo.InvariantCulture) + " -addon " + Quote(gma) + " -icon " + Quote(icon) +
                        " -changes \"Updated with RoPorter\""
                    : "create -icon " + Quote(icon) + " -addon " + Quote(gma);
                var published = await RunAsync(gmpublish, arguments, cancellation).ConfigureAwait(false);
                string output = published.Value;
                if (Regex.IsMatch(output, @"\b(error|failed|failure|couldn't|could not)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(output, @"\bSuccess", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("gmpublish could not upload the addon (is Steam running and signed in?):\n" + Tail(output));
                if (existing.HasValue)
                {
                    if (published.Key != 0)
                        throw new InvalidDataException("gmpublish could not update the addon:\n" + Tail(output));
                    // Updates replace only the addon and its icon: the description and page image were set when the item
                    // was created, and may have been edited on its Workshop page since.
                    SetPublishedId(folder, existing.Value);
                    return existing.Value;
                }
                Match uid = Regex.Match(output, @"UID:\s*(\d+)", RegexOptions.IgnoreCase);
                if (!uid.Success)
                    uid = Regex.Matches(output, @"\b(\d{6,})\b").Cast<Match>().LastOrDefault() ?? uid;
                if (!uid.Success || !long.TryParse(uid.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                    throw new InvalidDataException("gmpublish did not report a Workshop item ID (is Steam running and signed in?):\n" + Tail(output));
                SetPublishedId(folder, id);
                await UpdateDetailsAsync(folder, id, progress, cancellation).ConfigureAwait(false);
                return id;
            }
            finally
            {
                try
                {
                    File.Delete(gma);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
        }

        internal static string Quote(string path) => "\"" + path + "\"";

        internal static string Tail(string output)
        {
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith("Press any key", StringComparison.OrdinalIgnoreCase)).ToArray();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 12)));
        }

        // Runs a Garry's Mod tool without a window and returns its exit code and output. gmpublish waits for a key press
        // ("Press any key to continue") before it starts, so a line is sent to its input.
        internal static async Task<System.Collections.Generic.KeyValuePair<int, string>> RunAsync(string file, string arguments, CancellationToken cancellation,
            (string Name, string Value)[] environment = null)
        {
            var output = new StringBuilder();
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(file),
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                },
                EnableRaisingEvents = true
            })
            {
                foreach (var variable in environment ?? new (string, string)[0])
                    process.StartInfo.EnvironmentVariables[variable.Name] = variable.Value;
                var exited = new TaskCompletionSource<bool>();
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.Exited += (sender, args) => exited.TrySetResult(true);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.StandardInput.WriteLine();
                process.StandardInput.Close();
                using (cancellation.Register(() =>
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }))
                    await exited.Task.ConfigureAwait(false);
                process.WaitForExit();
                cancellation.ThrowIfCancellationRequested();
                lock (output)
                    return new System.Collections.Generic.KeyValuePair<int, string>(process.ExitCode, output.ToString());
            }
        }
    }
}
