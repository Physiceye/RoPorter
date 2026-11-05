using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Rbx2Source.Export;

namespace Rbx2Source
{
    // RoPorter's window: the Export page (what to export, where, in which format, and its log) and the Settings page, with
    // Discord, GitHub and bug-report links along the bottom. Runs on Windows, Linux and macOS.
    public sealed class MainWindow : Window
    {
        public const string DefaultApiBase = "https://api.physiceye.com";
        // Source 2 (ExportFormat.Source2, icon "source2.png") is left out of the list for now.
        private static readonly ExportFormat[] Formats = { ExportFormat.Glb, ExportFormat.Gltf, ExportFormat.Fbx, ExportFormat.GarrysMod, ExportFormat.SourceFilmmaker,
            ExportFormat.TowerUnite, ExportFormat.Deadlock };
        private static readonly string[] FormatNames = { "GLB", "glTF", "FBX", "Garry's Mod", "Source Filmmaker", "Tower Unite", "Deadlock" };
        // Model files share one icon; each game has its Steam icon.
        private static readonly string[] FormatIcons = { "mesh_icon.png", "mesh_icon.png", "mesh_icon.png", "gmod.tga", "sfm.tga", "towerunite.tga", "deadlock.tga" };
        private const string FolderSetting = "folder", DarkModeSetting = "darkmode", CacheSetting = "cache", CloudKeySetting = "cloudkey", HeroSetting = "deadlockhero";
        private static readonly string[] SettingNames = { FolderSetting, DarkModeSetting, "singletexture", "vrchat", "facetracking", "connectjoints", "discord", "mood", "ownfolder",
            CacheSetting, CloudKeySetting, HeroSetting, "r6leftarm", "r6rightarm", "r6leftleg", "r6rightleg", "r6torso", "r6head" };
        private static readonly Dictionary<string, string> Saved = SettingsStore.Load(SettingNames, FolderSetting);

        // The buttons in the bottom right, from the repository (Data/links.json) so they can change without a new release:
        // { "1": { "text": "...", "url": "https://..." }, "2": ... }, in key order. The last copy downloaded is kept beside the
        // settings and shown until (or if) a new one arrives.
        private const string LinksUrl = "https://raw.githubusercontent.com/Physiceye/RoPorter/refs/heads/main/Data/links.json";
        private static readonly string LinksCache = Path.Combine(Export.ContentCache.DataFolder, "links.json");
        private readonly StackPanel links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        private const string CloudKeysPage = "https://create.roblox.com/dashboard/credentials?activeTab=ApiKeysTab";
        // The steps behind the "How to set up a key" button: the permissions RoPorter's direct requests use.
        private const string CloudKeyHowTo =
            "1. Click the \"Roblox Open Cloud\" button (create.roblox.com/dashboard/credentials?activeTab=ApiKeysTab).\n" +
            "2. Click \"Create API Key\".\n" +
            "3. Give the key a name.\n" +
            "4. In \"Access Permissions\" add \"legacy-asset\" and give it the operation \"manage\", then add \"thumbnail\" and give it \"read\".\n" +
            "5. Scroll to the top or bottom and click \"Save & Generate Key\".\n" +
            "6. Copy the key into the \"Your own API key\" box. Then you're done!";
        private const string ReleasesApi = "https://api.github.com/repos/Physiceye/RoPorter/releases/latest";

        // Export types: 0 Avatar (a user), 1 Catalog Item, 2 Bundle (exported like an avatar wearing it), 3 Model (a .rbxm or
        // .rbxmx file, exported like a catalog item), 4 Place (a .rbxl or .rbxlx file; a map in Garry's Mod).
        private const int ModelType = 3, PlaceType = 4;
        private static readonly string[] TypeNames = { "Avatar", "Catalog Item", "Bundle", "Model", "Place" };
        private static readonly string[] TypeIcons = { "Humanoid_icon", "Accoutrement_icon", "Player_icon", "Model_icon", "Workspace_icon" };

