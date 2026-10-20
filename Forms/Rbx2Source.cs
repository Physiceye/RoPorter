using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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
        private static readonly ExportFormat[] Formats = { ExportFormat.Glb, ExportFormat.Gltf, ExportFormat.Fbx, ExportFormat.GarrysMod, ExportFormat.SourceFilmmaker };
        // Settings are remembered between runs in "littlefart" next to the exe, as "name=value" lines anyone can edit.
        private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "littlefart");
        private const string FolderSetting = "folder", DarkModeSetting = "darkmode";
        private static readonly string[] SettingNames = { FolderSetting, DarkModeSetting, "singletexture", "vrchat", "facetracking", "connectjoints", "discord", "mood", "ownfolder" };

        // Discord Rich Presence: what RoPorter is doing, shown on the user's Discord profile (on by default).
        private readonly CheckBox discordPresence = new CheckBox { Text = "Discord Rich Presence", AutoSize = true, Checked = true, Margin = new Padding(3, 3, 3, 6) };
        private readonly DiscordPresence discord = new DiscordPresence();
        private string presenceDetails = "Browsing", presenceState;
        // The loaded avatar's username or item's name (as in the window title).
        private string loadedName;
        private static readonly string[] FormatNames = { "GLB", "glTF", "FBX", "Garry's Mod", "Source Filmmaker" };

        private void ShowPresence(string details, string state)
        {
            presenceDetails = details;
            presenceState = state;
            if (discordPresence.Checked)
                _ = discord.SetAsync(details, state);
        }
        private static readonly Dictionary<string, string> Saved = LoadSettings();
        private readonly TextBox outputFolder = new TextBox { Text = LoadOutputFolder() };
        private readonly CheckBox darkMode = new CheckBox { Text = "Dark mode", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        private readonly Button exportTab = new Button { Text = "Export", AutoSize = true, Margin = new Padding(0, 0, 2, 0) };
        private readonly Button settingsTab = new Button { Text = "Settings", AutoSize = true, Margin = new Padding(0) };
        private readonly Button github = new Button { Text = "GitHub", AutoSize = true, Margin = new Padding(0) };
        private const string GitHubPage = "https://github.com/Physiceye/RoPorter";
        private readonly Button reportBug = new Button { Text = "Report a bug", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        private const string IssuesPage = "https://github.com/Physiceye/RoPorter/issues";
        // Export stays disabled until the entered user or item has loaded its preview.
        private readonly Button export = new Button { Text = "Export", AutoSize = true, Enabled = false };
        private bool loaded;
        // Enabled once a Garry's Mod export of the loaded avatar or item has finished; uploads that addon folder.
        private readonly Button upload = new Button { Text = "Upload to Workshop", AutoSize = true, Enabled = false };
        private string exportedAddon;
        // The format of that export: Garry's Mod (an addon) or Source Filmmaker (SFM's Workshop).
        private ExportFormat exportedFormat;
        // Export options, on the Settings page.
        private readonly CheckBox singleTexture = new CheckBox
        {
            Text = "Single texture", AutoSize = true, Margin = new Padding(3, 3, 3, 6)
        };
        private readonly CheckBox vrchatShapes = new CheckBox
        {
            Text = "Add VRChat shape keys", AutoSize = true, Margin = new Padding(3, 3, 3, 6)
        };
        private readonly CheckBox faceTracking = new CheckBox
        {
            Text = "Add FT support", AutoSize = true, Margin = new Padding(3, 3, 3, 6)
        };
        // The avatar's Mood: its resting expression (on by default, as in Roblox).
        private readonly CheckBox applyMood = new CheckBox
        {
            Text = "Apply mood expression", AutoSize = true, Checked = true, Margin = new Padding(3, 3, 3, 6)
        };
        // Each export in its own folder (on by default); off collects many exports in the output folder itself.
        private readonly CheckBox ownFolder = new CheckBox
        {
            Text = "Export into its own folder", AutoSize = true, Checked = true, Margin = new Padding(3, 3, 3, 6)
        };
        private readonly CheckBox connectJoints = new CheckBox
        {
            Text = "Connect body joints", AutoSize = true, Margin = new Padding(3, 3, 3, 6)
        };
        // Export scale in hundredths: 100 is full size (the maximum), 10 is the minimum (0.1), 80 is the default.
        private readonly TrackBar scale = new TrackBar
        {
            Minimum = 10, Maximum = 100, Value = 80, TickFrequency = 10, SmallChange = 1, LargeChange = 10,
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
        private readonly Image[] typeIcons = { LoadImage("RoPorter.Humanoid_icon.png"), LoadImage("RoPorter.Accoutrement_icon.png"), LoadImage("RoPorter.Player_icon.png"),
            LoadImage("RoPorter.Model_icon.png"), LoadImage("RoPorter.Workspace_icon.png") };
        private readonly Image[] tabIcons = { LoadImage("RoPorter.Configuration_icon.png"), LoadImage("RoPorter.Starterpack_icon.png") };
        // Export types: 0 Avatar (a user), 1 Catalog Item, 2 Bundle (exported like an avatar wearing it), 3 Model (a .rbxm or
        // .rbxmx file, exported like a catalog item), 4 Place (a .rbxl or .rbxlx file; a map in Garry's Mod).
        private const int ModelType = 3, PlaceType = 4;
        private bool IsBundle => exportType.SelectedIndex == 2;
        private bool IsModel => exportType.SelectedIndex == ModelType;
        private bool IsPlace => exportType.SelectedIndex == PlaceType;
        private bool IsFile => IsModel || IsPlace;
        private bool IsAvatarLike => exportType.SelectedIndex == 0 || exportType.SelectedIndex == 2;
        // The file picker of the Model and Place types, beside the ID box (which holds the file's path for those types).
        private readonly Button openModel = new Button { Text = "Open...", AutoSize = true, Visible = false, Margin = new Padding(3, 0, 0, 0) };
        // What was entered for each kind of input (an ID, a model file, a place file), so switching type and back restores it.
        private readonly Dictionary<int, string> inputs = new Dictionary<int, string>();
        private int shownType;
        private static int InputSlot(int type) => type == ModelType || type == PlaceType ? type : 0;
        // The loaded bundle's details (name, outfit, items), fetched with its preview.
        private RobloxModelDownloader.BundleInfo loadedBundle;
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

            exportType.Items.AddRange(new object[] { "Avatar", "Catalog Item", "Bundle", "Model", "Place" });
            // Page tabs with their icons: Export and Settings.
            for (int i = 0; i < 2; i++)
            {
                Button tab = i == 0 ? exportTab : settingsTab;
                tab.Image = tabIcons[i];
                tab.ImageAlign = ContentAlignment.MiddleLeft;
                tab.TextImageRelation = TextImageRelation.ImageBeforeText;
            }
            exportType.DrawMode = DrawMode.OwnerDrawFixed;
            exportType.ItemHeight = 20;
            exportType.DrawItem += DrawExportType;
            exportType.SelectedIndex = 0;
            exportFormat.Items.AddRange(FormatNames);
            exportFormat.SelectedIndex = 0;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 7
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(layout, 0, "Export:", exportType);
            var inputRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            input.Dock = DockStyle.Fill;
            inputRow.Controls.Add(input, 0, 0);
            inputRow.Controls.Add(openModel, 1, 0);
            AddRow(layout, 1, "User ID:", inputRow);
            new ToolTip().SetToolTip(openModel, "Choose a model (.rbxm, .rbxmx) or place (.rbxl, .rbxlx) saved from Roblox Studio. You can also drop one on the window.");
            openModel.Click += OpenModel_Click;
            // A model or place file dropped anywhere on the window switches to its type and loads it.
            AllowDrop = true;
            DragEnter += (sender, args) => args.Effect = DroppedFile(args) != null && cancellation == null ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (sender, args) =>
            {
                string file = DroppedFile(args);
                if (file == null || cancellation != null)
                    return;
                exportType.SelectedIndex = IsPlaceFile(file) ? PlaceType : ModelType;
                input.Text = file;
            };
            AddRow(layout, 2, "Output folder:", outputFolder);
            layout.Controls.Add(browse, 0, 3);
            var scaleRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            scaleRow.Controls.AddRange(new Control[] { new Label { Text = "Scale:", AutoSize = true, Margin = new Padding(3, 9, 0, 0) }, scale, scaleValue });
            layout.Controls.Add(scaleRow, 1, 3);
            new ToolTip().SetToolTip(scale, "Scales the exported model down. 1 is full size, 0.8 is the default and 0.1 is the smallest.\n" +
                "Places always export at full size, where a Roblox character is a Garry's Mod player's size.");
            scale.ValueChanged += (sender, args) => scaleValue.Text = (scale.Value / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            scaleValue.Text = (scale.Value / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            layout.Controls.Add(new Label { Text = "Export format:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(3, 8, 3, 0) }, 0, 4);
            preview.Image = brokenPreview;
            layout.Controls.Add(preview, 2, 0);
            layout.SetRowSpan(preview, 5);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            actions.Controls.AddRange(new Control[] { exportFormat, export, upload, cancel, openFolder });
            new ToolTip().SetToolTip(upload, "Packs the exported Garry's Mod addon and uploads it to the Steam Workshop. Steam must be running.");
            new ToolTip().SetToolTip(singleTexture, "Merges every material into one material and one texture.");
            new ToolTip().SetToolTip(vrchatShapes, "Adds VRChat visemes.");
            new ToolTip().SetToolTip(faceTracking, "Adds Unified Expressions face shapes for VRCFaceTracking.");
            new ToolTip().SetToolTip(exportFormat, "Garry's Mod builds an addon folder: avatars become a playermodel that also works as a ragdoll, catalog items and " +
                "models become props, places become a map.\n" +
                "Source Filmmaker compiles the model with SFM's own tools; copy its models and materials folders into SFM's game\\usermod folder.");
            layout.Controls.Add(actions, 0, 5);
            layout.SetColumnSpan(actions, 3);
            layout.Controls.Add(log, 0, 6);
            layout.SetColumnSpan(log, 3);
            for (int row = 0; row < 6; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var settingsPage = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false
            };
            settingsPage.Controls.Add(darkMode);
            settingsPage.Controls.Add(discordPresence);
            BindSetting(discordPresence, "discord");
            discordPresence.Enabled = DiscordPresence.Configured;
            new ToolTip().SetToolTip(discordPresence, DiscordPresence.Configured
                ? "Shows what you are exporting on your Discord profile while RoPorter is open."
                : "This build has no Discord application ID set, so Rich Presence is unavailable.");
            discordPresence.CheckedChanged += (sender, args) =>
            {
                if (discordPresence.Checked)
                    ShowPresence(presenceDetails, presenceState);
                else
                    _ = discord.ClearAsync();
            };
            settingsPage.Controls.Add(new Label { Text = "Export options", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 12, 3, 6) });
            settingsPage.Controls.AddRange(new Control[] { singleTexture, vrchatShapes, faceTracking, connectJoints, applyMood });
            BindSetting(applyMood, "mood");
            settingsPage.Controls.Add(ownFolder);
            BindSetting(ownFolder, "ownfolder");
            new ToolTip().SetToolTip(ownFolder, "Each export gets a folder named after the avatar or item. Off, exports go straight into the output folder, " +
                "so many can be collected in one.\nGarry's Mod: they become one addon. If the output folder is Garry's Mod's addons folder, that addon is addons\\RoPorter.");
            new ToolTip().SetToolTip(applyMood, "Gives the face the avatar's equipped Mood (its resting expression, e.g. a smile). " +
                "Off, the face is neutral; GLB, glTF and FBX keep the mood as a \"Default\" shape key you can turn on.");
            BindSetting(singleTexture, "singletexture");
            BindSetting(vrchatShapes, "vrchat");
            BindSetting(faceTracking, "facetracking");
            BindSetting(connectJoints, "connectjoints");
            new ToolTip().SetToolTip(connectJoints, "Blends the body's weights across its joints so the surface stretches when they bend, instead of each " +
                "part moving on its own. For UGC bundles modelled as one connected body; R6 avatars stay blocky.");
            var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(12, 8, 12, 0) };
            tabs.Controls.AddRange(new Control[] { exportTab, settingsTab });
            // Bottom bar: the app version on the left, GitHub in the right corner with the bug report button beside it.
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 0, 12, 10)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
            links.Controls.Add(github);
            links.Controls.Add(reportBug);
            bottom.Controls.Add(new Label
            {
                Text = "v" + typeof(Rbx2Source).Assembly.GetName().Version.ToString(3), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0)
            }, 0, 0);
            bottom.Controls.Add(links, 1, 0);
            new ToolTip().SetToolTip(github, "Opens RoPorter's GitHub page.");
            new ToolTip().SetToolTip(reportBug, "Opens RoPorter's GitHub issues page to report a bug.");
            reportBug.Click += (sender, args) => Open(IssuesPage);
            // Docking runs from the last added control, so the tab strip and the bottom bar take their space before the pages fill the rest.
            Controls.Add(layout);
            Controls.Add(settingsPage);
            Controls.Add(tabs);
            Controls.Add(bottom);
            exportTab.Click += (sender, args) => ShowPage(layout, settingsPage, false);
            settingsTab.Click += (sender, args) => ShowPage(layout, settingsPage, true);
            github.Click += (sender, args) => Open(GitHubPage);
            BindSetting(darkMode, DarkModeSetting);
            darkMode.CheckedChanged += (sender, args) => ApplyTheme();
            ApplyTheme();

            exportType.SelectedIndexChanged += (sender, args) =>
            {
                ((Label)layout.GetControlFromPosition(0, 1)).Text = exportType.SelectedIndex == 0 ? "User ID:" : IsBundle ? "Bundle ID:"
                    : IsModel ? "Model file:" : IsPlace ? "Place file:" : "Item ID:";
                // The ID box holds the file's path for the Model and Place types.
                if (InputSlot(shownType) != InputSlot(exportType.SelectedIndex))
                {
                    inputs[InputSlot(shownType)] = input.Text;
                    input.MaxLength = IsFile ? 32767 : 19;
                    input.Text = inputs.TryGetValue(InputSlot(exportType.SelectedIndex), out string saved) ? saved : "";
                }
                shownType = exportType.SelectedIndex;
                openModel.Visible = IsFile;
                UpdateFaceOptions(false);
                UpdateScale(false);
                SchedulePreview();
            };
            // IDs only: typed non-digits are ignored, and pasted text keeps just its digits. A model file's path is any text.
            input.KeyPress += (sender, args) => args.Handled = !IsFile && !char.IsDigit(args.KeyChar) && !char.IsControl(args.KeyChar);
            input.TextChanged += (sender, args) =>
            {
                if (IsFile)
                {
                    SchedulePreview();
                    return;
                }
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
            exportFormat.SelectedIndexChanged += (sender, args) =>
            {
                UpdateFaceOptions(false);
                UpdateUpload();
            };
            upload.Click += Upload_Click;
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
            Shown += async (sender, args) =>
            {
                ShowPresence("Browsing", null);
                await CheckForUpdatesAsync();
            };
            FormClosed += (sender, args) =>
            {
                discord.Dispose();
                previewTimer.Dispose();
                previewCancellation?.Cancel();
                if (preview.Image != brokenPreview)
                    preview.Image?.Dispose();
                brokenPreview?.Dispose();
                foreach (Image icon in typeIcons.Concat(tabIcons))
                    icon?.Dispose();
            };
        }

        private static bool IsModelFile(string path) =>
            path != null && (path.EndsWith(".rbxm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rbxmx", StringComparison.OrdinalIgnoreCase));

        private static bool IsPlaceFile(string path) =>
            path != null && (path.EndsWith(".rbxl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rbxlx", StringComparison.OrdinalIgnoreCase));

        // The one model or place file being dragged onto the window, if that is what it is.
        private static string DroppedFile(DragEventArgs args) =>
            args.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && (IsModelFile(files[0]) || IsPlaceFile(files[0])) &&
            File.Exists(files[0]) ? files[0] : null;

        private void OpenModel_Click(object sender, EventArgs args)
        {
            string filter = IsPlace ? "Roblox places (*.rbxl;*.rbxlx)|*.rbxl;*.rbxlx" : "Roblox models (*.rbxm;*.rbxmx)|*.rbxm;*.rbxmx";
            using (var dialog = new OpenFileDialog { Title = IsPlace ? "Choose a Roblox place file" : "Choose a Roblox model file", Filter = filter, CheckFileExists = true })
            {
                string current = input.Text.Trim();
                if (current.Length > 0)
                    try
                    {
                        if (Directory.Exists(Path.GetDirectoryName(current)))
                            dialog.InitialDirectory = Path.GetDirectoryName(current);
                    }
                    catch (ArgumentException) { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    input.Text = dialog.FileName;
            }
        }

        // A model or place file's preview: its type's icon, with its part count in the tooltip. The file is read to check it is
        // a Roblox model with parts (or a place with parts in its Workspace) before Export is enabled.
        private async Task PreviewModelAsync(string file, CancellationTokenSource current)
        {
            bool place = IsPlace;
            if (!(place ? IsPlaceFile(file) : IsModelFile(file)) || !File.Exists(file))
            {
                ShowPreview(null, place ? "Choose a .rbxl or .rbxlx file." : "Choose a .rbxm or .rbxmx file.");
                return;
            }
            int parts;
            Bitmap picture;
            try
            {
                ModelContents contents = await Task.Run(() => ModelFileBuilder.Read(File.ReadAllBytes(file)));
                if (place && !contents.IsPlace)
                {
                    if (current == previewCancellation)
                        ShowPreview(null, "This file has no Workspace; export it as a Model.");
                    return;
                }
                parts = contents.Parts.Count;
                // A picture of what is in the file (its meshes and unions as boxes, since nothing is downloaded yet).
                picture = parts > 0 ? await Task.Run(() => PlaceRender.Preview(contents, 256)) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                error is NotSupportedException || error is ArgumentException || error is IndexOutOfRangeException)
            {
                if (current == previewCancellation)
                    ShowPreview(null, "Not a readable Roblox " + (place ? "place: " : "model: ") + error.Message);
                return;
            }
            if (current != previewCancellation)
            {
                picture?.Dispose();
                return;
            }
            if (parts == 0)
            {
                ShowPreview(null, place ? "The place's Workspace has no parts to export." : "The model file has no parts to export.");
                return;
            }
            Image icon = typeIcons[place ? PlaceType : ModelType];
            if (picture == null && icon != null)
            {
                // Nothing visible to draw: the type's icon instead.
                picture = new Bitmap(128, 128);
                using (Graphics graphics = Graphics.FromImage(picture))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    graphics.DrawImage(icon, new Rectangle(28, 28, 72, 72));
                }
            }
            string name = Path.GetFileNameWithoutExtension(file);
            ShowPreview(picture, name + ": " + parts + (parts == 1 ? " part" : " parts"));
            SetLoaded(true);
            Text = "RoPorter - " + name;
            loadedName = name;
            ShowPresence("Viewing " + name, place ? "Place file" : "Model file");
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
            // A different avatar or item no longer matches the exported addon.
            if (!value)
                exportedAddon = null;
            UpdateUpload();
        }

        private void UpdateUpload() =>
            upload.Enabled = cancellation == null && exportedAddon != null && Formats[Math.Max(0, exportFormat.SelectedIndex)] == exportedFormat;

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
            bool avatar = exportType.SelectedIndex == 0, bundle = IsBundle;
            Text = "RoPorter";
            loadedBundle = null;
            if (text.Length == 0)
            {
                // Nothing entered: back to the placeholder image and the plain window title.
                ShowPreview(null, null);
                loadedName = null;
                ShowPresence("Browsing", null);
                return;
            }
            if (IsFile)
            {
                await PreviewModelAsync(text, current);
                return;
            }
            try
            {
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    if (bundle)
                    {
                        // Bundles: the details (name and outfit) the export needs, then the catalog picture. A missing picture
                        // only leaves the broken preview.
                        long bundleId = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                        RobloxModelDownloader.BundleInfo details = await downloader.GetBundleAsync(bundleId, current.Token);
                        Image picture = null;
                        string problem = null;
                        try
                        {
                            using (var stream = new MemoryStream(await downloader.DownloadBundleThumbnailAsync(bundleId, current.Token)))
                            using (var image = Image.FromStream(stream))
                                picture = new Bitmap(image);
                        }
                        catch (Exception error) when (!(error is OperationCanceledException) && !(error is ApiMessageException))
                        {
                            problem = error.Message;
                        }
                        if (current != previewCancellation)
                        {
                            picture?.Dispose();
                            return;
                        }
                        ShowPreview(picture, picture != null ? "Bundle " + bundleId : "No picture: " + problem);
                        loadedBundle = details;
                        SetLoaded(true);
                        Text = "RoPorter - " + details.Name;
                        loadedName = details.Name;
                        ShowPresence("Viewing " + details.Name, "Bundle");
                        return;
                    }
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
            catch (ApiMessageException error)
            {
                // The API is down for maintenance (HTTP 503): its message is shown alone, and in the log too, where it is seen
                // without hovering.
                if (current != previewCancellation)
                    return;
                SetLoaded(false);
                ShowPreview(null, error.Message);
                if (error.Message != lastApiMessage)
                    AppendLog(error.Message);
                lastApiMessage = error.Message;
            }
            catch (Exception error)
            {
                if (current == previewCancellation)
                    ShowPreview(null, "No preview: " + error.Message);
            }
        }

        // The last message from the API that was logged, so looking up several IDs in a row doesn't repeat it.
        private string lastApiMessage;

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
            {
                Text = "RoPorter - " + title;
                loadedName = title;
                ShowPresence("Viewing " + title, avatar ? "Avatar" : "Catalog item");
            }
        }

        private const string ReleasesApi = "https://api.github.com/repos/Physiceye/RoPorter/releases/latest";

        // Compares the latest GitHub release's tag (e.g. "v1.0.1") with this build's AssemblyVersion and offers the
        // release page when it is newer. A failed check is only logged.
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                Newtonsoft.Json.Linq.JObject release;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("RoPorter");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    release = Newtonsoft.Json.Linq.JObject.Parse(await client.GetStringAsync(ReleasesApi));
                }
                string tag = release.Value<string>("tag_name") ?? "";
                Version current = Normalize(typeof(Rbx2Source).Assembly.GetName().Version);
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version latest) || Normalize(latest) <= current)
                    return;
                AppendLog("RoPorter " + tag + " is available (this is v" + current.ToString(3) + ").");
                if (Uri.TryCreate(release.Value<string>("html_url"), UriKind.Absolute, out Uri page) && page.Host == "github.com" &&
                    MessageBox.Show(this, "RoPorter " + tag + " is available. You have v" + current.ToString(3) +
                        ".\n\nOpen the download page?", "RoPorter update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Open(page.AbsoluteUri);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException || error is Newtonsoft.Json.JsonException)
            {
                AppendLog("Could not check for updates: " + error.Message);
            }
        }

        // Missing version parts count as zero, so "1.0.0" equals AssemblyVersion 1.0.0.0.
        private static Version Normalize(Version version) =>
            new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

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
                {
                    outputFolder.Text = dialog.SelectedPath;
                    SaveSetting(FolderSetting, dialog.SelectedPath);
                }
            }
        }

        // Reads "name=value" lines. An older file that holds only a folder path is read as that folder.
        private static Dictionary<string, string> LoadSettings()
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsFile))
                    return settings;
                foreach (string line in File.ReadAllLines(SettingsFile))
                {
                    int equals = line.IndexOf('=');
                    string name = equals > 0 ? line.Substring(0, equals).Trim() : "";
                    if (SettingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        settings[name] = line.Substring(equals + 1).Trim();
                    else if (line.Trim().Length > 0 && settings.Count == 0)
                        settings[FolderSetting] = line.Trim();
                }
            }
            catch (Exception) { }
            return settings;
        }

        private static void SaveSetting(string name, string value)
        {
            Saved[name] = value;
            try
            {
                File.WriteAllLines(SettingsFile, Saved.Select(setting => setting.Key + "=" + setting.Value));
            }
            catch (Exception) { }
        }

        // Starts the checkbox from its saved value (or its default) and saves every change.
        private static void BindSetting(CheckBox box, string name)
        {
            if (Saved.TryGetValue(name, out string value))
                box.Checked = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            box.CheckedChanged += (sender, args) => SaveSetting(name, box.Checked ? "true" : "false");
        }

        private static string LoadOutputFolder() =>
            Saved.TryGetValue(FolderSetting, out string folder) && folder.Length > 0
                ? folder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Roblox Exports");

        private void ShowPage(Control exportPage, Control settingsPage, bool settings)
        {
            exportPage.Visible = !settings;
            settingsPage.Visible = settings;
            ApplyTheme();
        }

        private static readonly Color DarkBack = Color.FromArgb(32, 32, 32), DarkField = Color.FromArgb(45, 45, 45),
            DarkButton = Color.FromArgb(55, 55, 55), DarkBorder = Color.FromArgb(85, 85, 85), DarkText = Color.FromArgb(230, 230, 230);

        // Recolours every control for dark or light mode, including the window's title bar.
        private void ApplyTheme()
        {
            bool dark = darkMode.Checked;
            Theme(this, dark);
            foreach (Button tab in new[] { exportTab, settingsTab })
            {
                bool selected = tab == settingsTab ? darkMode.Parent.Visible : !darkMode.Parent.Visible;
                tab.FlatStyle = FlatStyle.Flat;
                tab.FlatAppearance.BorderSize = 0;
                tab.UseVisualStyleBackColor = false;
                tab.BackColor = selected ? (dark ? DarkButton : SystemColors.Window) : (dark ? DarkBack : SystemColors.Control);
                // One bold font for the selected tab, made once (a new Font on every switch would leak GDI handles).
                tab.Font = selected ? tabFont ?? (tabFont = new Font(Font, FontStyle.Bold)) : Font;
            }
            if (IsHandleCreated)
                DarkTitleBar(dark);
        }

        private void Theme(Control control, bool dark)
        {
            if (control is TextBox box)
            {
                box.BackColor = dark ? DarkField : box.ReadOnly ? SystemColors.Control : SystemColors.Window;
                box.ForeColor = dark ? DarkText : SystemColors.WindowText;
                box.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                // Windows' dark theme for the log's scrollbar.
                if (box.Multiline)
                    try { SetWindowTheme(box.Handle, dark ? "DarkMode_Explorer" : null, null); } catch (Exception) { }
            }
            else if (control is ComboBox combo)
            {
                combo.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                combo.BackColor = dark ? DarkField : SystemColors.Window;
                combo.ForeColor = dark ? DarkText : SystemColors.WindowText;
            }
            else if (control is Button button)
            {
                button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                button.FlatAppearance.BorderColor = DarkBorder;
                button.BackColor = dark ? DarkButton : SystemColors.Control;
                button.ForeColor = dark ? DarkText : SystemColors.ControlText;
                button.UseVisualStyleBackColor = !dark;
            }
            else if (control == preview)
            {
                preview.BackColor = dark ? DarkField : Color.White;
            }
            else
            {
                control.BackColor = dark ? DarkBack : SystemColors.Control;
                control.ForeColor = dark ? DarkText : SystemColors.ControlText;
                if (control is CheckBox check && themedChecks.Add(check))
                {
                    check.Paint += PaintDisabledCheck;
                    check.EnabledChanged += (sender, args) => check.Invalidate();
                }
            }
            foreach (Control child in control.Controls)
                Theme(child, dark);
        }

        private readonly HashSet<CheckBox> themedChecks = new HashSet<CheckBox>();
        private Font tabFont;
        private static readonly Color DarkDisabledText = Color.FromArgb(128, 128, 128);

        // Windows draws a disabled checkbox's text in a fixed dark grey that is hard to see on the dark background, so in
        // dark mode its text is drawn again in a lighter grey.
        private void PaintDisabledCheck(object sender, PaintEventArgs args)
        {
            var check = (CheckBox)sender;
            if (check.Enabled || !darkMode.Checked)
                return;
            int glyph = CheckBoxRenderer.GetGlyphSize(args.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
            var area = new Rectangle(check.Padding.Left + glyph + 1, 0, Math.Max(0, check.Width - glyph - 1 - check.Padding.Left), check.Height);
            using (var background = new SolidBrush(check.BackColor))
                args.Graphics.FillRectangle(background, area);
            var text = new Rectangle(area.X + 3, 0, Math.Max(0, area.Width - 3), area.Height);
            TextRenderer.DrawText(args.Graphics, check.Text, check.Font, text, DarkDisabledText, check.BackColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            DarkTitleBar(darkMode.Checked);
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        // DWMWA_USE_IMMERSIVE_DARK_MODE (20, or 19 on Windows 10 before 20H1). Older Windows ignores it.
        private void DarkTitleBar(bool dark)
        {
            int value = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref value, sizeof(int));
            }
            catch (Exception) { }
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
            exportedAddon = null;
            SetBusy(true);
            log.Clear();
            try
            {
                bool avatar = IsAvatarLike;
                RobloxModelDownloader.BundleInfo bundle = IsBundle ? loadedBundle : null;
                if (IsBundle && bundle == null)
                    throw new InvalidDataException("The bundle's details have not loaded yet.");
                string selection = input.Text;
                ExportFormat format = Formats[exportFormat.SelectedIndex];
                string folder = Path.GetFullPath(outputFolder.Text);
                SaveSetting(FolderSetting, folder);
                var progress = new Progress<string>(AppendLog);
                string name = loadedName ?? selection;
                ShowPresence("Exporting " + name, "To " + FormatNames[exportFormat.SelectedIndex]);
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    string modelFile = IsFile ? Path.GetFullPath(selection.Trim()) : null;
                    long id = modelFile != null ? 0 : bundle?.Id ?? await downloader.ResolveIdAsync(selection, avatar, cancellation.Token);
                    string path = await downloader.ExportAsync(id, avatar, "", folder, progress, cancellation.Token, format,
                        avatar && faceTracking.Checked, vrchatShapes.Checked, singleTexture.Checked, IsPlace ? 1f : scale.Value / 100f, avatar && connectJoints.Checked, bundle, applyMood.Checked, ownFolder.Checked,
                        modelFile);
                    AppendLog("Export complete: " + path);
                    ShowPresence("Exported " + name, "To " + FormatNames[exportFormat.SelectedIndex]);
                    if (downloader.ExportFolder != null && format == ExportFormat.GarrysMod && Workshop.IsAddon(downloader.ExportFolder) ||
                        downloader.ExportFolder != null && format == ExportFormat.SourceFilmmaker && SfmWorkshop.IsExport(downloader.ExportFolder))
                    {
                        exportedAddon = downloader.ExportFolder;
                        exportedFormat = format;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ShowError(cancellation.IsCancellationRequested ? "Export cancelled." : "The API request timed out. Try again.");
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
                // A failed or cancelled export goes back to showing what is loaded.
                if (presenceDetails.StartsWith("Exporting ", StringComparison.Ordinal))
                    ShowPresence(loadedName != null ? "Viewing " + loadedName : "Browsing", null);
            }
        }

        // Places always export at full size: the slider shows 1 and is off while the Place type is selected, and gets back the
        // scale chosen before when another type is.
        private int scaleBeforePlace = -1;

        private void UpdateScale(bool busy)
        {
            if (IsPlace && scaleBeforePlace < 0)
            {
                scaleBeforePlace = scale.Value;
                scale.Value = scale.Maximum;
            }
            else if (!IsPlace && scaleBeforePlace >= 0)
            {
                scale.Value = scaleBeforePlace;
                scaleBeforePlace = -1;
            }
            scale.Enabled = !busy && !IsPlace;
        }

        private void SetBusy(bool busy)
        {
            foreach (Control control in new Control[] { exportType, exportFormat, input, openModel, outputFolder, browse, singleTexture, ownFolder, scale })
                control.Enabled = !busy;
            export.Enabled = !busy && loaded;
            UpdateFaceOptions(busy);
            UpdateScale(busy);
            UpdateUpload();
            cancel.Enabled = busy;
        }

        private async void Upload_Click(object sender, EventArgs args)
        {
            string folder = exportedAddon;
            if (folder == null)
                return;
            bool sfm = exportedFormat == ExportFormat.SourceFilmmaker;
            long? existing = sfm ? SfmWorkshop.PublishedId(folder) : Workshop.PublishedId(folder);
            string question = existing.HasValue
                ? "Update Workshop item " + existing.Value + " with this export of " + Path.GetFileName(folder) + "?"
                : "Upload " + Path.GetFileName(folder) + " to the Steam Workshop as a new item?\n\nIt starts hidden; you can make it public from its Workshop page.";
            if (MessageBox.Show(this, question + (sfm ? "\n\nIt goes to the Source Filmmaker Workshop. Steam must be running and signed in to an account with Source Filmmaker."
                : "\n\nSteam must be running and signed in to an account that owns Garry's Mod."), "Upload to Workshop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var progress = new Progress<string>(AppendLog);
                ShowPresence("Uploading " + (loadedName ?? Path.GetFileName(folder)), sfm ? "To the SFM Workshop" : "To the Steam Workshop");
                string title = "RoPorter - " + (loadedName ?? Path.GetFileName(folder));
                long id = sfm ? await Task.Run(() => SfmWorkshop.PublishAsync(folder, title, progress, cancellation.Token))
                    : await Task.Run(() => Workshop.PublishAsync(folder, progress, cancellation.Token));
                ShowPresence((existing.HasValue ? "Updated " : "Uploaded ") + (loadedName ?? Path.GetFileName(folder)), "On the Steam Workshop");
                string page = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
                AppendLog((existing.HasValue ? "Workshop item updated: " : "Uploaded to the Workshop: ") + page);
                if (MessageBox.Show(this, (existing.HasValue ? "The Workshop item was updated." : "The addon was uploaded.") + "\n\nOpen its Workshop page?",
                    "Upload to Workshop", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Open(page);
            }
            catch (OperationCanceledException)
            {
                ShowError("Upload cancelled.");
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

        // Face shape options apply to avatars outside Garry's Mod (Source models get no shape keys).
        // Face tracking and the VRChat shape keys are independent.
        private void UpdateFaceOptions(bool busy)
        {
            ExportFormat chosen = Formats[Math.Max(0, exportFormat.SelectedIndex)];
            bool avatar = IsAvatarLike && chosen != ExportFormat.GarrysMod && chosen != ExportFormat.SourceFilmmaker;
            vrchatShapes.Enabled = !busy && avatar;
            faceTracking.Enabled = !busy && avatar;
            // Joint blending applies to avatars and bundles in every format.
            connectJoints.Enabled = !busy && IsAvatarLike;
            applyMood.Enabled = !busy && IsAvatarLike;
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
