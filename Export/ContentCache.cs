using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rbx2Source.Export
{
    // Downloaded files kept for later exports, so the same meshes, textures and models are not asked of the API again. They
    // live in "rbx_cache" next to the exe: Roblox CDN files by their content hash (they never change) in cdn\, and assets by
    // their ID and requested format in assets\ (an asset changes only when its creator updates it; clearing the cache fetches
    // it again). Things that change on their own (avatars, thumbnails, item lists) are never cached. Every failure to read or
    // write the cache is ignored: it only saves downloads.
    public static class ContentCache
    {
        public static string Folder => Path.Combine(DataFolder, "rbx_cache");

        // Where RoPorter keeps its files (the settings file and this cache): next to the program, or, where that folder cannot
        // be written (an app installed in macOS's Applications folder, Program Files), the user's RoPorter folder.
        public static string DataFolder => dataFolder.Value;
        private static readonly Lazy<string> dataFolder = new Lazy<string>(() =>
        {
            string beside = AppContext.BaseDirectory;
            try
            {
                string probe = Path.Combine(beside, ".roporter-write-test");
                File.WriteAllBytes(probe, new byte[0]);
                File.Delete(probe);
                return beside;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "RoPorter");
                Directory.CreateDirectory(user);
                return user;
            }
        });

        // Set from the "Content cache" setting (on by default).
        public static bool Enabled { get; set; } = true;

        public const string Cdn = "cdn", Assets = "assets";

        private static string PathOf(string kind, string key) =>
            Path.Combine(Folder, kind, Regex.Replace(key, @"[^A-Za-z0-9_.-]", "_"));

        public static byte[] Get(string kind, string key)
        {
            if (!Enabled)
                return null;
            try
            {
                string file = PathOf(kind, key);
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void Put(string kind, string key, byte[] data)
        {
            if (!Enabled || data == null || data.Length == 0)
                return;
            string file = PathOf(kind, key), temporary = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                // Written beside it and then renamed, so an export running at the same time never reads half a file.
                File.WriteAllBytes(temporary, data);
                if (File.Exists(file))
                    File.Delete(temporary);
                else
                    File.Move(temporary, file);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanupError) when (cleanupError is IOException || cleanupError is UnauthorizedAccessException)
                {
                }
            }
        }

        // How many files the cache holds and their total size in bytes.
        public static (int Files, long Bytes) Size()
        {
            try
            {
                if (!Directory.Exists(Folder))
                    return (0, 0);
                var files = new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                return (files.Count, files.Sum(file => file.Length));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

        // Deletes every cached file. Returns false when some could not be deleted (in use by a running export).
        public static bool Clear()
        {
            bool all = true;
            if (!Directory.Exists(Folder))
                return true;
            foreach (string file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).ToList())
                try
                {
                    File.Delete(file);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    all = false;
                }
            foreach (string folder in Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            return all;
        }
    }
}