        private readonly ComboBox exportType = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        private readonly ComboBox exportFormat = new ComboBox { Width = 185 };
        private readonly Button r6PartsButton = new Button { Content = "R6 Configuration", IsVisible = false };
        // The Deadlock hero the avatar replaces, listed from the game the first time Deadlock is chosen.
        private readonly ComboBox deadlockHero = new ComboBox { Width = 170, IsVisible = false, PlaceholderText = "Hero to replace" };
        private List<DeadlockHero> heroes;
        private readonly TextBox input = new TextBox();
        private readonly TextBlock inputLabel = new TextBlock { Text = "User ID:", VerticalAlignment = VerticalAlignment.Center };
        private readonly Button openModel = new Button { Content = "Open...", IsVisible = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBox outputFolder = new TextBox { Text = LoadOutputFolder() };
        private readonly Button browse = new Button { Content = "Browse..." };
        private readonly Slider scale = new Slider { Minimum = 10, Maximum = 100, Value = 80, Width = 220, SmallChange = 1, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock scaleValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        // The scale slider's row, hidden for places: they always export at full size, where a Roblox character is a Garry's
        // Mod player's size.
        private readonly StackPanel scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        private readonly Button export = new Button { Content = "Export", IsEnabled = false };
        // Enabled once a Garry's Mod or SFM export of the loaded avatar or item has finished; uploads that folder.
        private readonly Button upload = new Button { Content = "Upload to Workshop", IsEnabled = false };
        private readonly Button cancel = new Button { Content = "Cancel", IsEnabled = false };
        private readonly Button openFolder = new Button { Content = "Open export folder" };
        private readonly TextBox log = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Stretch,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12
        };
        private readonly Image preview = new Image { Width = 128, Height = 128, Stretch = Stretch.Uniform };
        private readonly Border previewFrame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly Bitmap brokenPreview = LoadBitmap("BrokenPreview.png");
        private readonly Bitmap partIcon = LoadBitmap("part_icon.png");
        private readonly Bitmap[] typeBitmaps = TypeIcons.Select(name => LoadBitmap(name + ".png")).ToArray();
        private readonly Bitmap[] formatBitmaps = FormatIcons.Select(LoadBitmap).ToArray();

        // Settings page.
        private readonly CheckBox darkMode = new CheckBox { Content = "Dark mode" };
        private readonly CheckBox discordPresence = new CheckBox { Content = "Discord Rich Presence", IsChecked = true };
        private readonly CheckBox singleTexture = new CheckBox { Content = "Single texture" };
        private readonly CheckBox vrchatShapes = new CheckBox { Content = "Add VRChat shape keys" };
        private readonly CheckBox faceTracking = new CheckBox { Content = "Add FT support" };
        private readonly CheckBox connectJoints = new CheckBox { Content = "Connect body joints" };
        // The avatar's Mood: its resting expression (on by default, as in Roblox).
        private readonly CheckBox applyMood = new CheckBox { Content = "Apply mood expression", IsChecked = true };
        // Each export in its own folder (on by default); off collects many exports in the output folder itself.
        private readonly CheckBox ownFolder = new CheckBox { Content = "Export into its own folder", IsChecked = true };
        private readonly CheckBox cacheContent = new CheckBox { Content = "Keep downloaded files", IsChecked = true };
        private readonly TextBlock cacheSize = new TextBlock();
        private readonly TextBox cloudKey = new TextBox { PasswordChar = '•', Width = 340, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox showCloudKey = new CheckBox { Content = "Show key" };

        private readonly Button exportTab = new Button(), settingsTab = new Button(), libraryTab = new Button();
        // The Library page: every export in the output folder, as cards that open its folder.
        private readonly WrapPanel libraryCards = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox libraryFilter = new ComboBox { Width = 160 };
        private readonly TextBlock libraryInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private static readonly string[] LibraryFilters = { "All", "Avatars", "Catalog items", "Bundles", "Models", "Places" };
        private Control libraryPage;
        private readonly Control exportPage, settingsPage;

        private readonly DiscordPresence discord = new DiscordPresence();
        private string presenceDetails = "Browsing", presenceState;
        // The loaded avatar's username or item's name (as in the window title).
        private string loadedName;
        private bool loaded;
        private string exportedAddon;
        // The format of that export: Garry's Mod (an addon) or Source Filmmaker (SFM's Workshop).
        private ExportFormat exportedFormat;
        private CancellationTokenSource cancellation, previewCancellation;
        // The loaded bundle's details (name, outfit, items), fetched with its preview.
        private RobloxModelDownloader.BundleInfo loadedBundle;
        // What was entered for each kind of input (an ID, a model file, a place file), so switching type and back restores it.
        private readonly Dictionary<int, string> inputs = new Dictionary<int, string>();
        private int shownType;
        // Waits for typing to pause before looking up the preview.
        private readonly DispatcherTimer previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        // The last message from the API that was logged, so looking up several IDs in a row doesn't repeat it.
        private string lastApiMessage;

        private bool IsBundle => exportType.SelectedIndex == 2;
        private bool IsModel => exportType.SelectedIndex == ModelType;
        private bool IsPlace => exportType.SelectedIndex == PlaceType;
        private bool IsFile => IsModel || IsPlace;
        private bool IsAvatarLike => exportType.SelectedIndex == 0 || exportType.SelectedIndex == 2;
        private bool IsDeadlock => Formats[Math.Max(0, exportFormat.SelectedIndex)] == ExportFormat.Deadlock;
        // Deadlock replaces a hero, so it takes avatars and bundles only.
        private bool TypeAllowed(int type) => !IsDeadlock || type == 0 || type == 2;
        private static int InputSlot(int type) => type == ModelType || type == PlaceType ? type : 0;

        public MainWindow()
        {
            Title = "RoPorter";
            using (Stream icon = Resource("favicon.ico"))
                if (icon != null)
                    Icon = new WindowIcon(icon);
            MinWidth = 680;
            MinHeight = 540;
            Width = 900;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Each type's icon and name come from a template, so the closed box makes its own copy that follows the theme
            // (an item made of controls is shown there as a picture of the list's item, which kept light mode's black text).
            exportType.ItemTemplate = new FuncDataTemplate<int>((i, scope) => IconText(typeBitmaps[i], TypeNames[i]));
            exportType.SelectionBoxItemTemplate = exportType.ItemTemplate;
            exportType.ItemsSource = Enumerable.Range(0, TypeNames.Length).ToList();
            exportType.ContainerPrepared += (sender, args) => args.Container.IsEnabled = TypeAllowed(args.Index);
            exportType.SelectedIndex = 0;
            exportFormat.ItemTemplate = new FuncDataTemplate<int>((i, scope) => IconText(formatBitmaps[i], FormatNames[i]));
            exportFormat.SelectionBoxItemTemplate = exportFormat.ItemTemplate;
            exportFormat.ItemsSource = Enumerable.Range(0, FormatNames.Length).ToList();
            exportFormat.SelectedIndex = 0;
            SetTab(exportTab, "Configuration_icon.png", "Export");
            SetTab(settingsTab, "Starterpack_icon.png", "Settings");
            SetTab(libraryTab, "Workspace_icon.png", "Library");
            preview.Source = brokenPreview;
            previewFrame.Child = preview;
            scaleRow.Children.Add(new TextBlock { Text = "Scale:", VerticalAlignment = VerticalAlignment.Center });
            scaleRow.Children.Add(scale);
            scaleRow.Children.Add(scaleValue);

            exportPage = BuildExportPage();
            settingsPage = BuildSettingsPage();
            settingsPage.IsVisible = false;
            libraryPage = BuildLibraryPage();
            libraryPage.IsVisible = false;

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 8, 12, 0) };
            tabs.Children.Add(exportTab);
            tabs.Children.Add(settingsTab);
            tabs.Children.Add(libraryTab);
            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 12, 10) };
            bottom.Children.Add(new TextBlock { Text = "v" + typeof(MainWindow).Assembly.GetName().Version.ToString(3), VerticalAlignment = VerticalAlignment.Center });
            ShowLinks(ReadCachedLinks());
            Grid.SetColumn(links, 1);
            bottom.Children.Add(links);
            var root = new DockPanel();
            DockPanel.SetDock(tabs, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(tabs);
            root.Children.Add(bottom);
            root.Children.Add(new Panel { Children = { exportPage, settingsPage, libraryPage } });
            Content = root;

            exportTab.Click += (sender, args) => ShowPage(ExportPageIndex);
            settingsTab.Click += async (sender, args) =>
            {
                ShowPage(SettingsPageIndex);
                await UpdateCacheSizeAsync();
            };
            libraryTab.Click += async (sender, args) =>
            {
                ShowPage(LibraryPageIndex);
                await RefreshLibraryAsync();
            };
            WireExportPage();
            BindSetting(darkMode, DarkModeSetting);
            darkMode.IsCheckedChanged += (sender, args) => ApplyTheme();
            ApplyTheme();
            ShowPage(ExportPageIndex);

            Opened += async (sender, args) =>
            {
                ShowPresence("Browsing", null);
                await Task.WhenAll(UpdateLinksAsync(), CheckForUpdatesAsync());
            };
            Closing += (sender, args) =>
            {
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    args.Cancel = true;
                    AppendLog("Cancelling export. Close the window again after cancellation finishes.");
                }
            };
            Closed += (sender, args) =>
            {
                discord.Dispose();
                previewTimer.Stop();
                previewCancellation?.Cancel();
            };
        }

        private Grid BuildExportPage()
        {
            var layout = new Grid
            {
                Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 6, ColumnSpacing = 8
            };
            void Place(Control control, int row, int column, int columnSpan = 1)
            {
                Grid.SetRow(control, row);
                Grid.SetColumn(control, column);
                Grid.SetColumnSpan(control, columnSpan);
                layout.Children.Add(control);
            }
            Place(new TextBlock { Text = "Export:", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            Place(exportType, 0, 1);
            Place(inputLabel, 1, 0);
            var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            inputRow.Children.Add(input);
            Grid.SetColumn(openModel, 1);
            inputRow.Children.Add(openModel);
            Place(inputRow, 1, 1);
            Place(new TextBlock { Text = "Output folder:", VerticalAlignment = VerticalAlignment.Center }, 2, 0);
            Place(outputFolder, 2, 1);
            Place(browse, 3, 0);
            Place(scaleRow, 3, 1);
            Place(new TextBlock { Text = "Export format:", VerticalAlignment = VerticalAlignment.Bottom }, 4, 0);
            Grid.SetRowSpan(previewFrame, 5);
            Place(previewFrame, 0, 2);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (Control control in new Control[] { exportFormat, deadlockHero, export, upload, cancel, openFolder, r6PartsButton })
            {
                control.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(control);
            }
            Place(actions, 5, 0, 3);
            Place(log, 6, 0, 3);

            ToolTip.SetTip(openModel, "Choose a model (.rbxm, .rbxmx) or place (.rbxl, .rbxlx) saved from Roblox Studio. You can also drop one on the window.");
            ToolTip.SetTip(scale, "Scales the exported model down. 1 is full size, 0.8 is the default and 0.1 is the smallest.");
            ToolTip.SetTip(upload, "Packs the exported Garry's Mod addon and uploads it to the Steam Workshop. Steam must be running.");
            return layout;
        }

        private Control BuildSettingsPage()
        {
            var page = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            TextBlock Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 2) };
            page.Children.Add(darkMode);
            page.Children.Add(discordPresence);
            BindSetting(discordPresence, "discord");
            discordPresence.IsEnabled = DiscordPresence.Configured;
            ToolTip.SetTip(discordPresence, DiscordPresence.Configured
                ? "Shows what you are exporting on your Discord profile while RoPorter is open."
                : "This build has no Discord application ID set, so Rich Presence is unavailable.");
            discordPresence.IsCheckedChanged += (sender, args) =>
            {
                if (discordPresence.IsChecked == true)
                    ShowPresence(presenceDetails, presenceState);
                else
                    _ = discord.ClearAsync();
            };

            page.Children.Add(Heading("Export options"));
            foreach (CheckBox box in new[] { singleTexture, vrchatShapes, faceTracking, connectJoints, applyMood, ownFolder })
                page.Children.Add(box);
            BindSetting(singleTexture, "singletexture");
            BindSetting(vrchatShapes, "vrchat");
            BindSetting(faceTracking, "facetracking");
            BindSetting(connectJoints, "connectjoints");
            BindSetting(applyMood, "mood");
            BindSetting(ownFolder, "ownfolder");
            ToolTip.SetTip(singleTexture, "Merges every material into one material and one texture.");
            ToolTip.SetTip(vrchatShapes, "Adds VRChat visemes.");
            ToolTip.SetTip(faceTracking, "Adds Unified Expressions face shapes for VRCFaceTracking.");
            ToolTip.SetTip(connectJoints, "Blends the body's weights across its joints so the surface stretches when they bend, instead of each " +
                "part moving on its own. For UGC bundles modelled as one connected body; R6 avatars stay blocky. " +
                "Deadlock always puts the arms and legs on the hero's joints so its animations work; this option only chooses " +
                "whether the surface stretches across the joints or the parts stay separate blocks.");
            ToolTip.SetTip(applyMood, "Gives the face the avatar's equipped Mood (its resting expression, e.g. a smile). " +
                "Off, the face is neutral; GLB, glTF and FBX keep the mood as a \"Default\" shape key you can turn on.");
            ToolTip.SetTip(ownFolder, "Each export gets a folder named after the avatar or item. Off, exports go straight into the output folder, " +
                "so many can be collected in one.\nGarry's Mod: they become one addon. If the output folder is Garry's Mod's addons folder, that addon is addons/RoPorter.");

            page.Children.Add(Heading("Content cache"));
            page.Children.Add(cacheContent);
            BindSetting(cacheContent, CacheSetting);
            ContentCache.Enabled = cacheContent.IsChecked == true;
            cacheContent.IsCheckedChanged += (sender, args) => ContentCache.Enabled = cacheContent.IsChecked == true;
            page.Children.Add(cacheSize);
            var openCache = new Button { Content = "Open cache folder" };
            var clearCache = new Button { Content = "Clear cache" };
            page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { openCache, clearCache } });
            ToolTip.SetTip(cacheContent, "Keeps downloaded meshes, textures and models in the rbx_cache folder next to RoPorter, so exporting them " +
                "again does not download them again.\nAvatars, thumbnails and item lists are always downloaded fresh. Clear the cache to get items whose creators have updated them.");
            openCache.Click += async (sender, args) =>
            {
                try
                {
                    Directory.CreateDirectory(ContentCache.Folder);
                    await OpenAsync(ContentCache.Folder);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    await ShowErrorAsync("The cache folder could not be created: " + error.Message);
                }
            };
            clearCache.Click += async (sender, args) =>
            {
                clearCache.IsEnabled = false;
                bool all = await Task.Run(() => ContentCache.Clear());
                clearCache.IsEnabled = true;
                if (!all)
                    await ShowErrorAsync("Some cached files are in use by an export and were kept.");
                await UpdateCacheSizeAsync();
            };

            page.Children.Add(Heading("Roblox Open Cloud"));
            page.Children.Add(new TextBlock { Text = "Your own API key (optional):" });
            page.Children.Add(cloudKey);
            var openCloudKeys = new Button { Content = "Roblox Open Cloud" };
            var keyHowTo = new Button { Content = "How to set up a key" };
            page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { showCloudKey, openCloudKeys, keyHowTo } });
            cloudKey.Text = Saved.TryGetValue(CloudKeySetting, out string key) ? key : "";
            RobloxModelDownloader.CloudKey = cloudKey.Text;
            cloudKey.TextChanged += (sender, args) =>
            {
                RobloxModelDownloader.CloudKey = cloudKey.Text;
                SaveSetting(CloudKeySetting, (cloudKey.Text ?? "").Trim());
            };
            showCloudKey.IsCheckedChanged += (sender, args) => cloudKey.PasswordChar = showCloudKey.IsChecked == true ? '\0' : '•';
            openCloudKeys.Click += async (sender, args) => await OpenAsync(CloudKeysPage);
            keyHowTo.Click += async (sender, args) => await Dialogs.Show(this, CloudKeyHowTo, "Setting up a Roblox API key");
            ToolTip.SetTip(cloudKey, "An API key you create on the Creator Dashboard (create.roblox.com > Open Cloud > API Keys).\n" +
                "With a key, RoPorter downloads from Roblox directly, so exports keep working even when RoPorter's server is down. " +
                "Anything Roblox refuses the key for still goes through the server.\n" +
                "Leave it empty to download through RoPorter's server.");
            ToolTip.SetTip(openCloudKeys, "Opens the API Keys page of Roblox's Creator Dashboard in your browser, where you can create a key.");
            ToolTip.SetTip(keyHowTo, "Shows which permissions the key needs and how to create it.");
            return new ScrollViewer { Content = page };
        }

        private void WireExportPage()
        {
            exportType.SelectionChanged += (sender, args) =>
            {
                inputLabel.Text = exportType.SelectedIndex == 0 ? "User ID:" : IsBundle ? "Bundle ID:" : IsModel ? "Model file:" : IsPlace ? "Place file:" : "Item ID:";
                // The ID box holds the file's path for the Model and Place types.
                if (InputSlot(shownType) != InputSlot(exportType.SelectedIndex))
                {
                    inputs[InputSlot(shownType)] = input.Text;
                    input.Text = inputs.TryGetValue(InputSlot(exportType.SelectedIndex), out string saved) ? saved : "";
                }
                shownType = exportType.SelectedIndex;
                openModel.IsVisible = IsFile;
                UpdateFaceOptions(false);
                UpdateScale(false);
                SchedulePreview();
            };
            // IDs only: typed non-digits are ignored, and pasted text keeps just its digits. A model file's path is any text.
            input.AddHandler(TextInputEvent, (sender, args) =>
            {
                if (!IsFile && args.Text != null && args.Text.Any(c => !char.IsDigit(c)))
                    args.Handled = true;
            }, RoutingStrategies.Tunnel);
            input.TextChanged += (sender, args) =>
            {
                string text = input.Text ?? "";
                if (!IsFile)
                {
                    string digits = new string(text.Where(char.IsDigit).Take(19).ToArray());
                    if (digits != text)
                    {
                        input.Text = digits;
                        input.CaretIndex = digits.Length;
                        return;
                    }
                }
                SchedulePreview();
            };
            openModel.Click += async (sender, args) => await OpenModelAsync();
            // A model or place file dropped anywhere on the window switches to its type and loads it.
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, (sender, args) =>
                args.DragEffects = DroppedFile(args) != null && cancellation == null ? DragDropEffects.Copy : DragDropEffects.None);
            AddHandler(DragDrop.DropEvent, (sender, args) =>
            {
                string file = DroppedFile(args);
                if (file == null || cancellation != null)
                    return;
                exportType.SelectedIndex = IsPlaceFile(file) ? PlaceType : ModelType;
                input.Text = file;
            });
            scale.PropertyChanged += (sender, args) =>
            {
                if (args.Property == RangeBase.ValueProperty)
                    scaleValue.Text = (Math.Round(scale.Value) / 100.0).ToString("0.00", CultureInfo.CurrentCulture);
            };
            scaleValue.Text = (scale.Value / 100.0).ToString("0.00", CultureInfo.CurrentCulture);
            exportFormat.SelectionChanged += async (sender, args) =>
            {
                UpdateFaceOptions(false);
                UpdateUpload();
                deadlockHero.IsVisible = IsDeadlock;
                // Deadlock mods don't go on the Workshop; the hero list takes the button's place in the row.
                upload.IsVisible = !deadlockHero.IsVisible;
                UpdateScale(false);
                for (int type = 0; type < TypeNames.Length; type++)
                    if (exportType.ContainerFromIndex(type) is Control item)
                        item.IsEnabled = TypeAllowed(type);
                if (IsDeadlock && !IsAvatarLike)
                    exportType.SelectedIndex = 0;
                if (deadlockHero.IsVisible)
                    await LoadHeroesAsync();
            };
            deadlockHero.SelectionChanged += (sender, args) =>
            {
                if (deadlockHero.SelectedIndex >= 0)
                    SaveSetting(HeroSetting, heroes[deadlockHero.SelectedIndex].Key);
            };
            previewTimer.Tick += async (sender, args) =>
            {
                previewTimer.Stop();
                await UpdatePreviewAsync();
            };
            r6PartsButton.Click += async (sender, args) => await ShowR6PartsAsync();
            ToolTip.SetTip(r6PartsButton, "This avatar is R6: choose which bone each body part moves with.");
            browse.Click += async (sender, args) => await BrowseAsync();
            export.Click += async (sender, args) => await ExportAsync();
            upload.Click += async (sender, args) => await UploadAsync();
            cancel.Click += (sender, args) => cancellation?.Cancel();
            openFolder.Click += async (sender, args) =>
            {
                if (Directory.Exists(outputFolder.Text))
                    await OpenAsync(outputFolder.Text);
                else
                    await ShowErrorAsync("The export folder does not exist yet.");
            };
            UpdateFaceOptions(false);
        }

        private static Control IconText(Bitmap icon, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (icon != null)
                row.Children.Add(new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        private static void SetTab(Button tab, string icon, string text)
        {
            tab.Content = IconText(LoadBitmap(icon), text);
            tab.Background = Brushes.Transparent;
            tab.BorderThickness = new Thickness(0);
        }

        private Button LinkButton(string text, string target, string tip)
        {
            var button = new Button { Content = text };
            ToolTip.SetTip(button, tip);
            button.Click += async (sender, args) => await OpenAsync(target);
            return button;
        }

        // The buttons from links.json: at most eight, each with some text and a web address (anything else is skipped).
        private static List<KeyValuePair<string, string>> ParseLinks(string json)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(json))
                return result;
            try
            {
                var entries = Newtonsoft.Json.Linq.JObject.Parse(json).Properties()
                    .OrderBy(p => int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue)
                    .ThenBy(p => p.Name, StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!(entry.Value is Newtonsoft.Json.Linq.JObject link))
                        continue;
                    string text = link.Value<string>("text")?.Trim(), url = link.Value<string>("url")?.Trim();
                    if (string.IsNullOrEmpty(text) || text.Length > 40 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        continue;
                    result.Add(new KeyValuePair<string, string>(text, uri.AbsoluteUri));
                    if (result.Count == 8)
                        break;
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
            return result;
        }

        private static List<KeyValuePair<string, string>> ReadCachedLinks()
        {
            try
            {
                return File.Exists(LinksCache) ? ParseLinks(File.ReadAllText(LinksCache)) : new List<KeyValuePair<string, string>>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return new List<KeyValuePair<string, string>>();
            }
        }

        private void ShowLinks(List<KeyValuePair<string, string>> entries)
        {
            links.Children.Clear();
            foreach (var entry in entries)
                links.Children.Add(LinkButton(entry.Key, entry.Value, "Opens " + entry.Value));
        }

        // Downloads the current links.json; the buttons change only if it holds at least one usable link, and it is kept for
        // the next start.
        private async Task UpdateLinksAsync()
        {
            try
            {
                string json;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("RoPorter/" + RobloxModelDownloader.AppVersion);
                    json = await client.GetStringAsync(LinksUrl);
                }
                var entries = ParseLinks(json);
                if (entries.Count == 0)
                    return;
                ShowLinks(entries);
                try
                {
                    File.WriteAllText(LinksCache, json);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                // Offline or GitHub unreachable: the buttons from the last start stay.
            }
        }

        private const int ExportPageIndex = 0, SettingsPageIndex = 1, LibraryPageIndex = 2;

        private void ShowPage(int page)
        {
            var pages = new[] { (exportPage, exportTab), (settingsPage, settingsTab), (libraryPage, libraryTab) };
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Item1.IsVisible = i == page;
                pages[i].Item2.FontWeight = i == page ? FontWeight.Bold : FontWeight.Normal;
                pages[i].Item2.Background = i == page ? null : Brushes.Transparent;
            }
        }

        // The Library page: a filter, the folder being shown, and the export cards.
        private Control BuildLibraryPage()
        {
            foreach (string filter in LibraryFilters)
                libraryFilter.Items.Add(new ComboBoxItem { Content = filter });
            libraryFilter.SelectedIndex = 0;
            libraryFilter.SelectionChanged += async (sender, args) => await RefreshLibraryAsync();
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (sender, args) => await RefreshLibraryAsync();
            var open = new Button { Content = "Open folder" };
            open.Click += async (sender, args) =>
            {
                if (Directory.Exists(outputFolder.Text))
                    await OpenAsync(outputFolder.Text);
            };
            ToolTip.SetTip(refresh, "Looks through the output folder again.");
            ToolTip.SetTip(open, "Opens the output folder.");
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 12, 12, 6) };
            bar.Children.Add(new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(libraryFilter);
            bar.Children.Add(refresh);
            bar.Children.Add(open);
            bar.Children.Add(libraryInfo);
            var page = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            page.Children.Add(bar);
            page.Children.Add(new ScrollViewer { Content = libraryCards, Margin = new Thickness(6, 0, 6, 6) });
            return page;
        }

        // Lists the exports in the output folder (read off the UI thread; a folder of Garry's Mod addons can be large).
        private async Task RefreshLibraryAsync()
        {
            string folder = outputFolder.Text ?? "";
            string type = new[] { null, "Avatar", "Catalog Item", "Bundle", "Model", "Place" }[Math.Max(0, libraryFilter.SelectedIndex)];
            List<ExportLibrary.Entry> entries = await Task.Run(() => ExportLibrary.List(folder));
            var shown = entries.Where(e => type == null || e.Type == type).ToList();
            libraryCards.Children.Clear();
            libraryInfo.Text = !Directory.Exists(folder) ? "The output folder doesn't exist yet." : shown.Count == 0 ? "Nothing exported here yet."
                : shown.Count + " export" + (shown.Count == 1 ? "" : "s") + " in " + folder;
            foreach (ExportLibrary.Entry entry in shown)
                libraryCards.Children.Add(LibraryCard(entry));
        }

        private Control LibraryCard(ExportLibrary.Entry entry)
        {
            Bitmap picture = null;
            try
            {
                if (entry.Picture != null && File.Exists(entry.Picture))
                    picture = ToBitmap(File.ReadAllBytes(entry.Picture));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
            }
            int typeIcon = Array.IndexOf(TypeNames, entry.Type);
            var content = new StackPanel { Spacing = 4, Width = 150 };
            content.Children.Add(new Border
            {
                Width = 150, Height = 150, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), CornerRadius = new CornerRadius(4),
                Child = new Image
                {
                    Source = picture ?? (typeIcon >= 0 ? typeBitmaps[typeIcon] : partIcon),
                    Stretch = Stretch.Uniform, Width = picture != null ? 150 : 48, Height = picture != null ? 150 : 48
                }
            });
            content.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = entry.Type + " · " + entry.Format, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = entry.Date.ToString("g", CultureInfo.CurrentCulture), FontSize = 12, Opacity = 0.7 });
            var card = new Button { Content = content, Margin = new Thickness(6), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
            ToolTip.SetTip(card, "Opens " + entry.Folder + "\n" + entry.File);
            card.Click += async (sender, args) => await OpenAsync(entry.Folder);
            return card;
        }

        private void ApplyTheme()
        {
            bool dark = darkMode.IsChecked == true;
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            previewFrame.Background = dark ? new SolidColorBrush(Color.FromRgb(45, 45, 45)) : Brushes.White;
            previewFrame.BorderBrush = dark ? new SolidColorBrush(Color.FromRgb(85, 85, 85)) : Brushes.Gray;
        }

        private static Stream Resource(string name) => typeof(MainWindow).Assembly.GetManifestResourceStream("RoPorter." + name);

        private static Bitmap LoadBitmap(string name)
        {
            using (Stream stream = Resource(name))
            {
                if (stream == null)
                    return null;
                if (!name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    return new Bitmap(stream);
                using (var data = new MemoryStream())
                {
                    stream.CopyTo(data);
                    byte[] png = TgaToPng(data.ToArray());
                    return png != null ? ToBitmap(png) : null;
                }
            }
        }

        // An uncompressed 24- or 32-bit TGA (the Steam game icons) as a PNG, which Avalonia can load; null for anything else.
        private static byte[] TgaToPng(byte[] tga)
        {
            if (tga.Length < 18 || tga[2] != 2 || (tga[16] != 24 && tga[16] != 32))
                return null;
            int width = tga[12] | tga[13] << 8, height = tga[14] | tga[15] << 8, bytes = tga[16] / 8;
            int start = 18 + tga[0];
            bool topDown = (tga[17] & 0x20) != 0;
            if (width == 0 || height == 0 || start + width * height * bytes > tga.Length)
                return null;
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = start + ((topDown ? y : height - 1 - y) * width + x) * bytes;
                    int alpha = bytes == 4 ? tga[at + 3] : 255;
                    pixels[y * width + x] = alpha << 24 | tga[at + 2] << 16 | tga[at + 1] << 8 | tga[at];
                }
            using (var picture = new Export.Picture(width, height))
            {
                picture.SetPixels(pixels);
                return picture.Encode(Export.PictureFormat.Png);
            }
        }

        private static Bitmap ToBitmap(byte[] image)
        {
            using (var stream = new MemoryStream(image))
                return new Bitmap(stream);
        }

        private static bool IsModelFile(string path) =>
            path != null && (path.EndsWith(".rbxm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rbxmx", StringComparison.OrdinalIgnoreCase));

        private static bool IsPlaceFile(string path) =>
            path != null && (path.EndsWith(".rbxl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rbxlx", StringComparison.OrdinalIgnoreCase));

        // The one model or place file being dragged onto the window, if that is what it is.
        private static string DroppedFile(DragEventArgs args)
        {
            var files = args.DataTransfer.TryGetFiles()?.ToList();
            string path = files != null && files.Count == 1 ? files[0].TryGetLocalPath() : null;
            return path != null && (IsModelFile(path) || IsPlaceFile(path)) && File.Exists(path) ? path : null;
        }

        private async Task OpenModelAsync()
        {
            bool place = IsPlace;
            var options = new FilePickerOpenOptions
            {
                Title = place ? "Choose a Roblox place file" : "Choose a Roblox model file", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(place ? "Roblox places" : "Roblox models") { Patterns = place ? new[] { "*.rbxl", "*.rbxlx" } : new[] { "*.rbxm", "*.rbxmx" } } }
            };
            string current = (input.Text ?? "").Trim();
            try
            {
                if (current.Length > 0 && Directory.Exists(Path.GetDirectoryName(current)))
                    options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(current));
            }
            catch (ArgumentException)
            {
            }
            var files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
                input.Text = path;
        }

        private async Task BrowseAsync()
        {
            var options = new FolderPickerOpenOptions { Title = "Choose the export folder", AllowMultiple = false };
            if (Directory.Exists(outputFolder.Text))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(outputFolder.Text);
            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is string path)
            {
                outputFolder.Text = path;
                SaveSetting(FolderSetting, path);
            }
        }

        // A model or place file's preview: a picture of its parts, with its part count in the tooltip. The file is read to
        // check it is a Roblox model with parts (or a place with parts in its Workspace) before Export is enabled.
        private async Task PreviewModelAsync(string file, CancellationTokenSource current)
        {
            bool place = IsPlace;
            if (!(place ? IsPlaceFile(file) : IsModelFile(file)) || !File.Exists(file))
            {
                ShowPreview(null, place ? "Choose a .rbxl or .rbxlx file." : "Choose a .rbxm or .rbxmx file.");
                return;
            }
            int parts;
            byte[] picture;
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
                picture = parts > 0 ? await Task.Run(() =>
                {
                    using (Picture render = PlaceRender.Preview(contents, 256))
                        return render?.Encode(PictureFormat.Png);
                }) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                error is NotSupportedException || error is ArgumentException || error is IndexOutOfRangeException)
            {
                if (current == previewCancellation)
                    ShowPreview(null, "Not a readable Roblox " + (place ? "place: " : "model: ") + error.Message);
                return;
            }
            if (current != previewCancellation)
                return;
            if (parts == 0)
            {
                ShowPreview(null, place ? "The place's Workspace has no parts to export." : "The model file has no parts to export.");
                return;
            }
            string name = Path.GetFileNameWithoutExtension(file);
            string description = name + ": " + parts + (parts == 1 ? " part" : " parts");
            // Nothing visible to draw: the type's icon instead.
            ShowPreview(picture != null ? ToBitmap(picture) : typeBitmaps[place ? PlaceType : ModelType], description, picture);
            SetLoaded(true);
            Title = "RoPorter - " + name;
            loadedName = name;
            ShowPresence("Viewing " + name, place ? "Place file" : "Model file");
            if (!place)
                await PreviewMeshesAsync(file, description, current);
        }

        // A model file's real look: its meshes and textures downloaded (kept in the content cache, so exporting it then needs
        // no downloads) and drawn in place of the boxes. A failure keeps the boxes.
        private async Task PreviewMeshesAsync(string file, string description, CancellationTokenSource current)
        {
            try
            {
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    CancellationToken token = current.Token;
                    byte[] picture = await Task.Run(async () =>
                    {
                        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        ModelContents contents = ModelFileBuilder.Read(File.ReadAllBytes(file));
                        WavefrontModel model = await ModelFileBuilder.BuildAsync(contents, (id, cancel) => downloader.DownloadAssetAsync(id, cancel), textures, null, token);
                        using (Picture render = PlaceRender.Render(model, textures, 256))
                            return render?.Encode(PictureFormat.Png);
                    }, token);
                    if (picture != null && current == previewCancellation)
                        ShowPreview(ToBitmap(picture), description, picture);
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // The boxes stay.
            }
        }

        private void SetLoaded(bool value)
        {
            loaded = value;
            export.IsEnabled = loaded && cancellation == null;
            // A different avatar or item no longer matches the exported addon.
            if (!value)
                exportedAddon = null;
            UpdateUpload();
        }

        private void UpdateUpload() =>
            upload.IsEnabled = cancellation == null && exportedAddon != null && Formats[Math.Max(0, exportFormat.SelectedIndex)] == exportedFormat;

        private void SchedulePreview()
        {
            SetLoaded(false);
            previewCancellation?.Cancel();
            previewTimer.Stop();
            previewTimer.Start();
        }

        // bytes: the picture's file (PNG or JPEG), kept for the Library when the item is exported.
        private void ShowPreview(Bitmap image, string description, byte[] bytes = null)
        {
            preview.Source = image ?? brokenPreview;
            previewBytes = image != null ? bytes : null;
            ToolTip.SetTip(previewFrame, description);
        }

        private byte[] previewBytes;

        // Shows the avatar's headshot or the item's thumbnail; anything that can't be loaded shows the broken preview.
        private async Task UpdatePreviewAsync()
        {
            previewCancellation?.Cancel();
            var current = previewCancellation = new CancellationTokenSource();
            string text = (input.Text ?? "").Trim();
            bool avatar = exportType.SelectedIndex == 0, bundle = IsBundle;
            r6PartsButton.IsVisible = false;
            Title = "RoPorter";
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
                        long bundleId = long.Parse(text, CultureInfo.InvariantCulture);
                        RobloxModelDownloader.BundleInfo details = await downloader.GetBundleAsync(bundleId, current.Token);
                        Bitmap picture = null;
                        byte[] pictureBytes = null;
                        string problem = null;
                        try
                        {
                            pictureBytes = await downloader.DownloadBundleThumbnailAsync(bundleId, current.Token);
                            picture = ToBitmap(pictureBytes);
                        }
                        catch (Exception error) when (!(error is OperationCanceledException) && !(error is ApiMessageException))
                        {
                            problem = error.Message;
                        }
                        if (current != previewCancellation)
                            return;
                        ShowPreview(picture, picture != null ? "Bundle " + bundleId : "No picture: " + problem, pictureBytes);
                        loadedBundle = details;
                        SetLoaded(true);
                        Title = "RoPorter - " + details.Name;
                        loadedName = details.Name;
                        ShowPresence("Viewing " + details.Name, "Bundle");
                        return;
                    }
                    long id = await downloader.ResolveIdAsync(text, avatar, current.Token);
                    byte[] bytes = await downloader.DownloadThumbnailAsync(id, avatar, current.Token);
                    if (current != previewCancellation)
                        return;
                    ShowPreview(ToBitmap(bytes), (avatar ? "User " : "Item ") + id, bytes);
                    SetLoaded(true);
                    await ShowTitleAsync(downloader, id, avatar, current);
                    if (avatar)
                    {
                        string rig = await downloader.GetAvatarTypeAsync(id, current.Token);
                        if (current == previewCancellation)
                            r6PartsButton.IsVisible = rig == "R6";
                    }
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

        // A loaded avatar or catalog item names the window: "RoPorter - <username>" or "RoPorter - <item name>", or its ID
        // when the name can't be looked up.
        private async Task ShowTitleAsync(RobloxModelDownloader downloader, long id, bool avatar, CancellationTokenSource current)
        {
            string title = id.ToString(CultureInfo.InvariantCulture);
            try
            {
                title = avatar ? await downloader.GetUsernameAsync(id, current.Token) : await downloader.GetCatalogDisplayNameAsync(id, current.Token);
            }
            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is Newtonsoft.Json.JsonException)
            {
            }
            if (current == previewCancellation)
            {
                Title = "RoPorter - " + title;
                loadedName = title;
                ShowPresence("Viewing " + title, avatar ? "Avatar" : "Catalog item");
            }
        }

        // Compares the latest GitHub release's tag (e.g. "v1.0.1") with this build's version and offers the release page when
        // it is newer. A failed check is only logged.
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
                Version current = Normalize(typeof(MainWindow).Assembly.GetName().Version);
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version latest) || Normalize(latest) <= current)
                    return;
                AppendLog("RoPorter " + tag + " is available (this is v" + current.ToString(3) + ").");
                if (Uri.TryCreate(release.Value<string>("html_url"), UriKind.Absolute, out Uri page) && page.Host == "github.com" &&
                    await Dialogs.Ask(this, "RoPorter " + tag + " is available. You have v" + current.ToString(3) + ".\n\nOpen the download page?", "RoPorter update",
                        details: ReleaseNotes(release.Value<string>("body"))))
                    await OpenAsync(page.AbsoluteUri);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException || error is Newtonsoft.Json.JsonException)
            {
                AppendLog("Could not check for updates: " + error.Message);
            }
        }

        // A GitHub release's Markdown as plain text: no images or HTML tags, links as their text, no bold or italic marks.
        private static string ReleaseNotes(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            string text = body.Replace("\r\n", "\n");
            text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"<[^>\n]+>", "");
            text = Regex.Replace(text, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
            text = Regex.Replace(text, @"(?<![\w*])[*_]([^\s*_](?:[^\n]*?[^\s])?)[*_](?![\w*])", "$1");
            text = Regex.Replace(text, @"^#+\s*", "", RegexOptions.Multiline);
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }

        // Missing version parts count as zero, so "1.0.0" equals assembly version 1.0.0.0.
        private static Version Normalize(Version version) =>
            new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static void SaveSetting(string name, string value)
        {
            Saved[name] = value;
            SettingsStore.Save(Saved);
        }

        // Starts the checkbox from its saved value (or its default) and saves every change.
        private static void BindSetting(CheckBox box, string name)
        {
            if (Saved.TryGetValue(name, out string value))
                box.IsChecked = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            box.IsCheckedChanged += (sender, args) => SaveSetting(name, box.IsChecked == true ? "true" : "false");
        }

        private static string LoadOutputFolder() =>
            Saved.TryGetValue(FolderSetting, out string folder) && folder.Length > 0
                ? folder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Roblox Exports");

        // Shows how much the content cache holds (counted off the UI thread; a big cache has many files).
        private async Task UpdateCacheSizeAsync()
        {
            var (files, bytes) = await Task.Run(() => ContentCache.Size());
            cacheSize.Text = files == 0 ? "The cache is empty." : "The cache holds " + files.ToString("N0", CultureInfo.CurrentCulture) +
                " file" + (files == 1 ? "" : "s") + " (" + (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB).";
        }

        private static HttpClient CreateClient() => new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private async Task ExportAsync()
        {
            cancellation = new CancellationTokenSource();
            exportedAddon = null;
            SetBusy(true);
            log.Text = "";
            try
            {
                bool avatar = IsAvatarLike;
                RobloxModelDownloader.BundleInfo bundle = IsBundle ? loadedBundle : null;
                if (IsBundle && bundle == null)
                    throw new InvalidDataException("The bundle's details have not loaded yet.");
                string selection = input.Text ?? "";
                ExportFormat format = Formats[exportFormat.SelectedIndex];
                string hero = format == ExportFormat.Deadlock && deadlockHero.SelectedIndex >= 0 ? heroes[deadlockHero.SelectedIndex].Key : null;
                if (format == ExportFormat.Deadlock && hero == null)
                    throw new InvalidDataException("Choose the hero to replace.");
                if (format == ExportFormat.Deadlock && !avatar)
                    throw new InvalidDataException("Only avatars and bundles can replace a Deadlock hero.");
                string folder = Path.GetFullPath(outputFolder.Text ?? "");
                SaveSetting(FolderSetting, folder);
                var progress = new Progress<string>(AppendLog);
                string name = loadedName ?? selection;
                ShowPresence("Exporting " + name, "To " + FormatNames[exportFormat.SelectedIndex]);
                bool faces = faceTracking.IsChecked == true, vrchat = vrchatShapes.IsChecked == true, single = singleTexture.IsChecked == true,
                    joints = connectJoints.IsChecked == true, mood = applyMood.IsChecked == true, own = ownFolder.IsChecked == true;
                float size = IsPlace ? 1f : (float)Math.Round(scale.Value) / 100f;
                using (var client = CreateClient())
                {
                    var downloader = new RobloxModelDownloader(client, DefaultApiBase);
                    string modelFile = IsFile ? Path.GetFullPath(selection.Trim()) : null;
                    long id = modelFile != null ? 0 : bundle?.Id ?? await downloader.ResolveIdAsync(selection, avatar, cancellation.Token);
                    var r6 = R6Attachments();
                    CancellationToken token = cancellation.Token;
                    // Off the UI thread: building and compiling take a while.
                    string path = await Task.Run(() => downloader.ExportAsync(id, avatar, "", folder, progress, token, format,
                        avatar && faces, vrchat, single, size, avatar && joints, bundle, mood, own, modelFile, r6, hero));
                    AppendLog("Export complete: " + path);
                    RecordExport(path, downloader.ExportFolder, id, format, name);
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
                await ShowErrorAsync(cancellation.IsCancellationRequested ? "Export cancelled." : "The API request timed out. Try again.");
            }
            catch (Exception error)
            {
                await ShowErrorAsync(error.Message);
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

        // Fills the hero list from the installed game.
        private async Task LoadHeroesAsync()
        {
            if (heroes != null)
                return;
            try
            {
                DeadlockInstall install = DeadlockInstall.Find();
                if (install == null)
                {
                    AppendLog("Deadlock wasn't found in your Steam libraries.");
                    return;
                }
                heroes = await Task.Run(() => DeadlockExporter.Heroes(install));
                foreach (DeadlockHero hero in heroes)
                    deadlockHero.Items.Add(new ComboBoxItem { Content = hero.Name });
                if (Saved.TryGetValue(HeroSetting, out string key))
                    deadlockHero.SelectedIndex = heroes.FindIndex(hero => hero.Key == key);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException)
            {
                heroes = null;
                AppendLog("Deadlock's heroes couldn't be read: " + error.Message);
            }
        }

        // What each R6 body part can be attached to (the first choice is the default). An R6 limb is one rigid block found as its
        // middle part, so that part names it. Nothing is applied to R15 avatars.
        private static readonly (string Label, BodyPart Part, string Setting, (string Text, string Bone)[] Choices)[] R6Parts =
        {
            ("Left arm", BodyPart.LeftLowerArm, "r6leftarm", new[] { ("Wrist (hand)", "LeftHand"), ("Elbow (forearm)", "LeftLowerArm"), ("Shoulder (upper arm)", "LeftUpperArm") }),
            ("Right arm", BodyPart.RightLowerArm, "r6rightarm", new[] { ("Wrist (hand)", "RightHand"), ("Elbow (forearm)", "RightLowerArm"), ("Shoulder (upper arm)", "RightUpperArm") }),
            ("Left leg", BodyPart.LeftLowerLeg, "r6leftleg", new[] { ("Ankle (foot)", "LeftFoot"), ("Knee (calf)", "LeftLowerLeg"), ("Hip (thigh)", "LeftUpperLeg") }),
            ("Right leg", BodyPart.RightLowerLeg, "r6rightleg", new[] { ("Ankle (foot)", "RightFoot"), ("Knee (calf)", "RightLowerLeg"), ("Hip (thigh)", "RightUpperLeg") }),
            ("Torso", BodyPart.UpperTorso, "r6torso", new[] { ("Spine", "Spine"), ("Chest", "Chest"), ("Upper chest", "UpperChest"), ("Hips", "Hips") }),
            ("Head", BodyPart.Head, "r6head", new[] { ("Head", "Head"), ("Neck", "Neck") }),
        };

        private static Dictionary<BodyPart, string> R6Attachments()
        {
            var attachments = new Dictionary<BodyPart, string>();
            foreach (var part in R6Parts)
                attachments[part.Part] = Saved.TryGetValue(part.Setting, out string bone) && part.Choices.Any(choice => choice.Bone == bone) ? bone : part.Choices[0].Bone;
            return attachments;
        }

        // The R6 Configuration window: a dropdown per part, saved as soon as it changes.
        private async Task ShowR6PartsAsync()
        {
            var dialog = new Window
            {
                Title = "R6 Configuration", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Icon = Icon,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,240"), RowSpacing = 8, ColumnSpacing = 12 };
            var boxes = new List<ComboBox>();
            for (int i = 0; i < R6Parts.Length; i++)
            {
                var part = R6Parts[i];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = part.Label + ":", VerticalAlignment = VerticalAlignment.Center };
                var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var choice in part.Choices)
                    box.Items.Add(new ComboBoxItem { Content = choice.Text });
                int saved = Saved.TryGetValue(part.Setting, out string bone) ? Array.FindIndex(part.Choices, choice => choice.Bone == bone) : 0;
                box.SelectedIndex = Math.Max(0, saved);
                box.SelectionChanged += (sender, args) => SaveSetting(part.Setting, part.Choices[Math.Max(0, box.SelectedIndex)].Bone);
                Grid.SetRow(label, i);
                Grid.SetRow(box, i);
                Grid.SetColumn(box, 1);
                grid.Children.Add(label);
                grid.Children.Add(box);
                boxes.Add(box);
            }
            var reset = new Button { Content = "Reset" };
            reset.Click += (sender, args) =>
            {
                foreach (ComboBox box in boxes)
                    box.SelectedIndex = 0;
            };
            var done = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
            done.Click += (sender, args) => dialog.Close();
            var content = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            content.Children.Add(new TextBlock { Text = "What each body part moves with. Applies to the next export.", TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            content.Children.Add(grid);
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { reset, done } });
            dialog.Content = content;
            await dialog.ShowDialog(this);
        }

        // Adds the export to the Library, with the preview picture shown while it was exported.
        private void RecordExport(string path, string exportFolder, long id, ExportFormat format, string name)
        {
            byte[] picture = previewBytes;
            ExportLibrary.Add(new ExportLibrary.Entry
            {
                // A model or place file is named by its file name, not the path typed in.
                Name = IsFile ? Path.GetFileNameWithoutExtension(name.Trim()) : name, Type = TypeNames[exportType.SelectedIndex], Format = FormatNames[Array.IndexOf(Formats, format)], Id = id,
                Date = DateTime.Now, File = path, Folder = exportFolder ?? Path.GetDirectoryName(path)
            }, picture);
        }

        // Places always export at full size, so the slider is hidden while the Place type is selected (keeping the scale chosen
        // for the other types).
        private void UpdateScale(bool busy)
        {
            scaleRow.IsVisible = !IsPlace;
            // A Deadlock hero sets the avatar's size.
            scale.IsEnabled = !busy && !IsDeadlock;
        }

        private void SetBusy(bool busy)
        {
            foreach (Control control in new Control[] { exportType, exportFormat, deadlockHero, input, openModel, outputFolder, browse, singleTexture, ownFolder, scale })
                control.IsEnabled = !busy;
            export.IsEnabled = !busy && loaded;
            UpdateFaceOptions(busy);
            UpdateScale(busy);
            UpdateUpload();
            cancel.IsEnabled = busy;
        }

        private async Task UploadAsync()
        {
            string folder = exportedAddon;
            if (folder == null)
                return;
            bool sfm = exportedFormat == ExportFormat.SourceFilmmaker;
            long? existing = sfm ? SfmWorkshop.PublishedId(folder) : Workshop.PublishedId(folder);
            string question = existing.HasValue
                ? "Update Workshop item " + existing.Value + " with this export of " + Path.GetFileName(folder) + "?"
                : "Upload " + Path.GetFileName(folder) + " to the Steam Workshop as a new item?\n\nIt starts hidden; you can make it public from its Workshop page.";
            if (!await Dialogs.Ask(this, question + (sfm ? "\n\nIt goes to the Source Filmmaker Workshop. Steam must be running and signed in to an account with Source Filmmaker."
                : "\n\nSteam must be running and signed in to an account that owns Garry's Mod."), "Upload to Workshop"))
                return;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var progress = new Progress<string>(AppendLog);
                ShowPresence("Uploading " + (loadedName ?? Path.GetFileName(folder)), sfm ? "To the SFM Workshop" : "To the Steam Workshop");
                string title = loadedName ?? Path.GetFileName(folder);
                CancellationToken token = cancellation.Token;
                long id = sfm ? await Task.Run(() => SfmWorkshop.PublishAsync(folder, title, progress, token))
                    : await Task.Run(() => Workshop.PublishAsync(folder, progress, token));
                ShowPresence((existing.HasValue ? "Updated " : "Uploaded ") + (loadedName ?? Path.GetFileName(folder)), "On the Steam Workshop");
                string page = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
                AppendLog((existing.HasValue ? "Workshop item updated: " : "Uploaded to the Workshop: ") + page);
                if (await Dialogs.Ask(this, (existing.HasValue ? "The Workshop item was updated." : "The addon was uploaded.") + "\n\nOpen its Workshop page?", "Upload to Workshop"))
                    await OpenAsync(page);
            }
            catch (OperationCanceledException)
            {
                await ShowErrorAsync("Upload cancelled.");
            }
            catch (Exception error)
            {
                await ShowErrorAsync(error.Message);
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
            // The game formats bake the face's mood into the mesh instead of keeping shape keys.
            bool avatar = IsAvatarLike && chosen != ExportFormat.GarrysMod && chosen != ExportFormat.SourceFilmmaker &&
                chosen != ExportFormat.TowerUnite && chosen != ExportFormat.Source2 && chosen != ExportFormat.Deadlock;
            vrchatShapes.IsEnabled = !busy && avatar;
            faceTracking.IsEnabled = !busy && avatar;
            // Joint blending applies to avatars and bundles in every format.
            connectJoints.IsEnabled = !busy && IsAvatarLike;
            applyMood.IsEnabled = !busy && IsAvatarLike;
        }

        private void ShowPresence(string details, string state)
        {
            presenceDetails = details;
            presenceState = state;
            if (discordPresence.IsChecked == true)
                _ = discord.SetAsync(details, state);
        }

        private void AppendLog(string message)
        {
            log.Text += message + Environment.NewLine;
            log.CaretIndex = log.Text.Length;
        }

        private async Task ShowErrorAsync(string message)
        {
            AppendLog(message);
            await Dialogs.Show(this, message);
        }

        // Opens a web page or a folder with the system's default program.
        private async Task OpenAsync(string target)
        {
            try
            {
                bool opened = Directory.Exists(target)
                    ? await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(target))
                    : await Launcher.LaunchUriAsync(new Uri(target));
                if (!opened)
                    throw new IOException("the system has no program to open it");
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                await ShowErrorAsync("Could not open the browser or folder: " + error.Message);
            }
        }
    }
}
