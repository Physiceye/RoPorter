using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rbx2Source.Export;

namespace Rbx2Source
{
    public sealed class Rbx2Source : Form
    {
        private readonly ComboBox exportType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox exportFormat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
        private readonly TextBox input = new TextBox { MaxLength = 19 };
        public const string DefaultApiBase = "https://api.ocublox.tv"; // IT WAS ME! // Note: I'm a peace of shit for using Rbx2Source code and dumping it into a AI to make somthing I always wanted and I'm leaving this comment to say sorry.
        private static readonly ExportFormat[] Formats = { ExportFormat.Glb, ExportFormat.Gltf, ExportFormat.Fbx, ExportFormat.GarrysMod };
        private readonly TextBox outputFolder = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Roblox Exports")
        };
        // Export stays disabled until the entered user or item has loaded its preview.
        private readonly Button export = new Button { Text = "Export", AutoSize = true, Enabled = false };
        private bool loaded;
        private readonly CheckBox singleTexture = new CheckBox
        {
            Text = "Single texture", AutoSize = true, Margin = new Padding(6, 6, 6, 3)
        };
        private readonly CheckBox vrchatShapes = new CheckBox
        {
            Text = "Add VRChat shape keys", AutoSize = true, Checked = true, Margin = new Padding(6, 6, 6, 3)
        };
        private readonly CheckBox faceTracking = new CheckBox
        {
            Text = "Add FT support", AutoSize = true, Margin = new Padding(6, 6, 6, 3)
        };
        // Export scale in hundredths: 100 is full size (the previous default and the maximum), 10 is the minimum (0.1).
        private readonly TrackBar scale = new TrackBar
        {
            Minimum = 10, Maximum = 100, Value = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 10,
            TickStyle = TickStyle.None, AutoSize = false, Width = 220, Height = 26, Margin = new Padding(3, 6, 3, 0)
        };
        private readonly Label scaleValue = new Label { AutoSize = true, Margin = new Padding(0, 9, 3, 0) };
        private readonly Button cancel = new Button { Text = "Cancel", Enabled = false, AutoSize = true };
        private readonly Button openFolder = new Button { Text = "Open export folder", AutoSize = true };
        private readonly Button browse = new Button { Text = "Browse...", AutoSize = true };
        private readonly TextBox log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
        };
        private CancellationTokenSource cancellation;
        private readonly PictureBox preview = new PictureBox
        {
            Size = new Size(128, 128), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White, Margin = new Padding(10, 3, 0, 3)
        };
        private readonly ToolTip previewTip = new ToolTip();
        private readonly Image brokenPreview = LoadImage("RoPorter.BrokenPreview.png");
        private readonly Image[] typeIcons = { LoadImage("RoPorter.Humanoid_icon.png"), LoadImage("RoPorter.Accoutrement_icon.png") };
        // Waits for typing to pause before looking up the preview.
        private readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 600 };
        private CancellationTokenSource previewCancellation;

        public Rbx2Source()
        {
            Text = "RoPorter";
            using (Stream iconStream = typeof(Rbx2Source).Assembly.GetManifestResourceStream("RoPorter.favicon.ico"))
                if (iconStream != null)
                    Icon = new Icon(iconStream);
            MinimumSize = new Size(680, 540);
            ClientSize = new Size(900, 580);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;

            exportType.Items.AddRange(new object[] { "Avatar", "Catalog Item" });
            exportType.DrawMode = DrawMode.OwnerDrawFixed;
            exportType.ItemHeight = 20;
            exportType.DrawItem += DrawExportType;
            exportType.SelectedIndex = 0;
            exportFormat.Items.AddRange(new object[] { "GLB", "glTF", "FBX", "Garry's Mod" });
            exportFormat.SelectedIndex = 0;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 7
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(layout, 0, "Export:", exportType);
            AddRow(layout, 1, "User ID:", input);
            AddRow(layout, 2, "Output folder:", outputFolder);
            layout.Controls.Add(browse, 0, 3);
            var scaleRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            scaleRow.Controls.AddRange(new Control[] { new Label { Text = "Scale:", AutoSize = true, Margin = new Padding(3, 9, 0, 0) }, scale, scaleValue });
            layout.Controls.Add(scaleRow, 1, 3);
            new ToolTip().SetToolTip(scale, "Scales the exported model down. 1 is full size (the default); 0.1 is the smallest.");
            scale.ValueChanged += (sender, args) => scaleValue.Text = (scale.Value / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            scaleValue.Text = (1.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            layout.Controls.Add(new Label { Text = "Export format:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(3, 8, 3, 0) }, 0, 4);
            preview.Image = brokenPreview;
            layout.Controls.Add(preview, 2, 0);
            layout.SetRowSpan(preview, 5);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            actions.Controls.AddRange(new Control[] { exportFormat, singleTexture, vrchatShapes, faceTracking, export, cancel, openFolder });
            new ToolTip().SetToolTip(singleTexture, "Merges every material into one material and one texture.");
            new ToolTip().SetToolTip(vrchatShapes, "Adds VRChat visemes.");
            new ToolTip().SetToolTip(faceTracking, "Adds Unified Expressions face shapes for VRCFaceTracking.");
            new ToolTip().SetToolTip(exportFormat, "Garry's Mod builds an addon folder: avatars become a playermodel that also works as a ragdoll, catalog items become props.");
            layout.Controls.Add(actions, 0, 5);
            layout.SetColumnSpan(actions, 3);
            layout.Controls.Add(log, 0, 6);
            layout.SetColumnSpan(log, 3);
            for (int row = 0; row < 6; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            exportType.SelectedIndexChanged += (sender, args) =>
            {
                ((Label)layout.GetControlFromPosition(0, 1)).Text =
                    exportType.SelectedIndex == 0 ? "User ID:" : "Item ID:";
                UpdateFaceOptions(false);
                SchedulePreview();
            };
            // IDs only: typed non-digits are ignored, and pasted text keeps just its digits.
            input.KeyPress += (sender, args) => args.Handled = !char.IsDigit(args.KeyChar) && !char.IsControl(args.KeyChar);
            input.TextChanged += (sender, args) =>
            {
                string digits = new string(input.Text.Where(char.IsDigit).ToArray());
                if (digits != input.Text)
                {
                    int caret = Math.Min(digits.Length, input.SelectionStart);
                    input.Text = digits;
                    input.SelectionStart = caret;
                    return;
                }
                SchedulePreview();
            };
            exportFormat.SelectedIndexChanged += (sender, args) => UpdateFaceOptions(false);
            previewTimer.Tick += async (sender, args) =>
            {
                previewTimer.Stop();
                await UpdatePreviewAsync();
            };
            browse.Click += Browse_Click;
            export.Click += Export_Click;
            cancel.Click += (sender, args) => cancellation?.Cancel();
            openFolder.Click += (sender, args) =>
            {
                if (Directory.Exists(outputFolder.Text))
                    Open(outputFolder.Text);
                else
                    ShowError("The export folder does not exist yet.");
            };
            FormClosing += (sender, args) =>
            {
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    args.Cancel = true;
                    AppendLog("Cancelling export. Close the window again after cancellation finishes.");
                }
            };
            FormClosed += (sender, args) =>
            {
                previewTimer.Dispose();
                previewCancellation?.Cancel();
                if (preview.Image != brokenPreview)
                    preview.Image?.Dispose();
                brokenPreview?.Dispose();
                foreach (Image icon in typeIcons)
                    icon?.Dispose();
            };
        }

        private static Image LoadImage(string resource)
        {
            using (Stream stream = typeof(Rbx2Source).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                    return null;
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
        }

        // Export type dropdown entries with their icons.
        private void DrawExportType(object sender, DrawItemEventArgs args)
        {
            args.DrawBackground();
            if (args.Index >= 0)
            {
                Image icon = typeIcons[args.Index];
                int x = args.Bounds.X + 3;
                if (icon != null)
                {
                    args.Graphics.DrawImage(icon, x, args.Bounds.Y + (args.Bounds.Height - icon.Height) / 2, icon.Width, icon.Height);
                    x += icon.Width + 5;
                }
                TextRenderer.DrawText(args.Graphics, exportType.Items[args.Index].ToString(), args.Font,
                    new Rectangle(x, args.Bounds.Y, args.Bounds.Right - x, args.Bounds.Height), args.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            args.DrawFocusRectangle();
        }

        private void SetLoaded(bool value)
        {
            loaded = value;
            export.Enabled = loaded && cancellation == null;
        }

        private void SchedulePreview()
        {
            SetLoaded(false);
            previewCancellation?.Cancel();
            previewTimer.Stop();
            previewTimer.Start();
        }

        private void ShowPreview(Image image, string description)
        {
            Image old = preview.Image;
            preview.Image = image ?? brokenPreview;
            if (old != null && old != brokenPreview && old != preview.Image)
                old.Dispose();
            previewTip.SetToolTip(preview, description);
        }

        // Shows the avatar's headshot or the item's thumbnail; anything that can't be loaded shows the broken preview.
        private async Task UpdatePreviewAsync()
        {
            previewCancellation?.Cancel();
            var current = previewCancellation = new CancellationTokenSource();
            string text = input.Text.Trim();
            bool avatar = exportType.SelectedIndex == 0;
            Text = "RoPorter";
            if (text.Length == 0)
            {
                return;
            }
            try
            {
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    long id = await downloader.ResolveIdAsync(text, avatar, current.Token);
                    byte[] bytes = await downloader.DownloadThumbnailAsync(id, avatar, current.Token);
                    if (current != previewCancellation)
                        return;
                    using (var stream = new MemoryStream(bytes))
                    using (var image = Image.FromStream(stream))
                        ShowPreview(new Bitmap(image), (avatar ? "User " : "Item ") + id);
                    SetLoaded(true);
                    await ShowTitleAsync(downloader, id, avatar, current);
                }
            }
            catch (OperationCanceledException)
            {
                if (current == previewCancellation && !current.IsCancellationRequested)
                    ShowPreview(null, "The preview timed out.");
            }
            catch (Exception error)
            {
                if (current == previewCancellation)
                    ShowPreview(null, "No preview: " + error.Message);
            }
        }

        // A loaded avatar or catalog item names the window: "RoPorter - <username>" or "RoPorter - <item name>", or its ID
        // when the name can't be looked up.
        private async Task ShowTitleAsync(RobloxModelDownloader downloader, long id, bool avatar, CancellationTokenSource current)
        {
            string title = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                title = avatar ? await downloader.GetUsernameAsync(id, current.Token)
                    : await downloader.GetCatalogDisplayNameAsync(id, current.Token);
            }
            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is Newtonsoft.Json.JsonException)
            {
            }
            if (current == previewCancellation)
                Text = "RoPorter - " + title;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control, 1, row);
        }

        private void Browse_Click(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose the export folder" })
            {
                if (Directory.Exists(outputFolder.Text))
                    dialog.SelectedPath = outputFolder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    outputFolder.Text = dialog.SelectedPath;
            }
        }

        private static HttpClient CreateClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        private async void Export_Click(object sender, EventArgs args)
        {
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            log.Clear();
            try
            {
                bool avatar = exportType.SelectedIndex == 0;
                string selection = input.Text;
                ExportFormat format = Formats[exportFormat.SelectedIndex];
                string folder = Path.GetFullPath(outputFolder.Text);
                var progress = new Progress<string>(AppendLog);
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    long id = await downloader.ResolveIdAsync(selection, avatar, cancellation.Token);
                    string path = await downloader.ExportAsync(id, avatar, "", folder, progress, cancellation.Token, format,
                        avatar && faceTracking.Checked, vrchatShapes.Checked, singleTexture.Checked, scale.Value / 100f);
                    AppendLog("Export complete: " + path);
                }
            }
            catch (OperationCanceledException)
            {
                ShowError(cancellation.IsCancellationRequested ? "Export cancelled." : "The Roblox request timed out. Try again.");
            }
            catch (Exception error)
            {
                ShowError(error.Message);
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            foreach (Control control in new Control[] { exportType, exportFormat, input, outputFolder, browse, singleTexture, scale })
                control.Enabled = !busy;
            export.Enabled = !busy && loaded;
            UpdateFaceOptions(busy);
            cancel.Enabled = busy;
        }

        // Face shape options apply to avatars outside Garry's Mod (Source models get no shape keys).
        // Face tracking and the VRChat shape keys are independent.
        private void UpdateFaceOptions(bool busy)
        {
            bool avatar = exportType.SelectedIndex == 0 && Formats[Math.Max(0, exportFormat.SelectedIndex)] != ExportFormat.GarrysMod;
            vrchatShapes.Enabled = !busy && avatar;
            faceTracking.Enabled = !busy && avatar;
        }

        private void AppendLog(string message)
        {
            log.AppendText(message + Environment.NewLine);
        }

        private void ShowError(string message)
        {
            AppendLog(message);
            MessageBox.Show(this, message, "RoPorter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                ShowError("Could not open the browser or folder: " + error.Message);
            }
        }
    }
}
