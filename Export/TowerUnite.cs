using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rbx2Source.Export
{
    // A Tower Unite install, for the Workshop character skeleton its SDK ships (Tower/Workshop/SDK).
    public sealed class TowerUniteInstall
    {
        public string Folder { get; }
        public string Rig => Path.Combine(Folder, "Tower", "Workshop", "SDK", "TowerUniteWorkshop_rig.dae");

        private TowerUniteInstall(string folder)
        {
            Folder = folder;
        }

        public static TowerUniteInstall Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                var install = new TowerUniteInstall(Path.Combine(library, "steamapps", "common", "Tower Unite"));
                if (File.Exists(install.Rig))
                    return install;
            }
            return null;
        }
    }

    // Material slots for a game's importer: names it accepts (letters, digits and underscores, unique), and each slot's
    // textures copied beside the model as <Slot>_diffuse.png and <Slot>_normal.png.
    internal static class GameMaterials
    {
        public static Dictionary<string, string> Rename(WavefrontModel model, Dictionary<string, string> textures, Dictionary<string, MaterialMaps> maps)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string material in model.Triangles.Select(t => t.Material).Distinct())
            {
                string name = Regex.Replace(material, "[^A-Za-z0-9_]", "_").Trim('_');
                if (name.Length == 0 || char.IsDigit(name[0]))
                    name = "Material_" + name;
                string unique = name;
                for (int n = 2; !used.Add(unique); n++)
                    unique = name + "_" + n;
                names[material] = unique;
            }
            var materials = model.Materials.ToDictionary(pair => pair.Key, pair => pair.Value);
            model.Materials.Clear();
            foreach (var pair in names)
            {
                ObjMaterial material = materials[pair.Key];
                material.Name = pair.Value;
                model.Materials[pair.Value] = material;
            }
            foreach (ObjTriangle triangle in model.Triangles)
                triangle.Material = names[triangle.Material];
            foreach (var pair in names)
            {
                if (textures.Remove(pair.Key, out string texture))
                    textures[pair.Value] = texture;
                if (maps.Remove(pair.Key, out MaterialMaps extra))
                    maps[pair.Value] = extra;
            }
            return names;
        }

        // Copies a texture beside the model as a PNG with the given name (relative to folder); returns that name.
        public static string Copy(string folder, string relative, string fileName)
        {
            string source = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(folder, fileName);
            if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, true);
            else
                using (Picture picture = Picture.Load(source))
                    picture.Save(target, PictureFormat.Png);
            return fileName;
        }
    }

    // Builds Tower Unite Workshop imports. An avatar becomes a player model (Workshop Editor > Player Model/Item Creation >
    // Player Model): a COLLADA file rigged to Tower Unite's mannequin skeleton, read from the game's SDK, in its frame (Z up,
    // centimetres) with one mesh and one texture. Catalog items and models become items: an FBX in the same frame with each
    // material's textures named for the importer.
    public static class TowerUniteExporter
    {
        // Tower Unite's limit for player models.
        public const int MaxPlayerVertices = 30000;

        // Scene (Y up, facing +Z) to Tower Unite (Z up, facing -Y, left +X), and back.
        private static readonly double[] FromScene = { 1, 0, 0, 0, 0, -1, 0, 1, 0 };
        private static readonly double[] ToScene = Matrix.Transpose(FromScene);

        public static TargetSkeleton Skeleton(TowerUniteInstall install)
        {
            TargetSkeleton skeleton = TargetSkeleton.FromCollada(install.Rig, ToScene, 0.01);
            string Side(string side) => side == "Left" ? "_l" : "_r";
            string[] fingers = { "thumb", "index", "middle", "ring", "pinky" };
            string[] rigFingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            string[] phalanges = { "Proximal", "Intermediate", "Distal" };
            skeleton.MapHumanoid(part =>
            {
                switch (part)
                {
                    case "Hips": return "pelvis";
                    case "Spine": return "spine_01";
                    case "Chest": return "spine_02";
                    case "UpperChest": return "spine_03";
                    case "Neck": return "neck_01";
                    case "Head": return "head";
                }
                return null;
            }, (side, part) =>
            {
                switch (part)
                {
                    case "Shoulder": return "clavicle" + Side(side);
                    case "UpperArm": return "upperarm" + Side(side);
                    case "LowerArm": return "lowerarm" + Side(side);
                    case "Hand": return "hand" + Side(side);
                    case "UpperLeg": return "thigh" + Side(side);
                    case "LowerLeg": return "calf" + Side(side);
                    case "Foot": return "foot" + Side(side);
                    case "Toes": return "ball" + Side(side);
                }
                for (int f = 0; f < fingers.Length; f++)
                    for (int p = 0; p < phalanges.Length; p++)
                        if (part == rigFingers[f] + phalanges[p])
                            return fingers[f] + "_0" + (p + 1) + Side(side);
                return null;
            });
            return skeleton;
        }

        public static string Export(ExportScene scene, Dictionary<string, string> textures, Dictionary<string, MaterialMaps> maps, string folder,
            string name, TowerUniteInstall install, IProgress<string> progress, CancellationToken cancellation)
        {
            WavefrontModel model = scene.Model;
            string file = Regex.Replace(name, "[^A-Za-z0-9_]", "_");
            GameMaterials.Rename(model, textures, maps);
            var images = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string material in model.Triangles.Select(t => t.Material).Distinct())
            {
                // Tower Unite's importer looks for the lower-case names (it warned about "_Diffuse" and fell back to "_diffuse").
                if (textures.TryGetValue(material, out string texture))
                    images[material] = GameMaterials.Copy(folder, texture, material + "_diffuse.png");
                if (maps.TryGetValue(material, out MaterialMaps extra) && extra.Normal != null)
                    GameMaterials.Copy(folder, extra.Normal, material + "_normal.png");
            }

            if (scene.Rig == null)
            {
                progress?.Report("Writing Tower Unite item (" + model.Triangles.Count + " triangles)...");
                var relative = images.ToDictionary(pair => pair.Key, pair => pair.Value);
                FbxWriter.Write(scene, relative, null, Path.Combine(folder, file + ".fbx"), cancellation, new FbxWriter.Options
                {
                    FromScene = FromScene, UnitsPerMetre = 100, UnitScaleFactor = 1, UpAxis = 2, FrontAxis = 1, FrontAxisSign = -1,
                    Shapes = false, EmbedTextures = false
                });
                return file + ".fbx";
            }

            TargetSkeleton skeleton = Skeleton(install);
            SourceExporter.BakeDefaultExpression(model, scene.Face);
            skeleton.Pose(model, scene.Rig);
            // The avatar's head goes where the mannequin's is (so Tower Unite's camera and animations fit), then the export scale.
            double fit = skeleton.FitScale(new ExportScene(model, scene.Rig, null, 1));
            var fitted = new ExportScene(model, scene.Rig, null, (float)(fit * scene.Scale));
            List<TargetSkeleton.Bone> bones = skeleton.Fit(fitted);
            string dae = Path.Combine(folder, file + ".dae");
            int Write()
            {
                ColladaWriter.Write(fitted, bones, skeleton.RigToBone(), images, dae, new ColladaWriter.Options
                {
                    FromScene = FromScene, UnitsPerMetre = 100, UnitName = "centimeter", MetresPerUnit = 0.01, UpAxis = "Z_UP"
                }, cancellation);
                // Tower Unite splits vertices further than a plain count shows (about 7% more), so its own compiler is asked.
                return CompiledVertexCount(install, dae, folder) ?? (int)(ColladaWriter.EngineVertexCount(model) * EstimatedSplit);
            }
            progress?.Report("Writing Tower Unite player model (" + model.Triangles.Count + " triangles, " + bones.Count + " bones)...");
            int vertices = Write();
            // Over Tower Unite's limit: the shortest edges are collapsed (where it changes the shape least) until the model fits.
            for (int pass = 0; pass < MaxReductionPasses && vertices > MaxPlayerVertices; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                int before = vertices;
                // Tower Unite counts more vertices than the mesh has, so a little more than the excess is removed each pass.
                int removed = Reduce(model, (int)Math.Ceiling((vertices - MaxPlayerVertices + ReductionMargin) / EstimatedSplit));
                if (removed == 0)
                    break;
                vertices = Write();
                progress?.Report("Tower Unite counts " + before + " vertices (the limit is " + MaxPlayerVertices + "); " + removed +
                    " of the smallest details were merged: " + vertices + ".");
            }
            progress?.Report("Tower Unite player model: " + vertices + " vertices.");
            if (vertices > MaxPlayerVertices)
                progress?.Report("Warning: Tower Unite accepts player models of up to " + MaxPlayerVertices + " vertices; this one still has " + vertices +
                    ". Take off some accessories, or reduce it in Blender, before importing.");
            return file + ".dae";
        }

        private const double EstimatedSplit = 1.08;
        private const int MaxReductionPasses = 6, ReductionMargin = 150;

        // Tower Unite's own vertex count for a model, from its model compiler (Tower/Workshop/Model/ModelCompiler.exe), or null
        // where it can't run. Its output goes to a folder inside the export (named relative to the compiler's "built" folder,
        // where it always writes), not into the game folder, and is deleted.
        private static int? CompiledVertexCount(TowerUniteInstall install, string dae, string folder)
        {
            string tools = Path.Combine(install.Folder, "Tower", "Workshop", "Model");
            string compiler = Path.Combine(tools, "ModelCompiler.exe");
            string output = Path.Combine(folder, "tu_compile");
            if (!OperatingSystem.IsWindows() || !File.Exists(compiler))
                return null;
            string relative = Path.GetRelativePath(Path.Combine(tools, "built"), output);
            if (Path.IsPathRooted(relative))
                return null;
            try
            {
                Directory.CreateDirectory(output);
                var start = new System.Diagnostics.ProcessStartInfo(compiler, "\"" + dae + "\" \"" + relative + "\" PLAYERMODEL")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = tools
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    string log = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(120000))
                    {
                        process.Kill();
                        return null;
                    }
                    Match count = Regex.Match(log, @"Total Vertices:\s*(\d+)");
                    return count.Success ? int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(output))
                        Directory.Delete(output, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
        }

        // Merges up to count vertices into a neighbour, shortest edges first: only inside one part and one stretch of texture
        // (never on a mesh border or a UV seam), and not twice around the same spot in one call, so the shape, texture
        // mapping and outline hardly change. The merged vertex takes its neighbour's texture coordinate, normal and skin
        // weights; triangles left with no area are removed. Returns how many were merged.
        private static int Reduce(WavefrontModel model, int count)
        {
            var edgeUse = new Dictionary<(int, int), int>();
            var trianglesAt = new Dictionary<int, List<ObjTriangle>>();
            var textureAt = new Dictionary<int, HashSet<int>>();
            var groupAt = new Dictionary<int, HashSet<string>>();
            foreach (ObjTriangle triangle in model.Triangles)
                for (int c = 0; c < 3; c++)
                {
                    int p = triangle.Corners[c].Position, q = triangle.Corners[(c + 1) % 3].Position;
                    var edge = p < q ? (p, q) : (q, p);
                    edgeUse[edge] = (edgeUse.TryGetValue(edge, out int used) ? used : 0) + 1;
                    if (!trianglesAt.TryGetValue(p, out List<ObjTriangle> list))
                        trianglesAt[p] = list = new List<ObjTriangle>();
                    list.Add(triangle);
                    if (!textureAt.TryGetValue(p, out HashSet<int> uvs))
                        textureAt[p] = uvs = new HashSet<int>();
                    uvs.Add(triangle.Corners[c].Texture);
                    if (!groupAt.TryGetValue(p, out HashSet<string> groups))
                        groupAt[p] = groups = new HashSet<string>(StringComparer.Ordinal);
                    groups.Add(triangle.Group);
                }
            var border = new HashSet<int>(edgeUse.Where(pair => pair.Value != 2).SelectMany(pair => new[] { pair.Key.Item1, pair.Key.Item2 }));
            bool Free(int p) => !border.Contains(p) && textureAt[p].Count == 1 && groupAt[p].Count == 1;
            double Length((int, int) edge)
            {
                float[] a = model.Positions[edge.Item1], b = model.Positions[edge.Item2];
                double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
                return x * x + y * y + z * z;
            }
            var candidates = edgeUse.Keys.Where(edge => Free(edge.Item1) && Free(edge.Item2) &&
                groupAt[edge.Item1].First() == groupAt[edge.Item2].First()).OrderBy(Length).ToList();

            var locked = new HashSet<int>();
            int merged = 0;
            foreach (var (keep, gone) in candidates)
            {
                if (merged >= count)
                    break;
                if (locked.Contains(keep) || locked.Contains(gone))
                    continue;
                ObjCorner model0 = trianglesAt[keep][0].Corners.First(c => c.Position == keep);
                foreach (ObjTriangle triangle in trianglesAt[gone])
                    foreach (ObjCorner corner in triangle.Corners.Where(c => c.Position == gone))
                    {
                        corner.Position = keep;
                        corner.Texture = model0.Texture;
                        corner.Normal = model0.Normal;
                    }
                // Nothing around either end changes again this call, so every merge sees the mesh as it really is.
                foreach (int p in trianglesAt[keep].Concat(trianglesAt[gone]).SelectMany(t => t.Corners).Select(c => c.Position))
                    locked.Add(p);
                merged++;
            }
            model.Triangles.RemoveAll(t => t.Corners[0].Position == t.Corners[1].Position || t.Corners[1].Position == t.Corners[2].Position ||
                t.Corners[0].Position == t.Corners[2].Position);
            return merged;
        }
    }
}
