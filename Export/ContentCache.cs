using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rbx2Source.Export
{
    // Downloaded files kept for later exports, so the same meshes, textures and models are not asked of the API again. They
    // live in "rbx_cache" in the user's local app data folder (%LOCALAPPDATA%\RoPorter on Windows, ~/.local/share/RoPorter on
    // Linux, ~/Library/Application Support/RoPorter on macOS): Roblox CDN files by their content hash (they never change) in cdn\, and assets by
    // their ID and requested format in assets\ (an asset changes only when its creator updates it; clearing the cache fetches
    // it again). Things that change on their own (avatars, thumbnails, item lists) are never cached. Every failure to read or
    // write the cache is ignored: it only saves downloads.
    public static class ContentCache
    {
        public static string Folder => folder.Value;
        private static readonly Lazy<string> folder = new Lazy<string>(() =>
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "RoPorter", "rbx_cache");
            MoveOldCache(Path.Combine(AppContext.BaseDirectory, "rbx_cache"), local);
            MoveOldCache(Path.Combine(DataFolder, "rbx_cache"), local);
            return local;
        });

        // Earlier versions kept the cache beside the exe, or in the roaming RoPorter folder. It is moved to the
        // new folder once; where it can't be moved (another drive, a file in use), what is left is deleted, since everything in
        // it can be downloaded again.
        private static void MoveOldCache(string old, string current)
        {
            try
            {
                if (!Directory.Exists(old) || string.Equals(Path.GetFullPath(old).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(current));
                if (!Directory.Exists(current))
                {
                    try
                    {
                        Directory.Move(old, current);
                        return;
                    }
                    catch (IOException)
                    {
                        // A different drive: Directory.Move only renames. The old files are dropped below.
                    }
                }
                Directory.Delete(old, true);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
        }

        // Where RoPorter keeps its other files (the settings file, the Library's index and pictures, the Deadlock mod lists): the
        // user's RoPorter folder in app data (%APPDATA%\RoPorter on Windows, ~/.config/RoPorter on Linux, ~/Library/Application
        // Support/RoPorter on macOS), beside the settings key. Earlier versions kept them next to the program; they are moved here.
        public static string DataFolder => dataFolder.Value;
        private static readonly Lazy<string> dataFolder = new Lazy<string>(() =>
        {
            string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "RoPorter");
            try
            {
                Directory.CreateDirectory(user);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
            MoveOldData(AppContext.BaseDirectory, user);
            return user;
        });

        // The files and folders earlier versions kept next to the program.
        private static readonly string[] OldDataFiles = { "littlefart", "library.json", "deadlock_mods.txt", "deadlock_names.txt", "links.json" };
        private static readonly string[] OldDataFolders = { "library" };

        // Moves each old file and folder the new folder doesn't have yet; one it already has is left where it is. Failures are
        // ignored: that file is simply not carried over.
        private static void MoveOldData(string old, string current)
        {
            try
            {
                string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(Full(old), Full(current), StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            {
                return;
            }
            foreach (string name in OldDataFiles)
                try
                {
                    string from = Path.Combine(old, name), to = Path.Combine(current, name);
                    if (File.Exists(from) && !File.Exists(to))
                        File.Move(from, to);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            foreach (string name in OldDataFolders)
                try
                {
                    string from = Path.Combine(old, name), to = Path.Combine(current, name);
                    if (!Directory.Exists(from) || Directory.Exists(to))
                        continue;
                    try
                    {
                        Directory.Move(from, to);
                    }
                    catch (IOException)
                    {
                        // Another drive: copied, then the old folder removed.
                        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                        {
                            string target = Path.Combine(to, Path.GetRelativePath(from, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.Copy(file, target, false);
                        }
                        Directory.Delete(from, true);
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
        }

        // Set from the "Content cache" setting (on by default).
        public static bool Enabled { get; set; } = true;

        public const string Cdn = "cdn", Assets = "assets";

        // Keys are kept readable; one that had to be changed to be a safe file name (or is too long, or starts with a dot) gets a
        // hash of the real key appended, so two different keys never share a file.
        private static string PathOf(string kind, string key)
        {
            string name = Regex.Replace(key, @"[^A-Za-z0-9_.-]", "_");
            if (name != key || name.Length > 120 || name.StartsWith(".", StringComparison.Ordinal))
            {
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
                name = name.TrimStart('.');
                name = name.Substring(0, Math.Min(name.Length, 100)) + "~" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
            }
            return Path.Combine(Folder, kind, name);
        }

        // Whether a file is kept, without reading it.
        public static bool Contains(string kind, string key)
        {
            if (!Enabled)
                return false;
            try
            {
                return File.Exists(PathOf(kind, key));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return false;
            }
        }

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
