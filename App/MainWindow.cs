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
    public sealed partial class MainWindow : Window
    {
        public const string DefaultApiBase = "https://api.physiceye.com";
        // Source 2 (ExportFormat.Source2, icon "source2.png") is left out of the list for now.
        private static readonly ExportFormat[] Formats = { ExportFormat.Glb, ExportFormat.Gltf, ExportFormat.Fbx, ExportFormat.GarrysMod, ExportFormat.SourceFilmmaker,
            ExportFormat.TowerUnite, ExportFormat.Deadlock, ExportFormat.Left4Dead2, ExportFormat.TeamFortress2 };
        private static readonly string[] FormatNames = { "GLB", "glTF", "FBX", "Garry's Mod", "Source Filmmaker", "Tower Unite", "Deadlock", "Left 4 Dead 2", "Team Fortress 2" };
        // Model files share one icon; each game has its Steam icon.
        private static readonly string[] FormatIcons = { "mesh_icon.png", "mesh_icon.png", "mesh_icon.png", "gmod.tga", "sfm.tga", "towerunite.tga", "deadlock.tga", "l4d2.tga", "TF2.tga" };
        private const string FolderSetting = "folder", DarkModeSetting = "darkmode", CacheSetting = "cache", CloudKeySetting = "cloudkey", HeroSetting = "deadlockhero",
            SurvivorSetting = "l4d2survivor", ClassSetting = "tf2class";
        private static readonly string[] SettingNames = new[] { FolderSetting, DarkModeSetting, "singletexture", "vrchat", "facetracking", "connectjoints", "discord", "mood", "ownfolder",
            CacheSetting, CloudKeySetting, HeroSetting, SurvivorSetting, ClassSetting, "r6leftarm", "r6rightarm", "r6leftleg", "r6rightleg", "r6torso", "r6head", ShareLikesSetting }
            .Concat(Formats.Select(FolderSettingFor)).Distinct().ToArray();

        // The output folder is remembered per format: FBX, GLB and glTF share the "folder" setting, and each game has its own
        // ("folder_garrysmod", ...), which starts as that shared folder until a different one is chosen or exported to.
        private static string FolderSettingFor(ExportFormat format) =>
            format == ExportFormat.Glb || format == ExportFormat.Gltf || format == ExportFormat.Fbx
                ? FolderSetting : FolderSetting + "_" + format.ToString().ToLowerInvariant();
        private static readonly Dictionary<string, string> Saved = LoadSettings();

        private static Dictionary<string, string> LoadSettings()
        {
            SettingsStore.Secrets.Add(CloudKeySetting);
            return SettingsStore.Load(SettingNames, FolderSetting);
        }

        // The buttons in the bottom right, from the repository (Data/links.json) so they can change without a new release:
        // { "1": { "text": "...", "url": "https://..." }, "2": ... }, in key order. It is downloaded at every start and never
        // saved on the user's PC; offline, there are no buttons.
        private const string LinksUrl = "https://raw.githubusercontent.com/Physiceye/RoPorter/refs/heads/main/Data/links.json";
        // Where versions up to 2.1.2 saved a copy of links.json; deleted at start.
        private static readonly string OldLinksCache = Path.Combine(Export.ContentCache.DataFolder, "links.json");
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
        private const int BundleType = 2, ModelType = 3, PlaceType = 4, MeshType = 5, OutfitType = 6;
        private static readonly string[] TypeNames = { "Avatar", "Catalog Item", "Bundle", "Model", "Place", "Mesh", "Outfit" };
        private static readonly string[] TypeIcons = { "Humanoid_icon", "Accoutrement_icon", "Player_icon", "Model_icon", "Workspace_icon", "mesh_icon", "Starterpack_icon" };

        private readonly ComboBox exportType = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        private readonly ComboBox exportFormat = new ComboBox { Width = 185 };
        private readonly Button r6PartsButton = new Button { Content = "R6 Configuration", IsVisible = false };
        // The Deadlock hero the avatar replaces, listed from the game the first time Deadlock is chosen.
        private readonly ComboBox deadlockHero = new ComboBox { Width = 170, IsVisible = false, PlaceholderText = "Hero to replace" };
        private List<DeadlockHero> heroes; 
        // The Left 4 Dead 2 survivor the avatar (and its first-person arms) replaces.
        private readonly ComboBox l4d2Survivor = new ComboBox
        {
            Width = 170, IsVisible = false, PlaceholderText = "Survivor to replace",
            ItemsSource = L4d2Survivor.All.Select(survivor => survivor.Name).ToList()
        };
        // The Team Fortress 2 class the avatar (and its first-person arms) replaces.
        private readonly ComboBox tf2Class = new ComboBox
        {
            Width = 170, IsVisible = false, PlaceholderText = "Class to replace",
            ItemsSource = Tf2Class.All.Select(playerClass => playerClass.Name).ToList()
        };
        private readonly TextBox input = new TextBox();
        private readonly TextBlock inputLabel = new TextBlock { Text = "User ID:", VerticalAlignment = VerticalAlignment.Center };
        private readonly Button openModel = new Button { Content = "Open...", IsVisible = false, Margin = new Thickness(6, 0, 0, 0) };
        // The Mesh type's texture: an image asset ID put on the mesh (optional; without one the mesh is plain grey).
        private readonly TextBlock textureLabel = new TextBlock { Text = "Texture ID:", VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Margin = new Thickness(8, 0, 6, 0) };
        private readonly TextBox textureInput = new TextBox { Width = 160, IsVisible = false, PlaceholderText = "optional" };
        private readonly TextBox outputFolder = new TextBox { Text = LoadOutputFolder() };
        // Optional: replaces the username or item name in the export's folder, files and in-game name.
        private readonly TextBox exportName = new TextBox { PlaceholderText = "Optional", MaxLength = 64 };
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
        // The avatar's own first-person hands in Garry's Mod, Left 4 Dead 2 and Team Fortress 2 (on by default); off keeps the
        // game's (Garry's Mod's citizen hands, or the survivor's or class's own arms).
        private readonly CheckBox firstPersonHands = new CheckBox { Content = "Export first-person hands", IsChecked = true };
        // Garry's Mod avatars and bundles also as friendly and hostile NPCs (off by default: it compiles a second model).
        private readonly CheckBox gmodNpcs = new CheckBox { Content = "Make Garry's Mod NPCs" };
        private readonly CheckBox cacheContent = new CheckBox { Content = "Keep downloaded files", IsChecked = true };
        private readonly TextBlock cacheSize = new TextBlock();
        private readonly TextBox cloudKey = new TextBox { PasswordChar = '•', Width = 340, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox showCloudKey = new CheckBox { Content = "Show key" };

        private readonly Button exportTab = new Button(), settingsTab = new Button(), libraryTab = new Button();
        // The Library page: every export in the output folder, as cards that open its folder.
        private readonly WrapPanel libraryCards = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox libraryFilter = new ComboBox { Width = 160 };
        private readonly TextBlock libraryInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private static readonly string[] LibraryFilters = { "All", "Avatars", "Catalog items", "Bundles", "Models", "Places", "Outfits" };
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

        // Bundles and saved outfits both export as Roblox's render of an outfit, with its items as the assets.
        private bool IsBundle => exportType.SelectedIndex == BundleType || IsOutfit;
        private bool IsOutfit => exportType.SelectedIndex == OutfitType;
        private bool IsModel => exportType.SelectedIndex == ModelType;
        private bool IsPlace => exportType.SelectedIndex == PlaceType;
        private bool IsFile => IsModel || IsPlace;
        private bool IsMesh => exportType.SelectedIndex == MeshType;
        private bool IsAvatarLike => exportType.SelectedIndex == 0 || IsBundle;
        private bool IsDeadlock => Formats[Math.Max(0, exportFormat.SelectedIndex)] == ExportFormat.Deadlock;
        private bool IsL4d2 => Formats[Math.Max(0, exportFormat.SelectedIndex)] == ExportFormat.Left4Dead2;
        private bool IsTf2 => Formats[Math.Max(0, exportFormat.SelectedIndex)] == ExportFormat.TeamFortress2;
        // Deadlock replaces a hero and Left 4 Dead 2 a survivor, so they take avatars and bundles only; Team Fortress 2 replaces a
        // class with those, and builds maps from places.
        // Which export types each format takes: Deadlock replaces a hero, so only avatars and bundles; every other format takes
        // anything (an avatar or bundle replaces a Left 4 Dead 2 survivor or Team Fortress 2 class, anything else becomes a map
        // prop there, and a place a Team Fortress 2 map), except places in Left 4 Dead 2, which has no map export.
        private static bool Supported(int type, ExportFormat format) =>
            format == ExportFormat.Deadlock ? type == 0 || type == BundleType || type == OutfitType : !(format == ExportFormat.Left4Dead2 && type == PlaceType);
        private bool TypeAllowed(int type) => Supported(type, Formats[Math.Max(0, exportFormat.SelectedIndex)]);
        private bool FormatAllowed(int format) => Supported(Math.Max(0, exportType.SelectedIndex), Formats[format]);
        private static int InputSlot(int type) => type == ModelType || type == PlaceType || type == MeshType ? type : 0;

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
            exportFormat.ContainerPrepared += (sender, args) => args.Container.IsEnabled = FormatAllowed(args.Index);
            exportFormat.SelectedIndex = 0;
            SetTab(exportTab, "Configuration_icon.png", "Export");
            SetTab(settingsTab, "Starterpack_icon.png", "Settings");
            SetTab(libraryTab, "Workspace_icon.png", "Library");
            SetTab(shareTab, "Player_icon.png", "Community");
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
            sharePage = BuildSharePage();
            sharePage.IsVisible = false;

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 8, 12, 0) };
            tabs.Children.Add(exportTab);
            tabs.Children.Add(settingsTab);
            tabs.Children.Add(libraryTab);
            tabs.Children.Add(shareTab);
            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 12, 10) };
            bottom.Children.Add(new TextBlock { Text = "v" + typeof(MainWindow).Assembly.GetName().Version.ToString(3), VerticalAlignment = VerticalAlignment.Center });
            DeleteOldLinksCache();
            Grid.SetColumn(links, 1);
            bottom.Children.Add(links);
            var root = new DockPanel();
            DockPanel.SetDock(tabs, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(tabs);
            root.Children.Add(bottom);
            root.Children.Add(new Panel { Children = { exportPage, settingsPage, libraryPage, sharePage } });
            Content = root;

            exportTab.Click += (sender, args) => ShowPage(ExportPageIndex);
            settingsTab.Click += async (sender, args) =>
            {
                ShowPage(SettingsPageIndex);
                await Guarded(UpdateCacheSizeAsync);
            };
            libraryTab.Click += async (sender, args) =>
            {
                ShowPage(LibraryPageIndex);
                await Guarded(RefreshLibraryAsync);
            };
            // The shared list loads the first time the tab is opened; Refresh loads it again.
            shareTab.Click += async (sender, args) =>
            {
                ShowPage(SharePageIndex);
                if (shareCards.Children.Count == 0 && !shareLoading)
                    await Guarded(() => RefreshSharesAsync(false));
            };
            WireExportPage();
            WireMeshPreview();
            BindSetting(darkMode, DarkModeSetting);
            darkMode.IsCheckedChanged += (sender, args) => ApplyTheme();
            ApplyTheme();
            ShowPage(ExportPageIndex);

            Opened += async (sender, args) =>
            {
                ShowPresence("Browsing", null);
                await Guarded(() => Task.WhenAll(UpdateLinksAsync(), CheckForUpdatesAsync()));
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
                ownedPreview?.Dispose();
                foreach (Bitmap old in libraryPictures)
                    old.Dispose();
            };
        }

        // Runs background work started from an event handler; an unexpected failure is logged instead of closing the app.
        private async Task Guarded(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception error)
            {
                AppendLog("Something went wrong: " + error.Message);
            }
        }

        private Grid BuildExportPage()
        {
            var layout = new Grid
            {
                Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 6, ColumnSpacing = 8
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
            var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            inputRow.Children.Add(input);
            Grid.SetColumn(openModel, 1);
            inputRow.Children.Add(openModel);
            Grid.SetColumn(textureLabel, 2);
            inputRow.Children.Add(textureLabel);
            Grid.SetColumn(textureInput, 3);
            inputRow.Children.Add(textureInput);
            Place(inputRow, 1, 1);
            Place(new TextBlock { Text = "Export name:", VerticalAlignment = VerticalAlignment.Center }, 2, 0);
            Place(exportName, 2, 1);
            Place(new TextBlock { Text = "Output folder:", VerticalAlignment = VerticalAlignment.Center }, 3, 0);
            Place(outputFolder, 3, 1);
            Place(browse, 4, 0);
            Place(scaleRow, 4, 1);
            Place(new TextBlock { Text = "Export format:", VerticalAlignment = VerticalAlignment.Bottom }, 5, 0);
            Grid.SetRowSpan(previewFrame, 6);
            Place(previewFrame, 0, 2);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (Control control in new Control[] { exportFormat, deadlockHero, l4d2Survivor, tf2Class, export, upload, cancel, openFolder, r6PartsButton })
            {
                control.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(control);
            }
            // Share sits at the far right of the row, apart from the export buttons.
            var actionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            actionRow.Children.Add(actions);
            share.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(share, 1);
            actionRow.Children.Add(share);
            Place(actionRow, 6, 0, 3);
            Place(log, 7, 0, 3);

            ToolTip.SetTip(exportName, "A name for the export instead of the username or item name, so several exports of one account don't replace each other.\n" +
                "Letters, digits and underscores are kept; anything else becomes an underscore.");
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
            foreach (CheckBox box in new[] { singleTexture, vrchatShapes, faceTracking, connectJoints, applyMood, ownFolder, firstPersonHands, gmodNpcs })
                page.Children.Add(box);
            BindSetting(singleTexture, "singletexture");
            BindSetting(vrchatShapes, "vrchat");
            BindSetting(faceTracking, "facetracking");
            BindSetting(connectJoints, "connectjoints");
            BindSetting(applyMood, "mood");
            BindSetting(ownFolder, "ownfolder");
            BindSetting(firstPersonHands, "firstpersonhands");
            ToolTip.SetTip(firstPersonHands, "Garry's Mod, Left 4 Dead 2 and Team Fortress 2: makes first-person hands (viewmodel arms) from the avatar's arms.\n" +
                "Off, the game's own are kept: Garry's Mod's citizen hands, or the replaced survivor's or class's arms.");
            BindSetting(gmodNpcs, "gmodnpcs");
            ToolTip.SetTip(gmodNpcs, "Garry's Mod avatars and bundles: also adds the avatar as a friendly and a hostile NPC (a citizen with " +
                "Half-Life 2's citizen animations), in the spawn menu's NPC tab under RoPorter.");
            ToolTip.SetTip(singleTexture, "Merges every material into one material and one texture.\n" +
                "Deadlock exports and Tower Unite avatars always use one texture; Garry's Mod and Team Fortress 2 maps don't use this option.");
            ToolTip.SetTip(vrchatShapes, "Adds VRChat visemes. Avatars, bundles and outfits in GLB, glTF and FBX.");
            ToolTip.SetTip(faceTracking, "Adds Unified Expressions face shapes for VRCFaceTracking. Avatars, bundles and outfits in GLB, glTF and FBX.");
            ToolTip.SetTip(connectJoints, "Blends the body's weights across its joints so the surface stretches when they bend, instead of each " +
                "part moving on its own, and lets accessories worn on the torso (coats, sashes, skirts) bend at the waist with it. " +
                "For UGC bundles modelled as one connected body; R6 avatars stay blocky. " +
                "Deadlock always puts the arms and legs on the hero's joints so its animations work; this option only chooses " +
                "whether the surface stretches across the joints or the parts stay separate blocks.");
            ToolTip.SetTip(applyMood, "Gives the face the avatar's equipped Mood (its resting expression, e.g. a smile). " +
                "Off, the face is neutral; GLB, glTF and FBX keep the mood as a \"Default\" shape key you can turn on.");
            // Options greyed out for the chosen type and format (see UpdateFaceOptions) still explain themselves when hovered.
            foreach (CheckBox box in new[] { singleTexture, vrchatShapes, faceTracking, connectJoints, applyMood, firstPersonHands, gmodNpcs })
                ToolTip.SetShowOnDisabled(box, true);
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
            ToolTip.SetTip(cacheContent, "Keeps downloaded meshes, textures and models in the rbx_cache folder in your local app data, so exporting them " +
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
                inputLabel.Text = exportType.SelectedIndex == 0 ? "User ID:" : IsOutfit ? "Outfit ID:" : IsBundle ? "Bundle ID:" : IsModel ? "Model file:" : IsPlace ? "Place file:"
                    : IsMesh ? "Mesh ID:" : "Item ID:";
                // The ID box holds the file's path for the Model and Place types.
                if (InputSlot(shownType) != InputSlot(exportType.SelectedIndex))
                {
                    inputs[InputSlot(shownType)] = input.Text;
                    input.Text = inputs.TryGetValue(InputSlot(exportType.SelectedIndex), out string saved) ? saved : "";
                }
                shownType = exportType.SelectedIndex;
                openModel.IsVisible = IsFile;
                textureLabel.IsVisible = textureInput.IsVisible = IsMesh;
                l4d2Survivor.IsVisible = IsL4d2 && IsAvatarLike;
                tf2Class.IsVisible = IsTf2 && IsAvatarLike;
                // Formats this type can't go to are greyed out, as types are for the chosen format; the format falls back to GLB.
                for (int format = 0; format < FormatNames.Length; format++)
                    if (exportFormat.ContainerFromIndex(format) is Control option)
                        option.IsEnabled = FormatAllowed(format);
                if (!FormatAllowed(exportFormat.SelectedIndex))
                    exportFormat.SelectedIndex = 0;
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
            // The texture ID is digits only too; pasted text (an rbxassetid:// link, say) keeps just its digits.
            textureInput.AddHandler(TextInputEvent, (sender, args) =>
            {
                if (args.Text != null && args.Text.Any(c => !char.IsDigit(c)))
                    args.Handled = true;
            }, RoutingStrategies.Tunnel);
            textureInput.TextChanged += (sender, args) =>
            {
                string text = textureInput.Text ?? "";
                string digits = new string(text.Where(char.IsDigit).Take(19).ToArray());
                if (digits != text)
                {
                    textureInput.Text = digits;
                    textureInput.CaretIndex = digits.Length;
                    return;
                }
                // The mesh preview is drawn with the texture, so a new texture ID redraws it.
                if (IsMesh)
                    SchedulePreview();
            };
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
                ShowFormatFolder();
                UpdateFaceOptions(false);
                UpdateUpload();
                deadlockHero.IsVisible = IsDeadlock;
                l4d2Survivor.IsVisible = IsL4d2 && IsAvatarLike;
                tf2Class.IsVisible = IsTf2 && IsAvatarLike;
                // Deadlock, Left 4 Dead 2 and Team Fortress 2 mods don't go on the Workshop; the hero, survivor or class list takes the button's place.
                upload.IsVisible = !IsDeadlock && !IsL4d2 && !IsTf2;
                UpdateScale(false);
                for (int type = 0; type < TypeNames.Length; type++)
                    if (exportType.ContainerFromIndex(type) is Control item)
                        item.IsEnabled = TypeAllowed(type);
                if (!TypeAllowed(exportType.SelectedIndex))
                    exportType.SelectedIndex = 0;
                if (deadlockHero.IsVisible)
                    await LoadHeroesAsync();
            };
            if (Saved.TryGetValue(SurvivorSetting, out string savedSurvivor))
                l4d2Survivor.SelectedIndex = L4d2Survivor.All.ToList().FindIndex(survivor => survivor.Key == savedSurvivor);
            l4d2Survivor.SelectionChanged += (sender, args) =>
            {
                if (l4d2Survivor.SelectedIndex >= 0)
                    SaveSetting(SurvivorSetting, L4d2Survivor.All[l4d2Survivor.SelectedIndex].Key);
            };
            if (Saved.TryGetValue(ClassSetting, out string savedClass))
                tf2Class.SelectedIndex = Tf2Class.All.ToList().FindIndex(playerClass => playerClass.Key == savedClass);
            tf2Class.SelectionChanged += (sender, args) =>
            {
                if (tf2Class.SelectedIndex >= 0)
                    SaveSetting(ClassSetting, Tf2Class.All[tf2Class.SelectedIndex].Key);
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

        private static void DeleteOldLinksCache()
        {
            try
            {
                if (File.Exists(OldLinksCache))
                    File.Delete(OldLinksCache);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
        }

        private void ShowLinks(List<KeyValuePair<string, string>> entries)
        {
            links.Children.Clear();
            foreach (var entry in entries)
                links.Children.Add(LinkButton(entry.Key, entry.Value, "Opens " + entry.Value));
        }

        // Downloads the current links.json; the buttons show only if it holds at least one usable link.
        private async Task UpdateLinksAsync()
        {
            try
            {
                string json = await GetTextAsync(LinksUrl, "RoPorter/" + RobloxModelDownloader.AppVersion);
                var entries = ParseLinks(json);
                if (entries.Count == 0)
                    return;
                ShowLinks(entries);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                // Offline or GitHub unreachable: no buttons.
            }
        }

        private const int ExportPageIndex = 0, SettingsPageIndex = 1, LibraryPageIndex = 2, SharePageIndex = 3;

        private void ShowPage(int page)
        {
            var pages = new[] { (exportPage, exportTab), (settingsPage, settingsTab), (libraryPage, libraryTab), (sharePage, shareTab) };
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
            libraryFilter.SelectionChanged += async (sender, args) => await Guarded(RefreshLibraryAsync);
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (sender, args) => await RefreshLibraryAsync();
            var open = new Button { Content = "Open folder" };
            open.Click += async (sender, args) =>
            {
                if (Directory.Exists(outputFolder.Text))
                    await OpenAsync(outputFolder.Text);
            };
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

        // Lists the exports in every format's output folder (read off the UI thread; a folder of Garry's Mod addons can be large).
        // An export found through two folders (one inside the other) is listed once.
        private async Task RefreshLibraryAsync()
        {
            List<string> folders = AllOutputFolders();
            string folder = folders.Count == 1 ? folders[0] : folders.Count + " output folders";
            string type = new[] { null, "Avatar", "Catalog Item", "Bundle", "Model", "Place", "Outfit" }[Math.Max(0, libraryFilter.SelectedIndex)];
            List<ExportLibrary.Entry> entries = await Task.Run(() => folders.SelectMany(ExportLibrary.List)
                .GroupBy(e => e.File ?? e.Folder ?? "", StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderByDescending(e => e.Date).ToList());
            var shown = entries.Where(e => type == null || e.Type == type).ToList();
            libraryCards.Children.Clear();
            foreach (Bitmap old in libraryPictures)
                old.Dispose();
            libraryPictures.Clear();
            libraryInfo.Text = !folders.Any(Directory.Exists) ? "The output folder doesn't exist yet." : shown.Count == 0 ? "Nothing exported here yet."
                : shown.Count + " export" + (shown.Count == 1 ? "" : "s") + " in " + folder;
            foreach (ExportLibrary.Entry entry in shown)
                libraryCards.Children.Add(LibraryCard(entry));
        }

        // The Library cards' pictures, freed when the cards are rebuilt.
        private readonly List<Bitmap> libraryPictures = new List<Bitmap>();

        private Control LibraryCard(ExportLibrary.Entry entry)
        {
            Bitmap picture = null;
            try
            {
                if (entry.Picture != null && File.Exists(entry.Picture))
                    libraryPictures.Add(picture = ToBitmap(File.ReadAllBytes(entry.Picture)));
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
                SaveSetting(FolderSettingFor(Formats[Math.Max(0, exportFormat.SelectedIndex)]), path);
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

        // A mesh's real look: the mesh downloaded and drawn, with the texture ID's image on it when one is entered (both kept in
        // the content cache, so exporting it then needs no downloads). Roblox's own mesh thumbnail, shown until then, has no
        // texture and is often blank. A mesh or texture that can't be downloaded keeps the thumbnail.
        private async Task PreviewMeshAsync(RobloxModelDownloader downloader, long meshId, CancellationTokenSource current)
        {
            long? textureId = long.TryParse((textureInput.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long texture) && texture > 0
                ? texture : (long?)null;
            try
            {
                CancellationToken token = current.Token;
                MeshRender mesh = await Task.Run(async () =>
                {
                    var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    ModelContents contents = ModelFileBuilder.FromMesh(meshId, textureId, "Mesh " + meshId);
                    WavefrontModel model = await ModelFileBuilder.BuildAsync(contents, (id, cancel) => downloader.DownloadAssetAsync(id, cancel), textures, null, token);
                    return MeshRender.Prepare(model, textures);
                });
                if (current != previewCancellation || mesh == null)
                    return;
                // Drawn at Roblox's thumbnail angle; dragging the preview turns it.
                ShowMeshPreview(mesh, "Mesh " + meshId + (textureId.HasValue ? " with texture " + textureId.Value : ""));
            }
            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is NotSupportedException ||
                error is AssetAccessException || error is IOException || error is ArgumentException || error is IndexOutOfRangeException)
            {
                // The thumbnail stays.
            }
        }

        // A model file's real look: its meshes and textures downloaded (kept in the content cache, so exporting it then needs
        // no downloads) and drawn in place of the boxes. A failure keeps the boxes.
        private async Task PreviewMeshesAsync(string file, string description, CancellationTokenSource current)
        {
            try
            {
                var downloader = new RobloxModelDownloader(Http, DefaultApiBase);
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
            UpdateShare();
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
        // keepMesh: a new drawing of the turnable mesh preview; anything else replaces it.
        private void ShowPreview(Bitmap image, string description, byte[] bytes = null, bool keepMesh = false)
        {
            if (!keepMesh)
                ForgetMeshPreview();
            preview.Source = image ?? brokenPreview;
            previewBytes = image != null ? bytes : null;
            ToolTip.SetTip(previewFrame, description);
            // Pictures made for one preview are freed when replaced; the app's own icons are shared and kept.
            if (ownedPreview != null && ownedPreview != image)
                ownedPreview.Dispose();
            ownedPreview = image != null && image != brokenPreview && image != partIcon && Array.IndexOf(typeBitmaps, image) < 0 ? image : null;
        }

        private Bitmap ownedPreview;

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
                var downloader = new RobloxModelDownloader(Http, DefaultApiBase);
                if (bundle)
                {
                    // Bundles and outfits: the details (name and outfit) the export needs, then the picture. A missing picture
                    // only leaves the broken preview.
                    long bundleId = long.Parse(text, CultureInfo.InvariantCulture);
                    bool outfit = IsOutfit;
                    RobloxModelDownloader.BundleInfo details = outfit ? await downloader.GetOutfitAsync(bundleId, current.Token)
                        : await downloader.GetBundleAsync(bundleId, current.Token);
                    Bitmap picture = null;
                    byte[] pictureBytes = null;
                    string problem = null;
                    try
                    {
                        pictureBytes = await downloader.DownloadPictureAsync(details, current.Token);
                        picture = ToBitmap(pictureBytes);
                    }
                    catch (Exception error) when (!(error is OperationCanceledException) && !(error is ApiMessageException))
                    {
                        problem = error.Message;
                    }
                    if (current != previewCancellation)
                        return;
                    ShowPreview(picture, picture != null ? (outfit ? "Outfit " : "Bundle ") + bundleId : "No picture: " + problem, pictureBytes);
                    loadedBundle = details;
                    SetLoaded(true);
                    Title = "RoPorter - " + details.Name;
                    loadedName = details.Name;
                    ShowPresence("Viewing " + details.Name, outfit ? "Outfit" : "Bundle");
                    return;
                }
                if (IsMesh)
                {
                    // A mesh needs no details to export: its thumbnail is shown when Roblox has one.
                    long meshId = long.Parse(text, CultureInfo.InvariantCulture);
                    Bitmap meshPicture = null;
                    byte[] meshBytes = null;
                    try
                    {
                        meshBytes = await downloader.DownloadThumbnailAsync(meshId, false, current.Token);
                        meshPicture = ToBitmap(meshBytes);
                    }
                    catch (Exception error) when (!(error is OperationCanceledException) && !(error is ApiMessageException))
                    {
                    }
                    if (current != previewCancellation)
                        return;
                    ShowPreview(meshPicture ?? typeBitmaps[MeshType], "Mesh " + meshId, meshBytes);
                    SetLoaded(true);
                    Title = "RoPorter - Mesh " + meshId;
                    loadedName = "Mesh " + meshId;
                    ShowPresence("Viewing a mesh", "Mesh");
                    await PreviewMeshAsync(downloader, meshId, current);
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
                Newtonsoft.Json.Linq.JObject release = Newtonsoft.Json.Linq.JObject.Parse(
                    await GetTextAsync(ReleasesApi, "RoPorter", "application/vnd.github+json"));
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

        private static string LoadOutputFolder() => LoadOutputFolder(Formats[0]);

        private static string LoadOutputFolder(ExportFormat format) =>
            Saved.TryGetValue(FolderSettingFor(format), out string own) && own.Length > 0 ? own
            : Saved.TryGetValue(FolderSetting, out string shared) && shared.Length > 0 ? shared
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Roblox Exports");

        // The format whose folder the output folder box shows, and folders typed for a format but not yet saved (only a chosen
        // or exported-to folder is saved), so switching format and back keeps what was typed.
        private int folderFormat;
        private readonly Dictionary<string, string> typedFolders = new Dictionary<string, string>(StringComparer.Ordinal);

        // Shows the chosen format's output folder.
        private void ShowFormatFolder()
        {
            int now = Math.Max(0, exportFormat.SelectedIndex);
            string before = FolderSettingFor(Formats[folderFormat]), after = FolderSettingFor(Formats[now]);
            folderFormat = now;
            if (before == after)
                return;
            typedFolders[before] = outputFolder.Text ?? "";
            outputFolder.Text = typedFolders.TryGetValue(after, out string typed) ? typed : LoadOutputFolder(Formats[now]);
        }

        // Every format's output folder (the Library lists them all), each once.
        private List<string> AllOutputFolders()
        {
            var folders = new List<string>();
            foreach (string folder in new[] { outputFolder.Text ?? "" }.Concat(Formats.Select(LoadOutputFolder)))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
                {
                    continue;
                }
                if (folder.Trim().Length > 0 && !folders.Contains(full, StringComparer.OrdinalIgnoreCase))
                    folders.Add(full);
            }
            return folders;
        }

        // Shows how much the content cache holds (counted off the UI thread; a big cache has many files).
        private async Task UpdateCacheSizeAsync()
        {
            var (files, bytes) = await Task.Run(() => ContentCache.Size());
            cacheSize.Text = files == 0 ? "The cache is empty." : "The cache holds " + files.ToString("N0", CultureInfo.CurrentCulture) +
                " file" + (files == 1 ? "" : "s") + " (" + (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB).";
        }

        // One client for the whole run, so connections are reused instead of opened for every preview and export.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        // A GET with its own headers and a shorter timeout, through the shared client (which follows no redirects).
        private static async Task<string> GetTextAsync(string url, string userAgent, string accept = null)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                for (int redirects = 0; ; redirects++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.UserAgent.ParseAdd(userAgent);
                        if (accept != null)
                            request.Headers.Accept.ParseAdd(accept);
                        using (HttpResponseMessage response = await Http.SendAsync(request, timeout.Token))
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status < 400 && response.Headers.Location != null && redirects < 3)
                            {
                                url = new Uri(request.RequestUri, response.Headers.Location).AbsoluteUri;
                                continue;
                            }
                            response.EnsureSuccessStatusCode();
                            return await response.Content.ReadAsStringAsync(timeout.Token);
                        }
                    }
                }
            }
        }

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
                    throw new InvalidDataException("The " + (IsOutfit ? "outfit" : "bundle") + "'s details have not loaded yet.");
                string selection = input.Text ?? "";
                ExportFormat format = Formats[exportFormat.SelectedIndex];
                string hero = format == ExportFormat.Deadlock && deadlockHero.SelectedIndex >= 0 ? heroes[deadlockHero.SelectedIndex].Key : null;
                if (format == ExportFormat.Deadlock && hero == null)
                    throw new InvalidDataException("Choose the hero to replace.");
                if (format == ExportFormat.Deadlock && !avatar)
                    throw new InvalidDataException("Only avatars and bundles can replace a Deadlock hero.");
                // Avatars and bundles replace a survivor or class; anything else becomes a prop (or a place a map) for the game.
                string survivor = format == ExportFormat.Left4Dead2 && avatar && l4d2Survivor.SelectedIndex >= 0 ? L4d2Survivor.All[l4d2Survivor.SelectedIndex].Key : null;
                if (format == ExportFormat.Left4Dead2 && avatar && survivor == null)
                    throw new InvalidDataException("Choose the survivor to replace.");
                string playerClass = format == ExportFormat.TeamFortress2 && avatar && tf2Class.SelectedIndex >= 0 ? Tf2Class.All[tf2Class.SelectedIndex].Key : null;
                if (format == ExportFormat.TeamFortress2 && avatar && playerClass == null)
                    throw new InvalidDataException("Choose the class to replace.");
                string folder = Path.GetFullPath(outputFolder.Text ?? "");
                SaveSetting(FolderSettingFor(format), folder);
                var progress = new Progress<string>(AppendLog);
                string customName = string.IsNullOrWhiteSpace(exportName.Text) ? null : exportName.Text.Trim();
                string name = customName ?? loadedName ?? selection;
                ShowPresence("Exporting " + name, "To " + FormatNames[exportFormat.SelectedIndex]);
                bool faces = faceTracking.IsChecked == true, vrchat = vrchatShapes.IsChecked == true, single = singleTexture.IsChecked == true,
                    joints = connectJoints.IsChecked == true, mood = applyMood.IsChecked == true, own = ownFolder.IsChecked == true,
                    hands = firstPersonHands.IsChecked == true, npcs = gmodNpcs.IsChecked == true;
                float size = IsPlace ? 1f : (float)Math.Round(scale.Value) / 100f;
                var downloader = new RobloxModelDownloader(Http, DefaultApiBase);
                string modelFile = IsFile ? Path.GetFullPath(selection.Trim()) : null;
                long? meshId = IsMesh ? long.Parse(selection.Trim(), CultureInfo.InvariantCulture) : (long?)null;
                long? textureId = IsMesh && !string.IsNullOrWhiteSpace(textureInput.Text) ? long.Parse(textureInput.Text.Trim(), CultureInfo.InvariantCulture) : (long?)null;
                long id = modelFile != null ? 0 : meshId ?? bundle?.Id ?? await downloader.ResolveIdAsync(selection, avatar, cancellation.Token);
                var r6 = R6Attachments();
                CancellationToken token = cancellation.Token;
                // Off the UI thread: building and compiling take a while.
                string path = await Task.Run(() => downloader.ExportAsync(id, avatar, "", folder, progress, token, format,
                    avatar && faces, vrchat, single, size, avatar && joints, bundle, mood, own, modelFile, r6, hero, customName, survivor, playerClass, hands, avatar && npcs, meshId, textureId));
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
        // middle part, so that part names it. Nothing is applied to R15 avatars. Arms default to the shoulder, as in Roblox: games
        // turn the hand a lot (holding weapons), and a block hung from the wrist then swings out from the body.
        private static readonly (string Label, BodyPart Part, string Setting, (string Text, string Bone)[] Choices)[] R6Parts =
        {
            ("Left arm", BodyPart.LeftLowerArm, "r6leftarm", new[] { ("Shoulder (upper arm)", "LeftUpperArm"), ("Elbow (forearm)", "LeftLowerArm"), ("Wrist (hand)", "LeftHand") }),
            ("Right arm", BodyPart.RightLowerArm, "r6rightarm", new[] { ("Shoulder (upper arm)", "RightUpperArm"), ("Elbow (forearm)", "RightLowerArm"), ("Wrist (hand)", "RightHand") }),
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
            foreach (Control control in new Control[] { exportType, exportFormat, deadlockHero, l4d2Survivor, tf2Class, input, openModel, textureInput, exportName, outputFolder, browse, ownFolder, scale })
                control.IsEnabled = !busy;
            export.IsEnabled = !busy && loaded;
            UpdateFaceOptions(busy);
            UpdateScale(busy);
            UpdateUpload();
            UpdateShare();
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

        // Greys out the export options the chosen type and format don't use. Face shape options apply to avatars outside the
        // game formats (Source models get no shape keys); face tracking and the VRChat shape keys are independent.
        private void UpdateFaceOptions(bool busy)
        {
            ExportFormat chosen = Formats[Math.Max(0, exportFormat.SelectedIndex)];
            // The game formats bake the face's mood into the mesh instead of keeping shape keys.
            bool avatar = IsAvatarLike && chosen != ExportFormat.GarrysMod && chosen != ExportFormat.SourceFilmmaker &&
                chosen != ExportFormat.TowerUnite && chosen != ExportFormat.Source2 && chosen != ExportFormat.Deadlock && chosen != ExportFormat.Left4Dead2 &&
                chosen != ExportFormat.TeamFortress2;
            vrchatShapes.IsEnabled = !busy && avatar;
            faceTracking.IsEnabled = !busy && avatar;
            // Joint blending applies to avatars and bundles in every format.
            connectJoints.IsEnabled = !busy && IsAvatarLike;
            applyMood.IsEnabled = !busy && IsAvatarLike;
            // Deadlock and Tower Unite avatars always merge into one texture, and Garry's Mod and Team Fortress 2 maps keep
            // the place's own materials.
            singleTexture.IsEnabled = !busy && chosen != ExportFormat.Deadlock && !(chosen == ExportFormat.TowerUnite && IsAvatarLike) &&
                !(IsPlace && (chosen == ExportFormat.GarrysMod || chosen == ExportFormat.TeamFortress2));
            // First-person hands are made for the games with viewmodel arms; NPCs only for Garry's Mod.
            firstPersonHands.IsEnabled = !busy && IsAvatarLike && (chosen == ExportFormat.GarrysMod || chosen == ExportFormat.Left4Dead2 ||
                chosen == ExportFormat.TeamFortress2);
            gmodNpcs.IsEnabled = !busy && IsAvatarLike && chosen == ExportFormat.GarrysMod;
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
