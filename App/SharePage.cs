using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rbx2Source.Export;
using SharedEntry = Rbx2Source.Export.RobloxModelDownloader.SharedEntry;

namespace Rbx2Source
{
    // The Community page: avatars, outfits, bundles and catalog items other RoPorter users recommend, newest or most liked first.
    // Only Roblox IDs are shared (the API names each entry from Roblox), so opening one loads it on the Export page to export
    // from Roblox like anything typed in. The Export page's Share button adds what is loaded there.
    public sealed partial class MainWindow
    {
        private readonly Button shareTab = new Button();
        private Control sharePage;
        private readonly WrapPanel shareCards = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox shareFilter = new ComboBox { Width = 130 };
        private readonly ComboBox shareSort = new ComboBox { Width = 130 };
        // Finds entries by name (any part, ignoring case) or by Roblox ID; the list reloads once typing pauses.
        private readonly TextBox shareSearch = new TextBox { Width = 200, PlaceholderText = "Search names or IDs", MaxLength = 50 };
        private readonly Avalonia.Threading.DispatcherTimer shareSearchTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly TextBlock shareInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6), IsVisible = false };
        private readonly Button shareMore = new Button { Content = "Load more", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 12), IsVisible = false };
        // On the Export page: shares the avatar, outfit, bundle or catalog item loaded there.
        private readonly Button share = new Button { Content = "Share", IsEnabled = false };

        private static readonly string[] ShareFilters = { "All", "Avatars", "Outfits", "Bundles", "Catalog items", "Meshes" };
        private static readonly string[] ShareFilterTypes = { null, "avatar", "outfit", "bundle", "item", "mesh" };
        private const string ShareLikesSetting = "sharelikes";

        private CancellationTokenSource shareCancellation;
        private int? shareNextPage;
        private bool shareLoading;
        // Card pictures shown now (freed when the cards are rebuilt), and downloaded thumbnails kept for this run.
        private readonly List<Bitmap> sharePictures = new List<Bitmap>();
        private readonly Dictionary<string, byte[]> shareThumbnails = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // The entries liked from this PC (so the button shows whether it is liked); the API counts each like once anyway.
        private readonly HashSet<long> likedShares = LoadLikedShares();

        private static HashSet<long> LoadLikedShares() => new HashSet<long>(
            (Saved.TryGetValue(ShareLikesSetting, out string saved) ? saved : "").Split(',')
                .Select(key => long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0).Where(key => key > 0));

        private void SaveLikedShares() =>
            SaveSetting(ShareLikesSetting, string.Join(",", likedShares.OrderByDescending(key => key).Take(1000).Select(key => key.ToString(CultureInfo.InvariantCulture))));

        private Control BuildSharePage()
        {
            foreach (string filter in ShareFilters)
                shareFilter.Items.Add(new ComboBoxItem { Content = filter });
            shareFilter.SelectedIndex = 0;
            shareSort.Items.Add(new ComboBoxItem { Content = "Newest" });
            shareSort.Items.Add(new ComboBoxItem { Content = "Most liked" });
            shareSort.SelectedIndex = 0;
            shareFilter.SelectionChanged += async (sender, args) => await Guarded(() => RefreshSharesAsync(false));
            shareSort.SelectionChanged += async (sender, args) => await Guarded(() => RefreshSharesAsync(false));
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (sender, args) => await Guarded(() => RefreshSharesAsync(false));
            shareMore.Click += async (sender, args) => await Guarded(() => RefreshSharesAsync(true));
            shareSearch.TextChanged += (sender, args) =>
            {
                shareSearchTimer.Stop();
                shareSearchTimer.Start();
            };
            shareSearchTimer.Tick += async (sender, args) =>
            {
                shareSearchTimer.Stop();
                await Guarded(() => RefreshSharesAsync(false));
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 12, 12, 6) };
            bar.Children.Add(new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(shareFilter);
            bar.Children.Add(shareSort);
            bar.Children.Add(shareSearch);
            bar.Children.Add(refresh);
            var header = new StackPanel { Spacing = 2 };
            header.Children.Add(bar);
            // Only shown while loading, when nothing is shared, or when the list can't be loaded, so the cards start right under
            // the filters as in the Library.
            header.Children.Add(shareInfo);

            var list = new StackPanel();
            list.Children.Add(shareCards);
            list.Children.Add(shareMore);
            var page = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            page.Children.Add(header);
            page.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(6, 0, 6, 6) });

            share.Click += async (sender, args) => await ShareLoadedAsync();
            ToolTip.SetTip(share, "Adds the loaded avatar, outfit, bundle or catalog item to the Community tab for other RoPorter users. " +
                "Only its Roblox ID is shared; its name comes from Roblox.");
            return page;
        }

        // The Community page's type of what the Export page has loaded, or null for things that can't be shared (model and place
        // files, meshes).
        private string LoadedShareType() =>
            exportType.SelectedIndex == 0 ? "avatar" : exportType.SelectedIndex == 1 ? "item" : IsOutfit ? "outfit" : IsBundle ? "bundle"
            : IsMesh ? "mesh" : null;

        private void UpdateShare() => share.IsEnabled = cancellation == null && loaded && LoadedShareType() != null;

        private async Task ShareLoadedAsync()
        {
            string type = LoadedShareType();
            if (type == null || !loaded)
                return;
            long id = IsBundle && loadedBundle != null ? loadedBundle.Id
                : long.TryParse((input.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long typed) ? typed : 0;
            if (id <= 0)
                return;
            // A mesh is shared with the texture entered for it, if any.
            long? textureId = type == "mesh" && long.TryParse((textureInput.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long texture) && texture > 0
                ? texture : (long?)null;
            string what = type == "item" ? "catalog item" : type;
            string ids = textureId.HasValue ? "Only its Roblox IDs (mesh " + id + ", texture " + textureId.Value + ") are shared."
                : "Only its Roblox ID (" + id + ") is shared.";
            if (!await Dialogs.Ask(this, "Share " + (loadedName ?? what + " " + id) + (textureId.HasValue ? " with its texture" : "") +
                " on the Community tab?\n\nEveryone using RoPorter will see it. " + ids, "Share"))
                return;
            share.IsEnabled = false;
            try
            {
                SharedEntry entry = await new RobloxModelDownloader(Http, DefaultApiBase).ShareAsync(type, id, CancellationToken.None, textureId);
                AppendLog("Shared " + entry.Name + " (" + what + " " + entry.Id + ") on the Community tab.");
                await Dialogs.Show(this, entry.Name + " is on the Community tab.", "Share");
            }
            catch (Exception error) when (error is HttpRequestException || error is ApiMessageException || error is TaskCanceledException ||
                error is Newtonsoft.Json.JsonException || error is FormatException)
            {
                await ShowErrorAsync("Could not share it: " + error.Message);
            }
            finally
            {
                UpdateShare();
            }
        }

        private void ShowShareInfo(string text)
        {
            shareInfo.Text = text;
            shareInfo.IsVisible = !string.IsNullOrEmpty(text);
        }

        // Loads the first page again, or with more, the next page after the cards shown.
        private async Task RefreshSharesAsync(bool more)
        {
            if (more && (shareLoading || shareNextPage == null))
                return;
            shareCancellation?.Cancel();
            var current = shareCancellation = new CancellationTokenSource();
            shareLoading = true;
            int page = more ? shareNextPage.Value : 0;
            if (!more)
            {
                shareCards.Children.Clear();
                foreach (Bitmap old in sharePictures)
                    old.Dispose();
                sharePictures.Clear();
                shareMore.IsVisible = false;
            }
            ShowShareInfo("Loading...");
            var downloader = new RobloxModelDownloader(Http, DefaultApiBase);
            try
            {
                string search = (shareSearch.Text ?? "").Trim();
                var (entries, next) = await downloader.GetSharesAsync(ShareFilterTypes[Math.Max(0, shareFilter.SelectedIndex)],
                    shareSort.SelectedIndex == 1, page, current.Token, search);
                if (current != shareCancellation)
                    return;
                shareNextPage = next;
                shareMore.IsVisible = next != null;
                var cards = entries.Select(entry => (Entry: entry, Picture: new Image { Stretch = Stretch.Uniform, Width = 48, Height = 48 })).ToList();
                foreach (var card in cards)
                    shareCards.Children.Add(ShareCard(card.Entry, card.Picture));
                int shown = shareCards.Children.Count;
                ShowShareInfo(shown > 0 ? "" : search.Length > 0 ? "Nothing shared matches \"" + search + "\"." : "Nothing has been shared here yet.");
                await LoadSharePicturesAsync(downloader, cards, current);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is HttpRequestException || error is ApiMessageException || error is TaskCanceledException ||
                error is Newtonsoft.Json.JsonException || error is FormatException)
            {
                if (current == shareCancellation)
                    ShowShareInfo("The shared list could not be loaded: " + error.Message);
            }
            finally
            {
                if (current == shareCancellation)
                    shareLoading = false;
            }
        }

        // Each card's picture: the avatar's headshot or the outfit's, bundle's or item's thumbnail, a few at a time.
        private async Task LoadSharePicturesAsync(RobloxModelDownloader downloader, List<(SharedEntry Entry, Image Picture)> cards, CancellationTokenSource current)
        {
            using (var limit = new SemaphoreSlim(4))
                await Task.WhenAll(cards.Select(async card =>
                {
                    string key = card.Entry.Type + ":" + card.Entry.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                        (card.Entry.TextureId?.ToString(CultureInfo.InvariantCulture) ?? "");
                    byte[] bytes;
                    if (!shareThumbnails.TryGetValue(key, out bytes))
                    {
                        await limit.WaitAsync(current.Token);
                        try
                        {
                            bytes = card.Entry.Type == "mesh" ? await RenderSharedMeshAsync(downloader, card.Entry, current.Token)
                                : card.Entry.Type == "avatar" ? await downloader.DownloadThumbnailAsync(card.Entry.Id, true, current.Token)
                                : card.Entry.Type == "outfit" ? await downloader.DownloadOutfitThumbnailAsync(card.Entry.Id, current.Token)
                                : card.Entry.Type == "bundle" ? await downloader.DownloadBundleThumbnailAsync(card.Entry.Id, current.Token)
                                : await downloader.DownloadThumbnailAsync(card.Entry.Id, false, current.Token);
                            shareThumbnails[key] = bytes;
                        }
                        catch (Exception error) when (!(error is OperationCanceledException))
                        {
                            return;
                        }
                        finally
                        {
                            limit.Release();
                        }
                    }
                    // A card no longer shown (the list was loaded again) gets no picture.
                    if (!shareCards.Children.Any(c => ReferenceEquals(c.Tag, card.Entry)))
                        return;
                    try
                    {
                        Bitmap picture = ToBitmap(bytes);
                        sharePictures.Add(picture);
                        card.Picture.Source = picture;
                        card.Picture.Width = card.Picture.Height = 150;
                    }
                    catch (Exception)
                    {
                        // Not an image Avalonia can show: the type's icon stays.
                    }
                }));
        }

        private static int ShareTypeIndex(string type) =>
            type == "avatar" ? 0 : type == "item" ? 1 : type == "bundle" ? BundleType : type == "outfit" ? OutfitType : type == "mesh" ? MeshType : -1;

        // A shared mesh's card picture: nobody uploads one, so it is drawn here from the mesh and its texture (both kept in
        // the content cache), the way the mesh preview draws it.
        private static async Task<byte[]> RenderSharedMeshAsync(RobloxModelDownloader downloader, SharedEntry entry, CancellationToken cancellation) =>
            await Task.Run(async () =>
            {
                var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                ModelContents contents = ModelFileBuilder.FromMesh(entry.Id, entry.TextureId, entry.Name);
                WavefrontModel model = await ModelFileBuilder.BuildAsync(contents, (id, cancel) => downloader.DownloadAssetAsync(id, cancel), textures, null, cancellation);
                using (Picture picture = MeshRender.Render(model, textures, 256))
                    return picture?.Encode(PictureFormat.Png) ?? throw new System.IO.InvalidDataException("The mesh has nothing to draw.");
            }, cancellation);

        // A card like the Library's: the picture, the name, its type and ID, and its like button. Clicking it
        // loads it on the Export page, its like button likes it, and right-clicking copies its ID or reports it.
        private Control ShareCard(SharedEntry entry, Image picture)
        {
            int type = ShareTypeIndex(entry.Type);
            picture.Source = type >= 0 ? typeBitmaps[type] : partIcon;
            string label = type >= 0 ? TypeNames[type] : entry.Type;

            var content = new StackPanel { Spacing = 4, Width = 150 };
            content.Children.Add(new Border
            {
                Width = 150, Height = 150, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), CornerRadius = new CornerRadius(4),
                Child = picture
            });
            content.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock
            {
                Text = label + " · " + entry.Id.ToString(CultureInfo.InvariantCulture) + (entry.TextureId.HasValue ? " + texture" : ""),
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis
            });
            // The like button: "♡ 3", or "♥ 4" once liked from this PC; clicking again takes the like back.
            var like = new Button { Padding = new Thickness(12, 5), FontSize = 15, MinWidth = 56, HorizontalContentAlignment = HorizontalAlignment.Center };
            void ShowLike() => like.Content = (likedShares.Contains(entry.Key) ? "♥ " : "♡ ") + entry.Likes.ToString(CultureInfo.CurrentCulture);
            ShowLike();
            like.Click += async (sender, args) =>
            {
                // Handled here, so the click doesn't also reach the card and open the Export page.
                args.Handled = true;
                bool liking = !likedShares.Contains(entry.Key);
                like.IsEnabled = false;
                try
                {
                    entry.Likes = await new RobloxModelDownloader(Http, DefaultApiBase).LikeShareAsync(entry.Key, liking, CancellationToken.None);
                    if (liking)
                        likedShares.Add(entry.Key);
                    else
                        likedShares.Remove(entry.Key);
                    SaveLikedShares();
                    ShowLike();
                }
                catch (Exception error) when (error is HttpRequestException || error is ApiMessageException || error is TaskCanceledException ||
                    error is Newtonsoft.Json.JsonException)
                {
                    await ShowErrorAsync("The like didn't go through: " + error.Message);
                }
                finally
                {
                    like.IsEnabled = true;
                }
            };
            like.HorizontalAlignment = HorizontalAlignment.Right;
            content.Children.Add(like);

            var copy = new MenuItem { Header = "Copy ID" };
            copy.Click += async (sender, args) =>
            {
                if (Clipboard != null)
                    await Clipboard.SetTextAsync(entry.Id.ToString(CultureInfo.InvariantCulture));
            };
            var report = new MenuItem { Header = "Report..." };
            report.Click += async (sender, args) => await ReportSharedAsync(entry);

            var card = new Button
            {
                Content = content, Margin = new Thickness(6), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Tag = entry,
                IsEnabled = type >= 0, ContextMenu = new ContextMenu { ItemsSource = new[] { copy, report } }
            };
            card.Click += (sender, args) => UseShared(entry);
            return card;
        }

        // Loads a shared entry on the Export page, where its preview loads like a typed ID.
        private void UseShared(SharedEntry entry)
        {
            int type = ShareTypeIndex(entry.Type);
            if (type < 0 || cancellation != null)
                return;
            // A format that can't take this type (Deadlock takes only avatars, bundles and outfits) falls back to GLB.
            if (!Supported(type, Formats[Math.Max(0, exportFormat.SelectedIndex)]))
                exportFormat.SelectedIndex = 0;
            exportType.SelectedIndex = type;
            // A mesh's texture goes in the texture box (emptied for one shared without).
            if (type == MeshType)
                textureInput.Text = entry.TextureId?.ToString(CultureInfo.InvariantCulture) ?? "";
            input.Text = entry.Id.ToString(CultureInfo.InvariantCulture);
            ShowPage(ExportPageIndex);
        }

        private async Task ReportSharedAsync(SharedEntry entry)
        {
            if (!await Dialogs.Ask(this, "Report " + entry.Name + "?\n\nReport entries that are offensive, inappropriate or spam. " +
                "Entries several people report are hidden until they are reviewed.", "Report"))
                return;
            try
            {
                await new RobloxModelDownloader(Http, DefaultApiBase).ReportShareAsync(entry.Key, CancellationToken.None);
                await Dialogs.Show(this, "Thanks, it was reported.", "Report");
            }
            catch (Exception error) when (error is HttpRequestException || error is ApiMessageException || error is TaskCanceledException ||
                error is Newtonsoft.Json.JsonException)
            {
                await ShowErrorAsync("The report didn't go through: " + error.Message);
            }
        }
    }
}
