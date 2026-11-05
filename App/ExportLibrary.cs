using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Rbx2Source
{
    // What the Library page lists: every export in the output folder. Exports made by this version are recorded in an index beside
    // the settings (library.json, with each export's preview picture in "library"), so nothing extra goes into the exports
    // themselves (Garry's Mod's gmad refuses unknown files). Older exports are recognised from their files.
    public static class ExportLibrary
    {
        public sealed class Entry
        {
            public string Name;
            // "Avatar", "Catalog Item", "Bundle", "Model" or "Place", or "Export" for an older export that can't tell.
            public string Type;
            // The export format's name as shown in the app ("GLB", "Garry's Mod", ...).
            public string Format;
            public long Id;
            public DateTime Date;
            // The exported file, and the folder to open (the export's own folder, or the file's).
            public string File;
            public string Folder;
            // A picture of it: the preview shown when it was exported, or a Garry's Mod addon's Workshop icon.
            public string Picture;
        }

        private static readonly string IndexFile = Path.Combine(Export.ContentCache.DataFolder, "library.json");
        private static readonly string Pictures = Path.Combine(Export.ContentCache.DataFolder, "library");

        private static List<Entry> ReadIndex()
        {
            try
            {
                return System.IO.File.Exists(IndexFile)
                    ? JsonConvert.DeserializeObject<List<Entry>>(System.IO.File.ReadAllText(IndexFile)) ?? new List<Entry>()
                    : new List<Entry>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
            {
                return new List<Entry>();
            }
        }

        // Records an export (replacing an earlier one of the same folder or file), with its preview picture (a PNG or JPEG file's bytes) or null.
        public static void Add(Entry entry, byte[] picture)
        {
            try
            {
                var index = ReadIndex();
                index.RemoveAll(e => Same(e.File, entry.File));
                if (picture != null)
                {
                    Directory.CreateDirectory(Pictures);
                    entry.Picture = Path.Combine(Pictures, Guid.NewGuid().ToString("N") + ".image");
                    System.IO.File.WriteAllBytes(entry.Picture, picture);
                }
                index.Add(entry);
                System.IO.File.WriteAllText(IndexFile, JsonConvert.SerializeObject(index, Formatting.Indented));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Read-only data folder: the export still shows, recognised from its files.
            }
        }

        private static bool Same(string a, string b) => a != null && b != null &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static bool Inside(string path, string folder)
        {
            string full = Path.GetFullPath(path), root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        // Every export in the folder, newest first: recorded ones whose files are still there, then the rest recognised from
        // their files (one per sub-folder, or per model file directly in the folder).
        public static List<Entry> List(string folder)
        {
            var result = new List<Entry>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return result;
            foreach (Entry entry in ReadIndex())
                if (entry.File != null && System.IO.File.Exists(entry.File) && Inside(entry.File, folder))
                {
                    if (entry.Picture != null && !System.IO.File.Exists(entry.Picture))
                        entry.Picture = null;
                    result.Add(entry);
                }
            bool Known(string path) => result.Any(e => Same(e.Folder, path) || Same(e.File, path) || e.File != null && Inside(e.File, path));
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    if (Known(sub))
                        continue;
                    Entry found = Recognise(sub);
                    if (found != null)
                        result.Add(found);
                }
                foreach (string file in Directory.EnumerateFiles(folder))
                    if (!Known(file) && ModelFormat(file) is string format)
                        result.Add(new Entry
                        {
                            Name = Path.GetFileNameWithoutExtension(file), Type = "Export", Format = format, File = file, Folder = folder,
                            Date = System.IO.File.GetLastWriteTime(file)
                        });
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
            return result.OrderByDescending(e => e.Date).ToList();
        }

        private static string ModelFormat(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".glb": return "GLB";
                case ".gltf": return "glTF";
                case ".fbx": return "FBX";
                case ".dae": return "Tower Unite";
                default: return null;
            }
        }

        // An export made before the library index: a Garry's Mod addon (its player model, map or prop), a Source Filmmaker
        // export, or a folder holding a model file.
        private static Entry Recognise(string folder)
        {
            var entry = new Entry { Name = Path.GetFileName(folder), Folder = folder, Date = Directory.GetLastWriteTime(folder) };
            string icon = Path.Combine(folder, Export.Workshop.IconFile);
            if (System.IO.File.Exists(icon))
                entry.Picture = icon;
            string Find(string sub, string pattern)
            {
                string path = Path.Combine(folder, sub);
                return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).FirstOrDefault() : null;
            }
            if (System.IO.File.Exists(Path.Combine(folder, "addon.json")))
            {
                entry.Format = "Garry's Mod";
                string map = Find("maps", "*.bsp"), player = Find(Path.Combine("models", "player"), "*.mdl");
                entry.Type = map != null ? "Place" : player != null ? "Avatar" : "Catalog Item";
                entry.File = map ?? player ?? Find("models", "*.mdl") ?? Path.Combine(folder, "addon.json");
                return entry;
            }
            if (Export.SfmWorkshop.IsExport(folder))
            {
                entry.Format = "Source Filmmaker";
                string player = Find("models", "*.mdl");
                entry.Type = player != null && player.Replace('\\', '/').Contains("/roporter/items/") ? "Catalog Item" : "Avatar";
                entry.File = player ?? folder;
                return entry;
            }
            string model = Directory.EnumerateFiles(folder).FirstOrDefault(f => ModelFormat(f) != null);
            if (model == null)
                return null;
            entry.Format = ModelFormat(model);
            entry.File = model;
            // A Tower Unite .dae is always a player model; other files can't tell an avatar from an item.
            entry.Type = entry.Format == "Tower Unite" ? "Avatar" : "Export";
            return entry;
        }
    }
}
