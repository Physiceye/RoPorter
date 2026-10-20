using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Rbx2Source.Export
{
    // Source Filmmaker Workshop uploads. SFM's Workshop is Steam's older one (ISteamRemoteStorage): an item is a zip of
    // files under the game root (models/..., materials/...), written to the user's Steam Cloud for SFM with a preview
    // image and published. SFM unpacks subscribed items into game\workshop. Publishing goes through Garry's Mod's
    // steam_api (which still has the flat RemoteStorage calls), signed in as SFM (app 1840) in a separate RoPorter process.
    public static class SfmWorkshop
    {
        public const string PublishArgument = "--sfm-publish";
        // The Workshop item from an earlier upload of this export; uploading again updates it.
        public const string IdFile = "sfm_workshop_id.txt";

        private const uint SfmAppId = 1840;
        private const int PublishResult = 1309, UpdateResult = 1316; // k_iSteamRemoteStorageCallbacks + 9 / + 16
        private const int ResultOk = 1;
        private const int Hidden = 2; // k_ERemoteStoragePublishedFileVisibilityPrivate: starts hidden, like Garry's Mod uploads
        private const string Description = "[url=https://github.com/Physiceye/RoPorter/releases][h2]Made with RoPorter[/h2][/url]";

        public static bool IsExport(string folder) =>
            !string.IsNullOrEmpty(folder) && Directory.Exists(Path.Combine(folder, "models")) && Directory.Exists(Path.Combine(folder, "materials")) &&
            !File.Exists(Path.Combine(folder, "addon.json"));

        public static long? PublishedId(string folder)
        {
            string file = Path.Combine(folder, IdFile);
            return File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
                ? id : (long?)null;
        }

        // Packs the export and publishes it (or updates its earlier item); returns the Workshop item ID.
        public static async Task<long> PublishAsync(string folder, string title, IProgress<string> progress, CancellationToken cancellation)
        {
            if (!IsExport(folder))
                throw new InvalidDataException("The export folder is not a Source Filmmaker export: " + folder);
            string icon = Path.Combine(folder, Workshop.IconFile);
            if (!File.Exists(icon))
                Workshop.WriteIcon(null, icon);
            string work = Path.Combine(Path.GetTempPath(), "roporter-sfm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                progress?.Report("Packing the models and materials...");
                string zip = Path.Combine(work, "item.zip");
                using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    foreach (string top in new[] { "models", "materials" })
                        foreach (string file in Directory.GetFiles(Path.Combine(folder, top), "*", SearchOption.AllDirectories))
                            archive.CreateEntryFromFile(file, file.Substring(folder.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'), CompressionLevel.Optimal);

                long? existing = PublishedId(folder);
                string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                string job = Path.Combine(work, "job.json");
                File.WriteAllText(job, new JObject
                {
                    ["zip"] = zip, ["preview"] = icon, ["title"] = title, ["description"] = Description,
                    ["cloudName"] = "roporter/" + name, ["id"] = existing ?? 0,
                    ["pageImage"] = File.Exists(Path.Combine(folder, Workshop.PreviewFile)) ? Path.Combine(folder, Workshop.PreviewFile) : ""
                }.ToString());
                progress?.Report(existing.HasValue ? "Updating SFM Workshop item " + existing.Value + "..." : "Uploading to the SFM Steam Workshop...");
                string self = Environment.ProcessPath;
                var result = await Workshop.RunAsync(self, PublishArgument + " \"" + job + "\"", cancellation,
                    new[] { ("SteamAppId", "1840"), ("SteamGameId", "1840") }).ConfigureAwait(false);
                string last = (Workshop.Tail(result.Value).Split('\n').LastOrDefault() ?? "").Trim();
                if (result.Key != 0 || !long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
                    throw new InvalidDataException("The SFM Workshop upload failed: " + (last.Length > 0 ? last : "no reason given"));
                File.WriteAllText(Path.Combine(folder, IdFile), id.ToString(CultureInfo.InvariantCulture));
                return id;
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitFn(byte[] interfaces, byte[] error);
        private const string Interfaces = "STEAMREMOTESTORAGE_INTERFACE_VERSION016\0STEAMUGC_INTERFACE_VERSION021\0SteamUtils010\0\0";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong StartItemUpdateFn(IntPtr ugc, uint appId, ulong item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool AddItemPreviewFileFn(IntPtr ugc, ulong update, byte[] file, int type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SubmitItemUpdateFn(IntPtr ugc, ulong update, byte[] note);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InterfaceFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool FileWriteFn(IntPtr storage, byte[] name, byte[] data, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong PublishFn(IntPtr storage, byte[] file, byte[] preview, uint consumerApp, byte[] title, byte[] description,
            int visibility, IntPtr tags, int fileType);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong CreateUpdateFn(IntPtr storage, ulong item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool UpdateTextFn(IntPtr storage, ulong update, byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong CommitFn(IntPtr storage, ulong update);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsApiCallCompletedFn(IntPtr utils, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetApiCallResultFn(IntPtr utils, ulong call, byte[] result, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        // Entry point for "--sfm-publish <job.json>": prints the Workshop item ID as the last line and returns 0, or writes
        // the reason to the error output and returns 1.
        public static int Run(string[] args)
        {
            try
            {
                if (args.Length != 2 || !File.Exists(args[1]))
                    throw new ArgumentException("Usage: " + PublishArgument + " <job.json>");
                JObject job = JObject.Parse(File.ReadAllText(args[1]));
                Console.WriteLine(Publish(job).ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        private static ulong Publish(JObject job)
        {
            GarrysModInstall install = GarrysModInstall.Find() ?? throw new InvalidOperationException("Garry's Mod was not found (its steam_api is used).");
            string library = Environment.Is64BitProcess ? Path.Combine(install.Folder, "bin", "win64", "steam_api64.dll") : Path.Combine(install.Folder, "bin", "steam_api.dll");
            IntPtr module = LoadLibrary(library);
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Could not load " + library + " (error " + Marshal.GetLastWin32Error() + ").");
            T Get<T>(string name) where T : Delegate
            {
                IntPtr function = GetProcAddress(module, name);
                if (function == IntPtr.Zero)
                    throw new InvalidOperationException("Garry's Mod's steam_api has no " + name + ".");
                return Marshal.GetDelegateForFunctionPointer<T>(function);
            }

            var message = new byte[1024];
            if (Get<InitFn>("SteamInternal_SteamAPI_Init")(Encoding.ASCII.GetBytes(Interfaces), message) != 0)
            {
                string reason = Encoding.UTF8.GetString(message).TrimEnd('\0').Trim();
                throw new InvalidOperationException("Steam could not be reached" + (reason.Length > 0 ? " (" + reason + ")" : "") +
                    ". Start Steam and sign in to an account with Source Filmmaker.");
            }
            try
            {
                IntPtr storage = Get<InterfaceFn>("SteamAPI_SteamRemoteStorage_v016")(), utils = Get<InterfaceFn>("SteamAPI_SteamUtils_v010")();
                if (storage == IntPtr.Zero || utils == IntPtr.Zero)
                    throw new InvalidOperationException("Steam's Workshop interface is unavailable.");
                // The zip and preview go into the user's Steam Cloud for SFM, where the Workshop item points to them.
                string cloud = job.Value<string>("cloudName");
                string cloudZip = cloud + ".zip", cloudPreview = cloud + "_preview.jpg";
                var write = Get<FileWriteFn>("SteamAPI_ISteamRemoteStorage_FileWrite");
                foreach (var file in new[] { (cloudZip, job.Value<string>("zip")), (cloudPreview, job.Value<string>("preview")) })
                {
                    byte[] data = File.ReadAllBytes(file.Item2);
                    if (!write(storage, Utf8(file.Item1), data, data.Length))
                        throw new InvalidOperationException("Steam Cloud refused " + file.Item1 + " (" + data.Length / 1024 + " KB); check Source Filmmaker's Steam Cloud space.");
                }

                ulong existing = job.Value<ulong>("id");
                ulong call;
                if (existing == 0)
                    call = Get<PublishFn>("SteamAPI_ISteamRemoteStorage_PublishWorkshopFile")(storage, Utf8(cloudZip), Utf8(cloudPreview), SfmAppId,
                        Utf8(job.Value<string>("title")), Utf8(job.Value<string>("description")), Hidden, IntPtr.Zero, 0);
                else
                {
                    // Updates replace the files only; the title, description and visibility may have been edited on the page.
                    ulong update = Get<CreateUpdateFn>("SteamAPI_ISteamRemoteStorage_CreatePublishedFileUpdateRequest")(storage, existing);
                    if (!Get<UpdateTextFn>("SteamAPI_ISteamRemoteStorage_UpdatePublishedFileFile")(storage, update, Utf8(cloudZip)) ||
                        !Get<UpdateTextFn>("SteamAPI_ISteamRemoteStorage_UpdatePublishedFilePreviewFile")(storage, update, Utf8(cloudPreview)))
                        throw new InvalidOperationException("Steam rejected the Workshop update.");
                    // The change note names the RoPorter version, as for Garry's Mod addons.
                    IntPtr changes = GetProcAddress(module, "SteamAPI_ISteamRemoteStorage_UpdatePublishedFileSetChangeDescription");
                    if (changes != IntPtr.Zero)
                        Marshal.GetDelegateForFunctionPointer<UpdateTextFn>(changes)(storage, update, Utf8(Workshop.ChangeNote));
                    call = Get<CommitFn>("SteamAPI_ISteamRemoteStorage_CommitPublishedFileUpdate")(storage, update);
                }
                if (call == 0)
                    throw new InvalidOperationException("Steam did not start the Workshop upload.");

                var completed = Get<IsApiCallCompletedFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");
                DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                bool failed;
                while (!completed(utils, call, out failed))
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("Steam did not finish the Workshop upload within ten minutes.");
                    Thread.Sleep(250);
                }
                // RemoteStoragePublishFileResult_t / RemoteStorageUpdatePublishedFileResult_t: EResult, PublishedFileId_t (8-aligned),
                // bool m_bUserNeedsToAcceptWorkshopLegalAgreement.
                var result = new byte[24];
                if (failed || !Get<GetApiCallResultFn>("SteamAPI_ISteamUtils_GetAPICallResult")(utils, call, result, result.Length,
                    existing == 0 ? PublishResult : UpdateResult, out failed) || failed)
                    throw new InvalidOperationException("Steam did not report the Workshop upload's result.");
                int code = BitConverter.ToInt32(result, 0);
                if (code != ResultOk)
                    throw new InvalidOperationException("Steam refused the Workshop upload (EResult " + code + ")" +
                        (result[16] != 0 ? "; accept the Steam Workshop legal agreement first." : "."));
                ulong id = BitConverter.ToUInt64(result, 8);
                if (id == 0)
                    id = existing;
                // A new item also gets the page image (the 637x358 picture), like Garry's Mod uploads: the old Workshop system
                // has one image per item (the icon), so it is added through ISteamUGC. Updates leave the page alone.
                string page = job.Value<string>("pageImage");
                if (existing == 0 && !string.IsNullOrEmpty(page) && File.Exists(page))
                    try
                    {
                        IntPtr ugc = Get<InterfaceFn>("SteamAPI_SteamUGC_v021")();
                        ulong update = Get<StartItemUpdateFn>("SteamAPI_ISteamUGC_StartItemUpdate")(ugc, SfmAppId, id);
                        if (!Get<AddItemPreviewFileFn>("SteamAPI_ISteamUGC_AddItemPreviewFile")(ugc, update, Utf8(page), 0))
                            throw new InvalidOperationException("Steam rejected the page image.");
                        ulong submit = Get<SubmitItemUpdateFn>("SteamAPI_ISteamUGC_SubmitItemUpdate")(ugc, update, null);
                        DateTime until = DateTime.UtcNow.AddMinutes(5);
                        while (submit != 0 && !completed(utils, submit, out failed) && DateTime.UtcNow < until)
                            Thread.Sleep(250);
                        // SubmitItemUpdateResult_t (callback 3404): EResult first.
                        var submitted = new byte[16];
                        if (submit == 0 || !Get<GetApiCallResultFn>("SteamAPI_ISteamUtils_GetAPICallResult")(utils, submit, submitted, submitted.Length, 3404, out failed) ||
                            failed || BitConverter.ToInt32(submitted, 0) != ResultOk)
                            throw new InvalidOperationException("Steam did not add the page image" +
                                (submit != 0 ? " (EResult " + BitConverter.ToInt32(submitted, 0) + ")" : "") + ".");
                    }
                    catch (InvalidOperationException error)
                    {
                        Console.Error.WriteLine(error.Message + " Add it on the item's Workshop page instead.");
                    }
                return id;
            }
            finally
            {
                Get<ShutdownFn>("SteamAPI_Shutdown")();
            }
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
    }
}
