using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveModel = ValveResourceFormat.ResourceTypes.Model;
using ValveMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace Rbx2Source.Export
{
    // A hero that can be replaced: heroes.vdata's key, the name players know, and its model.
    public sealed class DeadlockHero
    {
        public string Key, Name, Model;
        // The hero's pictures (compiled file, and whether it's a full card rather than a small icon) and name logos.
        public List<(string File, bool Card)> Pictures = new List<(string, bool)>();
        public List<string> Logos = new List<string>();
        public override string ToString() => Name;
    }

    // A Deadlock install. Only Valve's own pak01 is read, never citadel/addons, so an installed skin can't end up in an export.
    public sealed class DeadlockInstall
    {
        public string Folder { get; }
        public string Citadel => Path.Combine(Folder, "game", "citadel");
        public string Pak => Path.Combine(Citadel, "pak01_dir.vpk");
        public string Addons => Path.Combine(Citadel, "addons");

        private DeadlockInstall(string folder)
        {
            Folder = folder;
        }

        public static DeadlockInstall Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                var install = new DeadlockInstall(Path.Combine(library, "steamapps", "common", "Deadlock"));
                if (File.Exists(install.Pak))
                    return install;
            }
            return null;
        }

        // Mods load only when gameinfo.gi's search paths list citadel/addons.
        public bool LoadsAddons => Regex.IsMatch(File.ReadAllText(Path.Combine(Citadel, "gameinfo.gi")), @"^\s*Game\s+citadel/addons", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public bool Running => Process.GetProcessesByName("deadlock").Length > 0;

        public Package Open()
        {
            var package = new Package();
            package.Read(Pak);
            return package;
        }
    }

    // Counter-Strike 2's Workshop Tools: Deadlock has no compiler of its own, and CS2's builds models Deadlock loads.
    public sealed class Cs2Tools
    {
        public string Folder { get; }
        public string Compiler => Path.Combine(Folder, "game", "bin", "win64", "resourcecompiler.exe");
        public string Game => Path.Combine(Folder, "game", "csgo");
        public string Content(string addon) => Path.Combine(Folder, "content", "csgo_addons", addon);
        public string Output(string addon) => Path.Combine(Folder, "game", "csgo_addons", addon);

        private Cs2Tools(string folder)
        {
            Folder = folder;
        }

        public static Cs2Tools Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                var tools = new Cs2Tools(Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive"));
                if (File.Exists(tools.Compiler) && Directory.Exists(Path.Combine(tools.Folder, "content", "csgo_addons")))
                    return tools;
            }
            return null;
        }
    }

    // Replaces a Deadlock hero with an avatar: the hero's model is rebuilt from the game's own files with the avatar as its mesh,
    // so it keeps the hero's skeleton, animations, attachments, camera, hitboxes and game data. The hero's material stays too
    // (Deadlock's shader isn't in CS2's tools): the avatar's merged texture replaces the one that material reads.
    public static class DeadlockExporter
    {
        // Off for test exports, which stop at the .vpk.
        public static bool InstallToGame = true;
        // One per running copy of the app: each export clears its folder first, so two copies must not share one.
        private static readonly string Addon = "roporter_deadlock_" + Environment.ProcessId;
        // The hero model's source numbers are inches; the FBX is in centimetres.
        private const double InchesPerCentimetre = 0.3937, MetresPerInch = 0.0254;
        // Deadlock's neutral normal map and mask, for textures laid out for the hero's mesh instead of the avatar's.
        private const string FlatNormal = "models/heroes_staging/viper/materials/viper_body_vmat_g_tnormalroughness_53b47c48.vtex_c",
            PlainMask = "materials/default/default_mask_tga_344101f8.vtex_c";

        // The heroes in hero select, by the names in the game's English text.
        public static List<DeadlockHero> Heroes(DeadlockInstall install)
        {
            using (Package package = install.Open())
            {
                string names = File.ReadAllText(Path.Combine(install.Citadel, "resource", "localization", "citadel_gc_hero_names", "citadel_gc_hero_names_english.txt"));
                var heroes = new List<DeadlockHero>();
                foreach (Match hero in Regex.Matches(Text(package, "scripts/heroes.vdata_c"), @"^\t?(hero_\w+)\s*=\s*\r?\n(.*?)(?=^\t?\w+\s*=\s*\r?\n\t?\{|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
                {
                    string key = hero.Groups[1].Value, body = hero.Groups[2].Value;
                    Match model = Regex.Match(body, "m_strModelName = resource_name:\"([^\"]+)\"");
                    Match name = Regex.Match(names, "\"" + key + ":n\"\\s+\"([^\"]+)\"");
                    if (key == "hero_base" || key == "hero_targetdummy" || !model.Success || !name.Success || package.FindEntry(model.Groups[1].Value + "_c") == null ||
                        Regex.IsMatch(body, @"m_bDisabled = true|m_bInDevelopment = true"))
                        continue;
                    var entry = new DeadlockHero { Key = key, Name = name.Groups[1].Value, Model = model.Groups[1].Value };
                    foreach (var (field, card) in new[] { ("IconHeroCard", true), ("IconHeroCardCritical", true), ("IconHeroCardGloat", true), ("TopBarVertical", true), ("IconImageSmall", false), ("MinimapImage", false) })
                    {
                        Match picture = Regex.Match(body, "m_str" + field + " = panorama:\"file://\\{images\\}/([^\"]+)\\.(\\w+)\"");
                        if (picture.Success)
                            entry.Pictures.Add(("panorama/images/" + picture.Groups[1].Value + "_" + picture.Groups[2].Value + ".vtex_c", card));
                    }
                    foreach (Match logo in Regex.Matches(body, "m_strLogoImage\\w+ = panorama:\"file://\\{images\\}/([^\"]+)\\.svg\""))
                        entry.Logos.Add("panorama/images/" + logo.Groups[1].Value + ".vsvg_c");
                    heroes.Add(entry);
                }
                return heroes.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // Decompiling looks files up through this: Valve's pak only, as the library's own loader would also search addons.
        private sealed class PakLoader : IFileLoader
        {
            private readonly Package package;

            public PakLoader(Package package)
            {
                this.package = package;
            }

            public Resource LoadFile(string file)
            {
                byte[] bytes = Bytes(package, file);
                if (bytes == null)
                    return null;
                var resource = new Resource { FileName = file };
                resource.Read(new MemoryStream(bytes));
                return resource;
            }

            public Resource LoadFileCompiled(string file) => LoadFile(file.EndsWith("_c", StringComparison.Ordinal) ? file : file + "_c");
            public ValveResourceFormat.CompiledShader.ShaderCollection LoadShader(string shaderName) => null;
            public Stream GetFileStream(string file) => Bytes(package, file) is byte[] bytes ? new MemoryStream(bytes) : null;
        }
        private static byte[] Bytes(Package package, string path)
        {
            PackageEntry entry = package.FindEntry(path);
            if (entry == null)
                return null;
            package.ReadEntry(entry, out byte[] bytes);
            return bytes;
        }

        private static Resource Read(Package package, string path)
        {
            byte[] bytes = Bytes(package, path) ?? throw new InvalidDataException(path + " isn't in Deadlock's files.");
            var resource = new Resource { FileName = path };
            resource.Read(new MemoryStream(bytes));
            return resource;
        }

        private static string Text(Package package, string path)
        {
            using (Resource resource = Read(package, path))
                return resource.DataBlock.ToString();
        }

        // The hero's bind pose. Source 2's frame is Z up, facing +X, left +Y.
        private static TargetSkeleton Skeleton(ValveModel model)
        {
            var world = new Dictionary<int, Matrix4x4>();
            var bones = new List<(string, int, double[], double[])>();
            foreach (var bone in model.Skeleton.Bones)
            {
                Matrix4x4 local = Matrix4x4.CreateFromQuaternion(bone.Angle) * Matrix4x4.CreateTranslation(bone.Position);
                Matrix4x4 w = bone.Parent != null ? local * world[bone.Parent.Index] : local;
                world[bone.Index] = w;
                bones.Add((bone.Name, bone.Parent?.Index ?? -1, new double[] { w.M41, w.M42, w.M43 },
                    new double[] { w.M11, w.M21, w.M31, w.M12, w.M22, w.M32, w.M13, w.M23, w.M33 }));
            }
            TargetSkeleton skeleton = TargetSkeleton.FromWorld(bones, new double[] { 0, 1, 0, 0, 0, 1, 1, 0, 0 }, MetresPerInch);
            string[] fingers = { "thumb", "index", "middle", "ring" };
            string[] rigFingers = { "Thumb", "Index", "Middle", "Ring" };
            string[] phalanges = { "Proximal", "Intermediate", "Distal" };
            skeleton.MapHumanoid(part =>
            {
                switch (part)
                {
                    case "Hips": return "pelvis";
                    case "Spine": return "spine_0";
                    case "Chest": return "spine_1";
                    case "UpperChest": return "spine_2";
                    case "Neck": return "neck_0";
                    case "Head": return "head";
                }
                return null;
            }, (side, part) =>
            {
                string s = side == "Left" ? "_L" : "_R";
                switch (part)
                {
                    case "Shoulder": return "clavicle" + s;
                    case "UpperArm": return "arm_upper" + s;
                    case "LowerArm": return "arm_lower" + s;
                    case "Hand": return "hand" + s;
                    case "UpperLeg": return "leg_upper" + s;
                    case "LowerLeg": return "leg_lower" + s;
                    case "Foot": return "ankle" + s;
                    case "Toes": return "ball" + s;
                }
                for (int f = 0; f < fingers.Length; f++)
                    for (int p = 0; p < phalanges.Length; p++)
                        if (part == rigFingers[f] + phalanges[p])
                            return "finger_" + fingers[f] + "_" + p + s;
                return null;
            });
            return skeleton;
        }

        // Removes the first node of a class from a .vmdl's text.
        private static string Without(string vmdl, string nodeClass)
        {
            string node = Node(vmdl, nodeClass);
            return node == null ? vmdl : vmdl.Remove(vmdl.IndexOf(node, StringComparison.Ordinal), node.Length);
        }

        // The text of the first node of a class in a .vmdl, or null.
        private static string Node(string vmdl, string nodeClass)
        {
            int at = vmdl.IndexOf("_class = \"" + nodeClass + "\"", StringComparison.Ordinal);
            if (at < 0)
                return null;
            int start = vmdl.LastIndexOf('{', at), end = start, depth = 0;
            for (; end < vmdl.Length; end++)
            {
                if (vmdl[end] == '{')
                    depth++;
                else if (vmdl[end] == '}' && --depth == 0)
                    break;
            }
            return vmdl.Substring(start, vmdl.IndexOf('\n', end) - start);
        }

        // Weapon meshes, by the names Valve gives them; they stay on the avatar.
        private static readonly Regex WeaponMesh = new Regex(@"^(?!.*(phys|hull)).*(gun|weapon|sword|blade|knife|dagger|scabbard|hilt|sheath|rifle|pistol|revolver|cannon|wand|quiver|(^|_)bow(_|$)|(^|_)clip)", RegexOptions.IgnoreCase);

        // A compiled texture's source, as BC7 like the game's own (the compiler picks DXT1 for a material's opaque colour).
        private static string Vtex(string png, string format = "BC7") =>
            "<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->\n\"CDmeVtex\"\n{\n\t\"m_inputTextureArray\" \"element_array\"\n\t[\n\t\t\"CDmeInputTexture\"\n\t\t{\n" +
            "\t\t\t\"m_name\" \"string\" \"Colour\"\n\t\t\t\"m_fileName\" \"string\" \"" + png + "\"\n\t\t\t\"m_colorSpace\" \"string\" \"srgb\"\n\t\t\t\"m_typeString\" \"string\" \"2D\"\n" +
            "\t\t\t\"m_imageProcessorArray\" \"element_array\"\n\t\t\t[\n\t\t\t]\n\t\t}\n\t]\n\t\"m_outputTypeString\" \"string\" \"2D\"\n\t\"m_outputFormat\" \"string\" \"" + format + "\"\n" +
            "\t\"m_outputClearColor\" \"vector4\" \"0 0 0 0\"\n\t\"m_nOutputMinDimension\" \"int\" \"0\"\n\t\"m_nOutputMaxDimension\" \"int\" \"0\"\n\t\"m_textureOutputChannelArray\" \"element_array\"\n\t[\n" +
            "\t\t\"CDmeTextureOutputChannel\"\n\t\t{\n\t\t\t\"m_inputTextureArray\" \"string_array\"\n\t\t\t[\n\t\t\t\t\"Colour\"\n\t\t\t]\n\t\t\t\"m_srcChannels\" \"string\" \"rgba\"\n\t\t\t\"m_dstChannels\" \"string\" \"rgba\"\n" +
            "\t\t\t\"m_mipAlgorithm\" \"CDmeImageProcessor\"\n\t\t\t{\n\t\t\t\t\"m_algorithm\" \"string\" \"Box\"\n\t\t\t\t\"m_stringArg\" \"string\" \"\"\n\t\t\t\t\"m_vFloat4Arg\" \"vector4\" \"0 0 0 0\"\n\t\t\t}\n" +
            "\t\t\t\"m_outputColorSpace\" \"string\" \"srgb\"\n\t\t}\n\t]\n\t\"m_vClamp\" \"vector3\" \"0 0 0\"\n\t\"m_bNoLod\" \"bool\" \"1\"\n}\n";

        // A picture in a hero picture's size: a card is filled edge to edge, an icon shows the whole head.
        private static Picture Portrait(byte[] image, int width, int height, bool card)
        {
            using (Picture source = Picture.Load(image))
            {
                var result = new Picture(width, height);
                double scale = card ? Math.Max((double)width / source.Width, (double)height / source.Height) : Math.Min((double)width / source.Width, (double)height / source.Height);
                int drawnWidth = (int)Math.Round(source.Width * scale), drawnHeight = (int)Math.Round(source.Height * scale);
                result.Draw(source, (width - drawnWidth) / 2, (height - drawnHeight) / 2, drawnWidth, drawnHeight);
                return result;
            }
        }

        // A path in the commands Valve's logos use: the game draws no quadratic curves, which is what fonts are made of.
        private static string CubicPath(SKPath path)
        {
            var data = new StringBuilder();
            string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
            string P(SKPoint point) => N(point.X) + " " + N(point.Y);
            SKPoint Toward(SKPoint from, SKPoint control) => new SKPoint(from.X + (control.X - from.X) * 2 / 3, from.Y + (control.Y - from.Y) * 2 / 3);
            using (SKPath.RawIterator iterator = path.CreateRawIterator())
            {
                var points = new SKPoint[4];
                SKPathVerb verb;
                while ((verb = iterator.Next(points)) != SKPathVerb.Done)
                    switch (verb)
                    {
                        case SKPathVerb.Move:
                            data.Append('M').Append(P(points[0]));
                            break;
                        case SKPathVerb.Line:
                            data.Append('L').Append(P(points[1]));
                            break;
                        case SKPathVerb.Quad:
                        case SKPathVerb.Conic:
                            data.Append('C').Append(P(Toward(points[0], points[1]))).Append(' ').Append(P(Toward(points[2], points[1]))).Append(' ').Append(P(points[2]));
                            break;
                        case SKPathVerb.Cubic:
                            data.Append('C').Append(P(points[1])).Append(' ').Append(P(points[2])).Append(' ').Append(P(points[3]));
                            break;
                        case SKPathVerb.Close:
                            data.Append('Z');
                            break;
                    }
            }
            return data.ToString();
        }

        // The name as a logo the size of the hero's, in the Roblox lettering (outlines, so the game needs no font).
        private static string Logo(string name, double width, double height)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RoPorter.roblox2013.woff2"))
            using (var bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                using (SKData data = SKData.CreateCopy(Woff2.ToTrueType(bytes.ToArray())))
                using (SKTypeface typeface = SKTypeface.FromData(data))
                using (var font = new SKFont(typeface, 100))
                using (SKPath path = font.GetTextPath(name, new SKPoint(0, 0)))
                {
                    SKRect bounds = path.Bounds;
                    float scale = (float)Math.Min(width * 0.9 / bounds.Width, height * 0.7 / bounds.Height);
                    path.Transform(SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY).PostConcat(SKMatrix.CreateScale(scale, scale)).PostConcat(SKMatrix.CreateTranslation((float)width / 2, (float)height / 2)));
                    return string.Format(CultureInfo.InvariantCulture, "<svg width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\"><path fill=\"#ffffff\" d=\"{2}\"/></svg>", width, height, CubicPath(path));
                }
            }
        }

        // Writes the avatar's texture for the compiler at a hero's size. The compiler halves anything over 2048 on its long side,
        // which would cost a tall atlas most of its width; the mesh's texture coordinates run 0 to 1, so the atlas can go square.
        private static void WriteColour(string from, string to)
        {
            using (Picture picture = Picture.Load(from))
            using (Picture square = picture.Resized(2048, 2048))
                square.Save(to, PictureFormat.Png);
        }

        // The hero's source with its meshes and materials swapped for the avatar's: everything else stays as Valve made it.
        private static string HeroVmdl(string source, string mesh, IEnumerable<string> materials, string material, Dictionary<string, string> weapons)
        {
            // Bodygroups show or hide a weapon as Valve's animations ask (holstered or in hand); only its meshes stay in their lists.
            string groups = Node(source, "BodyGroupList");
            if (groups != null)
                groups = Regex.Replace(groups, "^\\s*\"([^\"]+)\",\\s*\\r?\\n", match => weapons.ContainsKey(match.Groups[1].Value) ? match.Value : "", RegexOptions.Multiline);
            string vmdl = source;
            foreach (string node in new[] { "AnimationList", "LODGroupList", "BodyGroupList", "MaterialGroupList", "RenderMeshList" })
                vmdl = Without(vmdl, node);
            // Ragdoll hulls kept in their own files, which decompiling doesn't give back. The ragdoll's capsules and the hitboxes stay.
            while (vmdl.Contains("_class = \"PhysicsHullFile\""))
                vmdl = Without(vmdl, "PhysicsHullFile");
            var nodes = new StringBuilder("\n\t\t\t{\n\t\t\t\t_class = \"MaterialGroupList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"\n\t\t\t\t\t\tremaps =\n\t\t\t\t\t\t[\n");
            foreach (string name in materials)
                nodes.Append("\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\tfrom = \"").Append(name).Append(".vmat\"\n\t\t\t\t\t\t\t\tto = \"").Append(material).Append("\"\n\t\t\t\t\t\t\t},\n");
            nodes.Append("\t\t\t\t\t\t]\n\t\t\t\t\t\tuse_global_default = false\n\t\t\t\t\t\tglobal_default_material = \"\"\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n")
                .Append("\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"avatar\"\n\t\t\t\t\t\tfilename = \"")
                .Append(mesh).Append("\"\n\t\t\t\t\t\timport_scale = ").Append(InchesPerCentimetre.ToString("0.0###", CultureInfo.InvariantCulture)).Append("\n\t\t\t\t\t},\n");
            foreach (var weapon in weapons)
                nodes.Append("\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"").Append(weapon.Key).Append("\"\n\t\t\t\t\t\tfilename = \"").Append(weapon.Value).Append("\"\n\t\t\t\t\t},\n");
            nodes.Append("\t\t\t\t]\n\t\t\t},\n");
            if (groups != null && weapons.Count > 0)
                nodes.Append("\t\t\t").Append(groups).Append("\n");
            int children = vmdl.IndexOf('[', vmdl.IndexOf("children", StringComparison.Ordinal));
            return vmdl.Insert(children + 1, nodes.ToString());
        }

        private static void Compile(Cs2Tools tools, string file, IProgress<string> progress, CancellationToken cancellation)
        {
            var start = new ProcessStartInfo(tools.Compiler)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(tools.Compiler)
            };
            foreach (string argument in new[] { "-game", tools.Game, "-addon", Addon, "-i", file, "-f" })
                start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start))
            {
                var output = new StringBuilder();
                process.OutputDataReceived += (sender, args) => { lock (output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) => { lock (output) output.AppendLine(args.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(200))
                    if (cancellation.IsCancellationRequested)
                    {
                        process.Kill();
                        cancellation.ThrowIfCancellationRequested();
                    }
                process.WaitForExit();
                string log;
                lock (output)
                    log = output.ToString();
                if (!log.Contains(" 1 compiled, 0 failed"))
                {
                    string problems = string.Join("\n", log.Split('\n').Where(line => line.Contains("ERROR") || line.Contains("resolve failure") || line.Contains("missing")).Take(8).Select(line => line.Trim()));
                    throw new InvalidDataException("CS2's compiler couldn't build the model:\n" + problems);
                }
            }
        }

        public static string Export(ExportScene scene, Dictionary<string, string> textures, string folder, string name, string heroKey, string playerName, byte[] head, byte[] portrait, IProgress<string> progress, CancellationToken cancellation)
        {
            if (scene.Rig == null)
                throw new InvalidOperationException("Only avatars can replace a Deadlock hero.");
            DeadlockInstall install = DeadlockInstall.Find() ?? throw new InvalidOperationException("Deadlock wasn't found in your Steam libraries.");
            Cs2Tools tools = Cs2Tools.Find() ?? throw new InvalidOperationException("Exporting to Deadlock needs Counter-Strike 2 with its Workshop Tools: " +
                "in Steam, open Counter-Strike 2's properties, go to DLC and install \"Counter-Strike 2 Workshop Tools\".");
            DeadlockHero hero = Heroes(install).FirstOrDefault(h => h.Key == heroKey) ?? throw new InvalidOperationException("That hero isn't in this version of Deadlock.");
            string modelFile = hero.Model + "_c", heroDir = Path.GetDirectoryName(hero.Model).Replace('\\', '/'), id = Path.GetFileNameWithoutExtension(hero.Model);
            string content = tools.Content(Addon), output = tools.Output(Addon);
            WavefrontModel model = scene.Model;
            try
            {
                foreach (string old in new[] { content, output })
                    if (Directory.Exists(old))
                        Directory.Delete(old, true);
                using (Package package = install.Open())
                using (Resource heroModel = Read(package, modelFile))
                {
                    var data = (ValveModel)heroModel.DataBlock;
                    List<string> references = heroModel.ExternalReferences.ResourceRefInfoList.Select(r => r.Name).ToList();
                    if (!references.Any(r => r.Contains(".vnmgraph")))
                        throw new InvalidOperationException(hero.Name + " uses Deadlock's older animation system, so it can't be replaced yet. Pick another hero.");
                    // The weapons stay, so the avatar takes a material none of them draw with.
                    var meshes = data.GetEmbeddedMeshes().Where(m => !m.Item3.Contains("_lod")).ToDictionary(m => m.Item3,
                        m => Regex.Matches(m.Item1.ToString(), "m_material = [^\"]*\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList());
                    List<string> weapons = meshes.Keys.Where(m => WeaponMesh.IsMatch(m)).ToList();
                    bool Usable(string file)
                    {
                        using (Resource resource = Read(package, file + "_c"))
                            return ((ValveMaterial)resource.DataBlock).TextureParams.ContainsKey("g_tColor");
                    }
                    List<string> Candidates() => meshes.Where(m => !weapons.Contains(m.Key)).SelectMany(m => m.Value).Distinct()
                        .Except(weapons.SelectMany(w => meshes[w])).Where(Usable).ToList();
                    List<string> materials = Candidates();
                    if (materials.Count == 0)
                    {
                        // Every material is shared with a weapon, which would wear the avatar's texture.
                        progress?.Report(hero.Name + "'s weapon shares its body's material, so it can't be kept.");
                        weapons.Clear();
                        materials = Candidates();
                    }
                    if (materials.Count == 0)
                        throw new InvalidDataException(hero.Name + " has no material the avatar can use, so it can't be replaced yet. Pick another hero.");
                    string material = materials.OrderByDescending(r => Path.GetFileName(r).Contains("body")).ThenByDescending(r => r.StartsWith(heroDir, StringComparison.Ordinal)).First();

                    // The avatar on the hero's bones, kept in its own proportions: limbs posed like the hero's, sized by the hero's height.
                    SourceExporter.BakeDefaultExpression(model, scene.Face);
                    TargetSkeleton skeleton = Skeleton(data);
                    skeleton.PoseTrunk(model, scene.Rig);
                    skeleton.Pose(model, scene.Rig);
                    double fit = skeleton.FitHeadTop(new ExportScene(model, scene.Rig, null, 1));
                    // Always the connected fit: Deadlock's animations carry the bones' positions, so a mesh left in Roblox
                    // proportions would swing its parts around the hero's joints, far outside them.
                    ExportScene fitted = skeleton.FitMesh(new ExportScene(model, scene.Rig, null, (float)fit), true);

                    // Its source: a placeholder material at the hero's material's path lets CS2 compile the model and the texture.
                    Directory.CreateDirectory(Path.Combine(content, heroDir));
                    string colour = textures.Values.FirstOrDefault();
                    if (colour == null)
                        throw new InvalidDataException("The avatar has no texture to use.");
                    string colourSource = heroDir + "/roporter_color.png";
                    string Local(string path) => Path.Combine(content, path.Replace('/', Path.DirectorySeparatorChar));
                    WriteColour(Path.Combine(folder, colour.Replace('/', Path.DirectorySeparatorChar)), Local(colourSource));
                    string colourTexture = Local(heroDir + "/roporter_color.vtex");
                    File.WriteAllText(colourTexture, Vtex(colourSource), new UTF8Encoding(false));
                    foreach (string placeholder in references.Where(r => r.EndsWith(".vmat", StringComparison.Ordinal)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Local(placeholder)));
                        File.WriteAllText(Local(placeholder), "Layer0\n{\n\tshader \"csgo_complex.vfx\"\n}\n", new UTF8Encoding(false));
                    }

                    string mesh = heroDir + "/roporter_avatar.fbx";
                    var fbx = new FbxWriter.Options { UnitsPerMetre = 100, UnitScaleFactor = 1, Shapes = false, EmbedTextures = false, Bones = skeleton.Bones, BoneOf = skeleton.RigToBone() };
                    progress?.Report("Writing the model on " + hero.Name + "'s skeleton (" + model.Triangles.Count + " triangles, " + skeleton.Bones.Count + " bones)...");
                    FbxWriter.Write(fitted, model.Materials.Keys.ToDictionary(key => key, key => "roporter_color.png"), null, Path.Combine(content, mesh.Replace('/', Path.DirectorySeparatorChar)), cancellation, fbx);

                    ContentFile extracted = new ModelExtract(heroModel, new PakLoader(package)).ToContentFile();
                    string source = Encoding.UTF8.GetString(extracted.Data);
                    var weaponFiles = new Dictionary<string, string>();
                    foreach (string weapon in weapons)
                    {
                        string file = Regex.Match(source, "name = \"" + Regex.Escape(weapon) + "\"\\s*filename = \"([^\"]+)\"").Groups[1].Value;
                        var subFile = extracted.SubFiles.FirstOrDefault(f => Path.GetFileName(f.FileName) == Path.GetFileName(file));
                        if (subFile == null)
                            continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(Local(file)));
                        File.WriteAllBytes(Local(file), subFile.Extract());
                        weaponFiles[weapon] = file;
                    }
                    progress?.Report(weaponFiles.Count > 0 ? "Keeping " + hero.Name + "'s weapon: " + string.Join(", ", weaponFiles.Keys) + "." : hero.Name + " has no weapon mesh to keep.");
                    string vmdl = Path.Combine(content, heroDir.Replace('/', Path.DirectorySeparatorChar), id + "_roporter.vmdl");
                    File.WriteAllText(vmdl, HeroVmdl(source, mesh, model.Triangles.Select(t => t.Material).Distinct(), material, weaponFiles), new UTF8Encoding(false));
                    progress?.Report("Compiling with Counter-Strike 2's tools...");
                    Compile(tools, colourTexture, progress, cancellation);
                    Compile(tools, vmdl, progress, cancellation);

                    // The mod: the compiled model at the hero's path, and the hero's material's textures replaced.
                    string compiled = Path.Combine(output, heroDir.Replace('/', Path.DirectorySeparatorChar), id + "_roporter.vmdl_c");
                    string texture = Path.Combine(output, heroDir.Replace('/', Path.DirectorySeparatorChar), "roporter_color.vtex_c");
                    var mod = new Package();
                    mod.AddFile(modelFile, File.ReadAllBytes(compiled));
                    using (Resource heroMaterial = Read(package, material + "_c"))
                        foreach (var parameter in ((ValveMaterial)heroMaterial.DataBlock).TextureParams)
                        {
                            string target = parameter.Value + "_c";
                            byte[] bytes = parameter.Key == "g_tColor" ? File.ReadAllBytes(texture)
                                : target.StartsWith("materials/default/", StringComparison.Ordinal) ? null
                                : Bytes(package, parameter.Key == "g_tNormalRoughness" ? FlatNormal : PlainMask);
                            if (bytes != null)
                                mod.AddFile(target, bytes);
                        }
                    // The hero's pictures and name logos become the player's.
                    Directory.CreateDirectory(Local("panorama/roporter"));
                    int number = 0;
                    byte[] Build(string extension, string source)
                    {
                        string file = "panorama/roporter/" + number++ + extension;
                        File.WriteAllText(Local(file), source, new UTF8Encoding(false));
                        Compile(tools, Local(file), progress, cancellation);
                        return File.ReadAllBytes(Path.Combine(output, file.Replace('/', Path.DirectorySeparatorChar) + "_c"));
                    }
                    foreach (var (file, card) in hero.Pictures)
                    {
                        byte[] image = card ? portrait ?? head : head ?? portrait;
                        if (image == null || package.FindEntry(file) == null)
                            continue;
                        using (Resource original = Read(package, file))
                        {
                            var size = (ValveResourceFormat.ResourceTypes.Texture)original.DataBlock;
                            string png = "panorama/roporter/picture" + number + ".png";
                            using (Picture picture = Portrait(image, size.Width, size.Height, card))
                                picture.Save(Local(png), PictureFormat.Png);
                            mod.AddFile(file, Build(".vtex", Vtex(png, "BGRA8888")));
                        }
                    }
                    foreach (string logo in hero.Logos.Where(l => package.FindEntry(l) != null))
                    {
                        Match size = Regex.Match(Text(package, logo), "width=\"([\\d.]+)\" height=\"([\\d.]+)\"");
                        double width = size.Success ? double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : 2000, height = size.Success ? double.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) : 1000;
                        mod.AddFile(logo, Build(".vsvg", Logo(playerName, width, height)));
                    }

                    string vpk = id + "_" + Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]", "_") + "_dir.vpk";
                    mod.Write(Path.Combine(folder, vpk));
                    progress?.Report(InstallToGame ? Install(install, hero, Path.Combine(folder, vpk), playerName) : "Saved " + vpk + ".");
                    return vpk;
                }
            }
            finally
            {
                foreach (string temporary in new[] { content, output })
                    try
                    {
                        if (Directory.Exists(temporary))
                            Directory.Delete(temporary, true);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                    }
            }
        }

        // Which addons file holds each hero's RoPorter mod, so exporting that hero again replaces it.
        private static string InstalledList => Path.Combine(ContentCache.DataFolder, "deadlock_mods.txt");
        private static string NamesList => Path.Combine(ContentCache.DataFolder, "deadlock_names.txt");
        private const string NamesFile = "resource/localization/citadel_gc_hero_names/citadel_gc_hero_names_english.txt";

        // Copies the mod into the game's addons as the next free pakNN_dir.vpk (Deadlock loads no other name), or over the file of
        // the last export for this hero. Lower numbers win, so an older mod for the same hero stays on top: that's left for the
        // player to remove, as RoPorter doesn't know whose it is.
        private static string Install(DeadlockInstall install, DeadlockHero hero, string vpk, string playerName)
        {
            if (!install.LoadsAddons)
                return "Saved " + Path.GetFileName(vpk) + ". Deadlock isn't set up to load mods: add \"Game citadel/addons\" to gameinfo.gi's SearchPaths, then copy it into game/citadel/addons as a pakNN_dir.vpk file.";
            if (install.Running)
                return "Saved " + Path.GetFileName(vpk) + ". Close Deadlock and export again to install it (the game keeps its mods open).";
            Directory.CreateDirectory(install.Addons);
            var installed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(InstalledList))
                foreach (string line in File.ReadAllLines(InstalledList))
                {
                    string[] parts = line.Split('=');
                    // Records from before sizes were kept have only the hero and the file.
                    if (parts.Length >= 2 && new FileInfo(Path.Combine(install.Addons, parts[1])) is FileInfo mod && mod.Exists &&
                        (parts.Length == 2 || mod.Length.ToString(CultureInfo.InvariantCulture) == parts[2]))
                        installed[parts[0]] = parts[1];
                }
            // A listed file of another size isn't the export any more (a mod manager may have reused the name), so it's left alone.
            string Slot(string key)
            {
                if (installed.TryGetValue(key, out string existing))
                    return existing;
                for (int number = 1; ; number++)
                {
                    string free = "pak" + number.ToString("00", CultureInfo.InvariantCulture) + "_dir.vpk";
                    if (!File.Exists(Path.Combine(install.Addons, free)))
                        return installed[key] = free;
                }
            }
            string file = Slot(hero.Key);
            File.Copy(vpk, Path.Combine(install.Addons, file), true);

            // One game file holds every hero's name, so the names share one mod with every replaced hero's, rebuilt from the game's
            // current file each time.
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(NamesList))
                foreach (string line in File.ReadAllLines(NamesList))
                    if (line.Split('=') is string[] parts && parts.Length == 2)
                        names[parts[0]] = parts[1];
            names[hero.Key] = playerName.Replace("\"", "").Replace("=", "");
            File.WriteAllLines(NamesList, names.Select(pair => pair.Key + "=" + pair.Value));
            string text = File.ReadAllText(Path.Combine(install.Citadel, NamesFile.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var pair in names)
                text = Regex.Replace(text, "(\"" + pair.Key + ":n\"\\s+\")[^\"]*", match => match.Groups[1].Value + pair.Value);
            var namesMod = new Package();
            namesMod.AddFile(NamesFile, Encoding.UTF8.GetBytes(text));
            namesMod.Write(Path.Combine(install.Addons, Slot("names")));
            File.WriteAllLines(InstalledList, installed.Select(pair => pair.Key + "=" + pair.Value + "=" + new FileInfo(Path.Combine(install.Addons, pair.Value)).Length.ToString(CultureInfo.InvariantCulture)));
            return "Installed over " + hero.Name + " as game/citadel/addons/" + file + ". Mods with lower pak numbers load first: if another mod replaces " + hero.Name + ", it wins. Other players still see the normal hero.";
        }
    }
}
