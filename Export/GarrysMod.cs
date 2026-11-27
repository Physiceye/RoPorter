using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Rbx2Source.Export
{
    // A Garry's Mod install: its model compiler and the Valve player data (skeleton, attachments, IK chains and ragdoll
    // limits) that RoPorter reads from the game files instead of shipping Valve's data.
    public sealed class GarrysModInstall
    {
        public string Folder { get; }
        public string StudioMdl => Path.Combine(Folder, "bin", "studiomdl.exe");
        public string Content => Path.Combine(Folder, "garrysmod");

        private GarrysModInstall(string folder)
        {
            Folder = folder;
        }

        public static bool IsInstall(string folder) =>
            !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "bin", "studiomdl.exe")) &&
            File.Exists(Path.Combine(folder, "garrysmod", "garrysmod_dir.vpk"));

        // An explicit folder first, then every Steam library.
        public static GarrysModInstall Find(string preferred = null)
        {
            if (IsInstall(preferred))
                return new GarrysModInstall(preferred);
            foreach (string library in SteamLibraries())
            {
                string folder = Path.Combine(library, "steamapps", "common", "GarrysMod");
                if (IsInstall(folder))
                    return new GarrysModInstall(folder);
            }
            return null;
        }

        internal static IEnumerable<string> SteamLibraries()
        {
            var steams = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var key in new[] { (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                    (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"), (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
                {
                    try
                    {
                        using (RegistryKey opened = key.Item1.OpenSubKey(key.Item2))
                            if (opened?.GetValue(key.Item3) is string path && Directory.Exists(path))
                                steams.Add(Path.GetFullPath(path));
                    }
                    catch (Exception error) when (error is System.Security.SecurityException || error is IOException || error is UnauthorizedAccessException)
                    {
                    }
                }
                steams.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            }
            else
            {
                // Steam on Linux (also as a Flatpak or Snap) and macOS.
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string folder in new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam",
                    "snap/steam/common/.local/share/Steam", "Library/Application Support/Steam" })
                    if (Directory.Exists(Path.Combine(home, folder)))
                        steams.Add(Path.GetFullPath(Path.Combine(home, folder)));
            }
            var libraries = new List<string>();
            foreach (string steam in steams.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private Vpk vpk;

        public byte[] ReadFile(string path)
        {
            vpk = vpk ?? new Vpk(Path.Combine(Content, "garrysmod_dir.vpk"));
            return vpk.Read(path) ?? throw new FileNotFoundException("Garry's Mod is missing " + path + ".");
        }

        private ValvePlayer player;
        public ValvePlayer Player => player = player ?? ValvePlayer.Read(this);
    }

    // A Source Filmmaker install, for its model compiler.
    public sealed class SfmInstall
    {
        public string Game { get; }
        public string StudioMdl => Path.Combine(Game, "bin", "studiomdl.exe");

        private SfmInstall(string game)
        {
            Game = game;
        }

        public static bool IsInstall(string game) =>
            !string.IsNullOrWhiteSpace(game) && File.Exists(Path.Combine(game, "bin", "studiomdl.exe")) &&
            File.Exists(Path.Combine(game, "usermod", "gameinfo.txt"));

        public static SfmInstall Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                string game = Path.Combine(library, "steamapps", "common", "SourceFilmmaker", "game");
                if (IsInstall(game))
                    return new SfmInstall(game);
            }
            return null;
        }
    }

    // One of Left 4 Dead 2's eight survivors, whose model (and first-person arms) an export replaces.
    public sealed class L4d2Survivor
    {
        public string Name { get; }
        // The model's internal name: models/survivors/survivor_<Key>.mdl.
        public string Key { get; }
        // models/weapons/arms/<Arms>.mdl
        public string Arms { get; }
        // The Passing and The Sacrifice show Zoey and Francis with a "_light" copy of their model.
        public bool HasLightModel { get; }

        private L4d2Survivor(string name, string key, string arms, bool light = false)
        {
            Name = name;
            Key = key;
            Arms = arms;
            HasLightModel = light;
        }

        public string Model => "survivors/survivor_" + Key + ".mdl";
        public string ArmsModel => "weapons/arms/" + Arms + ".mdl";
        // Left 4 Dead 1's cast, who have a character select picture (vgui/select_<name>) instead of a lobby picture.
        public bool Original => Arms.IndexOf("_new", StringComparison.Ordinal) < 0;
        // The HUD, lobby and character select pictures (materials/vgui/<name>.vtf).
        public string Portrait => "s_panel_" + Key;
        public string LobbyPicture => Original ? "select_" + Name.ToLowerInvariant() : "s_panel_lobby_" + Key;

        public static IReadOnlyList<L4d2Survivor> All { get; } = new[]
        {
            new L4d2Survivor("Nick", "gambler", "v_arms_gambler_new"),
            new L4d2Survivor("Rochelle", "producer", "v_arms_producer_new"),
            new L4d2Survivor("Coach", "coach", "v_arms_coach_new"),
            new L4d2Survivor("Ellis", "mechanic", "v_arms_mechanic_new"),
            new L4d2Survivor("Bill", "namvet", "v_arms_bill"),
            new L4d2Survivor("Zoey", "teenangst", "v_arms_zoey", true),
            new L4d2Survivor("Francis", "biker", "v_arms_francis", true),
            new L4d2Survivor("Louis", "manager", "v_arms_louis")
        };

        public static L4d2Survivor Find(string key) => All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // A Left 4 Dead 2 install: its model compiler and VPK packer (from the Authoring Tools, in bin) and each survivor's
    // skeleton, attachments, IK chains and ragdoll limits, read from the game's VPKs.
    public sealed class L4d2Install
    {
        public string Folder { get; }
        public string StudioMdl => Path.Combine(Folder, "bin", "studiomdl.exe");
        public string VpkTool => Path.Combine(Folder, "bin", "vpk.exe");

        // Highest priority first, as in the game's gameinfo.txt.
        private static readonly string[] GameFolders = { "update", "left4dead2_dlc3", "left4dead2_dlc2", "left4dead2_dlc1", "left4dead2" };
        public IEnumerable<string> Content => GameFolders.Select(f => Path.Combine(Folder, f)).Where(Directory.Exists);

        private L4d2Install(string folder)
        {
            Folder = folder;
        }

        public static bool IsInstall(string folder) =>
            !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "left4dead2", "pak01_dir.vpk"));

        public static L4d2Install Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                string folder = Path.Combine(library, "steamapps", "common", "Left 4 Dead 2");
                if (IsInstall(folder))
                    return new L4d2Install(folder);
            }
            return null;
        }

        private List<Vpk> vpks;

        public byte[] ReadFile(string path)
        {
            // Only Valve's own packs: loose files in the game folders may be someone's mod.
            vpks = vpks ?? Content.Select(f => Path.Combine(f, "pak01_dir.vpk")).Where(File.Exists).Select(f => new Vpk(f)).ToList();
            return vpks.Select(v => v.Read(path)).FirstOrDefault(data => data != null)
                ?? throw new FileNotFoundException("Left 4 Dead 2 is missing " + path + ". Verify its files in Steam.");
        }

        private readonly Dictionary<string, ValvePlayer> survivors = new Dictionary<string, ValvePlayer>(StringComparer.Ordinal);

        public ValvePlayer Survivor(L4d2Survivor survivor)
        {
            if (!survivors.TryGetValue(survivor.Key, out ValvePlayer player))
                survivors[survivor.Key] = player = ValvePlayer.ReadSurvivor(this, survivor);
            return player;
        }
    }

    // One of Team Fortress 2's nine classes, whose model (and first-person arms) an export replaces.
    public sealed class Tf2Class
    {
        public string Name { get; }
        // models/player/<Key>.mdl and models/weapons/c_models/c_<Key>_arms.mdl
        public string Key { get; }
        // The HUD's class pictures: materials/hud/class_<HudKey>red and class_<HudKey>blue.
        public string HudKey { get; }
        // The class portraits: materials/vgui/class_portraits/<PortraitKey> (and _blue, _grey, _blue_grey).
        public string PortraitKey { get; }

        private Tf2Class(string name, string key, string hudKey = null, string portraitKey = null)
        {
            Name = name;
            Key = key;
            HudKey = hudKey ?? key;
            PortraitKey = portraitKey ?? key;
        }

        public string Model => "player/" + Key + ".mdl";
        public string ArmsModel => "weapons/c_models/c_" + Key + "_arms.mdl";

        public static IReadOnlyList<Tf2Class> All { get; } = new[]
        {
            new Tf2Class("Scout", "scout"),
            new Tf2Class("Soldier", "soldier"),
            new Tf2Class("Pyro", "pyro"),
            new Tf2Class("Demoman", "demo", portraitKey: "demoman"),
            new Tf2Class("Heavy", "heavy"),
            new Tf2Class("Engineer", "engineer", hudKey: "engi"),
            new Tf2Class("Medic", "medic"),
            new Tf2Class("Sniper", "sniper"),
            new Tf2Class("Spy", "spy")
        };

        public static Tf2Class Find(string key) => All.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // Team Fortress 2's bip_ skeleton by the ValveBiped names the Source exporter fits avatars to: a class is read with its
    // bones renamed, and the compiled files get the bip_ names back.
    internal static class Tf2Bones
    {
        private const string Valve = "ValveBiped.Bip01_";
        private static readonly Dictionary<string, string> ToValveNames = Build();
        private static readonly Dictionary<string, string> ToTf2Names = ToValveNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

        private static Dictionary<string, string> Build()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bip_pelvis"] = "Pelvis", ["bip_spine_0"] = "Spine", ["bip_spine_1"] = "Spine1", ["bip_spine_2"] = "Spine2",
                ["bip_spine_3"] = "Spine4", ["bip_neck"] = "Neck1", ["bip_head"] = "Head1"
            };
            string[] fingers = { "thumb", "index", "middle", "ring", "pinky" };
            foreach (string side in new[] { "L", "R" })
            {
                names["bip_collar_" + side] = side + "_Clavicle";
                names["bip_upperArm_" + side] = side + "_UpperArm";
                names["bip_lowerArm_" + side] = side + "_Forearm";
                names["bip_hand_" + side] = side + "_Hand";
                names["bip_hip_" + side] = side + "_Thigh";
                names["bip_knee_" + side] = side + "_Calf";
                names["bip_foot_" + side] = side + "_Foot";
                names["bip_toe_" + side] = side + "_Toe0";
                for (int f = 0; f < fingers.Length; f++)
                    for (int joint = 0; joint < 3; joint++)
                        names["bip_" + fingers[f] + "_" + joint + "_" + side] = side + "_Finger" + f + (joint == 0 ? "" : joint.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return names.ToDictionary(pair => pair.Key, pair => Valve + pair.Value, StringComparer.Ordinal);
        }

        public static string ToValve(string name) => ToValveNames.TryGetValue(name, out string valve) ? valve : name;
        public static string ToTf2(string name) => ToTf2Names.TryGetValue(name, out string tf2) ? tf2 : name;
    }

    // A Team Fortress 2 install: its model compiler and VPK packer (in bin; bin\x64's studiomdl lacks the mdllib.dll it
    // loads) and each class's skeleton,
    // attachments, IK chains, hitboxes, ragdoll limits and first-person arms, read from the game's VPKs.
    public sealed class Tf2Install
    {
        public string Folder { get; }
        public string StudioMdl => Tool("studiomdl.exe");
        public string VpkTool => Tool("vpk.exe");

        // A tool in bin (or bin\x64), by name.
        public string Tool(string name) =>
            new[] { Path.Combine(Folder, "bin", name), Path.Combine(Folder, "bin", "x64", name) }.FirstOrDefault(File.Exists) ?? Path.Combine(Folder, "bin", name);

        // Valve's packs, highest priority first as in tf/gameinfo.txt (directory files, "<name>_dir.vpk").
        private static readonly string[] Packs = { @"tf\tf2_textures_dir.vpk", @"tf\tf2_misc_dir.vpk", @"hl2\hl2_textures_dir.vpk", @"hl2\hl2_misc_dir.vpk" };
        private IEnumerable<string> PackFiles => Packs.Select(p => Path.Combine(Folder, p.Replace('\\', Path.DirectorySeparatorChar))).Where(File.Exists);
        // What studiomdl mounts: the packs (by the name gameinfo.txt gives them, without "_dir") and the game folders.
        public IEnumerable<string> Content => PackFiles.Select(f => f.Replace("_dir.vpk", ".vpk"))
            .Concat(new[] { "tf", "hl2" }.Select(f => Path.Combine(Folder, f)).Where(Directory.Exists));

        private Tf2Install(string folder)
        {
            Folder = folder;
        }

        public static bool IsInstall(string folder) =>
            !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "tf", "tf2_misc_dir.vpk"));

        public static Tf2Install Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                string folder = Path.Combine(library, "steamapps", "common", "Team Fortress 2");
                if (IsInstall(folder))
                    return new Tf2Install(folder);
            }
            return null;
        }

        private List<Vpk> vpks;

        public byte[] ReadFile(string path)
        {
            // Only Valve's own packs: loose files in tf/custom may be someone's mod.
            vpks = vpks ?? PackFiles.Select(f => new Vpk(f)).ToList();
            return vpks.Select(v => v.Read(path)).FirstOrDefault(data => data != null)
                ?? throw new FileNotFoundException("Team Fortress 2 is missing " + path + ". Verify its files in Steam.");
        }

        private readonly Dictionary<string, ValvePlayer> classes = new Dictionary<string, ValvePlayer>(StringComparer.Ordinal);

        public ValvePlayer Player(Tf2Class tf2Class)
        {
            if (!classes.TryGetValue(tf2Class.Key, out ValvePlayer player))
                classes[tf2Class.Key] = player = ValvePlayer.ReadTf2Class(this, tf2Class);
            return player;
        }
    }

    // Valve pack file (VPK v1/v2) directory reader.
    public sealed class Vpk
    {
        private readonly string directory;
        private readonly int dataOffset;
        private readonly Dictionary<string, (ushort Archive, uint Offset, uint Length, byte[] Preload)> entries =
            new Dictionary<string, (ushort, uint, uint, byte[])>(StringComparer.OrdinalIgnoreCase);

        public Vpk(string directoryFile)
        {
            directory = directoryFile;
            using (var reader = new BinaryReader(File.OpenRead(directoryFile)))
            {
                if (reader.ReadUInt32() != 0x55aa1234)
                    throw new InvalidDataException("Not a VPK file: " + directoryFile);
                uint version = reader.ReadUInt32();
                uint treeSize = reader.ReadUInt32();
                if (version == 2)
                    reader.ReadBytes(16);
                dataOffset = (int)reader.BaseStream.Position + (int)treeSize;
                string Text()
                {
                    var bytes = new List<byte>();
                    byte b;
                    while ((b = reader.ReadByte()) != 0)
                        bytes.Add(b);
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }
                for (string extension = Text(); extension.Length > 0; extension = Text())
                    for (string path = Text(); path.Length > 0; path = Text())
                        for (string name = Text(); name.Length > 0; name = Text())
                        {
                            reader.ReadUInt32();
                            ushort preload = reader.ReadUInt16(), archive = reader.ReadUInt16();
                            uint offset = reader.ReadUInt32(), length = reader.ReadUInt32();
                            reader.ReadUInt16();
                            byte[] preloaded = reader.ReadBytes(preload);
                            string full = (path == " " ? "" : path + "/") + name + (extension == " " ? "" : "." + extension);
                            entries[full] = (archive, offset, length, preloaded);
                        }
            }
        }

        public byte[] Read(string path)
        {
            if (!entries.TryGetValue(path.Replace('\\', '/'), out var entry))
                return null;
            string archive = entry.Archive == 0x7fff ? directory : directory.Replace("_dir.vpk", "_" + entry.Archive.ToString("000") + ".vpk");
            long offset = entry.Archive == 0x7fff ? dataOffset + entry.Offset : entry.Offset;
            var result = new byte[entry.Preload.Length + entry.Length];
            Buffer.BlockCopy(entry.Preload, 0, result, 0, entry.Preload.Length);
            using (var stream = File.OpenRead(archive))
            {
                stream.Position = offset;
                int read = 0;
                while (read < entry.Length)
                {
                    int n = stream.Read(result, entry.Preload.Length + read, (int)entry.Length - read);
                    if (n <= 0)
                        throw new EndOfStreamException("The VPK archive is truncated.");
                    read += n;
                }
            }
            return result;
        }
    }

    // Garry's Mod's player data: the ValveBiped skeleton every player animation (models/m_anm.mdl) is authored for,
    // plus a stock playermodel's attachments, IK chains and ragdoll joint limits.
    public sealed class ValvePlayer
    {
        public sealed class Bone
        {
            public string Name;
            public int Parent;
            public double[] LocalPosition;
            public double[] LocalRotation;   // 3x3 row-major
            public double[] WorldPosition;
            public double[] WorldRotation;
        }

        public sealed class Attachment
        {
            public string Name, Bone;
            public double[] Rotation, Position;
        }

        public sealed class IkChain
        {
            public string Name, Bone;
            public double[] Knee;
        }

        public sealed class Constraint
        {
            public string Bone;
            public double[] Min = new double[3], Max = new double[3], Friction = new double[3];
        }

        public List<Bone> Bones { get; } = new List<Bone>();
        public List<Attachment> Attachments { get; } = new List<Attachment>();
        public List<IkChain> IkChains { get; } = new List<IkChain>();
        public List<Constraint> Constraints { get; } = new List<Constraint>();
        // The animation models a survivor includes (game paths, "models/..."); empty for Garry's Mod's player.
        public List<string> IncludeModels { get; } = new List<string>();
        // Team Fortress 2: the hitboxes (bone and hit group; the head's group 1 takes headshots) and the class's first-person arms.
        public List<(string Bone, int Group)> Hitboxes { get; } = new List<(string, int)>();
        public ValvePlayer ViewModel { get; private set; }
        public double TotalMass = 90, Inertia = 10, Damping = 0.01, RotationalDamping = 1.5;

        public Bone Find(string name) => Bones.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // The rotation from the model file's own frame to the Z-up, -Y-forward frame the exporter works in (bones' world
        // transforms are in the exporter's frame; their local ones stay as in the file). Identity for Garry's Mod's player.
        // Team Fortress 2's classes are stored Y-up and +Z-forward: read as they were, the avatar was sized by its eye height
        // along Z (about 2 units instead of 65), so it came out 40 times too small and lying down, and broke in game.
        public double[] Frame { get; private set; } = Matrix.Identity;

        // Turns a Y-up skeleton (its head above its root along Y) into the exporter's Z-up frame.
        private void UseZUp()
        {
            Bone root = Bones.FirstOrDefault(b => b.Parent < 0);
            Bone head = Bones.FirstOrDefault(b => b.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
            if (root == null || head == null)
                return;
            double[] up = Matrix.Subtract(head.WorldPosition, root.WorldPosition);
            if (up[1] <= Math.Abs(up[2]) || up[1] <= Math.Abs(up[0]))
                return;
            UseFrame(new double[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 });
        }

        private void UseFrame(double[] frame)
        {
            Frame = frame;
            foreach (Bone bone in Bones)
            {
                bone.WorldRotation = Matrix.Multiply(frame, bone.WorldRotation);
                bone.WorldPosition = Matrix.Apply(frame, bone.WorldPosition);
            }
        }

        public const string AnimationModel = "models/m_anm.mdl";
        public const string StockModel = "models/player/kleiner";

        public static ValvePlayer Read(GarrysModInstall install)
        {
            var player = new ValvePlayer();
            byte[] animations = install.ReadFile(AnimationModel);
            foreach (var bone in Mdl.Bones(animations))
                player.Bones.Add(bone);
            SetWorld(player.Bones);
            byte[] stock = install.ReadFile(StockModel + ".mdl");
            player.Attachments.AddRange(Mdl.Attachments(stock));
            player.IkChains.AddRange(Mdl.IkChains(stock));
            player.ReadPhysics(install.ReadFile(StockModel + ".phy"));
            return player;
        }

        // A Left 4 Dead 2 survivor: the skeleton of the animation model it includes (anim_<name>.mdl, which every one of its
        // animations is authored for), with the survivor model's own attachments, IK chains and ragdoll limits.
        internal static ValvePlayer ReadSurvivor(L4d2Install install, L4d2Survivor survivor)
        {
            string path = "models/" + survivor.Model;
            byte[] model = install.ReadFile(path);
            string animations = Mdl.Includes(model).FirstOrDefault(p => Path.GetFileName(p).StartsWith("anim_", StringComparison.OrdinalIgnoreCase))
                ?? "models/survivors/anim_" + survivor.Key + ".mdl";
            return ReadWithAnimations(install.ReadFile, path, model, animations);
        }

        // A Team Fortress 2 class, the same way (its animations are in <class>_animations.mdl), with its hitboxes and its
        // first-person arms (ViewModel), every bip_ bone going by its ValveBiped name. The arms' root takes the place of the
        // upper spine, which it matches.
        internal static ValvePlayer ReadTf2Class(Tf2Install install, Tf2Class tf2Class)
        {
            string path = "models/" + tf2Class.Model;
            byte[] model = install.ReadFile(path);
            string animations = Mdl.Includes(model).FirstOrDefault(p => Path.GetFileName(p).Equals(tf2Class.Key + "_animations.mdl", StringComparison.OrdinalIgnoreCase))
                ?? "models/player/" + tf2Class.Key + "_animations.mdl";
            ValvePlayer player = ReadWithAnimations(install.ReadFile, path, model, animations);
            player.Hitboxes.AddRange(Mdl.Hitboxes(model));
            player.Rename(Tf2Bones.ToValve);
            player.UseZUp();

            byte[] arms = install.ReadFile("models/" + tf2Class.ArmsModel);
            var viewModel = new ValvePlayer();
            viewModel.Bones.AddRange(Mdl.Bones(arms));
            SetWorld(viewModel.Bones);
            // The arms are stored in the class's frame; their root (the upper spine) has no "head" to find it by.
            viewModel.UseFrame(player.Frame);
            viewModel.Attachments.AddRange(Mdl.Attachments(arms));
            viewModel.IncludeModels.AddRange(Mdl.Includes(arms));
            viewModel.Rename(name => name == "root" ? Tf2Bones.ToValve("bip_spine_3") : Tf2Bones.ToValve(name));
            player.ViewModel = viewModel;
            return player;
        }

        private void Rename(Func<string, string> rename)
        {
            foreach (Bone bone in Bones)
                bone.Name = rename(bone.Name);
            foreach (Attachment attachment in Attachments)
                attachment.Bone = rename(attachment.Bone);
            foreach (IkChain chain in IkChains)
                chain.Bone = rename(chain.Bone);
            foreach (Constraint constraint in Constraints)
                constraint.Bone = rename(constraint.Bone);
            for (int i = 0; i < Hitboxes.Count; i++)
                Hitboxes[i] = (rename(Hitboxes[i].Bone), Hitboxes[i].Group);
        }

        // A model's skeleton from the animation model it includes, with the model's own attachments, IK chains and ragdoll limits.
        private static ValvePlayer ReadWithAnimations(Func<string, byte[]> read, string path, byte[] model, string animations)
        {
            var player = new ValvePlayer();
            player.IncludeModels.AddRange(Mdl.Includes(model));
            player.Bones.AddRange(Mdl.Bones(read(animations)));
            SetWorld(player.Bones);
            // Attachments on the survivor's own extra bones (Zoey's holster, a bandage helper) move to the nearest bone up its
            // chain that the animations have, keeping where they are.
            List<Bone> own = Mdl.Bones(model).ToList();
            SetWorld(own);
            foreach (Attachment attachment in Mdl.Attachments(model))
            {
                int index = own.FindIndex(b => b.Name == attachment.Bone);
                int target = index;
                while (target >= 0 && player.Find(own[target].Name) == null)
                    target = own[target].Parent;
                if (target < 0)
                    continue;
                if (target != index)
                {
                    Bone from = own[index], to = own[target];
                    double[] rotation = Matrix.Multiply(from.WorldRotation, attachment.Rotation);
                    double[] position = Matrix.Add(from.WorldPosition, Matrix.Apply(from.WorldRotation, attachment.Position));
                    double[] inverse = Matrix.Transpose(to.WorldRotation);
                    attachment.Bone = to.Name;
                    attachment.Rotation = Matrix.Multiply(inverse, rotation);
                    attachment.Position = Matrix.Apply(inverse, Matrix.Subtract(position, to.WorldPosition));
                }
                player.Attachments.Add(attachment);
            }
            player.IkChains.AddRange(Mdl.IkChains(model).Where(chain => player.Find(chain.Bone) != null));
            player.ReadPhysics(read(Path.ChangeExtension(path, ".phy")));
            return player;
        }

        private static void SetWorld(List<Bone> bones)
        {
            foreach (Bone bone in bones)
            {
                if (bone.Parent < 0)
                {
                    bone.WorldRotation = bone.LocalRotation;
                    bone.WorldPosition = bone.LocalPosition;
                }
                else
                {
                    Bone parent = bones[bone.Parent];
                    bone.WorldRotation = Matrix.Multiply(parent.WorldRotation, bone.LocalRotation);
                    bone.WorldPosition = Matrix.Add(parent.WorldPosition, Matrix.Apply(parent.WorldRotation, bone.LocalPosition));
                }
            }
        }

        // The text section at the end of a .phy file: solids (names, physics settings) and ragdoll constraints.
        private void ReadPhysics(byte[] phy)
        {
            int position = BitConverter.ToInt32(phy, 0);
            int solids = BitConverter.ToInt32(phy, 8);
            for (int i = 0; i < solids; i++)
                position += 4 + BitConverter.ToInt32(phy, position);
            string text = Encoding.ASCII.GetString(phy, position, phy.Length - position);
            var names = new Dictionary<int, string>();
            foreach (Match block in Regex.Matches(text, @"(\w+)\s*\{([^{}]*)\}"))
            {
                var values = Regex.Matches(block.Groups[2].Value, "\"([^\"]*)\"\\s+\"([^\"]*)\"").Cast<Match>()
                    .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value)).ToList();
                string Get(string key) => values.FirstOrDefault(v => v.Key == key).Value;
                double Number(string key, double fallback) =>
                    double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
                switch (block.Groups[1].Value)
                {
                    case "solid":
                        names[(int)Number("index", -1)] = Get("name");
                        Inertia = Number("inertia", Inertia);
                        Damping = Number("damping", Damping);
                        RotationalDamping = Number("rotdamping", RotationalDamping);
                        break;
                    case "ragdollconstraint":
                        if (!names.TryGetValue((int)Number("child", -1), out string child))
                            break;
                        var constraint = new Constraint { Bone = child };
                        string[] axes = { "x", "y", "z" };
                        for (int axis = 0; axis < 3; axis++)
                        {
                            constraint.Min[axis] = Number(axes[axis] + "min", 0);
                            constraint.Max[axis] = Number(axes[axis] + "max", 0);
                            constraint.Friction[axis] = Number(axes[axis] + "friction", 0);
                        }
                        Constraints.Add(constraint);
                        break;
                    case "editparams":
                        TotalMass = Number("totalmass", TotalMass);
                        break;
                }
            }
        }
    }

    // Minimal compiled-model (.mdl v44-49) reader for bones, attachments and IK chains.
    internal static class Mdl
    {
        private static string Text(byte[] data, int offset)
        {
            int end = Array.IndexOf(data, (byte)0, offset);
            return Encoding.ASCII.GetString(data, offset, (end < 0 ? data.Length : end) - offset);
        }

        private static double[] Vector(byte[] data, int offset, int count = 3) =>
            Enumerable.Range(0, count).Select(i => (double)BitConverter.ToSingle(data, offset + 4 * i)).ToArray();

        private static void Check(byte[] data)
        {
            if (data.Length < 408 || Encoding.ASCII.GetString(data, 0, 4) != "IDST")
                throw new InvalidDataException("Not a Source model file.");
        }

        public static List<string> BoneNames(byte[] data)
        {
            int count = BitConverter.ToInt32(data, 156), index = BitConverter.ToInt32(data, 160);
            return Enumerable.Range(0, count).Select(i => index + 216 * i).Select(o => Text(data, o + BitConverter.ToInt32(data, o))).ToList();
        }

        // The models whose animations this one includes ($includemodel), as game paths ("models/...").
        public static List<string> Includes(byte[] data)
        {
            Check(data);
            int count = BitConverter.ToInt32(data, 336), index = BitConverter.ToInt32(data, 340);
            return Enumerable.Range(0, count).Select(i => index + 8 * i).Select(o => Text(data, o + BitConverter.ToInt32(data, o + 4))).ToList();
        }

        public static IEnumerable<ValvePlayer.Bone> Bones(byte[] data)
        {
            Check(data);
            int count = BitConverter.ToInt32(data, 156), index = BitConverter.ToInt32(data, 160);
            for (int i = 0; i < count; i++)
            {
                int o = index + 216 * i;
                double[] q = Vector(data, o + 44, 4);
                yield return new ValvePlayer.Bone
                {
                    Name = Text(data, o + BitConverter.ToInt32(data, o)),
                    Parent = BitConverter.ToInt32(data, o + 4),
                    LocalPosition = Vector(data, o + 32),
                    LocalRotation = Matrix.FromQuaternion(q[0], q[1], q[2], q[3])
                };
            }
        }

        // The first hitbox set's boxes: each one's bone and hit group.
        public static List<(string Bone, int Group)> Hitboxes(byte[] data)
        {
            Check(data);
            List<string> bones = BoneNames(data);
            var result = new List<(string, int)>();
            if (BitConverter.ToInt32(data, 172) == 0)
                return result;
            int set = BitConverter.ToInt32(data, 176);
            int count = BitConverter.ToInt32(data, set + 4), index = set + BitConverter.ToInt32(data, set + 8);
            for (int i = 0; i < count; i++)
                result.Add((bones[BitConverter.ToInt32(data, index + 68 * i)], BitConverter.ToInt32(data, index + 68 * i + 4)));
            return result;
        }

        public static IEnumerable<ValvePlayer.Attachment> Attachments(byte[] data)
        {
            Check(data);
            List<string> bones = BoneNames(data);
            int count = BitConverter.ToInt32(data, 240), index = BitConverter.ToInt32(data, 244);
            for (int i = 0; i < count; i++)
            {
                int o = index + 92 * i;
                double[] m = Vector(data, o + 12, 12);
                yield return new ValvePlayer.Attachment
                {
                    Name = Text(data, o + BitConverter.ToInt32(data, o)),
                    Bone = bones[BitConverter.ToInt32(data, o + 8)],
                    Rotation = new[] { m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10] },
                    Position = new[] { m[3], m[7], m[11] }
                };
            }
        }

        public static IEnumerable<ValvePlayer.IkChain> IkChains(byte[] data)
        {
            Check(data);
            List<string> bones = BoneNames(data);
            int count = BitConverter.ToInt32(data, 284), index = BitConverter.ToInt32(data, 288);
            for (int i = 0; i < count; i++)
            {
                int o = index + 16 * i;
                int links = BitConverter.ToInt32(data, o + 8), linkIndex = o + BitConverter.ToInt32(data, o + 12);
                if (links == 0)
                    continue;
                double[] knee = Vector(data, linkIndex + 4);
                yield return new ValvePlayer.IkChain
                {
                    Name = Text(data, o + BitConverter.ToInt32(data, o)),
                    Bone = bones[BitConverter.ToInt32(data, linkIndex + 28 * (links - 1))],
                    Knee = knee
                };
            }
        }
    }

    // 3x3 row-major rotation helpers shared by the Source exporter.
    internal static class Matrix
    {
        public static double[] Identity => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        public static double[] FromQuaternion(double x, double y, double z, double w) => new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
        };

        public static double[] Multiply(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        public static double[] Transpose(double[] m) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

        public static double[] Apply(double[] m, double[] v) => new[]
        {
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]
        };

        public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        public static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        public static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        public static double Length(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        // Source's RadianEuler / SMD rotation (x, y, z), where the matrix is Rz * Ry * Rx.
        public static double[] ToEuler(double[] m)
        {
            double cy = Math.Sqrt(m[0] * m[0] + m[3] * m[3]);
            if (cy > 1e-6)
                return new[] { Math.Atan2(m[7], m[8]), Math.Atan2(-m[6], cy), Math.Atan2(m[3], m[0]) };
            return new[] { Math.Atan2(-m[5], m[4]), Math.Atan2(-m[6], cy), 0.0 };
        }

        public static double[] FromEuler(double x, double y, double z)
        {
            double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
            return new[]
            {
                cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx,
                sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx,
                -sy, cy * sx, cy * cx
            };
        }
    }
}
