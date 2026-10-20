using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Rbx2Source.Export
{
    public sealed class AssetAccessException : Exception
    {
        public AssetAccessException(string message) : base(message) { }
    }

    // RoPorter's API is down for maintenance (it answers HTTP 503). This stops what RoPorter was doing (no step treats it as
    // a missing optional file), and only its message is shown.
    public sealed class ApiMessageException : Exception
    {
        public const string Maintenance = "Server under maintenance. Please check back later.";

        public ApiMessageException(string message) : base(message) { }
    }

    // Every request goes to RoPorter's API (api.ocublox.tv, or a local copy of it): its own routes, and Roblox CDN files (3D
    // models, textures, thumbnails) through its /v1/cdn route. RoPorter never contacts Roblox directly.
    public sealed class RobloxModelDownloader
    {
        public const string DefaultApi = "https://api.ocublox.tv";

        private readonly HttpClient client;
        private readonly string apiBase;

        public RobloxModelDownloader(HttpClient client, string apiBase = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.apiBase = Api(apiBase);
        }

        private static string Api(string apiBase) => NormalizeApiBase(apiBase) ?? DefaultApi;

        // The API's address. HTTPS is required, except for loopback addresses so a locally running copy of the API can be
        // used during development.
        public static string NormalizeApiBase(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("The API base URL must be an HTTPS address (or http://localhost) without credentials, a query, or a fragment.");
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        public static string GetRequestUrl(long id, bool avatar, string apiBase = null)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException(nameof(id), "Roblox IDs must be positive.");
            return Api(apiBase) + "/v1/" + (avatar ? "users/avatar-3d?userId=" : "assets-thumbnail-3d?assetId=") +
                id.ToString(CultureInfo.InvariantCulture);
        }

        // 2D thumbnail for the preview: the avatar's headshot or the catalog item's icon (Roblox's thumbnail JSON). With
        // background, the API's headshot is asked for with its background (for the Workshop icon).
        public static string GetThumbnailUrl(long id, bool avatar, string apiBase = null, bool background = false)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException(nameof(id), "Roblox IDs must be positive.");
            return Api(apiBase) + (avatar ? "/v1/users/headshot?userId=" : "/v1/catalog/thumbnail?assetId=") +
                id.ToString(CultureInfo.InvariantCulture) + (avatar && background ? "&background=true" : "");
        }

        // Downloads the thumbnail image. Only completed thumbnails hosted on Roblox's CDN are accepted.
        public Task<byte[]> DownloadThumbnailAsync(long id, bool avatar, CancellationToken cancellation, bool background = false) =>
            DownloadThumbnailAsync(new Uri(GetThumbnailUrl(id, avatar, apiBase, background)), cancellation);

        // The avatar's full-body render (the API's /v1/users/avatar route), for the Workshop preview image.
        public Task<byte[]> DownloadAvatarRenderAsync(long userId, CancellationToken cancellation)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId), "Roblox IDs must be positive.");
            return DownloadThumbnailAsync(new Uri(apiBase + "/v1/users/avatar?userId=" + userId.ToString(CultureInfo.InvariantCulture)), cancellation);
        }

        // The user's profile background from the API's /v1/users/background route: its image, or else its colour. Both are
        // null when the user has not set one.
        public async Task<KeyValuePair<byte[], Color?>> DownloadProfileBackgroundAsync(long userId, CancellationToken cancellation)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId), "Roblox IDs must be positive.");
            JObject result = JObject.Parse(Encoding.UTF8.GetString(await DownloadAsync(new Uri(apiBase + "/v1/users/background?userId=" +
                userId.ToString(CultureInfo.InvariantCulture)), cancellation).ConfigureAwait(false)));
            Color? color = null;
            string hex = result.Value<string>("color");
            if (hex != null && Regex.IsMatch(hex, @"\A#[0-9A-Fa-f]{6}\z"))
                color = Color.FromArgb(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
            string reference = result.Value<string>("imageUrl");
            if (string.IsNullOrWhiteSpace(reference))
                return new KeyValuePair<byte[], Color?>(null, color);
            // The image is an asset on the API ("/v1/asset?id=..."), or a Roblox CDN file (fetched through the API).
            byte[] bytes = await DownloadAsync(Hosted(reference, "The profile background"), cancellation).ConfigureAwait(false);
            try
            {
                using (var stream = new MemoryStream(bytes))
                using (Image.FromStream(stream, false, true)) { }
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("The profile background is not an image.", error);
            }
            return new KeyValuePair<byte[], Color?>(bytes, color);
        }

        private async Task<byte[]> DownloadThumbnailAsync(Uri thumbnailUrl, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                byte[] json = await DownloadAsync(thumbnailUrl, cancellation).ConfigureAwait(false);
                JObject result = JObject.Parse(Encoding.UTF8.GetString(json));
                if (result["data"] is JArray batch)
                    result = batch.First as JObject ?? throw new InvalidDataException("Roblox returned no thumbnail.");
                string state = result.Value<string>("state");
                if (state == "Pending" && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), cancellation).ConfigureAwait(false);
                    continue;
                }
                if (state != "Completed")
                    throw new InvalidDataException("The thumbnail is unavailable (state: " + (state ?? "missing") + ").");
                byte[] bytes = await DownloadAsync(Hosted(result.Value<string>("imageUrl"), "The thumbnail"), cancellation).ConfigureAwait(false);
                try
                {
                    using (var stream = new MemoryStream(bytes))
                    using (Image decoded = Image.FromStream(stream, false, true))
                        if (decoded.Width <= 0 || decoded.Height <= 0)
                            throw new InvalidDataException("Roblox returned an invalid thumbnail.");
                }
                catch (ArgumentException error)
                {
                    throw new InvalidDataException("Roblox returned a thumbnail that is not an image.", error);
                }
                return bytes;
            }
        }

        // The ID typed in (the input box takes digits only).
        public Task<long> ResolveIdAsync(string input, bool avatar, CancellationToken cancellation)
        {
            if (long.TryParse(input?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0)
                return Task.FromResult(id);
            throw new ArgumentException(avatar ? "Enter a positive user ID." : "Enter a positive item ID.");
        }

        public static Uri ResolveCdnReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new InvalidDataException("Roblox returned a missing model or texture reference.");
            if (Uri.TryCreate(reference, UriKind.Absolute, out Uri uri))
            {
                if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)
                    || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                    throw new InvalidDataException("Model files must use HTTPS URLs hosted on rbxcdn.com.");
                return uri;
            }
            // Plain content hashes, lifetime-prefixed hashes (30DAY-...), and labelled render outputs (30DAY-Avatar-...-Obj).
            if (!Regex.IsMatch(reference, @"\A(?:[1-9][0-9]*DAY-)?(?:[A-Za-z]+-)?[0-9a-fA-F]{32,64}(?:-[A-Za-z]+)?\z"))
                throw new InvalidDataException("Unrecognized Roblox CDN hash: " + reference);
            int shard = 31;
            foreach (char character in reference)
                shard ^= character;
            return new Uri("https://t" + shard % 8 + ".rbxcdn.com/" + reference);
        }

        private bool IsApi(Uri uri)
        {
            var api = new Uri(apiBase + "/");
            return uri.Scheme == api.Scheme && uri.Authority == api.Authority;
        }

        private static bool IsRobloxCdn(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase);

        // Where a request really goes: the API's own routes as they are, and Roblox CDN files through its /v1/cdn route. Any
        // other host is refused, so nothing is ever requested from Roblox directly.
        private Uri ToApi(Uri uri)
        {
            if (IsApi(uri))
                return uri;
            if (IsRobloxCdn(uri))
                return new Uri(apiBase + "/v1/cdn?url=" + Uri.EscapeDataString(uri.AbsoluteUri));
            throw new InvalidDataException("RoPorter only downloads through its API, not from " + uri.Host + ".");
        }

        // A file the API points to: one of its own (absolute, or relative like "/v1/asset?id=...") or a Roblox CDN file.
        private Uri Hosted(string reference, string what)
        {
            if (string.IsNullOrWhiteSpace(reference) || !Uri.TryCreate(new Uri(apiBase + "/"), reference, out Uri uri) || !(IsApi(uri) || IsRobloxCdn(uri)))
                throw new InvalidDataException(what + " is not hosted on the API or Roblox's CDN.");
            return uri;
        }

        // Requests the API; the "Roblox-AssetFormat" header asks its asset route for a particular build of an asset. The API
        // passes on Roblox's rate limit (HTTP 429), so a limited request is tried again a couple of times, after the wait
        // the API asks for (Retry-After) or 2 and then 4 seconds.
        private async Task<HttpResponseMessage> GetAsync(Uri uri, string assetFormat, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, ToApi(uri)))
                {
                    // Signed CDN locations do not need the format header; the API's asset route does.
                    if (assetFormat != null && !IsRobloxCdn(uri))
                        request.Headers.TryAddWithoutValidation("Roblox-AssetFormat", assetFormat);
                    HttpResponseMessage response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
                    if ((int)response.StatusCode != 429 || attempt >= RateLimitRetries)
                        return response;
                    TimeSpan? asked = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;
                    TimeSpan wait = asked.HasValue && asked.Value > TimeSpan.Zero
                        ? TimeSpan.FromSeconds(Math.Min(asked.Value.TotalSeconds, 15)) : TimeSpan.FromSeconds(2 << attempt);
                    response.Dispose();
                    await Task.Delay(wait, cancellation).ConfigureAwait(false);
                }
            }
        }

        private const int RateLimitRetries = 2;

        private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellation)
        {
            using (HttpResponseMessage response = await GetAsync(uri, null, cancellation).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw await FailureAsync(response, ToApi(uri)).ConfigureAwait(false);
                byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return await DecodeAsync(data, uri.Host, cancellation).ConfigureAwait(false);
            }
        }

        // The error for a failed API request. HTTP 503 means the API is down for maintenance, shown as just "Server under
        // maintenance. Please check back later." (ApiMessageException). Anything else reads "The API answered HTTP 404 (Not
        // Found) for /v1/cdn: <the API's error message>." (an AssetAccessException for an asset the API may not download).
        private static async Task<Exception> FailureAsync(HttpResponseMessage response, Uri uri, bool assetAccess = false)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return new ApiMessageException(ApiMessageException.Maintenance);
            JObject body = null;
            try
            {
                body = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException || error is HttpRequestException)
            {
            }
            string detail = (body?["errors"] as JArray)?.OfType<JObject>().Select(entry => entry["message"]?.ToString())
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? (body?["message"] ?? body?["error"])?.ToString();
            string message = "The API answered HTTP " + (int)response.StatusCode + (string.IsNullOrEmpty(response.ReasonPhrase) ? "" : " (" + response.ReasonPhrase + ")") +
                " for " + uri.AbsolutePath + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail.Trim().TrimEnd('.') + ".");
            return assetAccess ? new AssetAccessException(message) : (Exception)new HttpRequestException(message);
        }

        private static async Task<byte[]> DecodeAsync(byte[] data, string host, CancellationToken cancellation)
        {
            // CDN objects can be gzip files even when Content-Encoding is missing.
            for (int layer = 0; data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b; layer++)
            {
                if (layer >= 4)
                    throw new InvalidDataException("Roblox returned too many nested gzip layers from " + host + ".");
                try
                {
                    using (var input = new MemoryStream(data))
                    using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        await gzip.CopyToAsync(output, 81920, cancellation).ConfigureAwait(false);
                        data = output.ToArray();
                    }
                }
                catch (InvalidDataException error)
                {
                    throw new InvalidDataException("Roblox returned invalid gzip data from " + host + ".", error);
                }
                cancellation.ThrowIfCancellationRequested();
            }
            if (data.Length == 0)
                throw new InvalidDataException("Roblox returned an empty file.");
            return data;
        }

        // Downloads an asset from the API's /v1/asset route. A redirect may only lead to the API or to a Roblox CDN file, which
        // is fetched through the API too.
        private async Task<byte[]> GetAssetBytesAsync(Uri uri, string assetFormat, CancellationToken cancellation)
        {
            for (int hop = 0; hop < 5; hop++)
            {
                Uri requested = ToApi(uri);
                using (HttpResponseMessage response = await GetAsync(uri, assetFormat, cancellation).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400 && response.Headers.Location != null)
                    {
                        uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(requested, response.Headers.Location);
                        if (!IsApi(uri) && !IsRobloxCdn(uri))
                            throw new InvalidDataException("An asset download redirected to an untrusted host: " + uri.Host);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw await FailureAsync(response, requested, assetAccess: status == 401 || status == 403).ConfigureAwait(false);
                    byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return await DecodeAsync(data, uri.Host, cancellation).ConfigureAwait(false);
                }
            }
            throw new InvalidDataException("An asset download redirected too many times.");
        }

        private Task<byte[]> DownloadAssetFormatAsync(long assetId, string assetFormat, CancellationToken cancellation) =>
            GetAssetBytesAsync(new Uri(apiBase + "/v1/asset/?id=" + assetId.ToString(CultureInfo.InvariantCulture)), assetFormat, cancellation);

        // Without a format, Roblox serves a legacy build of avatar assets. For dynamic heads that build is a
        // SpecialMesh whose mesh has no skeleton or FACS data, so "avatar_meshpart_head" must be requested.
        public async Task<byte[]> DownloadAssetAsync(long assetId, CancellationToken cancellation, string assetFormat = null)
        {
            if (assetFormat != null)
            {
                try
                {
                    return await DownloadAssetFormatAsync(assetId, assetFormat, cancellation).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    // Assets with no build in this format fail (Roblox answers HTTP 500); use the default build.
                }
            }
            return await DownloadAssetFormatAsync(assetId, null, cancellation).ConfigureAwait(false);
        }

        public const string HeadAssetFormat = "avatar_meshpart_head";

        private const string HeadTextureKey = "__dynamic_head_texture__";

        // Reads each worn accessory's model to learn its attachment and whether it is layered clothing. Accessories
        // whose model cannot be read fall back to the defaults for their asset type.
        private async Task<List<AccessoryInfo>> GetAccessoriesAsync(List<JObject> assets, IProgress<string> progress,
            CancellationToken cancellation)
        {
            var accessories = assets.Where(asset => AccessoryInfo.IsAccessoryType(asset["assetType"]?.Value<int?>("id") ?? 0) ||
                    asset["assetType"]?.Value<int?>("id") == AccessoryInfo.GearType)
                .Select(asset => AccessoryInfo.FromType(asset.Value<long>("id"), asset.Value<string>("name") ?? "accessory",
                    asset["assetType"].Value<int>("id")))
                .ToList();
            if (accessories.Count == 0)
                return accessories;
            progress?.Report("Reading " + accessories.Count + " accessories...");
            using (var limit = new SemaphoreSlim(4))
            {
                await Task.WhenAll(accessories.Select(async accessory =>
                {
                    await limit.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        // Gear is a Tool held at the right-hand grip; its model file has nothing to refine.
                        if (accessory.IsGear)
                            return;
                        byte[] data = await DownloadAssetAsync(accessory.Id, cancellation, AccessoryAssetFormat).ConfigureAwait(false);
                        accessory.Apply(RobloxModelFile.ReadAccessory(data));
                        // Layered clothing keeps its mesh's skin weights; every accessory's mesh also tells which rendered
                        // group is that accessory (see AvatarRig.MatchAccessories).
                        if (accessory.MeshId.HasValue)
                        {
                            try
                            {
                                accessory.Mesh = RobloxMesh.Parse(await DownloadAssetAsync(accessory.MeshId.Value, cancellation).ConfigureAwait(false));
                            }
                            catch (Exception error) when (!(error is OperationCanceledException) && !(error is OutOfMemoryException) &&
                                !(error is ApiMessageException))
                            {
                                progress?.Report(accessory.Name + ": its mesh could not be read (" + error.Message + ")" +
                                    (accessory.Layered ? "; it will be auto-weighted." : "; it is matched by position."));
                            }
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException || error is InvalidDataException ||
                        error is NotSupportedException || error is AssetAccessException)
                    {
                        progress?.Report(accessory.Name + ": using defaults for its type (" + error.Message + ").");
                    }
                    finally
                    {
                        limit.Release();
                    }
                })).ConfigureAwait(false);
            }
            return accessories;
        }

        public const string AccessoryAssetFormat = "avatar_meshpart_accessory";

        // The avatar's Mood (asset type 78): an Animation whose keyframe sets FACS control values, the face's idle
        // expression. Returns null when the avatar has no mood or it can't be read.
        private async Task<Dictionary<string, float>> GetMoodAsync(List<JObject> assets, IProgress<string> progress, CancellationToken cancellation)
        {
            JObject asset = assets.FirstOrDefault(a => a["assetType"]?.Value<int?>("id") == 78);
            if (asset == null)
                return null;
            try
            {
                byte[] data = await DownloadAssetAsync(asset.Value<long>("id"), cancellation).ConfigureAwait(false);
                long? animation = MoodAnimation.ReadAnimationId(data);
                if (animation.HasValue)
                    data = await DownloadAssetAsync(animation.Value, cancellation).ConfigureAwait(false);
                Dictionary<string, float> pose = MoodAnimation.ReadFacePose(data);
                if (pose.Count == 0)
                {
                    // Roblox's "Default Mood" is an empty animation: the face simply rests neutral.
                    progress?.Report("Mood " + asset.Value<string>("name") + " sets no expression (the face rests neutral), so there is no mood to apply.");
                    return null;
                }
                return pose;
            }
            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException ||
                error is NotSupportedException || error is AssetAccessException || error is EndOfStreamException)
            {
                progress?.Report("Mood " + asset.Value<string>("name") + " could not be read (" + error.Message + "); no Default expression was added.");
                return null;
            }
        }

        private async Task<FaceShapes> BuildFaceAsync(List<JObject> assets, WavefrontModel model, AvatarRig rig,
            Dictionary<string, byte[]> localTextures, bool faceTracking, bool vrchat, IProgress<string> progress, CancellationToken cancellation)
        {
            string headGroup = rig.HeadGroup;
            if (headGroup == null)
                throw new InvalidDataException("the head could not be identified in the render");
            JObject head = assets.FirstOrDefault(asset => asset["assetType"]?.Value<int?>("id") == 79)
                ?? assets.FirstOrDefault(asset => asset["assetType"]?.Value<int?>("id") == 17);
            if (head == null)
                throw new InvalidDataException("the avatar is not wearing a dynamic head");
            long headId = head.Value<long>("id");
            progress?.Report("Downloading dynamic head " + head.Value<string>("name") + " (" + headId + ")...");
            var references = RobloxModelFile.FindHeadAssets(await DownloadAssetAsync(headId, cancellation, HeadAssetFormat).ConfigureAwait(false));
            if (references.Key == null)
                throw new InvalidDataException("the head asset does not reference a mesh");
            RobloxMesh mesh = RobloxMesh.Parse(await DownloadAssetAsync(references.Key.Value, cancellation).ConfigureAwait(false));
            // Roblox serves its own dynamic heads' facial animation only in the "avatar_meshpart_head" build; without that
            // format it sends an older build of the head with a plain mesh.
            if (mesh.Facs == null)
                throw new InvalidDataException("this head (mesh " + mesh.Version + ") has no FACS facial animation data" +
                    (head["assetType"]?.Value<int?>("id") == 79
                        ? ". The API sent the head's older build: its /v1/asset route must pass the Roblox-AssetFormat header on to Roblox" : ""));
            progress?.Report("Building " + mesh.Facs.ControlNames.Count + " FACS face shapes...");
            Dictionary<string, float> mood = await GetMoodAsync(assets, progress, cancellation).ConfigureAwait(false);
            var shapes = FaceShapeBuilder.EvaluateShapes(mesh, faceTracking, vrchat, mood, cancellation);
            if (mood != null && shapes.Any(shape => shape.Key == FaceShapeBuilder.MoodShapeName))
                progress?.Report("Mood: \"" + FaceShapeBuilder.MoodShapeName + "\" shape key (on by default) from " + mood.Count + " FACS values.");

            byte[] texture = null;
            if (references.Value != null)
            {
                try
                {
                    texture = await DownloadAssetAsync(references.Value.Value, cancellation).ConfigureAwait(false);
                    using (var stream = new MemoryStream(texture))
                    using (Image.FromStream(stream, false, true)) { }
                }
                catch (Exception error) when (error is AssetAccessException || error is HttpRequestException ||
                    error is InvalidDataException || error is ArgumentException)
                {
                    progress?.Report("Head texture unavailable (" + error.Message + ").");
                    texture = null;
                }
            }
            FaceShapes face;
            if (texture != null)
            {
                // Swap in the neutral head mesh: the render may show the head with a mood expression already applied.
                FaceShapeBuilder.HeadFit fit = FaceShapeBuilder.Fit(mesh, model, headGroup, cancellation);
                localTextures[HeadTextureKey] = texture;
                face = FaceShapeBuilder.ReplaceHead(mesh, shapes, fit, model, rig, headGroup, "DynamicHeadMtl", HeadTextureKey);
            }
            else
            {
                progress?.Report("Face shapes were matched onto the rendered head, which may include a mood expression.");
                var notes = new List<string>();
                face = FaceShapeBuilder.Transfer(mesh, shapes, model, headGroup, notes, cancellation);
                foreach (string note in notes)
                    progress?.Report(note);
            }
            foreach (string layer in rig.HeadLayers.ToList())
                CopyFaceShapes(face, model, headGroup, layer);
            return face;
        }

        // Face makeup layers move with the face: each layer vertex takes the shape deltas of the nearest head vertex.
        private static void CopyFaceShapes(FaceShapes face, WavefrontModel model, string headGroup, string layer)
        {
            if (face == null)
                return;
            List<int> Positions(string group) => model.Triangles.Where(t => t.Group == group).SelectMany(t => t.Corners).Select(c => c.Position).Distinct().ToList();
            List<int> head = Positions(headGroup);
            if (head.Count == 0)
                return;
            foreach (int index in Positions(layer))
            {
                float[] p = model.Positions[index];
                int nearest = head[0];
                double best = double.MaxValue;
                foreach (int candidate in head)
                {
                    float[] q = model.Positions[candidate];
                    double d = (p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]) + (p[2] - q[2]) * (p[2] - q[2]);
                    if (d < best)
                    {
                        best = d;
                        nearest = candidate;
                    }
                }
                foreach (FaceShape shape in face.Shapes)
                    if (shape.Deltas.TryGetValue(nearest, out float[] delta))
                        shape.Deltas[index] = delta;
            }
            face.Layers.Add(layer);
        }

        // Roblox packs PBR as R = metalness, G = roughness, B = emissive mask; glTF wants G = roughness, B = metalness.
        private static void PackMetallicRoughness(string source, string destination)
        {
            using (var image = new Bitmap(source))
            using (var output = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
            {
                var rectangle = new Rectangle(0, 0, image.Width, image.Height);
                BitmapData input = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData result = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    var packed = new byte[result.Stride];
                    for (int y = 0; y < image.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(input.Scan0 + y * input.Stride, row, 0, row.Length);
                        for (int x = 0; x < image.Width; x++)
                        {
                            // Bitmap bytes are BGRA / BGR.
                            byte metalness = row[x * 4 + 2], roughness = row[x * 4 + 1];
                            packed[x * 3] = metalness;
                            packed[x * 3 + 1] = roughness;
                            packed[x * 3 + 2] = 255;
                        }
                        System.Runtime.InteropServices.Marshal.Copy(packed, 0, result.Scan0 + y * result.Stride, result.Stride);
                    }
                }
                finally
                {
                    image.UnlockBits(input);
                    output.UnlockBits(result);
                }
                output.Save(destination, ImageFormat.Png);
            }
        }

        // Roblox's renderer writes a black emissive map for SurfaceAppearances without emission.
        private static bool IsBlack(string path)
        {
            using (var image = new Bitmap(path))
            {
                var rectangle = new Rectangle(0, 0, image.Width, image.Height);
                BitmapData data = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    for (int y = 0; y < image.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 0; x < row.Length; x += 4)
                            if (row[x] > 2 || row[x + 1] > 2 || row[x + 2] > 2)
                                return false;
                    }
                    return true;
                }
                finally
                {
                    image.UnlockBits(data);
                }
            }
        }

        // Some render textures (layered clothing, hair) carry about 40% alpha over every texel although Roblox draws them
        // solid; read as transparency, the whole accessory turns see-through. A texture with no solid texels is treated that
        // way. Real translucent or cutout textures (hair cards, glasses) always have solid texels.
        private static bool NeverOpaque(string path)
        {
            using (var image = new Bitmap(path))
            {
                if (!Image.IsAlphaPixelFormat(image.PixelFormat))
                    return false;
                BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    long opaque = 0, partial = 0;
                    for (int y = 0; y < image.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 3; x < row.Length; x += 4)
                        {
                            if (row[x] >= 250)
                                opaque++;
                            else if (row[x] > 5)
                                partial++;
                        }
                    }
                    return partial > 0 && opaque < 0.01 * (opaque + partial);
                }
                finally
                {
                    image.UnlockBits(data);
                }
            }
        }

        // Keeps the colour and makes every texel solid, except fully clear ones, which stay cut out.
        private static void Solidify(string source, string destination)
        {
            using (var image = new Bitmap(source))
            using (var output = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
            {
                var rectangle = new Rectangle(0, 0, image.Width, image.Height);
                BitmapData input = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData result = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    for (int y = 0; y < image.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(input.Scan0 + y * input.Stride, row, 0, row.Length);
                        for (int x = 3; x < row.Length; x += 4)
                            row[x] = row[x] > 5 ? (byte)255 : (byte)0;
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, result.Scan0 + y * result.Stride, row.Length);
                    }
                }
                finally
                {
                    image.UnlockBits(input);
                    output.UnlockBits(result);
                }
                output.Save(destination, ImageFormat.Png);
            }
        }

        private static void CompositeOverColor(string source, string destination, float[] color)
        {
            using (var image = new Bitmap(source))
            using (var output = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.FromArgb(255, (int)Math.Round(color[0] * 255), (int)Math.Round(color[1] * 255), (int)Math.Round(color[2] * 255)));
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
                output.Save(destination, ImageFormat.Png);
            }
        }

        // The account's username (not the display name): from the API's /v1/users route.
        public static string GetUserUrl(long userId, string apiBase = null)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId), "Roblox IDs must be positive.");
            return Api(apiBase) + "/v1/users?userId=" + userId.ToString(CultureInfo.InvariantCulture);
        }

        // The avatar's worn items (Roblox's avatar details JSON, with its "assets" list) from the API's /v1/users/avatar-details
        // route.
        public static string GetAvatarDetailsUrl(long userId, string apiBase = null)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId), "Roblox IDs must be positive.");
            return Api(apiBase) + "/v1/users/avatar-details?userId=" + userId.ToString(CultureInfo.InvariantCulture);
        }

        // The last item list received for each user, kept on this PC (%LOCALAPPDATA%\RoPorter\avatars\<user ID>.json).
        private static readonly string SavedAvatars = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoPorter", "avatars");

        // The avatar's worn items. Roblox rate-limits the API's requests for them (HTTP 429), so each list received is kept,
        // and when the API can't answer, the one saved by an earlier export of the same user is used instead. Items worn since
        // then are missing from it; items no longer worn simply match nothing in the render.
        private async Task<List<JObject>> GetAvatarAssetsAsync(long userId, IProgress<string> progress, CancellationToken cancellation)
        {
            string saved = Path.Combine(SavedAvatars, userId.ToString(CultureInfo.InvariantCulture) + ".json");
            string json;
            try
            {
                json = Encoding.UTF8.GetString(await DownloadAsync(new Uri(GetAvatarDetailsUrl(userId, apiBase)), cancellation).ConfigureAwait(false));
                JObject.Parse(json);
                try
                {
                    Directory.CreateDirectory(SavedAvatars);
                    File.WriteAllText(saved, json, new UTF8Encoding(false));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
            catch (Exception error) when (error is HttpRequestException || error is JsonException)
            {
                string fallback = null;
                DateTime when = default;
                try
                {
                    if (File.Exists(saved))
                    {
                        fallback = File.ReadAllText(saved);
                        when = File.GetLastWriteTime(saved);
                    }
                }
                catch (Exception readError) when (readError is IOException || readError is UnauthorizedAccessException)
                {
                }
                if (fallback == null)
                    throw;
                json = fallback;
                progress?.Report("Avatar details unavailable (" + error.Message + "); using the item list saved by an earlier export on " +
                    when.ToString("g", CultureInfo.CurrentCulture) + ".");
            }
            return (JObject.Parse(json)["assets"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        }

        // Roblox usernames are 3-20 letters, digits, and underscores, so they are safe as file and Source model names.
        public async Task<string> GetUsernameAsync(long userId, CancellationToken cancellation)
        {
            JObject user = JObject.Parse(Encoding.UTF8.GetString(await DownloadAsync(new Uri(GetUserUrl(userId, apiBase)), cancellation).ConfigureAwait(false)));
            string name = user.Value<string>("name")?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException("Roblox returned no username for user " + userId + ".");
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_]{3,20}\z"))
                throw new InvalidDataException("Roblox returned an invalid username for user " + userId + ".");
            long? returned = user.Value<long?>("id");
            if (returned != null && returned != userId)
                throw new InvalidDataException("Roblox returned a different user than " + userId + ".");
            return name;
        }

        // A catalog item's name made safe for file and Source model names: runs of anything other than letters, digits, and
        // underscores become one underscore ("Starwisp", "Classic_Fedora").
        private static string SafeName(string displayName, long itemId)
        {
            string name = Regex.Replace(displayName, @"[^A-Za-z0-9_]+", "_").Trim('_');
            if (name.Length > 64)
                name = name.Substring(0, 64).TrimEnd('_');
            if (name.Length == 0)
                throw new InvalidDataException("Roblox returned no usable name for item " + itemId + ".");
            // Windows reserves these as device names, so they cannot be folder or file names.
            if (Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase))
                name += "_" + itemId.ToString(CultureInfo.InvariantCulture);
            return name;
        }

        // The catalog item's details from the API's /v1/catalog route: its name and taxonomy (item type).
        private async Task<JObject> GetCatalogItemAsync(long itemId, CancellationToken cancellation)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId), "Roblox IDs must be positive.");
            JObject item = JObject.Parse(Encoding.UTF8.GetString(await DownloadAsync(new Uri(apiBase +
                "/v1/catalog?itemId=" + itemId.ToString(CultureInfo.InvariantCulture)), cancellation).ConfigureAwait(false)));
            if (item["data"] is JArray batch)
                item = batch.First as JObject ?? throw new InvalidDataException("Roblox returned no details for item " + itemId + ".");
            return item;
        }

        private static string DisplayName(JObject item, long itemId)
        {
            string name = item.Value<string>("name")?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException("Roblox returned no name for item " + itemId + ".");
            return name;
        }

        // The item type Roblox files it under ("Full Masks", "Head Accessories"): the most specific taxonomy entry.
        private static string ItemType(JObject item) =>
            (item["taxonomy"] as JArray)?.OfType<JObject>().Select(t => t.Value<string>("taxonomyName")?.Trim())
                .LastOrDefault(n => !string.IsNullOrEmpty(n));

        // The catalog item's name as shown on Roblox ("Living Art: User Ads").
        public async Task<string> GetCatalogDisplayNameAsync(long itemId, CancellationToken cancellation) =>
            DisplayName(await GetCatalogItemAsync(itemId, cancellation).ConfigureAwait(false), itemId);

        private Task<string> GetDescriptorUrlAsync(long id, bool avatar, IProgress<string> progress, CancellationToken cancellation) =>
            GetDescriptorUrlAsync(new Uri(GetRequestUrl(id, avatar, apiBase)), progress, cancellation);

        // A Roblox bundle (a body, a costume...): its name, its items in the avatar API's asset format, and the outfit that
        // Roblox renders the whole bundle as.
        public sealed class BundleInfo
        {
            public long Id;
            public string Name;
            public long OutfitId;
            public List<JObject> Assets = new List<JObject>();
        }

        // Bundle details (Roblox's catalog bundle JSON) from the API's /v1/bundle route.
        public static string GetBundleUrl(long bundleId, string apiBase = null)
        {
            if (bundleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(bundleId), "Roblox IDs must be positive.");
            return Api(apiBase) + "/v1/bundle?bundleId=" + bundleId.ToString(CultureInfo.InvariantCulture);
        }

        // The outfit's 3D render (the same JSON as avatar-3d), from the API's /v1/users/outfit-3d route.
        public static string GetOutfitRequestUrl(long outfitId, string apiBase = null) =>
            Api(apiBase) + "/v1/users/outfit-3d?outfitId=" + outfitId.ToString(CultureInfo.InvariantCulture);

        public async Task<BundleInfo> GetBundleAsync(long bundleId, CancellationToken cancellation)
        {
            JObject details = JObject.Parse(Encoding.UTF8.GetString(await DownloadAsync(new Uri(GetBundleUrl(bundleId, apiBase)), cancellation).ConfigureAwait(false)));
            var bundle = new BundleInfo { Id = bundleId, Name = details.Value<string>("name")?.Trim() };
            foreach (JObject item in (details["items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string type = item.Value<string>("type");
                if (type == "UserOutfit" && bundle.OutfitId == 0)
                    bundle.OutfitId = item.Value<long?>("id") ?? 0;
                else if (type == "Asset")
                {
                    int assetType = item["assetType"] is JObject typed ? typed.Value<int?>("id") ?? 0 : item.Value<int?>("assetType") ?? 0;
                    bundle.Assets.Add(new JObject { ["id"] = item["id"], ["name"] = item["name"], ["assetType"] = new JObject { ["id"] = assetType } });
                }
            }
            if (string.IsNullOrEmpty(bundle.Name))
                throw new InvalidDataException("Roblox returned no bundle " + bundleId + ".");
            if (bundle.OutfitId <= 0)
                throw new InvalidDataException("Bundle " + bundleId + " has no outfit for Roblox to render.");
            return bundle;
        }

        // The bundle's catalog picture (Roblox's bundle thumbnail JSON) from the API's /v1/bundle/thumbnail route.
        public Task<byte[]> DownloadBundleThumbnailAsync(long bundleId, CancellationToken cancellation) =>
            DownloadThumbnailAsync(new Uri(apiBase + "/v1/bundle/thumbnail?bundleId=" + bundleId.ToString(CultureInfo.InvariantCulture)), cancellation);

        private async Task<string> GetDescriptorUrlAsync(Uri requestUrl, IProgress<string> progress, CancellationToken cancellation)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                byte[] bytes = await DownloadAsync(requestUrl, cancellation).ConfigureAwait(false);
                JObject result = JObject.Parse(Encoding.UTF8.GetString(bytes));
                if (result["data"] is JArray batch)
                    result = batch.First as JObject ?? throw new InvalidDataException("Roblox returned no 3D thumbnail.");
                string state = result.Value<string>("state");
                if (state == "Completed")
                {
                    string url = result.Value<string>("imageUrl");
                    return ResolveCdnReference(url).AbsoluteUri;
                }
                if (state != "Pending")
                    throw new InvalidDataException("Roblox's 3D model is unavailable (state: " + (state ?? "missing") +
                        "). The item may be moderated, unsupported, or not renderable in 3D.");
                progress?.Report("Waiting for Roblox to render the model (" + (attempt + 1) + "/20)...");
                if (attempt < 19)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            }
            throw new TimeoutException("Roblox did not finish rendering the model. Try again later.");
        }

        private const string CatalogTextureKey = "__catalog_texture__";

        // Catalog items are built from the item's own model file (its mesh and colour texture), since the API has no
        // 3D render route for them.
        private async Task<WavefrontModel> BuildCatalogModelAsync(long id, Dictionary<string, byte[]> localTextures,
            IProgress<string> progress, CancellationToken cancellation)
        {
            progress?.Report("Downloading catalog item " + id + "...");
            RobloxModelFile.AccessoryModel item = RobloxModelFile.ReadAccessory(
                await DownloadAssetAsync(id, cancellation, AccessoryAssetFormat).ConfigureAwait(false));
            if (!item.MeshId.HasValue)
                throw new InvalidDataException("The catalog item does not reference a mesh.");
            progress?.Report("Downloading mesh " + item.MeshId.Value + "...");
            RobloxMesh mesh = RobloxMesh.Parse(await DownloadAssetAsync(item.MeshId.Value, cancellation).ConfigureAwait(false));

            var material = new ObjMaterial { Name = "Item", DoubleSided = item.DoubleSided };
            if (item.TextureId.HasValue)
            {
                try
                {
                    localTextures[CatalogTextureKey] = await DownloadAssetAsync(item.TextureId.Value, cancellation).ConfigureAwait(false);
                    material.Texture = CatalogTextureKey;
                }
                catch (Exception error) when (error is AssetAccessException || error is HttpRequestException || error is InvalidDataException)
                {
                    progress?.Report("Item texture unavailable (" + error.Message + "); exporting it untextured.");
                }
            }

            var model = new WavefrontModel();
            model.Materials.Add(material.Name, material);
            model.Positions.AddRange(mesh.Positions);
            model.Normals.AddRange(mesh.Normals);
            model.Texcoords.AddRange(mesh.Texcoords);
            foreach (int[] face in mesh.Faces)
            {
                cancellation.ThrowIfCancellationRequested();
                model.Triangles.Add(new ObjTriangle
                {
                    Material = material.Name,
                    Group = "Handle",
                    Corners = face.Select(index => new ObjCorner { Position = index, Texture = index, Normal = index }).ToArray()
                });
            }
            progress?.Report("Mesh " + mesh.Version + ": " + model.Triangles.Count + " triangles.");
            return model;
        }

        // Export scale: 1 is full size (Roblox's proportions at 0.28 m per stud; Garry's Mod's player size), down to MinimumScale.
        public const float MinimumScale = 0.1f;

        // Garry's Mod install used for Source exports; found through Steam when not set.
        public string GarrysModPath { get; set; }

        // The folder the last successful export was written to (for Garry's Mod, the addon folder).
        public string ExportFolder { get; private set; }

        public async Task<string> ExportAsync(long id, bool avatar, string descriptorUrl, string outputFolder,
            IProgress<string> progress, CancellationToken cancellation, ExportFormat format = ExportFormat.Glb, bool faceTracking = false,
            bool vrchatShapes = true, bool singleTexture = false, float scale = 1, bool connectJoints = false, BundleInfo bundle = null,
            bool applyMood = true, bool ownFolder = true, string modelFile = null)
        {
            // A model file (.rbxm / .rbxmx) exports like a catalog item: a prop named after the file.
            if (modelFile != null)
            {
                avatar = false;
                bundle = null;
            }
            // A bundle exports like an avatar wearing it: Roblox's render of the bundle's outfit, with its items as the assets.
            if (bundle != null)
            {
                avatar = true;
                id = bundle.Id;
            }
            if (format != ExportFormat.Glb && format != ExportFormat.Gltf && format != ExportFormat.Fbx && format != ExportFormat.GarrysMod &&
                format != ExportFormat.SourceFilmmaker)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (!(scale >= MinimumScale && scale <= 1))
                throw new ArgumentOutOfRangeException(nameof(scale), "The export scale must be between " + MinimumScale.ToString(CultureInfo.InvariantCulture) + " and 1.");
            // Both Source formats build the same Source model; Valve's player skeleton is read from Garry's Mod's files.
            bool source = format == ExportFormat.GarrysMod || format == ExportFormat.SourceFilmmaker;
            GarrysModInstall garrysMod = null;
            SfmInstall sfm = null;
            if (source)
            {
                garrysMod = GarrysModInstall.Find(GarrysModPath) ?? throw new InvalidDataException(format == ExportFormat.GarrysMod
                    ? "Garry's Mod was not found. Install it through Steam (its studiomdl compiles the model)."
                    : "Garry's Mod was not found. Install it through Steam: Valve's player skeleton is read from its files.");
                faceTracking = false;
            }
            if (format == ExportFormat.SourceFilmmaker)
                sfm = SfmInstall.Find() ?? throw new InvalidDataException("Source Filmmaker was not found. Install it through Steam (its studiomdl compiles the model).");
            if (modelFile == null)
                GetRequestUrl(id, avatar);
            cancellation.ThrowIfCancellationRequested();
            WavefrontModel model;
            JObject descriptor = null;
            var localTextures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            // A place (.rbxl / .rbxlx) exported to Garry's Mod becomes a map; in other formats it is one static model.
            ModelContents placeMap = null;
            if (modelFile != null)
            {
                progress?.Report("Reading " + Path.GetFileName(modelFile) + "...");
                ModelContents contents = ModelFileBuilder.Read(File.ReadAllBytes(modelFile));
                if (contents.IsPlace && format == ExportFormat.GarrysMod)
                {
                    placeMap = contents;
                    model = new WavefrontModel();
                }
                else
                    model = await ModelFileBuilder.BuildAsync(contents, (assetId, token) => DownloadAssetAsync(assetId, token),
                        localTextures, progress, cancellation).ConfigureAwait(false);
            }
            else if (!avatar && string.IsNullOrWhiteSpace(descriptorUrl))
            {
                model = await BuildCatalogModelAsync(id, localTextures, progress, cancellation).ConfigureAwait(false);
            }
            else
            {
                progress?.Report("Fetching Roblox's rendered 3D model...");
                string url = !string.IsNullOrWhiteSpace(descriptorUrl) ? descriptorUrl.Trim()
                    : bundle != null ? await GetDescriptorUrlAsync(new Uri(GetOutfitRequestUrl(bundle.OutfitId, apiBase)), progress, cancellation).ConfigureAwait(false)
                    : await GetDescriptorUrlAsync(id, avatar, progress, cancellation).ConfigureAwait(false);
                Uri descriptorUri = ResolveCdnReference(url);
                descriptor = JObject.Parse(Encoding.UTF8.GetString(await DownloadAsync(descriptorUri, cancellation).ConfigureAwait(false)));
                string objReference = descriptor.Value<string>("obj");
                string mtlReference = descriptor.Value<string>("mtl");
                if (objReference == null || mtlReference == null)
                    throw new InvalidDataException("The URL is not a Roblox OBJ/MTL 3D model descriptor. Copy the 3D request's imageUrl, not a preview image.");

                progress?.Report("Downloading geometry and materials...");
                string obj = Encoding.UTF8.GetString(await DownloadAsync(ResolveCdnReference(objReference), cancellation).ConfigureAwait(false));
                string mtl = Encoding.UTF8.GetString(await DownloadAsync(ResolveCdnReference(mtlReference), cancellation).ConfigureAwait(false));
                model = WavefrontModel.Parse(obj, mtl, cancellation);
            }
            AvatarRig rig = null;
            FaceShapes face = null;
            HashSet<string> gearGroups = null;
            if (avatar)
            {
                List<JObject> avatarAssets = null;
                List<AccessoryInfo> accessories = null;
                try
                {
                    avatarAssets = bundle != null ? bundle.Assets : await GetAvatarAssetsAsync(id, progress, cancellation).ConfigureAwait(false);
                    accessories = await GetAccessoriesAsync(avatarAssets, progress, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                {
                    progress?.Report("Avatar details unavailable (" + error.Message + "); accessories are weighted from their shape.");
                }
                progress?.Report("Building humanoid rig...");
                rig = AvatarRig.Build(model, accessories, cancellation, connectJoints);
                progress?.Report("Rig parts: " + rig.Describe());
                // Accessories whose MeshPart is DoubleSided are drawn from both sides; everything else is culled like Roblox.
                foreach (ObjTriangle triangle in model.Triangles)
                    if (rig.AccessoryGroups.TryGetValue(triangle.Group, out AccessoryInfo worn) && worn.DoubleSided)
                        model.Materials[triangle.Material].DoubleSided = true;
                // Gear (asset type 19) is in Roblox's render but is not part of the avatar. Garry's Mod keeps it as a bodygroup
                // that starts hidden; other formats leave it out.
                if (rig.HeldGroups.Count > 0)
                {
                    gearGroups = new HashSet<string>(rig.HeldGroups, StringComparer.Ordinal);
                    string names = string.Join(", ", gearGroups.Select(group =>
                        rig.AccessoryGroups.TryGetValue(group, out AccessoryInfo item) ? item.Name : group).Distinct());
                    if (source)
                        progress?.Report("Gear " + names + " is a \"gear\" bodygroup (hidden until it is switched on).");
                    else
                    {
                        model.Triangles.RemoveAll(triangle => gearGroups.Contains(triangle.Group));
                        gearGroups = null;
                        progress?.Report("Left out gear: " + names + ".");
                    }
                }
                try
                {
                    if (avatarAssets == null)
                        throw new InvalidDataException("the avatar's item list could not be downloaded");
                    face = await BuildFaceAsync(avatarAssets, model, rig, localTextures, faceTracking, vrchatShapes, progress, cancellation).ConfigureAwait(false);
                    progress?.Report("Added " + face.Shapes.Count + " face shape keys.");
                    // Without the mood the face rests neutral: its "Default" shape key starts at 0 (and Source models don't
                    // bake it in).
                    FaceShape mood = face.Shapes.FirstOrDefault(shape => shape.Name == FaceShapeBuilder.MoodShapeName);
                    if (mood != null && !applyMood)
                    {
                        mood.Weight = 0;
                        progress?.Report("Mood expression not applied; the face is neutral.");
                    }
                    if (faceTracking)
                    {
                        var unified = new HashSet<string>(FaceShapeBuilder.UnifiedExpressionNames, StringComparer.Ordinal);
                        progress?.Report("Face tracking: " + face.Shapes.Count(shape => unified.Contains(shape.Name)) +
                            " Unified Expressions shapes for VRCFaceTracking.");
                    }
                }
                catch (Exception error) when (error is InvalidDataException || error is NotSupportedException ||
                    error is AssetAccessException || error is HttpRequestException || error is JsonException)
                {
                    progress?.Report("Face shape keys skipped: " + error.Message);
                    // Roblox renders the face with its mood, and without the head's own mesh it can't be undone.
                    progress?.Report("The face stays as Roblox rendered it" + (applyMood ? "." : ", with its mood: Apply mood expression only works when the face shapes are built."));
                    if (faceTracking)
                        progress?.Report("Face tracking support needs a dynamic head with facial animation (FACS); none was added.");
                }
                if (face != null)
                    foreach (var pair in face.FaceBones)
                        rig.SetFaceBone(pair.Key, pair.Value);
                rig.ApplyTPose(model);
                rig.AnchorHelperBones(model);
                progress?.Report("Posed in a T-pose with a full humanoid skeleton (" + AvatarRig.BoneNames.Length + " bones).");
            }
            // Face makeup layers (extra copies of the head) are decals: their texture's transparency is kept as it is.
            var headLayers = new HashSet<string>(rig?.HeadLayers ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var decalMaterials = new HashSet<string>(model.Triangles.Where(t => headLayers.Contains(t.Group)).Select(t => t.Material), StringComparer.Ordinal);
            var bodyGroups = new HashSet<string>(rig != null ? rig.Parts.Keys.Where(g => !headLayers.Contains(g)) :
                model.Triangles.Select(t => t.Group).Where(g => g.StartsWith("Player", StringComparison.OrdinalIgnoreCase)), StringComparer.Ordinal);
            // Body textures (clothing composites) and overlay materials show the part colour through transparent texels.
            var overlayMaterials = new HashSet<string>(model.Triangles.Where(t => bodyGroups.Contains(t.Group)).Select(t => t.Material)
                .Concat(model.Materials.Values.Where(m => m.Overlay).Select(m => m.Name)), StringComparer.Ordinal);
            overlayMaterials.ExceptWith(decalMaterials);
            string[] textureReferences = (descriptor?["textures"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
            var availableTextures = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (string reference in textureReferences)
            {
                Uri textureUri = ResolveCdnReference(reference);
                availableTextures[reference] = textureUri;
                availableTextures[Path.GetFileName(textureUri.AbsolutePath)] = textureUri;
            }

            string outputRoot = Path.GetFullPath(outputFolder);
            Directory.CreateDirectory(outputRoot);
            // Avatars are named after the account's username, items after their catalog name: the export folder, the model
            // file, the single-texture files, and the Garry's Mod paths. Exporting the same name again replaces its folder.
            string username = null, itemName = null, itemLabel = null, itemType = null;
            if (modelFile != null)
            {
                // Model files are named after the file and listed under "Models" in Garry's Mod's spawn menu.
                itemLabel = Path.GetFileNameWithoutExtension(modelFile).Trim();
                itemType = "Models";
                try
                {
                    itemName = SafeName(itemLabel, 0);
                }
                catch (InvalidDataException)
                {
                    itemName = "Model";
                }
                if (itemLabel.Length == 0)
                    itemLabel = itemName;
            }
            else if (bundle != null)
            {
                // Bundles are named after the bundle; its name is also the playermodel's name in Garry's Mod.
                username = bundle.Name;
                itemName = SafeName(bundle.Name, bundle.Id);
            }
            else if (avatar)
                try
                {
                    username = await GetUsernameAsync(id, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                {
                    progress?.Report("Username unavailable (" + error.Message + "); the export is named after user ID " + id + ".");
                }
            else
                try
                {
                    JObject item = await GetCatalogItemAsync(id, cancellation).ConfigureAwait(false);
                    itemType = ItemType(item);
                    itemLabel = DisplayName(item, id);
                    itemName = SafeName(itemLabel, id);
                }
                catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                {
                    progress?.Report("Item name unavailable (" + error.Message + "); the export is named after item ID " + id + ".");
                }
            string name = (bundle != null ? itemName : username ?? itemName) ?? id.ToString(CultureInfo.InvariantCulture);
            // A short name: Source paths inside it (materials\models\roporter\<name>\...) must stay under Windows' 260
            // characters even in Garry's Mod's deep addons folder.
            string staging;
            do
                staging = Path.Combine(outputRoot, ".rp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            while (Directory.Exists(staging) || File.Exists(staging));
            // Without its own folder (to collect many exports in one), the export goes straight into the output folder; its
            // textures folder is named after it so exports don't share one. Garry's Mod exports are collected into one addon:
            // the output folder itself, or, when that is Garry's Mod's addons folder (where loose files would not load), the
            // "RoPorter" addon inside it.
            bool flat = !ownFolder;
            string textureDir = flat ? name + "_textures" : "textures";
            string destination = Path.Combine(outputRoot, name);
            string shared = format == ExportFormat.GarrysMod && IsAddonsFolder(outputRoot) ? Path.Combine(outputRoot, SharedAddon) : outputRoot;
            Directory.CreateDirectory(staging);
            try
            {
                if (placeMap != null)
                {
                    string mapFile = await ExportPlaceMapAsync(placeMap, staging, name, itemLabel, scale, garrysMod, destination, progress, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (flat)
                    {
                        MergeInto(staging, shared, name);
                        ExportFolder = null;
                        return Path.Combine(shared, mapFile);
                    }
                    await PublishAsync(staging, destination, progress, cancellation).ConfigureAwait(false);
                    ExportFolder = destination;
                    return Path.Combine(destination, mapFile);
                }
                Directory.CreateDirectory(Path.Combine(staging, textureDir));
                var textures = new Dictionary<string, string>(StringComparer.Ordinal);
                var maps = new Dictionary<string, MaterialMaps>(StringComparer.Ordinal);
                var downloaded = new Dictionary<string, string>(StringComparer.Ordinal);
                var converted = new Dictionary<string, string>(StringComparer.Ordinal);
                string Full(string relative) => Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));

                async Task<string> FetchAsync(string texture)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Uri textureUri = null;
                    string sourceKey;
                    if (localTextures.TryGetValue(texture, out byte[] localImage))
                    {
                        sourceKey = "local:" + texture;
                    }
                    else
                    {
                        if (!availableTextures.TryGetValue(texture, out textureUri))
                        {
                            // Older renderers append an image extension to the hash in the MTL only.
                            string hash = Path.GetFileNameWithoutExtension(texture);
                            if (!availableTextures.TryGetValue(hash, out textureUri))
                                throw new InvalidDataException("Material texture is missing from Roblox's descriptor: " + texture);
                        }
                        sourceKey = textureUri.AbsoluteUri;
                    }
                    if (downloaded.TryGetValue(sourceKey, out string relativePath))
                        return relativePath;
                    progress?.Report("Downloading texture " + (downloaded.Count + 1) + "...");
                    byte[] image = localImage ?? await DownloadAsync(textureUri, cancellation).ConfigureAwait(false);
                    string extension;
                    if (image.Length >= 8 && image.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        extension = ".png";
                    else if (image.Length >= 3 && image[0] == 255 && image[1] == 216 && image[2] == 255)
                        extension = ".jpg";
                    else
                        throw new InvalidDataException("Roblox returned an unsupported texture format (expected PNG or JPEG).");
                    using (var stream = new MemoryStream(image))
                    using (Image decoded = Image.FromStream(stream, false, true))
                    {
                        if (decoded.Width <= 0 || decoded.Height <= 0)
                            throw new InvalidDataException("Roblox returned an invalid texture image.");
                    }
                    relativePath = textureDir + "/texture-" + downloaded.Count + extension;
                    File.WriteAllBytes(Full(relativePath), image);
                    downloaded.Add(sourceKey, relativePath);
                    return relativePath;
                }

                // PBR maps are optional: a map that cannot be downloaded is skipped rather than failing the export.
                async Task<string> FetchMapAsync(ObjMaterial material, string texture, string kind)
                {
                    if (texture == null)
                        return null;
                    try
                    {
                        return await FetchAsync(texture).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is ArgumentException)
                    {
                        progress?.Report(material.Name + ": " + kind + " map skipped (" + error.Message + ").");
                        return null;
                    }
                }

                foreach (ObjMaterial material in model.Triangles.Select(triangle => model.Materials[triangle.Material]).Distinct())
                {
                    if (material.Texture != null)
                    {
                        string relativePath = await FetchAsync(material.Texture).ConfigureAwait(false);
                        if (!overlayMaterials.Contains(material.Name) && !decalMaterials.Contains(material.Name) && !material.AlphaModeGiven &&
                            NeverOpaque(Full(relativePath)))
                        {
                            if (!converted.TryGetValue(relativePath + "|solid", out string solid))
                            {
                                solid = textureDir + "/material-" + textures.Count + "-solid.png";
                                Solidify(Full(relativePath), Full(solid));
                                converted[relativePath + "|solid"] = solid;
                            }
                            relativePath = solid;
                        }
                        if (overlayMaterials.Contains(material.Name))
                        {
                            string key = relativePath + "|" + string.Join(",", material.Diffuse);
                            if (!converted.TryGetValue(key, out string composite))
                            {
                                composite = textureDir + "/material-" + textures.Count + ".png";
                                CompositeOverColor(Full(relativePath), Full(composite), material.Diffuse);
                                converted[key] = composite;
                            }
                            relativePath = composite;
                        }
                        textures.Add(material.Name, relativePath);
                    }

                    var extra = new MaterialMaps { Normal = await FetchMapAsync(material, material.NormalTexture, "normal").ConfigureAwait(false) };
                    string packed = await FetchMapAsync(material, material.PackedTexture, "metalness/roughness").ConfigureAwait(false);
                    if (packed != null)
                    {
                        if (!converted.TryGetValue(packed, out string metallicRoughness))
                        {
                            metallicRoughness = textureDir + "/metal-rough-" + converted.Count + ".png";
                            PackMetallicRoughness(Full(packed), Full(metallicRoughness));
                            converted[packed] = metallicRoughness;
                        }
                        extra.MetallicRoughness = metallicRoughness;
                    }
                    string emissive = await FetchMapAsync(material, material.EmissiveTexture, "emissive").ConfigureAwait(false);
                    float[] strength = material.Emissive ?? new float[] { 1, 1, 1 };
                    float peak = strength.Max();
                    if (emissive != null && peak > 0 && !IsBlack(Full(emissive)))
                    {
                        extra.Emissive = emissive;
                        extra.EmissiveStrength = Math.Max(1, peak);
                        extra.EmissiveFactor = strength.Select(value => value / extra.EmissiveStrength).ToArray();
                    }
                    if (extra.Normal != null || extra.MetallicRoughness != null || extra.Emissive != null)
                        maps[material.Name] = extra;
                }
                if (maps.Count > 0)
                    progress?.Report("PBR maps: " + maps.Values.Count(m => m.Normal != null) + " normal, " +
                        maps.Values.Count(m => m.MetallicRoughness != null) + " metalness/roughness, " +
                        maps.Values.Count(m => m.Emissive != null) + " emissive.");

                TextureAtlas.Result atlas = null;
                if (singleTexture)
                {
                    progress?.Report("Merging " + model.Triangles.Select(t => t.Material).Distinct().Count() + " materials into one texture set...");
                    atlas = TextureAtlas.Build(model, staging, textures, maps, name, cancellation);
                    textures = atlas.Textures;
                    maps = atlas.Maps;
                    progress?.Report("Single texture: " + atlas.Width + "x" + atlas.Height + " (" + string.Join(", ",
                        atlas.Files.Except(atlas.GltfOnlyFiles)) + ").");
                }

                string fileName = name + (format == ExportFormat.Glb ? ".glb" : format == ExportFormat.Gltf ? ".gltf" : ".fbx");
                if (scale < 1)
                    progress?.Report("Scale: " + scale.ToString("0.##", CultureInfo.InvariantCulture) + " of full size.");
                if (!source)
                    progress?.Report("Writing " + format.ToString().ToUpperInvariant() + ": " + model.Triangles.Count + " triangles...");
                if (format == ExportFormat.SourceFilmmaker)
                {
                    fileName = SourceExporter.Export(new ExportScene(model, rig, face, scale), textures, maps, staging, name, garrysMod, progress, cancellation,
                        username, itemLabel, itemType, gearGroups, sfm);
                    Directory.Delete(Path.Combine(staging, textureDir), true);
                    foreach (string file in atlas?.Files ?? new List<string>())
                        if (File.Exists(Full(file)))
                            File.Move(Full(file), Path.Combine(staging, "src", file));
                    // SFM Workshop preview image: the avatar's headshot, the item's or the bundle's picture.
                    byte[] picture = null;
                    try
                    {
                        // A model file has no Roblox picture: its Workshop image is a plain background.
                        picture = modelFile != null ? null : bundle != null ? await DownloadBundleThumbnailAsync(bundle.Id, cancellation).ConfigureAwait(false)
                            : await DownloadThumbnailAsync(id, avatar, cancellation, background: true).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                    {
                        progress?.Report("Thumbnail unavailable (" + error.Message + "); the Workshop image is a plain background.");
                    }
                    Workshop.WriteIcon(picture, Path.Combine(staging, Workshop.IconFile), avatar && bundle == null);
                    // SFM Workshop page image, as for Garry's Mod: a bundle's picture, or an avatar's profile background with its
                    // full-body render.
                    try
                    {
                        if (bundle != null)
                        {
                            if (picture != null)
                                Workshop.WritePreview(null, null, picture, Path.Combine(staging, Workshop.PreviewFile));
                        }
                        else if (avatar)
                        {
                            KeyValuePair<byte[], Color?> background = new KeyValuePair<byte[], Color?>(null, null);
                            try
                            {
                                background = await DownloadProfileBackgroundAsync(id, cancellation).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                            {
                                progress?.Report("Profile background unavailable (" + error.Message + ").");
                            }
                            Workshop.WritePreview(background.Key, background.Value, await DownloadAvatarRenderAsync(id, cancellation).ConfigureAwait(false),
                                Path.Combine(staging, Workshop.PreviewFile));
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                    {
                        progress?.Report("Workshop page image skipped (" + error.Message + ").");
                    }
                    // A re-export keeps the SFM Workshop item of the last upload, so uploading again updates it.
                    string previousId = Path.Combine(destination, SfmWorkshop.IdFile);
                    if (File.Exists(previousId))
                        File.Copy(previousId, Path.Combine(staging, SfmWorkshop.IdFile), true);
                }
                else if (format == ExportFormat.GarrysMod)
                {
                    // Playermodels are listed under the account's username so players can find their avatar. Bundles have their
                    // own spawn menu category.
                    fileName = SourceExporter.Export(new ExportScene(model, rig, face, scale), textures, maps, staging, name, garrysMod, progress, cancellation,
                        username, itemLabel, itemType, gearGroups, playerCategory: bundle != null ? "Bundles" : null);
                    Directory.Delete(Path.Combine(staging, textureDir), true);
                    // The merged PNGs are source art for a Source model; keep them with the other sources.
                    foreach (string file in atlas?.Files ?? new List<string>())
                        if (File.Exists(Full(file)))
                            File.Move(Full(file), Path.Combine(staging, "src", file));

                    // Workshop icon: the avatar's headshot with its background, or the item's thumbnail.
                    byte[] thumbnail = null;
                    try
                    {
                        thumbnail = modelFile != null ? null : bundle != null ? await DownloadBundleThumbnailAsync(bundle.Id, cancellation).ConfigureAwait(false)
                            : await DownloadThumbnailAsync(id, avatar, cancellation, background: true).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                    {
                        progress?.Report("Thumbnail unavailable (" + error.Message + "); the Workshop icon is a plain background.");
                    }
                    Workshop.WriteIcon(thumbnail, Path.Combine(staging, Workshop.IconFile), avatar && bundle == null);
                    if (bundle != null)
                    {
                        // Workshop page image for a bundle: its catalog picture on a plain background.
                        if (thumbnail != null)
                            Workshop.WritePreview(null, null, thumbnail, Path.Combine(staging, Workshop.PreviewFile));
                    }
                    else if (avatar)
                        try
                        {
                            // Workshop page image: the profile background with the full-body render on top.
                            KeyValuePair<byte[], Color?> background = new KeyValuePair<byte[], Color?>(null, null);
                            try
                            {
                                background = await DownloadProfileBackgroundAsync(id, cancellation).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                            {
                                progress?.Report("Profile background unavailable (" + error.Message + ").");
                            }
                            byte[] render = await DownloadAvatarRenderAsync(id, cancellation).ConfigureAwait(false);
                            Workshop.WritePreview(background.Key, background.Value, render, Path.Combine(staging, Workshop.PreviewFile));
                            progress?.Report("Workshop page image: " + Workshop.PreviewFile + " (add it on the Workshop page; gmpublish cannot upload it).");
                        }
                        catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is JsonException)
                        {
                            progress?.Report("Workshop page image skipped (" + error.Message + ").");
                        }
                    // A re-export keeps the Workshop item of the last upload, so uploading again updates it.
                    long? published = Workshop.IsAddon(destination) ? Workshop.PublishedId(destination) : null;
                    if (published.HasValue)
                        Workshop.SetPublishedId(staging, published.Value);
                }
                else if (format == ExportFormat.Fbx)
                    FbxWriter.Write(new ExportScene(model, rig, face, scale), textures, maps, Path.Combine(staging, fileName), cancellation);
                else
                    GltfWriter.Write(new ExportScene(model, rig, face, scale), textures, maps, Path.Combine(staging, fileName), format, cancellation);
                if (format != ExportFormat.Gltf)
                    foreach (string file in atlas?.GltfOnlyFiles ?? new List<string>())
                        File.Delete(Full(file));
                if (format == ExportFormat.Glb)
                {
                    Directory.Delete(Path.Combine(staging, textureDir), true);
                }
                else if (!source)
                {
                    // Raw downloads replaced by composites, packed maps, or the atlas are not referenced by the model. FBX
                    // materials have no metalness/roughness input, so those maps are kept beside it instead.
                    var referenced = new HashSet<string>(textures.Values
                        .Concat(maps.Values.SelectMany(m => new[] { m.Normal, m.MetallicRoughness, m.Emissive }))
                        .Where(relative => relative != null).Select(relative => Path.GetFullPath(Full(relative))), StringComparer.OrdinalIgnoreCase);
                    string folder = Path.Combine(staging, textureDir);
                    foreach (string file in Directory.GetFiles(folder))
                        if (!referenced.Contains(Path.GetFullPath(file)))
                            File.Delete(file);
                    if (Directory.GetFileSystemEntries(folder).Length == 0)
                        Directory.Delete(folder);
                }
                cancellation.ThrowIfCancellationRequested();
                if (flat)
                {
                    MergeInto(staging, shared, name);
                    // A shared folder holds many exports, so it is not offered for Workshop upload.
                    ExportFolder = null;
                    return Path.Combine(shared, fileName);
                }
                await PublishAsync(staging, destination, progress, cancellation).ConfigureAwait(false);
                ExportFolder = destination;
                return Path.Combine(destination, fileName);
            }
            catch (Exception error)
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception cleanupError) when (cleanupError is IOException || cleanupError is UnauthorizedAccessException)
                {
                    throw new AggregateException("Export failed and temporary files could not be removed from " + staging, error, cleanupError);
                }
                throw;
            }
        }

        // A place as a Garry's Mod map addon: the map, its materials and prop models, and an addon.json of type "map".
        private async Task<string> ExportPlaceMapAsync(ModelContents contents, string staging, string name, string label, float scale,
            GarrysModInstall garrysMod, string destination, IProgress<string> progress, CancellationToken cancellation)
        {
            string map = await SourceMap.ExportAsync(contents, (assetId, token) => DownloadAssetAsync(assetId, token), staging, name, scale,
                garrysMod, progress, cancellation).ConfigureAwait(false);
            // gmad cannot parse addon.json with a byte order mark.
            File.WriteAllText(Path.Combine(staging, "addon.json"), "{\n\t\"title\": " + JsonConvert.ToString("RoPorter - " + label) +
                ",\n\t\"description\": " + JsonConvert.ToString(SourceExporter.WorkshopDescription) +
                ",\n\t\"type\": \"map\",\n\t\"tags\": [\"build\", \"fun\"],\n\t\"ignore\": [\"src/*\", \"" + Workshop.IconFile + "\", \"" + Workshop.PreviewFile + "\"]\n}\n",
                new UTF8Encoding(false));
            // A re-export keeps the Workshop item of the last upload, so uploading again updates it.
            long? published = Workshop.IsAddon(destination) ? Workshop.PublishedId(destination) : null;
            if (published.HasValue)
                Workshop.SetPublishedId(staging, published.Value);
            progress?.Report("Map " + Path.GetFileNameWithoutExtension(map) + ": start a game in Garry's Mod and pick it from the map list (Sandbox).");
            return map;
        }

        // The shared Garry's Mod addon that collects exports when the output folder is Garry's Mod's addons folder.
        private const string SharedAddon = "RoPorter";

        private static bool IsAddonsFolder(string folder) =>
            string.Equals(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "addons", StringComparison.OrdinalIgnoreCase);

        // Moves a finished export into a shared output folder, replacing files of the same name. Source sources go into
        // src\<name> and the per-export Workshop files are dropped, since a shared folder is never uploaded as one item. A
        // shared Garry's Mod addon has one addon.json: an existing one is kept, and a new one is titled "RoPorter".
        private static void MergeInto(string staging, string outputRoot, string name)
        {
            foreach (string drop in new[] { Workshop.IconFile, Workshop.PreviewFile, SfmWorkshop.IdFile })
                File.Delete(Path.Combine(staging, drop));
            string addon = Path.Combine(staging, "addon.json");
            if (File.Exists(addon))
            {
                if (File.Exists(Path.Combine(outputRoot, "addon.json")))
                    File.Delete(addon);
                else
                {
                    JObject json = JObject.Parse(File.ReadAllText(addon));
                    json["title"] = SharedAddon;
                    // gmad cannot parse addon.json with a byte order mark.
                    File.WriteAllText(addon, json.ToString(Formatting.Indented).Replace("  ", "\t") + "\n", new UTF8Encoding(false));
                }
            }
            string src = Path.Combine(staging, "src");
            if (Directory.Exists(src))
            {
                string named = Path.Combine(staging, "src-" + Guid.NewGuid().ToString("N"));
                Directory.Move(src, named);
                Directory.CreateDirectory(src);
                Directory.Move(named, Path.Combine(src, name));
            }
            string root = Path.GetFullPath(staging).TrimEnd('\\') + "\\";
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(outputRoot, Path.GetFullPath(file).Substring(root.Length));
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            Directory.Delete(staging, true);
        }

        private static async Task PublishAsync(string staging, string destination, IProgress<string> progress,
            CancellationToken cancellation)
        {
            // A previous export of the same name is set aside first and only deleted once the new one is in place.
            string previous = null;
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                previous = Path.Combine(Path.GetDirectoryName(destination), ".replaced-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await MoveAsync(destination, previous, progress, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && Directory.Exists(destination))
                {
                    // Something holds a handle inside the folder (Garry's Mod watches its addons' Lua folders, Explorer, an
                    // editor), so Windows refuses to rename it. Its files can still be overwritten.
                    ReplaceInPlace(staging, destination, progress, cancellation);
                    return;
                }
            }
            try
            {
                await MoveAsync(staging, destination, progress, cancellation).ConfigureAwait(false);
            }
            catch
            {
                if (previous != null)
                    Directory.Move(previous, destination);
                throw;
            }
            if (previous != null)
            {
                try
                {
                    Directory.Delete(previous, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    progress?.Report("The previous export could not be fully removed from " + previous + ": " + error.Message);
                }
            }
        }

        // Copies the new export over the previous one and removes the previous export's other files where possible.
        private static void ReplaceInPlace(string staging, string destination, IProgress<string> progress, CancellationToken cancellation)
        {
            string root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(destination, Path.GetFullPath(file).Substring(root.Length)));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                        File.SetAttributes(target, FileAttributes.Normal);
                    File.Copy(file, target, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    throw new IOException("The previous export at " + destination + " is in use and " + target + " could not be replaced (" +
                        error.Message + "). Close the program using it (for example Garry's Mod) and export again.", error);
                }
                written.Add(target);
            }
            int kept = 0;
            foreach (string file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
                if (!written.Contains(Path.GetFullPath(file)))
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        kept++;
                    }
            foreach (string folder in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            try
            {
                Directory.Delete(staging, true);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                progress?.Report("Temporary files could not be removed from " + staging + ": " + error.Message);
            }
            progress?.Report("The previous export is open in another program (for example Garry's Mod), so its files were replaced in place." +
                (kept > 0 ? " " + kept + " old file(s) in use could not be removed." : ""));
        }

        private static async Task MoveAsync(string source, string destination, IProgress<string> progress, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (IOException error) when (attempt < 4 &&
                    ((error.HResult & 0xffff) == 5 || (error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33))
                {
                    // Windows scanners can briefly hold newly written files open during a directory rename.
                    progress?.Report("Export files are busy; retrying publication...");
                    await Task.Delay(100 * (1 << attempt), cancellation).ConfigureAwait(false);
                }
            }
        }
    }
}
