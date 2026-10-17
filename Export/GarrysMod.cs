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

        private static IEnumerable<string> SteamLibraries()
        {
            var steams = new List<string>();
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
        public double TotalMass = 90, Inertia = 10, Damping = 0.01, RotationalDamping = 1.5;

        public Bone Find(string name) => Bones.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public const string AnimationModel = "models/m_anm.mdl";
        public const string StockModel = "models/player/kleiner";

        public static ValvePlayer Read(GarrysModInstall install)
        {
            var player = new ValvePlayer();
            byte[] animations = install.ReadFile(AnimationModel);
            foreach (var bone in Mdl.Bones(animations))
                player.Bones.Add(bone);
            for (int i = 0; i < player.Bones.Count; i++)
            {
                Bone bone = player.Bones[i];
                if (bone.Parent < 0)
                {
                    bone.WorldRotation = bone.LocalRotation;
                    bone.WorldPosition = bone.LocalPosition;
                }
                else
                {
                    Bone parent = player.Bones[bone.Parent];
                    bone.WorldRotation = Matrix.Multiply(parent.WorldRotation, bone.LocalRotation);
                    bone.WorldPosition = Matrix.Add(parent.WorldPosition, Matrix.Apply(parent.WorldRotation, bone.LocalPosition));
                }
            }
            byte[] stock = install.ReadFile(StockModel + ".mdl");
            player.Attachments.AddRange(Mdl.Attachments(stock));
            player.IkChains.AddRange(Mdl.IkChains(stock));
            player.ReadPhysics(install.ReadFile(StockModel + ".phy"));
            return player;
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
