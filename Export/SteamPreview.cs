using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rbx2Source.Export
{
    // Sets a Workshop item's description and adds its page image. gmpublish only uploads the addon, its icon and its title,
    // so this calls Steam's Workshop API (ISteamUGC) through Garry's Mod's own steam_api library, signed in as Garry's Mod
    // (app 4000, from the SteamAppId variable the parent process sets). It runs in its own RoPorter process
    // ("--workshop-details <id> <addon folder>") because Steam shows the account as playing Garry's Mod until it exits.
    public static class SteamPreview
    {
        private const uint GarrysModAppId = 4000;
        private const int SubmitItemUpdateResult = 3404; // k_iSteamUGCCallbacks + 4
        private const int ImagePreview = 0; // k_EItemPreviewType_Image
        private const int ResultOk = 1; // k_EResultOK

        // SteamAPI_Init is an inline wrapper in current Steamworks: the library exports SteamInternal_SteamAPI_Init, given
        // the interfaces to check (null-separated, ending in an empty name) and a 1024-byte buffer for the error message.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitFn(byte[] interfaces, byte[] error);
        private const string Interfaces = "STEAMUGC_INTERFACE_VERSION021\0SteamUtils010\0\0";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InterfaceFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong StartItemUpdateFn(IntPtr ugc, uint appId, ulong item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool AddItemPreviewFileFn(IntPtr ugc, ulong update, byte[] file, int type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetTextFn(IntPtr ugc, ulong update, byte[] text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SubmitItemUpdateFn(IntPtr ugc, ulong update, byte[] note);
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

        // Entry point for "--workshop-details <id> <addon folder>", run once when the Workshop item is created: 0 on success,
        // with what was set as the last output line; otherwise the reason is written to the error output. The description
        // comes from the addon's addon.json; the page image is its workshop_preview.jpg.
        public static int Run(string[] args)
        {
            try
            {
                if (args.Length != 3 || !ulong.TryParse(args[1], out ulong item) || item == 0 || !Workshop.IsAddon(args[2]))
                    throw new ArgumentException("Usage: " + Workshop.DetailsArgument + " <workshop item id> <addon folder>");
                string folder = Path.GetFullPath(args[2]);
                string description = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(folder, "addon.json"))).Value<string>("description");
                string preview = Path.Combine(folder, Workshop.PreviewFile);
                bool addPreview = File.Exists(preview);
                if (string.IsNullOrWhiteSpace(description) && !addPreview)
                    return 0;
                Update(item, description, addPreview ? preview : null);
                Console.WriteLine(string.IsNullOrWhiteSpace(description) ? "Added the Workshop page image"
                    : addPreview ? "Set the Workshop description and added the page image" : "Set the Workshop description");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        private static void Update(ulong item, string description, string image)
        {
            GarrysModInstall install = GarrysModInstall.Find() ?? throw new InvalidOperationException("Garry's Mod was not found.");
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
                    ". Start Steam and sign in to an account that owns Garry's Mod.");
            }
            try
            {
                IntPtr ugc = Get<InterfaceFn>("SteamAPI_SteamUGC_v021")(), utils = Get<InterfaceFn>("SteamAPI_SteamUtils_v010")();
                if (ugc == IntPtr.Zero || utils == IntPtr.Zero)
                    throw new InvalidOperationException("Steam's Workshop interface is unavailable.");
                ulong update = Get<StartItemUpdateFn>("SteamAPI_ISteamUGC_StartItemUpdate")(ugc, GarrysModAppId, item);
                if (!string.IsNullOrWhiteSpace(description) &&
                    !Get<SetTextFn>("SteamAPI_ISteamUGC_SetItemDescription")(ugc, update, Utf8(description)))
                    throw new InvalidOperationException("Steam rejected the description.");
                if (image != null && !Get<AddItemPreviewFileFn>("SteamAPI_ISteamUGC_AddItemPreviewFile")(ugc, update, Utf8(image), ImagePreview))
                    throw new InvalidOperationException("Steam rejected the image " + image + ".");
                ulong call = Get<SubmitItemUpdateFn>("SteamAPI_ISteamUGC_SubmitItemUpdate")(ugc, update, null);
                if (call == 0)
                    throw new InvalidOperationException("Steam did not start the Workshop update.");

                var completed = Get<IsApiCallCompletedFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");
                DateTime deadline = DateTime.UtcNow.AddMinutes(5);
                bool failed;
                while (!completed(utils, call, out failed))
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("Steam did not finish the Workshop update within five minutes.");
                    Thread.Sleep(200);
                }
                // SubmitItemUpdateResult_t: EResult m_eResult, bool m_bUserNeedsToAcceptWorkshopLegalAgreement, PublishedFileId_t.
                var result = new byte[16];
                if (failed || !Get<GetApiCallResultFn>("SteamAPI_ISteamUtils_GetAPICallResult")(utils, call, result, result.Length, SubmitItemUpdateResult, out failed) || failed)
                    throw new InvalidOperationException("Steam did not report the Workshop update's result.");
                int code = BitConverter.ToInt32(result, 0);
                if (code != ResultOk)
                    throw new InvalidOperationException("Steam refused the Workshop update (EResult " + code + ")" +
                        (result[4] != 0 ? "; accept the Steam Workshop legal agreement on the item's page first." : "."));
            }
            finally
            {
                Get<ShutdownFn>("SteamAPI_Shutdown")();
            }
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
    }
}
