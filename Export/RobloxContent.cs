using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Rbx2Source.Export
{
    // Roblox's own textures, shipped in Resources\Roblox (and embedded in RoPorter): the Materials 2022 colour maps (from the
    // client's material texture packs), Roblox's default sky, the classic rbxasset:// textures (SpawnLocation.png and the
    // like), and the client's table of rbxasset:// paths that Roblox serves as ordinary assets.
    public static class RobloxContent
    {
        private const string Prefix = "RoPorter.Roblox.";
        // Material colour maps tile every this many studs on parts, and every this many on terrain.
        public const float PartStudsPerTile = 4, TerrainStudsPerTile = 8;

        private static readonly Lazy<Dictionary<string, string>> Resources = new Lazy<Dictionary<string, string>>(() =>
            typeof(RobloxContent).Assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                .GroupBy(name => Normalize(name.Substring(Prefix.Length))).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

        private static readonly Lazy<Dictionary<string, long>> Legacy = new Lazy<Dictionary<string, long>>(() =>
        {
            var table = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            byte[] text = Read("rbxasset.txt");
            if (text != null)
                foreach (string line in System.Text.Encoding.UTF8.GetString(text).Split('\n'))
                {
                    string[] parts = line.Trim().Split(new[] { ' ' }, 2);
                    if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                        table[Normalize(parts[1])] = id;
                }
            return table;
        });

        // Resource paths compared with forward slashes, without "rbxasset://", case-insensitively.
        private static string Normalize(string path) => path.Replace('\\', '/').Replace("rbxasset://", "").TrimStart('/');

        private static byte[] Read(string path)
        {
            if (!Resources.Value.TryGetValue(Normalize(path), out string name))
                return null;
            using (Stream stream = typeof(RobloxContent).Assembly.GetManifestResourceStream(name))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        // Roblox's Material enum by name (the materials with Materials 2022 colour maps, and those without).
        private static readonly Dictionary<int, string> MaterialNames = new Dictionary<int, string>
        {
            { 256, "Plastic" }, { 272, "SmoothPlastic" }, { 288, "Neon" }, { 512, "Wood" }, { 528, "WoodPlanks" }, { 784, "Marble" }, { 788, "Basalt" },
            { 800, "Slate" }, { 804, "CrackedLava" }, { 816, "Concrete" }, { 820, "Limestone" }, { 832, "Granite" }, { 836, "Pavement" }, { 848, "Brick" },
            { 864, "Pebble" }, { 880, "Cobblestone" }, { 896, "Rock" }, { 912, "Sandstone" }, { 1040, "CorrodedMetal" }, { 1056, "DiamondPlate" },
            { 1072, "Foil" }, { 1088, "Metal" }, { 1280, "Grass" }, { 1284, "LeafyGrass" }, { 1296, "Sand" }, { 1312, "Fabric" }, { 1328, "Snow" },
            { 1344, "Mud" }, { 1360, "Ground" }, { 1376, "Asphalt" }, { 1392, "Salt" }, { 1536, "Ice" }, { 1552, "Glacier" }, { 1568, "Glass" },
            { 1584, "ForceField" }
        };

        public static string MaterialName(int material) => MaterialNames.TryGetValue(material, out string name) ? name : null;

        // A material's colour map (JPEG), or null for materials that show only the part's colour (Plastic, Neon, Glass...).
        public static byte[] MaterialTexture(string name) => name == null ? null : Read("materials/" + name + ".jpg");

        public static byte[] MaterialTexture(int material) => MaterialTexture(MaterialName(material));

        public static bool HasMaterialTexture(int material) =>
            MaterialName(material) is string name && Resources.Value.ContainsKey(Normalize("materials/" + name + ".jpg"));

        // The colour that tints a terrain material's colour map so the ground shows the place's colour for it on average
        // (Terrain.MaterialColors is the colour terrain is seen in; the maps average 65-75% grey, so multiplying by the colour alone
        // came out a third darker: tan sand, olive grass). At most 2.5 times the colour.
        public static float[] TerrainTint(string name, float[] color)
        {
            float[] average = name == null ? null : Averages.GetOrAdd(name, key =>
            {
                byte[] image = MaterialTexture(key);
                if (image == null)
                    return null;
                using (Picture picture = Picture.Load(image))
                {
                    var sum = new double[3];
                    int[] pixels = picture.GetPixels();
                    foreach (int pixel in pixels)
                    {
                        sum[0] += (pixel >> 16) & 255;
                        sum[1] += (pixel >> 8) & 255;
                        sum[2] += pixel & 255;
                    }
                    return sum.Select(channel => (float)(channel / pixels.Length / 255)).ToArray();
                }
            });
            return average == null ? color : color.Select((channel, i) => Math.Min(channel / Math.Max(average[i], 0.4f), 2.5f * channel)).ToArray();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> Averages =
            new System.Collections.Concurrent.ConcurrentDictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        // A face of Roblox's default sky (bk, dn, ft, lf, rt, up).
        public static byte[] DefaultSky(string face) => Read("sky/sky512_" + face + ".jpg");

        // An rbxasset:// file shipped with RoPorter, or null.
        public static byte[] Asset(string rbxasset) => string.IsNullOrEmpty(rbxasset) ? null : Read("content/" + Normalize(rbxasset));

        // The asset Roblox serves for a classic rbxasset:// path (as its client does), or null.
        public static long? LegacyAsset(string rbxasset) =>
            !string.IsNullOrEmpty(rbxasset) && Legacy.Value.TryGetValue(Normalize(rbxasset), out long id) ? id : (long?)null;

        public static bool IsRbxAsset(string content) =>
            content != null && content.TrimStart().StartsWith("rbxasset://", StringComparison.OrdinalIgnoreCase);
    }
}
