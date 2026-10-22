using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rbx2Source.Export
{
    // Builds a Garry's Mod map from a Roblox place. The place's large blocks and wedges become brushes (solid, lightmapped world
    // geometry) as far as Source's limits allow; meshes become static props; everything else (small details, spheres,
    // cylinders, unions, and parts past the brush limit) is merged into static props by area, each part keeping its own
    // collision. Terrain becomes displacements. The place's lights, sun, sky and spawn points carry over, a sky shell seals it,
    // and falling off the world kills, as in Roblox. Garry's Mod's own studiomdl, vbsp, vvis and vrad compile it.
    public static class SourceMap
    {
        // Player size, as for avatars: a Roblox character's eyes (about 4.6 studs up) at a Garry's Mod player's (64 units).
        public const double UnitsPerStud = 14;

        // Builds the map in folder (an addon folder: maps\, materials\, models\, with the sources in src\) and returns the
        // map's path in it.
        public static Task<string> ExportAsync(ModelContents contents, Func<long, CancellationToken, Task<byte[]>> download, string folder,
            string name, float scale, GarrysModInstall install, IProgress<string> progress, CancellationToken cancellation) =>
            new Builder(contents, download, folder, name, UnitsPerStud * scale, install, progress, cancellation).ExportAsync();

        // The map's name in Garry's Mod: gm_ and the place's name (Garry's Mod lists gm_ maps under Sandbox).
        public static string MapName(string name) => "gm_" + Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]", "_");

        private sealed class Side
        {
            public double[] A, B, C, Normal;
            public string Material;
            public double Area, Extent, MinZ, MaxZ;
            // World units per texel of the face's texture (sets how often a Roblox material's texture repeats).
            public double TextureScale = 0.25;
            // A displacement's dispinfo block (terrain), written inside the side.
            public string Displacement;
        }

        private enum Group { World, Detail, Illusionary, LitHolder, LitHolderPassable, Trigger }

        private sealed class Brush
        {
            public List<Side> Sides;
            public Group Group;
        }

        // A mesh compiled once and placed wherever it appears (MeshParts and file meshes).
        private sealed class Prop
        {
            public string Name, Model, Smd, Surface;
            public bool Solid, Collides, Compiled;
            public double[] Min, Max;
        }

        // One part's share of a merged prop: its triangles in map coordinates with their materials, and its collision hull
        // (the triangles of one convex shape).
        private sealed class Piece
        {
            public double[] Center;
            public readonly List<double[]> Points = new List<double[]>();
            public readonly List<double[]> Normals = new List<double[]>();
            public readonly List<float[]> Uvs = new List<float[]>();
            public readonly List<(int A, int B, int C, string Material)> Triangles = new List<(int, int, int, string)>();
            public readonly List<double[][]> Hull = new List<double[][]>();
            // Decals are already cut into cells over their part's face and need no more vertices for lighting.
            public bool Decal;
        }

        // A merged prop: the pieces in one area of the map, up to studiomdl's comfortable size.
        private sealed class Chunk
        {
            public string Name, Model;
            public double[] Origin;
            public readonly List<Piece> Pieces = new List<Piece>();
            public readonly HashSet<string> Materials = new HashSet<string>(StringComparer.Ordinal);
            public int Triangles, Hulls;
            public bool Compiled, Collides;
        }

        private sealed class Builder
        {
            // Source maps end at ±16384 units. The sky shell (16 units thick) leaves room around the place: to the sides,
            // above (for jumping and the sky) and below (where falling off the world kills).
            private const double Limit = 16384 - 16, SidePadding = 512, TopPadding = 2048, BottomPadding = 256;
            // vbsp's limits: 65536 planes, counting each plane and its flip, and the bevel planes it adds to every brush for
            // collision (a turned brush gets many). Parts beyond them are merged into props instead.
            private const int MaxBrushes = 30000, PlaneBudget = 44000;
            // Parts smaller than this (units) are details: merged into props rather than taking brushes.
            private const double DetailSize = 20;
            // Parts thinner than this are thickened to it: vbsp drops thinner brushes.
            private const double MinThickness = 0.5;
            // Luxels in the whole map's lightmaps: past this the lightmaps get coarser so the map stays within Source's limits.
            private const double LuxelBudget = 2.5e6;
            // How strongly materials glow by the place's Lighting.Ambient (see AmbientGlow).
            private const double AmbientGlowScale = 1, MaxAmbientGlow = 0.35;
            // Units per luxel on terrain displacements, set with the rest of the lightmap detail.
            private int terrainLuxel = 16;
            // Merged props: areas this wide, split further past these sizes.
            private const double CellSize = 2048;
            private const int ChunkTriangles = 12000, ChunkHulls = 400, ChunkMaterials = 24;
            // Meshes get their own models up to this many, and repeated unions up to this many more; the rest are merged too.
            private const int MaxPropModels = 120, MaxUnionModels = 300;
            // Terrain is lifted this far above parts at its height (a baseplate under it), so the two don't flicker.
            private const double TerrainLift = 1;

            private readonly ModelContents contents;
            private readonly Func<long, CancellationToken, Task<byte[]>> download;
            private readonly string folder, map, id, materialDir, materialFolder;
            private readonly double units;
            private readonly GarrysModInstall install;
            private readonly IProgress<string> progress;
            private readonly CancellationToken cancellation;
            private readonly ModelFileBuilder.MeshCache meshes;
            private readonly ModelFileBuilder.UnionCache unions;
            private readonly Dictionary<long, string> textureKeys = new Dictionary<long, string>();
            private readonly Dictionary<string, byte[]> textureBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private readonly List<Brush> brushes = new List<Brush>();
            private readonly List<string> entities = new List<string>();
            private readonly HashSet<string> materials = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, Prop> props = new Dictionary<string, Prop>(StringComparer.Ordinal);
            private readonly List<KeyValuePair<Prop, PlacedPart>> placements = new List<KeyValuePair<Prop, PlacedPart>>();
            private readonly List<Piece> pieces = new List<Piece>();
            private readonly List<Chunk> chunks = new List<Chunk>();
            private HashSet<RobloxInstance> lightHolders;
            // The distinct planes of the brushes so far (a plane and its flip count once), and vbsp's estimated plane count.
            private readonly HashSet<(long, long, long, long)> planes = new HashSet<(long, long, long, long)>();
            private int planeEntries;
            // The colours of merged props: one palette texture, a texel per colour (with its opacity).
            private readonly Dictionary<int, int> palette = new Dictionary<int, int>();
            private const int PaletteSize = 64;
            // The map's picture, and each material's average colour for it.
            private readonly PlaceRender render = new PlaceRender();
            private readonly Dictionary<string, float[]> materialColors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            private double[] sunTravel = { 0.3, 0.3, -1 };
            private double[] center = new double[3];
            private readonly double[] min = { double.MaxValue, double.MaxValue, double.MaxValue };
            private readonly double[] max = { double.MinValue, double.MinValue, double.MinValue };
            private int nextId = 1, missing, merged, displacements, decals, missingDecals;
            // Each decal image once: its texture's name and whether it has see-through parts (null when it couldn't be had).
            private readonly Dictionary<string, (string Name, bool Alpha)> decalTextures = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            // Parts too far away for the map were left out (their decals with them).
            private bool clipped;

            public Builder(ModelContents contents, Func<long, CancellationToken, Task<byte[]>> download, string folder, string name, double units,
                GarrysModInstall install, IProgress<string> progress, CancellationToken cancellation)
            {
                this.contents = contents;
                this.download = download;
                this.folder = folder;
                this.units = units;
                this.install = install;
                this.progress = progress;
                this.cancellation = cancellation;
                map = MapName(name);
                id = map.Substring(3);
                materialDir = "roporter/maps/" + id + "/";
                materialFolder = Path.Combine(folder, "materials", "roporter", "maps", id);
                meshes = new ModelFileBuilder.MeshCache(download, progress);
                unions = new ModelFileBuilder.UnionCache(download, progress);
                missing = contents.MissingMeshes;
            }

            private string Tool(string exe)
            {
                string path = Path.Combine(install.Folder, "bin", exe);
                if (!File.Exists(path))
                    throw new InvalidDataException("Garry's Mod's map compiler is missing (" + path + "). Verify Garry's Mod's files in Steam.");
                return path;
            }

            public async Task<string> ExportAsync()
            {
                string vbsp = Tool("vbsp.exe"), vvis = Tool("vvis.exe"), vrad = Tool("vrad.exe"), studiomdl = Tool("studiomdl.exe");
                Directory.CreateDirectory(materialFolder);
                List<PlacedPart> parts = Fit(contents.Parts.Where(p => p.Visible || p.CanCollide).ToList());
                lightHolders = new HashSet<RobloxInstance>(parts.Where(p => Lights(p).Any(l => l.ClassName != "SurfaceLight")).Select(p => p.Instance));
                progress?.Report("Place: " + parts.Count + " parts. Building the map at " + F(units) + " units per stud...");

                // Brushes first, the largest blocks and wedges ahead of the rest, while vbsp's limits allow.
                var blocks = parts.Where(p => p.Shape == PartShape.Box || p.Shape == PartShape.Wedge || p.Shape == PartShape.CornerWedge)
                    .Select(p => (Part: p, Size: p.Scale.Select(s => Math.Abs(s) * units).OrderByDescending(s => s).ToArray()))
                    .OrderByDescending(b => b.Size[0] * b.Size[1]).ToList();
                var others = new List<PlacedPart>();
                foreach (var block in blocks)
                    if (block.Size[0] < DetailSize || !AddBrush(block.Part))
                        others.Add(block.Part);
                // Meshes keep their own models (the commonest first) until there are too many to compile quickly.
                var meshParts = parts.Where(p => p.Shape == PartShape.Mesh).ToList();
                var common = new HashSet<long>(meshParts.GroupBy(p => p.MeshId.Value).OrderByDescending(g => g.Count()).Take(MaxPropModels).Select(g => g.Key));
                foreach (PlacedPart part in meshParts)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (common.Contains(part.MeshId.Value) && props.Count < MaxPropModels)
                        await AddPropAsync(part).ConfigureAwait(false);
                    else
                        others.Add(part);
                }
                // Unions that appear more than once (the same union, size and colour) are compiled once and placed as props; a
                // town's lamp posts and fence pieces share a model instead of being copied into the merged props.
                var unionParts = parts.Where(p => p.Shape == PartShape.Union).ToList();
                var repeated = unionParts.GroupBy(UnionKey).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Take(MaxUnionModels)
                    .SelectMany(g => g).ToList();
                foreach (PlacedPart part in repeated)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await AddUnionPropAsync(part).ConfigureAwait(false);
                }
                var instanced = new HashSet<PlacedPart>(repeated);
                others.AddRange(unionParts.Where(p => !instanced.Contains(p)));
                others.AddRange(parts.Where(p => p.Shape == PartShape.Sphere || p.Shape == PartShape.CylinderX || p.Shape == PartShape.CylinderY));
                foreach (PlacedPart part in others)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await AddPieceAsync(part).ConfigureAwait(false);
                }
                merged = others.Count;
                // Decals and Textures, also on invisible parts (a sign can be just its decal).
                foreach (PlacedPart part in clipped ? parts : contents.Parts)
                    await AddDecalsAsync(part).ConfigureAwait(false);
                if (missingDecals > 0)
                    progress?.Report(missingDecals + " decal image" + (missingDecals == 1 ? "" : "s") + " could not be downloaded; " + (missingDecals == 1 ? "it was" : "they were") + " left out.");
                AddTerrain();
                foreach (PlacedPart part in parts)
                    foreach (RobloxInstance light in Lights(part))
                        AddLight(part, light);
                AddSpawns(parts);
                AddSun();
                string sky = await SkyAsync().ConfigureAwait(false);
                ModelFileBuilder.ReportLeftOut(contents, contents.Parts.Count(p => !p.Visible && !p.CanCollide), missing, progress);
                if (brushes.Count == 0 && placements.Count == 0 && pieces.Count == 0 && displacements == 0)
                    throw new InvalidDataException("The place has no parts that could be built into a map.");
                BuildChunks();
                progress?.Report("Map: " + brushes.Count + " brushes, " + placements.Count + " props of " + props.Count + " models, " + merged +
                    " parts and " + decals + " decals merged into " + chunks.Count + " props, " + displacements + " terrain displacements, " +
                    entities.Count(e => Regex.IsMatch(e, "\"classname\" \"light(_spot)?\"")) + " lights.");

                string gameInfo = Path.Combine(folder, "gameinfo.txt");
                File.WriteAllText(gameInfo, GameInfo());
                try
                {
                    CompileModels(studiomdl);
                    // The models' sources are large and of no use once compiled (the map's .vmf, kept in src, can be opened
                    // in Hammer). ROPORTER_KEEP_SOURCES keeps them, for debugging.
                    if (Environment.GetEnvironmentVariable("ROPORTER_KEEP_SOURCES") == null)
                        try
                        {
                            Directory.Delete(Path.Combine(folder, "src", "props"), true);
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                        {
                        }
                    string src = Path.Combine(folder, "src");
                    Directory.CreateDirectory(src);
                    WritePalette();
                    WritePictures();
                    string vmf = Path.Combine(src, map + ".vmf");
                    File.WriteAllText(vmf, Vmf(sky), Encoding.ASCII);
                    string bsp = Path.Combine(src, map + ".bsp");
                    string game = "-game \"" + SourceExporter.ToolPath(folder.TrimEnd('\\', '/')) + "\" ";
                    progress?.Report("Compiling the map (vbsp)...");
                    // No t-junction repair: on a large place it overflows vbsp (a road of many parts merges into one face
                    // with too many edges to fix).
                    string log;
                    // The October 2026 vbsp crashes on loose textures like studiomdl (see HideTextures); vrad reads them fine.
                    using (SourceExporter.HideTextures(folder))
                        log = Run(vbsp, game + "-notjunc \"" + SourceExporter.ToolPath(vmf) + "\"");
                    File.WriteAllText(Path.Combine(src, "vbsp.log"), log);
                    if (!File.Exists(bsp))
                        throw new InvalidDataException("vbsp could not build the map:" + Environment.NewLine + SourceExporter.ToolFailure(log, vbsp));
                    if (log.IndexOf("LEAKED", StringComparison.OrdinalIgnoreCase) >= 0)
                        progress?.Report("The map leaked, so visibility was not computed.");
                    else
                    {
                        progress?.Report("Computing visibility (vvis)...");
                        Run(vvis, game + "\"" + SourceExporter.ToolPath(bsp) + "\"");
                    }
                    progress?.Report("Lighting the map (vrad)...");
                    var watch = Stopwatch.StartNew();
                    // Props are lit per vertex. vrad's fast mode takes seconds where full quality takes minutes on a place
                    // with detailed meshes.
                    log = Run(vrad, game + "-fast -StaticPropLighting \"" + SourceExporter.ToolPath(bsp) + "\"");
                    File.WriteAllText(Path.Combine(src, "vrad.log"), log);
                    if (!Regex.IsMatch(log, @"Writing .*\.bsp", RegexOptions.IgnoreCase))
                        throw new InvalidDataException("vrad could not light the map:" + Environment.NewLine + SourceExporter.ToolFailure(log, vrad));
                    progress?.Report("Lit in " + (int)watch.Elapsed.TotalSeconds + " seconds.");
                    string maps = Path.Combine(folder, "maps");
                    Directory.CreateDirectory(maps);
                    File.Move(bsp, Path.Combine(maps, map + ".bsp"));
                    foreach (string leftover in new[] { ".prt", ".lin" })
                        File.Delete(Path.Combine(src, map + leftover));
                }
                finally
                {
                    File.Delete(gameInfo);
                }
                return Path.Combine("maps", map + ".bsp");
            }

            // The map's picture: in Garry's Mod's map list (maps\thumb) and as the Workshop icon.
            private void WritePictures()
            {
                using (Picture picture = render.Draw(512, sunTravel))
                {
                    string thumbs = Path.Combine(folder, "maps", "thumb");
                    Directory.CreateDirectory(thumbs);
                    using (Picture thumbnail = picture.Resized(256, 256))
                        thumbnail.Save(Path.Combine(thumbs, map + ".png"), PictureFormat.Png);
                    Workshop.WriteIcon(picture.Encode(PictureFormat.Png), Path.Combine(folder, Workshop.IconFile));
                }
            }

            // studiomdl, vbsp, vvis and vrad find Garry's Mod's content (tool textures, the stock sky) and this addon's own
            // materials and models through a temporary gameinfo.txt in the addon folder.
            private string GameInfo()
            {
                string root = SourceExporter.ToolPath(install.Folder);
                string Path(string relative) => System.IO.Path.Combine(root, relative).Replace('\\', '/');
                return "\"GameInfo\"\n{\n\tgame \"RoPorter\"\n\ttype multiplayer_only\n\tFileSystem\n\t{\n\t\tSteamAppId 4000\n\t\tSearchPaths\n\t\t{\n" +
                    "\t\t\tgame+mod |gameinfo_path|.\n" +
                    "\t\t\tgame \"" + Path("garrysmod/garrysmod.vpk") + "\"\n" +
                    "\t\t\tgame \"" + Path("sourceengine/hl2_textures.vpk") + "\"\n" +
                    "\t\t\tgame \"" + Path("sourceengine/hl2_misc.vpk") + "\"\n" +
                    "\t\t\tgame \"" + Path("garrysmod") + "\"\n" +
                    "\t\t\tgame \"" + Path("sourceengine") + "\"\n" +
                    "\t\t\tplatform \"" + Path("platform/platform_misc.vpk") + "\"\n" +
                    "\t\t\tplatform \"" + Path("platform") + "\"\n" +
                    "\t\t}\n\t}\n}\n";
            }

            private string Run(string exe, string arguments, string workingDirectory = null)
            {
                ProcessStartInfo start = SourceExporter.ToolStart(exe, arguments);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe);
                var output = new StringBuilder();
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    while (!process.WaitForExit(200))
                        if (cancellation.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch (InvalidOperationException) { }
                            cancellation.ThrowIfCancellationRequested();
                        }
                    process.WaitForExit();
                }
                lock (output)
                    return output.ToString();
            }

            // The lines of a compile log that explain a failure.
            // Centres the place on the map, leaving out parts too far away to fit inside Source's world limits.
            private List<PlacedPart> Fit(List<PlacedPart> parts)
            {
                var boxes = parts.Select(Bounds).ToList();
                if (contents.Ground != null)
                {
                    TerrainSurface ground = contents.Ground;
                    var heights = ground.Heights.Where(h => !float.IsNaN(h)).ToList();
                    boxes.Add((new double[] { ground.MinX * RobloxTerrain.VoxelSize, heights.Min(), ground.MinZ * RobloxTerrain.VoxelSize },
                        new double[] { (ground.MinX + ground.Width) * RobloxTerrain.VoxelSize, heights.Max(), (ground.MinZ + ground.Depth) * RobloxTerrain.VoxelSize }));
                }
                double[] allowed = { (Limit - SidePadding) / units, (Limit - Math.Max(TopPadding, BottomPadding)) / units, (Limit - SidePadding) / units };
                bool Fits(IEnumerable<(double[] Min, double[] Max)> set, out double[] middle)
                {
                    var list = set.ToList();
                    middle = new double[3];
                    if (list.Count == 0)
                        return true;
                    bool fits = true;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double low = list.Min(b => b.Min[axis]), high = list.Max(b => b.Max[axis]);
                        middle[axis] = (low + high) / 2;
                        fits &= (high - low) / 2 <= allowed[axis];
                    }
                    return fits;
                }
                if (Fits(boxes, out center))
                    return parts;
                // Around the middle of the place's parts, keep what fits.
                double[] median = Enumerable.Range(0, 3).Select(axis => boxes.Select(b => (b.Min[axis] + b.Max[axis]) / 2).OrderBy(v => v).ElementAt(boxes.Count / 2)).ToArray();
                var kept = new List<PlacedPart>();
                for (int i = 0; i < parts.Count; i++)
                    if (Enumerable.Range(0, 3).All(axis => boxes[i].Min[axis] >= median[axis] - allowed[axis] && boxes[i].Max[axis] <= median[axis] + allowed[axis]))
                        kept.Add(parts[i]);
                progress?.Report((parts.Count - kept.Count) + " parts far from the rest were left out: a Source map is at most " +
                    F(2 * allowed[0]) + " studs across at this scale.");
                clipped = true;
                Fits(kept.Select(Bounds), out center);
                return kept;
            }

            // A part's bounds in Roblox world space (meshes are taken as their part's size).
            private static (double[] Min, double[] Max) Bounds(PlacedPart part)
            {
                bool sized = part.Shape == PartShape.Mesh || part.Shape == PartShape.Union;
                float[] half = sized ? part.Size : part.Scale, offset = sized ? new float[3] : part.Offset;
                float[] c = part.CFrame;
                var low = new double[3];
                var high = new double[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    double middle = c[axis] + c[3 + 3 * axis] * offset[0] + c[4 + 3 * axis] * offset[1] + c[5 + 3 * axis] * offset[2];
                    double reach = (Math.Abs(c[3 + 3 * axis] * half[0]) + Math.Abs(c[4 + 3 * axis] * half[1]) + Math.Abs(c[5 + 3 * axis] * half[2])) / 2;
                    low[axis] = middle - reach;
                    high[axis] = middle + reach;
                }
                return (low, high);
            }

            // Roblox world (studs; Y up, front -Z) to Source (units; Z up, X forward): x = -z, y = -x, z = y.
            private double[] Point(double x, double y, double z)
            {
                var point = new[] { -(z - center[2]) * units, -(x - center[0]) * units, (y - center[1]) * units };
                for (int axis = 0; axis < 3; axis++)
                {
                    min[axis] = Math.Min(min[axis], point[axis]);
                    max[axis] = Math.Max(max[axis], point[axis]);
                }
                return point;
            }

            private static double[] Direction(double[] roblox) => new[] { -roblox[2], -roblox[0], roblox[1] };

            // A point in the part's space (studs) to Roblox world space.
            private static double[] World(float[] c, double x, double y, double z) => new[]
            {
                c[0] + c[3] * x + c[4] * y + c[5] * z,
                c[1] + c[6] * x + c[7] * y + c[8] * z,
                c[2] + c[9] * x + c[10] * y + c[11] * z
            };

            private static double[] Rotate(float[] c, double[] v) => new[]
            {
                c[3] * v[0] + c[4] * v[1] + c[5] * v[2], c[6] * v[0] + c[7] * v[1] + c[8] * v[2], c[9] * v[0] + c[10] * v[1] + c[11] * v[2]
            };

            private double[] Source(float[] c, double x, double y, double z)
            {
                double[] w = World(c, x, y, z);
                return Point(w[0], w[1], w[2]);
            }

            // A normal in the part's space, under its stretch, to Source's axes.
            private static double[] SourceNormal(float[] c, double x, double y, double z, float[] scale)
            {
                double[] n = Direction(Rotate(c, new[] { x / ModelFileBuilder.NonZero(scale[0]), y / ModelFileBuilder.NonZero(scale[1]), z / ModelFileBuilder.NonZero(scale[2]) }));
                double length = Matrix.Length(n);
                return length > 1e-9 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 };
            }

            // Source angles (pitch yaw roll) for a part's rotation, as Source's MatrixAngles: the rotation in Source axes is
            // C R C^T, whose columns are the model's forward, left and up.
            private static string Angles(float[] c)
            {
                double[] Column(int j) => Direction(new double[] { c[3 + j], c[6 + j], c[9 + j] });
                // Source's forward (+X) is Roblox's -Z, left (+Y) is -X, up (+Z) is +Y.
                double[] forward = Column(2).Select(v => -v).ToArray(), left = Column(0).Select(v => -v).ToArray(), up = Column(1);
                double xy = Math.Sqrt(forward[0] * forward[0] + forward[1] * forward[1]);
                double pitch, yaw, roll;
                if (xy > 0.001)
                {
                    yaw = Math.Atan2(forward[1], forward[0]);
                    pitch = Math.Atan2(-forward[2], xy);
                    roll = Math.Atan2(left[2], up[2]);
                }
                else
                {
                    yaw = Math.Atan2(-left[0], left[1]);
                    pitch = Math.Atan2(-forward[2], xy);
                    roll = 0;
                }
                double degrees = 180 / Math.PI;
                return F(pitch * degrees) + " " + F(yaw * degrees) + " " + F(roll * degrees);
            }

            private static IEnumerable<RobloxInstance> Lights(PlacedPart part) =>
                part.Instance.Children.Where(c => (c.ClassName == "PointLight" || c.ClassName == "SpotLight" || c.ClassName == "SurfaceLight") &&
                    !(c.Properties.TryGetValue("Enabled", out object enabled) && enabled is bool on && !on));

            // A part's scale with paper-thin sides thickened (vbsp drops thinner brushes).
            private float[] Thickened(PlacedPart part) =>
                part.Scale.Select(s => Math.Abs(s) * units >= MinThickness ? s : (s < 0 ? -1 : 1) * (float)(MinThickness / units)).ToArray();

            // Canonical key of a plane: a plane and its flip are the same vbsp plane.
            private static (long, long, long, long) PlaneKey(double[] n, double d)
            {
                int sign = Math.Abs(n[0]) > 1e-6 ? Math.Sign(n[0]) : Math.Abs(n[1]) > 1e-6 ? Math.Sign(n[1]) : Math.Sign(n[2]);
                return ((long)Math.Round(sign * n[0] * 1e4), (long)Math.Round(sign * n[1] * 1e4), (long)Math.Round(sign * n[2] * 1e4), (long)Math.Round(sign * d * 10));
            }

            // A block or wedge as a brush, if it fits within vbsp's limits; false sends it to the merged props instead.
            private bool AddBrush(PlacedPart part)
            {
                if (brushes.Count >= MaxBrushes)
                    return false;
                float[] scale = Thickened(part), offset = part.Offset;
                bool mirrored = scale[0] * scale[1] * scale[2] < 0;
                var sides = new List<Side>();
                var outlines = new List<List<double[]>>();
                List<float[][]> polygons = ModelFileBuilder.Polygons(part.Shape, 0, out _);
                var low = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                var high = new[] { double.MinValue, double.MinValue, double.MinValue };
                foreach (float[][] polygon in polygons)
                {
                    var points = polygon.Select(v => SourceQuiet(part.CFrame, v[0] * scale[0] + offset[0], v[1] * scale[1] + offset[1], v[2] * scale[2] + offset[2])).ToList();
                    if (mirrored)
                        points.Reverse();
                    foreach (double[] p in points)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            low[axis] = Math.Min(low[axis], p[axis]);
                            high[axis] = Math.Max(high[axis], p[axis]);
                        }
                    Side side = MakeSide(points);
                    if (side == null)
                        return false;
                    sides.Add(side);
                    outlines.Add(points);
                }
                if (Enumerable.Range(0, 3).Min(axis => high[axis] - low[axis]) < 0.1)
                    return false;
                // vbsp's cost: the new planes (each with its flip), and for a turned brush its axial and edge bevels.
                var keys = sides.Select(s => PlaneKey(s.Normal, Matrix.Dot(s.Normal, s.A))).Distinct().ToList();
                bool axial = sides.All(s => s.Normal.Count(v => Math.Abs(v) > 0.9999) == 1);
                var bevels = axial ? new List<(long, long, long, long)>() : Enumerable.Range(0, 3).SelectMany(axis =>
                {
                    var n = new double[3];
                    n[axis] = 1;
                    return new[] { PlaneKey(n, low[axis]), PlaneKey(n, high[axis]) };
                }).Where(k => !keys.Contains(k)).Distinct().ToList();
                int cost = 2 * keys.Count(k => !planes.Contains(k)) + 2 * bevels.Count(k => !planes.Contains(k)) + (axial ? 0 : 24);
                if (planeEntries + cost > PlaneBudget)
                    return false;
                planeEntries += cost;
                planes.UnionWith(keys);
                planes.UnionWith(bevels);
                foreach (double[] p in new[] { low, high })
                    Point(-p[1] / units + center[0], p[2] / units + center[1], -p[0] / units + center[2]);
                string material = part.Visible ? ColorMaterial(part) : "TOOLS/TOOLSINVISIBLE";
                bool textured = part.Visible && part.Material != PlacedPart.NeonMaterial && RobloxContent.HasMaterialTexture(part.Material);
                foreach (Side side in sides)
                {
                    side.Material = material;
                    if (textured)
                        side.TextureScale = MaterialTexel(4);
                }
                if (part.Visible)
                {
                    foreach (List<double[]> outline in outlines)
                        render.AddPolygon(outline, part.Color);
                    render.AddPart(low, high);
                }
                Group group = !part.Visible ? Group.Detail
                    : lightHolders.Contains(part.Instance) ? (part.CanCollide ? Group.LitHolder : Group.LitHolderPassable)
                    : part.CanCollide ? Group.Detail : Group.Illusionary;
                brushes.Add(new Brush { Sides = sides, Group = group });
                return true;
            }

            // Source coordinates without growing the map's bounds (for geometry that may not be kept).
            private double[] SourceQuiet(float[] c, double x, double y, double z)
            {
                double[] w = World(c, x, y, z);
                return new[] { -(w[2] - center[2]) * units, -(w[0] - center[0]) * units, (w[1] - center[1]) * units };
            }

            // A brush side from a convex polygon (counter-clockwise from outside): the plane through three of its corners, far
            // apart, which vmf lists clockwise from outside.
            private static Side MakeSide(List<double[]> points)
            {
                double[] a = points[0];
                double best = 0;
                int bi = -1, bj = -1;
                for (int i = 1; i < points.Count - 1; i++)
                    for (int j = i + 1; j < points.Count; j++)
                    {
                        double area = Matrix.Length(Cross(Matrix.Subtract(points[i], a), Matrix.Subtract(points[j], a)));
                        if (area > best)
                        {
                            best = area;
                            bi = i;
                            bj = j;
                        }
                    }
                if (best < 1e-3)
                    return null;
                double[] normal = Matrix.Scale(Cross(Matrix.Subtract(points[bi], a), Matrix.Subtract(points[bj], a)), 1 / best);
                double total = 0;
                for (int i = 1; i + 1 < points.Count; i++)
                    total += Matrix.Length(Cross(Matrix.Subtract(points[i], a), Matrix.Subtract(points[i + 1], a))) / 2;
                double extent = 0;
                foreach (double[] p in points)
                    foreach (double[] q in points)
                        extent = Math.Max(extent, Matrix.Length(Matrix.Subtract(p, q)));
                return new Side
                {
                    A = a, B = points[bj], C = points[bi], Normal = normal, Area = total, Extent = extent,
                    MinZ = points.Min(p => p[2]), MaxZ = points.Max(p => p[2])
                };
            }

            private static double[] Cross(double[] u, double[] v) => new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };

            private static int Byte(float value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
            private static string Hex(float[] color) => Byte(color[0]).ToString("x2") + Byte(color[1]).ToString("x2") + Byte(color[2]).ToString("x2");

            // Footstep and impact sounds for Roblox's materials.
            private static string SurfaceProp(int material)
            {
                switch (material)
                {
                    case 512: case 528: return "wood";
                    case 816: case 836: case 1376: return "concrete";
                    case 784: case 788: case 800: case 804: case 820: case 832: case 880: case 896: case 912: return "rock";
                    case 848: return "brick";
                    case 864: return "gravel";
                    case 1040: case 1056: case 1072: case 1088: return "metal";
                    case 1280: case 1284: return "grass";
                    case 1296: case 1392: return "sand";
                    case 1312: return "carpet";
                    case 1328: return "snow";
                    case 1344: return "mud";
                    case 1360: return "dirt";
                    case 1536: case 1552: return "ice";
                    case 1568: case 1584: return "glass";
                    default: return "plastic";
                }
            }

            // The same for terrain materials.
            private static string TerrainSurfaceProp(int material)
            {
                switch (RobloxTerrain.MaterialName(material))
                {
                    case "Grass": case "LeafyGrass": return "grass";
                    case "Sand": case "Salt": return "sand";
                    case "Snow": case "Glacier": return "snow";
                    case "Ice": return "ice";
                    case "Mud": return "mud";
                    case "Ground": return "dirt";
                    case "WoodPlanks": return "wood";
                    case "Brick": return "brick";
                    case "Concrete": case "Asphalt": case "Pavement": return "concrete";
                    default: return "rock";
                }
            }

            // A lightmapped material in the part's colour (Neon is fullbright, as in Roblox), with the part's Roblox material
            // (wood, brick...) as a detail texture multiplying the colour, as Roblox's shading does.
            private string ColorMaterial(PlacedPart part)
            {
                int alpha = Byte(1 - part.Transparency);
                bool neon = part.Material == PlacedPart.NeonMaterial;
                string surface = SurfaceProp(part.Material);
                string texture = "c_" + Hex(part.Color);
                string detail = neon ? null : MaterialTexture(part.Material, true);
                string name = texture + "_" + (detail != null ? RobloxContent.MaterialName(part.Material).ToLowerInvariant() : surface) +
                    (alpha < 255 ? "_a" + alpha.ToString("x2") : "") + (neon ? "_neon" : "");
                if (materials.Add(name))
                {
                    SolidTexture(texture, part.Color);
                    var vmt = new StringBuilder("\"").Append(neon ? "UnlitGeneric" : "LightmappedGeneric").Append("\"\n{\n");
                    vmt.Append("\t\"$basetexture\" \"").Append(materialDir).Append(texture).Append("\"\n");
                    if (detail != null)
                        vmt.Append("\t\"$detail\" \"").Append(materialDir).Append(detail).Append("\"\n\t\"$detailscale\" \"1\"\n\t\"$detailblendmode\" \"0\"\n");
                    vmt.Append("\t\"$surfaceprop\" \"").Append(surface).Append("\"\n");
                    if (alpha < 255)
                        vmt.Append("\t\"$translucent\" \"1\"\n\t\"$alpha\" \"").Append(F(alpha / 255.0)).Append("\"\n");
                    if (!neon)
                        vmt.Append(AmbientGlow);
                    File.WriteAllText(Path.Combine(materialFolder, name + ".vmt"), vmt.Append("}\n").ToString(), Encoding.ASCII);
                }
                return materialDir + name;
            }

            // Roblox lights every surface with Lighting.Ambient however enclosed it is, while Source only bounces light in
            // through windows: interiors came out black. Materials glow faintly by the place's ambient instead (self-illumination
            // tinted by it, which adds the surface's own colour times the tint, as Roblox's ambient term does). Terrain is
            // outdoors and lit by the sky's ambient.
            private string ambientGlow;
            private string AmbientGlow
            {
                get
                {
                    if (ambientGlow != null)
                        return ambientGlow;
                    float[] ambient = contents.Lighting?.Get<float[]>("Ambient") ?? new[] { 0.275f, 0.275f, 0.275f };
                    double[] tint = ambient.Select(channel => Math.Min(MaxAmbientGlow, channel * AmbientGlowScale)).ToArray();
                    return ambientGlow = tint.Max() < 0.01 ? "" :
                        "\t\"$selfillum\" \"1\"\n\t\"$selfillumtint\" \"[" + F(tint[0]) + " " + F(tint[1]) + " " + F(tint[2]) + "]\"\n";
                }
            }

            // A Roblox material's colour map as a texture in the map's materials: halved for a detail texture (Source doubles
            // detail textures as it multiplies them in), or as it is for a model's base texture, faded by an opacity (0-255) for a
            // see-through part. Null for materials with none.
            private string MaterialTexture(int material, bool detail, int alpha = 255)
            {
                string roblox = RobloxContent.MaterialName(material);
                byte[] image = RobloxContent.MaterialTexture(roblox);
                if (image == null)
                    return null;
                string name = "mat_" + roblox.ToLowerInvariant() + (detail ? "_detail" : "") + (alpha < 255 ? "_a" + alpha.ToString("x2") : "");
                string vtf = Path.Combine(materialFolder, name + ".vtf");
                if (!File.Exists(vtf))
                    using (Picture source = Picture.Load(image))
                    using (var copy = new Picture(source.Width, source.Height))
                    {
                        copy.Draw(source, 0, 0, copy.Width, copy.Height, tint: detail ? new[] { 0.5f, 0.5f, 0.5f } : null, replace: true);
                        if (alpha < 255)
                            FadeAlpha(copy, alpha / 255.0);
                        VtfWriter.Write(copy, vtf, VtfWriter.Kind.Color, compress: true);
                    }
                return name;
            }

            // World units per texel that make a material texture (or a 4-texel solid colour carrying one as detail) repeat
            // every few studs, as on Roblox's parts.
            private double MaterialTexel(int texels) => RobloxContent.PartStudsPerTile * units / texels;

            private void SolidTexture(string texture, float[] color)
            {
                string vtf = Path.Combine(materialFolder, texture + ".vtf");
                if (!File.Exists(vtf))
                    VtfWriter.WriteSolid(vtf, color, 1);
            }

            // Meshes become static props: one compiled model per mesh, size and look, placed and turned by the prop.
            private async Task AddPropAsync(PlacedPart part)
            {
                RobloxMesh mesh = await meshes.GetAsync(part.MeshId.Value, cancellation).ConfigureAwait(false);
                if (mesh == null || mesh.Faces.Count == 0)
                {
                    missing++;
                    return;
                }
                float[] scale = part.Scale, offset = part.Offset;
                if (part.FitToSize)
                    ModelFileBuilder.FitToSize(mesh, part.Size, out scale, out offset);
                string material = await PropMaterialAsync(part).ConfigureAwait(false);
                AddInstance(part, "mesh" + part.MeshId.Value, mesh.Positions, mesh.Normals, mesh.Texcoords, mesh.Faces, scale, offset, face => material,
                    materialColors.TryGetValue(material, out float[] average) ? average : part.Color);
            }

            // A union that appears more than once becomes a prop the same way: one model per union, size and colours.
            private async Task AddUnionPropAsync(PlacedPart part)
            {
                CsgMesh mesh = await unions.GetAsync(part, cancellation).ConfigureAwait(false);
                if (mesh == null)
                {
                    missing++;
                    return;
                }
                float[] scale = ModelFileBuilder.UnionScale(part);
                List<float[]> uvs = Textured(part) ? BoxUvs(mesh.Positions, mesh.Normals, scale, new float[3]) : new List<float[]>();
                AddInstance(part, "union" + part.UnionAsset, mesh.Positions, mesh.Normals, uvs, mesh.Faces, scale, new float[3], UnionMaterials(part, mesh), part.Color);
            }

            // What makes two unions the same model: the union, its size and its colour.
            private static string UnionKey(PlacedPart part) =>
                (part.UnionAsset.HasValue ? part.UnionAsset.Value.ToString(CultureInfo.InvariantCulture) : "inline" + part.GetHashCode()) + "|" +
                string.Join(",", ModelFileBuilder.UnionScale(part).Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) + "|" +
                (part.UsePartColor ? Hex(part.Color) : "own") + "|" + Byte(1 - part.Transparency) + "|" + part.Material + "|" + part.CanCollide;

            // Places a part's mesh as a static prop, compiling its model the first time this mesh, size and look appear.
            private void AddInstance(PlacedPart part, string source, List<float[]> positions, List<float[]> normals, List<float[]> texcoords, List<int[]> faces,
                float[] scale, float[] offset, Func<int, string> materialOf, float[] renderColor)
            {
                var faceMaterials = Enumerable.Range(0, faces.Count).Select(materialOf).ToList();
                string key = source + "|" + string.Join(",", scale.Concat(offset).Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) +
                    "|" + string.Join(",", faceMaterials.Distinct()) + "|" + part.CanCollide;
                if (!props.TryGetValue(key, out Prop prop))
                {
                    prop = new Prop
                    {
                        Name = "p" + props.Count, Solid = part.CanCollide, Surface = SurfaceProp(part.Material),
                        Min = new[] { double.MaxValue, double.MaxValue, double.MaxValue }, Max = new[] { double.MinValue, double.MinValue, double.MinValue }
                    };
                    prop.Model = "roporter/maps/" + id + "/" + prop.Name + ".mdl";
                    prop.Smd = PropSmd(positions, normals, texcoords, faces, scale, offset, faceMaterials, prop);
                    props[key] = prop;
                }
                placements.Add(new KeyValuePair<Prop, PlacedPart>(prop, part));
                // The prop's corners count towards the map's bounds.
                var low = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                var high = new[] { double.MinValue, double.MinValue, double.MinValue };
                foreach (int corner in Enumerable.Range(0, 8))
                {
                    double[] local = { (corner & 1) != 0 ? prop.Max[0] : prop.Min[0], (corner & 2) != 0 ? prop.Max[1] : prop.Min[1], (corner & 4) != 0 ? prop.Max[2] : prop.Min[2] };
                    // Back from Source's axes to the part's (x = -y, y = z, z = -x), in studs.
                    double[] point = Source(part.CFrame, -local[1] / units, local[2] / units, -local[0] / units);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        low[axis] = Math.Min(low[axis], point[axis]);
                        high[axis] = Math.Max(high[axis], point[axis]);
                    }
                }
                if (part.Visible)
                {
                    render.AddPart(low, high);
                    double[][] world = positions.Select(v => Source(part.CFrame, v[0] * scale[0] + offset[0], v[1] * scale[1] + offset[1], v[2] * scale[2] + offset[2])).ToArray();
                    bool mirrored = scale[0] * scale[1] * scale[2] < 0;
                    foreach (int[] face in faces)
                        if (mirrored)
                            render.AddTriangle(world[face[2]], world[face[1]], world[face[0]], renderColor);
                        else
                            render.AddTriangle(world[face[0]], world[face[1]], world[face[2]], renderColor);
                }
            }

            // The mesh in the prop's own space: the part's, turned into Source's axes and units.
            private string PropSmd(List<float[]> positions, List<float[]> normals, List<float[]> texcoords, List<int[]> faces, float[] scale, float[] offset,
                List<string> faceMaterials, Prop prop)
            {
                var text = new StringBuilder("version 1\nnodes\n0 \"root\" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\ntriangles\n");
                bool mirrored = scale[0] * scale[1] * scale[2] < 0;
                bool smooth = normals.Count == positions.Count, uvs = texcoords.Count == positions.Count;
                for (int f = 0; f < faces.Count; f++)
                {
                    // A colour from the palette: every corner at its texel.
                    string material = faceMaterials[f];
                    int hash = material.IndexOf('#');
                    float[] paletteUv = hash < 0 ? null : PaletteUv(int.Parse(material.Substring(hash + 1), CultureInfo.InvariantCulture));
                    text.Append(Material(material)).Append('\n');
                    foreach (int index in mirrored ? Enumerable.Reverse(faces[f]) : faces[f])
                    {
                        float[] v = positions[index];
                        double[] p = Matrix.Scale(Direction(new double[] { v[0] * scale[0] + offset[0], v[1] * scale[1] + offset[1], v[2] * scale[2] + offset[2] }), units);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            prop.Min[axis] = Math.Min(prop.Min[axis], p[axis]);
                            prop.Max[axis] = Math.Max(prop.Max[axis], p[axis]);
                        }
                        double[] n = smooth ? Direction(new double[] { normals[index][0] / ModelFileBuilder.NonZero(scale[0]),
                            normals[index][1] / ModelFileBuilder.NonZero(scale[1]), normals[index][2] / ModelFileBuilder.NonZero(scale[2]) }) : new double[] { 0, 0, 1 };
                        double length = Matrix.Length(n);
                        n = length > 1e-9 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 };
                        SmdVertex(text, p, n, paletteUv ?? (uvs ? texcoords[index] : new float[2]));
                    }
                }
                return text.Append("end\n").ToString();
            }

            private static void SmdVertex(StringBuilder text, double[] p, double[] n, float[] uv)
            {
                text.Append('0');
                foreach (double value in new[] { p[0], p[1], p[2], n[0], n[1], n[2], uv[0], uv[1] })
                    Number(text.Append(' '), value);
                text.Append('\n');
            }

            // A number to three decimals without trailing zeros, as F() writes it but fast enough for millions of vertices.
            private static void Number(StringBuilder text, double value)
            {
                long thousandths = (long)Math.Round(value * 1000);
                if (thousandths < 0)
                {
                    text.Append('-');
                    thousandths = -thousandths;
                }
                text.Append(thousandths / 1000);
                int fraction = (int)(thousandths % 1000);
                if (fraction == 0)
                    return;
                text.Append('.').Append((char)('0' + fraction / 100));
                if (fraction % 100 != 0)
                {
                    text.Append((char)('0' + fraction / 10 % 10));
                    if (fraction % 10 != 0)
                        text.Append((char)('0' + fraction % 10));
                }
            }

            // A prop's material: its texture (with the part's colour showing through a MeshPart texture's transparent parts) or
            // its colour, in Source's model shader.
            private async Task<string> PropMaterialAsync(PlacedPart part)
            {
                string texture = null;
                if (part.TextureId.HasValue)
                    texture = await ModelFileBuilder.TextureAsync(part.TextureId.Value, download, textureKeys, textureBytes, progress, cancellation).ConfigureAwait(false);
                if (texture == null)
                    return Palette(part.Color, 1 - part.Transparency, part.Material == PlacedPart.NeonMaterial);
                int alpha = Byte(1 - part.Transparency);
                bool neon = part.Material == PlacedPart.NeonMaterial;
                string name = "t" + part.TextureId.Value + (part.Overlay ? "_" + Hex(part.Color) : "") +
                    (alpha < 255 ? "_a" + alpha.ToString("x2") : "") + (part.DoubleSided ? "_2" : "") + (neon ? "_neon" : "");
                if (!materials.Add(name))
                    return name;
                string vtf = Path.Combine(materialFolder, name + ".vtf");
                string blend = null;
                using (Picture image = Picture.Load(textureBytes[texture]))
                {
                    // The part's transparency goes into the texture's alpha: Garry's Mod ignores $alpha on static props.
                    if (part.Overlay)
                        using (var composite = new Picture(image.Width, image.Height))
                        {
                            composite.Clear(unchecked((int)0xFF000000) | (Byte(part.Color[0]) << 16) | (Byte(part.Color[1]) << 8) | Byte(part.Color[2]));
                            composite.Draw(image, 0, 0, image.Width, image.Height);
                            if (alpha < 255)
                                FadeAlpha(composite, alpha / 255.0);
                            if (VtfWriter.Write(composite, vtf, VtfWriter.Kind.Color, compress: true))
                                blend = "BLEND";
                            materialColors[name] = Average(composite);
                        }
                    else
                    {
                        if (alpha < 255)
                            FadeAlpha(image, alpha / 255.0);
                        if (VtfWriter.Write(image, vtf, VtfWriter.Kind.Color, compress: true))
                            blend = alpha < 255 ? "BLEND" : AlphaKind(image);
                        materialColors[name] = Average(image);
                    }
                }
                var vmt = new StringBuilder("\"VertexLitGeneric\"\n{\n");
                vmt.Append("\t\"$basetexture\" \"").Append(materialDir).Append(name).Append("\"\n");
                vmt.Append("\t\"$surfaceprop\" \"").Append(SurfaceProp(part.Material)).Append("\"\n");
                if (blend != null)
                    vmt.Append(blend == "MASK" ? "\t\"$alphatest\" \"1\"\n" : "\t\"$translucent\" \"1\"\n");
                if (part.DoubleSided)
                    vmt.Append("\t\"$nocull\" \"1\"\n");
                vmt.Append(neon ? "\t\"$selfillum\" \"1\"\n" : AmbientGlow);
                File.WriteAllText(Path.Combine(materialFolder, name + ".vmt"), vmt.Append("}\n").ToString(), Encoding.ASCII);
                return name;
            }

            // Solid colours of models share one palette texture, a texel per colour: "palette" (opaque), "palette_alpha"
            // (see-through, the texel's alpha) and "palette_neon" (fullbright). The returned name carries the texel's UV.
            private string Palette(float[] color, float opacity, bool neon)
            {
                int alpha = Byte(opacity);
                int argb = (alpha << 24) | (Byte(color[0]) << 16) | (Byte(color[1]) << 8) | Byte(color[2]);
                if (!palette.TryGetValue(argb, out int index))
                {
                    if (palette.Count < PaletteSize * PaletteSize)
                        palette[argb] = index = palette.Count;
                    else
                        index = palette.OrderBy(entry => ColorDistance(entry.Key, argb)).First().Value;
                }
                string material = neon ? "palette_neon" : alpha < 255 ? "palette_alpha" : "palette";
                return material + "#" + index.ToString(CultureInfo.InvariantCulture);
            }

            private static int ColorDistance(int a, int b)
            {
                int d = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    int x = ((a >> shift) & 255) - ((b >> shift) & 255);
                    d += x * x;
                }
                return d;
            }

            // A palette texel's centre (UVs from the bottom-left, as SMD has them).
            private static float[] PaletteUv(int index) =>
                new[] { (index % PaletteSize + 0.5f) / PaletteSize, 1 - (index / PaletteSize + 0.5f) / PaletteSize };

            private void WritePalette()
            {
                if (palette.Count == 0)
                    return;
                using (var image = new Picture(PaletteSize, PaletteSize))
                {
                    var texels = new int[PaletteSize * PaletteSize];
                    foreach (var entry in palette)
                        texels[entry.Value] = entry.Key;
                    image.SetPixels(texels);
                    VtfWriter.Write(image, Path.Combine(materialFolder, "palette.vtf"), VtfWriter.Kind.Color);
                }
                string Vmt(string extra) => "\"VertexLitGeneric\"\n{\n\t\"$basetexture\" \"" + materialDir + "palette\"\n\t\"$surfaceprop\" \"plastic\"\n" + extra + "}\n";
                File.WriteAllText(Path.Combine(materialFolder, "palette.vmt"), Vmt(AmbientGlow), Encoding.ASCII);
                File.WriteAllText(Path.Combine(materialFolder, "palette_alpha.vmt"), Vmt("\t\"$translucent\" \"1\"\n" + AmbientGlow), Encoding.ASCII);
                File.WriteAllText(Path.Combine(materialFolder, "palette_neon.vmt"), Vmt("\t\"$selfillum\" \"1\"\n"), Encoding.ASCII);
            }

            // Average colour of a texture (its visible pixels), for the map's picture.
            private static float[] Average(Picture image)
            {
                double r = 0, g = 0, b = 0, weight = 0;
                int[] pixels = image.GetPixels();
                for (int y = 0; y < image.Height; y += Math.Max(1, image.Height / 64))
                    for (int x = 0; x < image.Width; x += Math.Max(1, image.Width / 64))
                    {
                        int pixel = pixels[y * image.Width + x];
                        double a = ((pixel >> 24) & 255) / 255.0;
                        r += ((pixel >> 16) & 255) * a;
                        g += ((pixel >> 8) & 255) * a;
                        b += (pixel & 255) * a;
                        weight += a;
                    }
                return weight > 0 ? new[] { (float)(r / weight / 255), (float)(g / weight / 255), (float)(b / weight / 255) } : new float[] { 0.6f, 0.6f, 0.6f };
            }

            // Cut-out (only clear and solid pixels) or see-through.
            private static string AlphaKind(Picture image)
            {
                int[] pixels = image.GetPixels();
                for (int y = 0; y < image.Height; y += Math.Max(1, image.Height / 256))
                    for (int x = 0; x < image.Width; x += Math.Max(1, image.Width / 256))
                    {
                        int a = (pixels[y * image.Width + x] >> 24) & 255;
                        if (a > 8 && a < 247)
                            return "BLEND";
                    }
                return "MASK";
            }

            // A part merged into a prop: its triangles in map coordinates (smooth spheres and cylinders, a union's or mesh's
            // own triangles) and, if it collides, its collision hull. Parts holding a light don't block it.
            private async Task AddPieceAsync(PlacedPart part)
            {
                var piece = new Piece();
                float[] c = part.CFrame;
                bool render = part.Visible;
                bool collide = part.CanCollide && !lightHolders.Contains(part.Instance);
                List<float[]> positions, normals, texcoords;
                List<int[]> faces;
                float[] scale, offset = part.Offset;
                Func<int, string> materialOf;
                if (part.Shape == PartShape.Union)
                {
                    CsgMesh mesh = await unions.GetAsync(part, cancellation).ConfigureAwait(false);
                    if (mesh == null)
                    {
                        missing++;
                        return;
                    }
                    positions = mesh.Positions;
                    normals = mesh.Normals;
                    texcoords = new List<float[]>();
                    faces = mesh.Faces;
                    scale = ModelFileBuilder.UnionScale(part);
                    offset = new float[3];
                    materialOf = UnionMaterials(part, mesh);
                    if (Textured(part))
                        texcoords = BoxUvs(positions, normals, scale, offset);
                }
                else if (part.Shape == PartShape.Mesh)
                {
                    RobloxMesh mesh = await meshes.GetAsync(part.MeshId.Value, cancellation).ConfigureAwait(false);
                    if (mesh == null || mesh.Faces.Count == 0)
                    {
                        missing++;
                        return;
                    }
                    positions = mesh.Positions;
                    normals = mesh.Normals;
                    texcoords = mesh.Texcoords;
                    faces = mesh.Faces;
                    scale = part.Scale;
                    if (part.FitToSize)
                        ModelFileBuilder.FitToSize(mesh, part.Size, out scale, out offset);
                    string material = await PropMaterialAsync(part).ConfigureAwait(false);
                    materialOf = face => material;
                }
                else
                {
                    scale = part.Scale;
                    if (part.Shape == PartShape.Sphere || part.Shape == PartShape.CylinderX || part.Shape == PartShape.CylinderY)
                    {
                        // Small round parts (bolts, knobs, poles) need few segments.
                        double across = scale.Max(s => Math.Abs(s));
                        ModelFileBuilder.BuildPrimitive(part.Shape, out positions, out normals, out texcoords, out faces,
                            across < 1 ? 8 : across < 4 ? 12 : across < 16 ? 16 : 24);
                    }
                    else
                    {
                        // Blocks and wedges: flat faces with their own vertices.
                        positions = new List<float[]>();
                        normals = new List<float[]>();
                        texcoords = new List<float[]>();
                        faces = new List<int[]>();
                        foreach (float[][] polygon in ModelFileBuilder.Polygons(part.Shape, 0, out _))
                        {
                            float[] u = Subtract(polygon[1], polygon[0]), v = Subtract(polygon[2], polygon[0]);
                            float[] n = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
                            int start = positions.Count;
                            foreach (float[] corner in polygon)
                            {
                                positions.Add(corner);
                                normals.Add(n);
                                texcoords.Add(new float[2]);
                            }
                            for (int i = 1; i + 1 < polygon.Length; i++)
                                faces.Add(new[] { start, start + i, start + i + 1 });
                        }
                    }
                    string material = Textured(part) ? TexturedMaterial(part.Material, part.Color, 1 - part.Transparency)
                        : Palette(part.Color, 1 - part.Transparency, part.Material == PlacedPart.NeonMaterial);
                    if (Textured(part))
                        texcoords = BoxUvs(positions, normals, scale, offset);
                    materialOf = face => material;
                }

                bool mirrored = scale[0] * scale[1] * scale[2] < 0;
                double[][] world = positions.Select(v => Source(c, v[0] * scale[0] + offset[0], v[1] * scale[1] + offset[1], v[2] * scale[2] + offset[2])).ToArray();
                if (render)
                {
                    bool smooth = normals.Count == positions.Count;
                    bool uvs = texcoords.Count == positions.Count;
                    var used = new Dictionary<int, int>();
                    int Vertex(int index)
                    {
                        if (used.TryGetValue(index, out int at))
                            return at;
                        piece.Points.Add(world[index]);
                        piece.Normals.Add(smooth ? SourceNormal(c, normals[index][0], normals[index][1], normals[index][2], scale) : new double[] { 0, 0, 1 });
                        piece.Uvs.Add(uvs ? texcoords[index] : new float[2]);
                        used[index] = piece.Points.Count - 1;
                        return piece.Points.Count - 1;
                    }
                    for (int f = 0; f < faces.Count; f++)
                    {
                        int[] face = faces[f];
                        string material = materialOf(f);
                        int a = Vertex(face[0]), b = Vertex(face[1]), d = Vertex(face[2]);
                        piece.Triangles.Add(mirrored ? (d, b, a, material) : (a, b, d, material));
                    }
                    float[] shade = part.Color;
                    foreach (int[] face in faces)
                        if (mirrored)
                            this.render.AddTriangle(world[face[2]], world[face[1]], world[face[0]], shade);
                        else
                            this.render.AddTriangle(world[face[0]], world[face[1]], world[face[2]], shade);
                }
                if (collide)
                {
                    // The hull: a block's or wedge's own faces, a coarse sphere or cylinder, or a mesh's triangles; pulled a
                    // hair inwards so touching parts stay separate pieces.
                    List<double[][]> hull;
                    if (part.Shape == PartShape.Union || part.Shape == PartShape.Mesh)
                        hull = faces.Select(face => face.Select(i => world[i]).ToArray()).ToList();
                    else
                        hull = ModelFileBuilder.Polygons(part.Shape, 8, out _).SelectMany(polygon =>
                        {
                            double[][] points = polygon.Select(v => Source(c, v[0] * scale[0] + offset[0], v[1] * scale[1] + offset[1], v[2] * scale[2] + offset[2])).ToArray();
                            return Enumerable.Range(1, points.Length - 2).Select(i => new[] { points[0], points[i], points[i + 1] });
                        }).ToList();
                    if (hull.Count > 0)
                    {
                        var all = hull.SelectMany(t => t).ToList();
                        double[] middle = { all.Average(p => p[0]), all.Average(p => p[1]), all.Average(p => p[2]) };
                        double[] Shrink(double[] p)
                        {
                            double[] d = Matrix.Subtract(middle, p);
                            double length = Matrix.Length(d);
                            return length > 0.2 ? Matrix.Add(p, Matrix.Scale(d, 0.05 / length)) : p;
                        }
                        piece.Hull.AddRange(hull.Select(t => t.Select(Shrink).ToArray()));
                    }
                }
                if (piece.Triangles.Count == 0 && piece.Hull.Count == 0)
                    return;
                var points = piece.Points.Count > 0 ? piece.Points : piece.Hull.SelectMany(t => t).ToList();
                piece.Center = new[] { points.Average(p => p[0]), points.Average(p => p[1]), points.Average(p => p[2]) };
                pieces.Add(piece);
                if (render)
                {
                    var low = Enumerable.Range(0, 3).Select(axis => world.Min(p => p[axis])).ToArray();
                    var high = Enumerable.Range(0, 3).Select(axis => world.Max(p => p[axis])).ToArray();
                    this.render.AddPart(low, high);
                }
            }

            private static float[] Subtract(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

            private static bool Textured(PlacedPart part) => ModelFileBuilder.Textured(part);

            // A union's colours: its own, or the colours of the parts it was made from; textured by its material, or from the
            // palette.
            private Func<int, string> UnionMaterials(PlacedPart part, CsgMesh mesh)
            {
                float opacity = 1 - part.Transparency;
                bool neon = part.Material == PlacedPart.NeonMaterial;
                if (Textured(part))
                {
                    string own = TexturedMaterial(part.Material, part.Color, opacity);
                    return face => part.UsePartColor ? own : TexturedMaterial(part.Material, mesh.Colors[mesh.Faces[face][0]], opacity);
                }
                string paletted = Palette(part.Color, opacity, neon);
                return face => part.UsePartColor ? paletted : Palette(mesh.Colors[mesh.Faces[face][0]], opacity, neon);
            }

            // A model material: the Roblox material's texture tinted by the part's colour, as Roblox shades it.
            private string TexturedMaterial(int material, float[] color, float opacity)
            {
                int alpha = Byte(opacity);
                string name = "m_" + RobloxContent.MaterialName(material).ToLowerInvariant() + "_" + Hex(color) + (alpha < 255 ? "_a" + alpha.ToString("x2") : "");
                if (materials.Add(name))
                {
                    // A see-through part's opacity is in its texture's alpha: Garry's Mod ignores $alpha on static props.
                    string texture = MaterialTexture(material, false, alpha);
                    var vmt = new StringBuilder("\"VertexLitGeneric\"\n{\n");
                    vmt.Append("\t\"$basetexture\" \"").Append(materialDir).Append(texture).Append("\"\n");
                    vmt.Append("\t\"$color2\" \"[").Append(F(color[0])).Append(' ').Append(F(color[1])).Append(' ').Append(F(color[2])).Append("]\"\n");
                    vmt.Append("\t\"$surfaceprop\" \"").Append(SurfaceProp(material)).Append("\"\n");
                    if (alpha < 255)
                        vmt.Append("\t\"$translucent\" \"1\"\n");
                    vmt.Append(AmbientGlow);
                    File.WriteAllText(Path.Combine(materialFolder, name + ".vmt"), vmt.Append("}\n").ToString(), Encoding.ASCII);
                    materialColors[name] = color;
                }
                return name;
            }

            private static List<float[]> BoxUvs(List<float[]> positions, List<float[]> normals, float[] scale, float[] offset) =>
                ModelFileBuilder.BoxUvs(positions, normals, scale, offset);

            // Groups the merged pieces into props by area, each up to studiomdl's comfortable size.
            private void BuildChunks()
            {
                foreach (var cell in pieces.GroupBy(p => ((int)Math.Floor(p.Center[0] / CellSize), (int)Math.Floor(p.Center[1] / CellSize))))
                {
                    Chunk chunk = null;
                    foreach (Piece piece in cell)
                    {
                        var pieceMaterials = piece.Triangles.Select(t => Material(t.Material)).Distinct().ToList();
                        if (chunk == null || chunk.Triangles + piece.Triangles.Count > ChunkTriangles || chunk.Hulls + (piece.Hull.Count > 0 ? 1 : 0) > ChunkHulls ||
                            chunk.Materials.Union(pieceMaterials).Count() > ChunkMaterials)
                        {
                            chunk = new Chunk { Name = "m" + chunks.Count, Origin = new[] { (cell.Key.Item1 + 0.5) * CellSize, (cell.Key.Item2 + 0.5) * CellSize, 0 } };
                            chunk.Model = "roporter/maps/" + id + "/" + chunk.Name + ".mdl";
                            chunks.Add(chunk);
                        }
                        chunk.Pieces.Add(piece);
                        chunk.Triangles += piece.Triangles.Count;
                        chunk.Hulls += piece.Hull.Count > 0 ? 1 : 0;
                        chunk.Materials.UnionWith(pieceMaterials);
                    }
                }
                // Each prop's origin is the middle of what it holds. The middle of its grid cell could lie outside the sealed
                // map (an island at the map's edge), and vbsp then reports a leak and the map gets no visibility or bounced
                // light: shadows and interiors went black.
                foreach (Chunk chunk in chunks)
                {
                    var points = chunk.Pieces.SelectMany(piece => piece.Points).ToList();
                    if (points.Count > 0)
                        chunk.Origin = Enumerable.Range(0, 3).Select(axis => Math.Round((points.Min(p => p[axis]) + points.Max(p => p[axis])) / 2)).ToArray();
                }
            }

            // A triangle's material name without its palette texel.
            private static string Material(string name)
            {
                int hash = name.IndexOf('#');
                return hash < 0 ? name : name.Substring(0, hash);
            }

            // Writes and compiles every prop model, several at once. A mesh prop's collision is its mesh split into convex
            // pieces; when studiomdl can't split it, one hull, and failing that, none. A merged prop's collision is one hull
            // per part.
            private void CompileModels(string studiomdl)
            {
                string directory = Path.Combine(folder, "src", "props");
                Directory.CreateDirectory(directory);
                var jobs = new List<(string Name, string Model, List<string> Variants, Action<int> Done)>();
                foreach (Prop prop in props.Values)
                {
                    File.WriteAllText(Path.Combine(directory, prop.Name + ".smd"), prop.Smd, Encoding.ASCII);
                    prop.Smd = null;
                    var variants = (prop.Solid ? new[] { "\t$concave\n\t$maxconvexpieces 256\n", "", null } : new string[] { null })
                        .Select(collision => Qc(prop.Model, prop.Name + ".smd", prop.Surface, collision == null ? null : prop.Name + ".smd", collision)).ToList();
                    Prop current = prop;
                    jobs.Add((prop.Name, prop.Model, variants, variant =>
                    {
                        current.Compiled = true;
                        current.Collides = current.Solid && variant < 2;
                    }));
                }
                foreach (Chunk chunk in chunks)
                {
                    WriteChunk(chunk, directory);
                    Chunk current = chunk;
                    var variants = new List<string>();
                    if (chunk.Hulls > 0)
                        variants.Add(Qc(chunk.Model, chunk.Name + ".smd", "plastic", chunk.Name + "_phys.smd", "\t$concave\n\t$maxconvexpieces " + (chunk.Hulls + 64) + "\n"));
                    variants.Add(Qc(chunk.Model, chunk.Name + ".smd", "plastic", null, null));
                    jobs.Add((chunk.Name, chunk.Model, variants, variant =>
                    {
                        current.Compiled = true;
                        current.Collides = chunk.Hulls > 0 && variant == 0;
                    }));
                }
                if (jobs.Count == 0)
                    return;
                progress?.Report("Compiling " + jobs.Count + " prop model" + (jobs.Count == 1 ? "" : "s") + "...");
                int done = 0;
                var failures = new List<string>();
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancellation };
                // Back for vbsp and vrad once the props are compiled (see HideTextures).
                using IDisposable textures = SourceExporter.HideTextures(folder);
                Parallel.ForEach(jobs, options, job =>
                {
                    string qc = Path.Combine(directory, job.Name + ".qc");
                    string error = null;
                    for (int variant = 0; variant < job.Variants.Count; variant++)
                    {
                        File.WriteAllText(qc, job.Variants[variant], Encoding.ASCII);
                        string log = Run(studiomdl, "-nop4 -nox360 -game \"" + SourceExporter.ToolPath(folder.TrimEnd('\\', '/')) + "\" \"" +
                            SourceExporter.ToolPath(qc) + "\"", directory);
                        string mdl = Path.Combine(folder, "models", job.Model.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(mdl) && !Regex.IsMatch(log, @"^\s*ERROR", RegexOptions.Multiline))
                        {
                            job.Done(variant);
                            error = null;
                            break;
                        }
                        error = SourceExporter.ToolFailure(log, studiomdl).Replace(Environment.NewLine, " ");
                    }
                    if (error != null)
                        lock (failures)
                            failures.Add(job.Name + ": " + error);
                    int count = Interlocked.Increment(ref done);
                    if (count % 25 == 0)
                        progress?.Report("Compiled " + count + " of " + jobs.Count + " prop models...");
                });
                foreach (string failure in failures.Take(5))
                    progress?.Report("A prop was left out (" + failure + ").");
            }

            private string Qc(string model, string smd, string surface, string collisionSmd, string collision)
            {
                var text = new StringBuilder("// Generated by RoPorter\n");
                // studiomdl turns every model 90 degrees about Z unless $origin says otherwise; props must keep the place's
                // orientation exactly.
                text.Append("$modelname \"").Append(model).Append("\"\n$staticprop\n$origin 0 0 0 -90\n");
                text.Append("$body \"body\" \"").Append(smd).Append("\"\n");
                text.Append("$cdmaterials \"").Append(materialDir).Append("\"\n");
                text.Append("$surfaceprop \"").Append(surface).Append("\"\n$contents \"solid\"\n");
                text.Append("$sequence \"idle\" \"").Append(smd).Append("\" fps 30\n");
                if (collisionSmd != null)
                    text.Append("$collisionmodel \"").Append(collisionSmd).Append("\"\n{\n").Append(collision).Append("\t$mass 50\n}\n");
                return text.ToString();
            }

            // vrad lights static props at their vertices only, so a big wall lit at its four corners (buried in the floor and the
            // walls beside it) came out black. A triangle with a long edge is split: every edge into pieces at most this long
            // (counted from the edge alone, so the triangle sharing it splits it the same way and nothing cracks), fanned from
            // the triangle's centre.
            private const double MaxLitEdge = 128;

            private static IEnumerable<(double[] P, double[] N, float[] Uv)[]> LitSubdivision(Piece piece, int a, int b, int c)
            {
                var corners = new[] { a, b, c }.Select(i => (P: piece.Points[i], N: piece.Normals[i], Uv: piece.Uvs[i])).ToArray();
                if (piece.Decal)
                {
                    yield return corners;
                    yield break;
                }
                int[] segments = Enumerable.Range(0, 3).Select(e =>
                    Math.Min(16, Math.Max(1, (int)Math.Ceiling(Matrix.Length(Matrix.Subtract(corners[(e + 1) % 3].P, corners[e].P)) / MaxLitEdge)))).ToArray();
                if (segments.All(s => s == 1))
                {
                    yield return corners;
                    yield break;
                }
                (double[] P, double[] N, float[] Uv) Mix((double[] P, double[] N, float[] Uv) x, (double[] P, double[] N, float[] Uv) y, double t)
                {
                    double[] n = Enumerable.Range(0, 3).Select(k => x.N[k] + (y.N[k] - x.N[k]) * t).ToArray();
                    double length = Math.Max(Matrix.Length(n), 1e-9);
                    return (Enumerable.Range(0, 3).Select(k => x.P[k] + (y.P[k] - x.P[k]) * t).ToArray(), n.Select(v => v / length).ToArray(),
                        new[] { (float)(x.Uv[0] + (y.Uv[0] - x.Uv[0]) * t), (float)(x.Uv[1] + (y.Uv[1] - x.Uv[1]) * t) });
                }
                var ring = new List<(double[] P, double[] N, float[] Uv)>();
                for (int e = 0; e < 3; e++)
                    for (int s = 0; s < segments[e]; s++)
                        ring.Add(Mix(corners[e], corners[(e + 1) % 3], (double)s / segments[e]));
                var middle = Mix(Mix(corners[0], corners[1], 0.5), corners[2], 1 / 3.0);
                for (int i = 0; i < ring.Count; i++)
                    yield return new[] { middle, ring[i], ring[(i + 1) % ring.Count] };
            }

            // A merged prop's model (its parts' triangles around the prop's origin) and collision model (a hull per part).
            private void WriteChunk(Chunk chunk, string directory)
            {
                const string header = "version 1\nnodes\n0 \"root\" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\ntriangles\n";
                var text = new StringBuilder(header);
                foreach (Piece piece in chunk.Pieces)
                    foreach (var (a, b, c, material) in piece.Triangles)
                    {
                        string name = Material(material);
                        int hash = material.IndexOf('#');
                        float[] paletteUv = hash < 0 ? null : PaletteUv(int.Parse(material.Substring(hash + 1), CultureInfo.InvariantCulture));
                        foreach (var corners in LitSubdivision(piece, a, b, c))
                        {
                            text.Append(name).Append('\n');
                            foreach (var (p, n, uv) in corners)
                                SmdVertex(text, Matrix.Subtract(p, chunk.Origin), n, paletteUv ?? uv);
                        }
                    }
                File.WriteAllText(Path.Combine(directory, chunk.Name + ".smd"), text.Append("end\n").ToString(), Encoding.ASCII);
                if (chunk.Hulls == 0)
                    return;
                var physics = new StringBuilder(header);
                foreach (Piece piece in chunk.Pieces)
                    foreach (double[][] triangle in piece.Hull)
                    {
                        physics.Append("phys\n");
                        foreach (double[] p in triangle)
                            SmdVertex(physics, Matrix.Subtract(p, chunk.Origin), new double[] { 0, 0, 1 }, new float[2]);
                    }
                File.WriteAllText(Path.Combine(directory, chunk.Name + "_phys.smd"), physics.Append("end\n").ToString(), Encoding.ASCII);
                foreach (Piece piece in chunk.Pieces)
                    piece.Points.Clear();
            }

            // Terrain becomes displacements: tiles of 16 by 16 columns (wider on very large terrain), each the top of a thin
            // brush, its corners raised to the ground's height and its colour blended between the tile's two main materials.
            private void AddTerrain()
            {
                TerrainSurface ground = contents.Ground;
                if (ground == null)
                    return;
                int step = 1;
                while ((ground.Width / (16.0 * step)) * (ground.Depth / (16.0 * step)) > 1800)
                    step *= 2;
                int span = 16 * step;
                for (int tileZ = 0; tileZ < ground.Depth; tileZ += span)
                    for (int tileX = 0; tileX < ground.Width; tileX += span)
                    {
                        var columns = new List<byte>();
                        for (int z = tileZ; z < Math.Min(ground.Depth, tileZ + span); z++)
                            for (int x = tileX; x < Math.Min(ground.Width, tileX + span); x++)
                                if (ground.Has(x, z))
                                    columns.Add(ground.Materials[x + ground.Width * z]);
                        if (columns.Count == 0)
                            continue;
                        AddDisplacement(ground, tileX, tileZ, step, columns);
                    }
                if (displacements > 0)
                    progress?.Report("Terrain: " + displacements + " displacements.");
            }

            // How much of a material is around a corner of the column grid: the 4 by 4 columns about it, nearer ones counting more.
            // Roblox fades one material into the next over a couple of voxels; the 4 columns touching the corner switched within one.
            private static double Share(TerrainSurface ground, int cx, int cz, byte material)
            {
                double total = 0, matching = 0;
                for (int dz = -2; dz < 2; dz++)
                    for (int dx = -2; dx < 2; dx++)
                        if (ground.Has(cx + dx, cz + dz))
                        {
                            double weight = (2 - Math.Abs(dx + 0.5)) * (2 - Math.Abs(dz + 0.5));
                            total += weight;
                            if (ground.Materials[cx + dx + ground.Width * (cz + dz)] == material)
                                matching += weight;
                        }
                return total > 0 ? matching / total : 0;
            }

            private void AddDisplacement(TerrainSurface ground, int tileX, int tileZ, int step, List<byte> columns)
            {
                const int size = 17;
                var order = columns.GroupBy(m => m).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                byte first = order[0];
                byte? second = order.Count > 1 ? order[1] : (byte?)null;
                // Corner heights (studs); corners with no ground around them sink a voxel below the tile's lowest ground.
                var heights = new double[size, size];
                var alphas = new double[size, size];
                double lowest = double.MaxValue;
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                    {
                        int cx = tileX + i * step, cz = tileZ + j * step;
                        heights[i, j] = ground.CornerHeight(cx, cz, out bool any);
                        if (!any)
                            heights[i, j] = double.NaN;
                        else
                            lowest = Math.Min(lowest, heights[i, j]);
                        if (second.HasValue)
                            alphas[i, j] = 255 * Share(ground, cx, cz, second.Value);
                    }
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                        if (double.IsNaN(heights[i, j]))
                            heights[i, j] = lowest - RobloxTerrain.VoxelSize;
                // Source z of each corner; the brush's top sits at the lowest so every distance is upwards.
                double vs = RobloxTerrain.VoxelSize;
                double Z(double height) => (height - center[1]) * units + TerrainLift;
                double faceZ = double.MaxValue;
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                        faceZ = Math.Min(faceZ, Z(heights[i, j]));
                faceZ = Math.Floor(faceZ);
                // Roblox x runs against Source y, z against Source x.
                double x0 = (ground.MinX + tileX) * vs, x1 = (ground.MinX + tileX + 16 * step) * vs;
                double z0 = (ground.MinZ + tileZ) * vs, z1 = (ground.MinZ + tileZ + 16 * step) * vs;
                double[] lo = Point(x1, 0, z1), hi = Point(x0, 0, z0);
                lo[2] = faceZ - 16;
                hi[2] = faceZ;
                Point(x0, heights.Cast<double>().Max(), z0);
                Point(x0, heights.Cast<double>().Min(), z0);
                List<Side> sides = Box(lo, hi, "TOOLS/TOOLSNODRAW");
                Side top = sides[0];
                top.Material = TerrainMaterial(first, second);
                // Terrain textures (512 texels) repeat every couple of voxels.
                top.TextureScale = RobloxContent.TerrainStudsPerTile * units / 512;
                // vbsp lays the grid from the start corner (lowest x and y): rows run along +y (Roblox -x), columns along +x
                // (Roblox -z).
                var text = new StringBuilder();
                text.Append("\t\t\tdispinfo\n\t\t\t{\n\t\t\t\t\"power\" \"4\"\n");
                text.Append("\t\t\t\t\"startposition\" \"[").Append(V(new[] { lo[0], lo[1], faceZ })).Append("]\"\n");
                text.Append("\t\t\t\t\"flags\" \"0\"\n\t\t\t\t\"elevation\" \"0\"\n\t\t\t\t\"subdiv\" \"0\"\n");
                void Rows(string block, Func<int, int, string> value, int rows = size, int count = size)
                {
                    text.Append("\t\t\t\t").Append(block).Append("\n\t\t\t\t{\n");
                    for (int r = 0; r < rows; r++)
                        text.Append("\t\t\t\t\t\"row").Append(r).Append("\" \"").Append(string.Join(" ", Enumerable.Range(0, count).Select(c => value(r, c)))).Append("\"\n");
                    text.Append("\t\t\t\t}\n");
                }
                Rows("normals", (r, c) => "0 0 1");
                Rows("distances", (r, c) => F(Z(heights[16 - r, 16 - c]) - faceZ));
                Rows("offsets", (r, c) => "0 0 0");
                Rows("offset_normals", (r, c) => "0 0 1");
                Rows("alphas", (r, c) => F(alphas[16 - r, 16 - c]));
                Rows("triangle_tags", (r, c) => "9", 16, 32);
                text.Append("\t\t\t\tallowed_verts\n\t\t\t\t{\n\t\t\t\t\t\"10\" \"-1 -1 -1 -1 -1 -1 -1 -1 -1 -1\"\n\t\t\t\t}\n\t\t\t}\n");
                top.Displacement = text.ToString();
                brushes.Add(new Brush { Sides = sides, Group = Group.World });
                displacements++;
                // The tile in the map's picture.
                float[] color = RobloxTerrainColor(first);
                for (int r = 0; r < 16; r++)
                    for (int c = 0; c < 16; c++)
                    {
                        double[] P(int rr, int cc) => new[] { lo[0] + (hi[0] - lo[0]) * cc / 16.0, lo[1] + (hi[1] - lo[1]) * rr / 16.0, Z(heights[16 - rr, 16 - cc]) };
                        render.AddPolygon(new[] { P(r, c), P(r, c + 1), P(r + 1, c + 1), P(r + 1, c) }, color);
                    }
                render.AddPart(new[] { lo[0], lo[1], faceZ }, new[] { hi[0], hi[1], faceZ + 1 });
            }

            private float[] RobloxTerrainColor(byte material) => contents.Terrain.Color(material);

            // A terrain material's texture: Roblox's texture for it tinted to the place's colour for it (Terrain.MaterialColors),
            // or just the colour when there is no texture.
            private void TerrainTexture(byte material)
            {
                string name = "terrain_" + RobloxTerrain.MaterialName(material).ToLowerInvariant();
                string vtf = Path.Combine(materialFolder, name + ".vtf");
                if (File.Exists(vtf))
                    return;
                byte[] image = RobloxContent.MaterialTexture(RobloxTerrain.MaterialName(material));
                if (image == null)
                {
                    VtfWriter.WriteSolid(vtf, RobloxTerrainColor(material), 1);
                    return;
                }
                float[] tint = RobloxContent.TerrainTint(RobloxTerrain.MaterialName(material), RobloxTerrainColor(material));
                using (Picture source = Picture.Load(image))
                using (var copy = new Picture(source.Width, source.Height))
                {
                    copy.Draw(source, 0, 0, copy.Width, copy.Height, tint: tint, replace: true);
                    VtfWriter.Write(copy, vtf, VtfWriter.Kind.Color, compress: true);
                }
            }

            // A terrain material: one texture, or two blended by the displacement's alphas.
            private string TerrainMaterial(byte first, byte? second)
            {
                string Name(byte m) => "terrain_" + RobloxTerrain.MaterialName(m).ToLowerInvariant();
                TerrainTexture(first);
                if (!second.HasValue)
                {
                    if (materials.Add(Name(first)))
                        File.WriteAllText(Path.Combine(materialFolder, Name(first) + ".vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"" + materialDir + Name(first) +
                            "\"\n\t\"$surfaceprop\" \"" + TerrainSurfaceProp(first) + "\"\n}\n", Encoding.ASCII);
                    return materialDir + Name(first);
                }
                TerrainTexture(second.Value);
                string blend = Name(first) + "_" + RobloxTerrain.MaterialName(second.Value).ToLowerInvariant();
                if (materials.Add(blend))
                    File.WriteAllText(Path.Combine(materialFolder, blend + ".vmt"), "\"WorldVertexTransition\"\n{\n\t\"$basetexture\" \"" + materialDir + Name(first) +
                        "\"\n\t\"$surfaceprop\" \"" + TerrainSurfaceProp(first) + "\"\n\t\"$basetexture2\" \"" + materialDir + Name(second.Value) +
                        "\"\n\t\"$surfaceprop2\" \"" + TerrainSurfaceProp(second.Value) + "\"\n}\n", Encoding.ASCII);
                return materialDir + blend;
            }

            // Point and spot lights shine from their part's centre; surface lights from the face they are on.
            private void AddLight(PlacedPart part, RobloxInstance light)
            {
                float[] color = light.Get<float[]>("Color") ?? new float[] { 1, 1, 1 };
                double brightness = light.Number("Brightness") ?? 1;
                double range = light.Number("Range") ?? (light.ClassName == "PointLight" ? 8 : 16);
                if (brightness <= 0 || range <= 0)
                    return;
                double[] origin;
                double[] direction = null;
                if (light.ClassName == "PointLight")
                    origin = Source(part.CFrame, 0, 0, 0);
                else
                {
                    // NormalId: Right, Top, Back, Left, Bottom, Front.
                    int face = light.Enum("Face") ?? 5;
                    var normal = new double[3];
                    normal[face % 3] = face < 3 ? 1 : -1;
                    double[] world = Rotate(part.CFrame, normal);
                    direction = Direction(world);
                    double reach = light.ClassName == "SurfaceLight" ? part.Size[face % 3] / 2 + 2 / units : 0;
                    origin = Source(part.CFrame, normal[0] * reach, normal[1] * reach, normal[2] * reach);
                }
                var text = new StringBuilder("entity\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "classname", direction == null ? "light" : "light_spot");
                Key(text, "origin", V(origin));
                Key(text, "_light", Byte(color[0]) + " " + Byte(color[1]) + " " + Byte(color[2]) + " " + F(Math.Min(2000, 150 * brightness)));
                Key(text, "_lightHDR", "-1 -1 -1 1");
                Key(text, "_lightscaleHDR", "1");
                Key(text, "_fifty_percent_distance", F(0.4 * range * units));
                Key(text, "_zero_percent_distance", F(range * units));
                Key(text, "_hardfalloff", "0");
                if (direction != null)
                {
                    // vrad takes a light_spot's pitch as negative downwards.
                    double degrees = 180 / Math.PI, pitch = Math.Asin(Math.Max(-1, Math.Min(1, direction[2]))) * degrees;
                    double yaw = Math.Atan2(direction[1], direction[0]) * degrees;
                    double cone = Math.Max(1, Math.Min(89, (light.Number("Angle") ?? 90) / 2));
                    Key(text, "angles", F(pitch) + " " + F(yaw) + " 0");
                    Key(text, "pitch", F(pitch));
                    Key(text, "_cone", F(cone));
                    Key(text, "_inner_cone", F(cone / 2));
                    Key(text, "_exponent", "1");
                }
                entities.Add(text.Append("}\n").ToString());
            }

            // Players spawn on the place's SpawnLocations, or above its middle when it has none.
            private void AddSpawns(List<PlacedPart> parts)
            {
                var spawns = parts.Where(p => p.Instance.ClassName == "SpawnLocation" &&
                    !(p.Instance.Properties.TryGetValue("Enabled", out object enabled) && enabled is bool on && !on)).ToList();
                foreach (PlacedPart spawn in spawns)
                {
                    double[] origin = Source(spawn.CFrame, 0, spawn.Size[1] / 2 + 1 / units, 0);
                    double[] look = Direction(Rotate(spawn.CFrame, new double[] { 0, 0, -1 }));
                    AddEntity("info_player_start", origin, "0 " + F(Math.Atan2(look[1], look[0]) * 180 / Math.PI) + " 0");
                }
                if (spawns.Count > 0)
                    return;
                // The top of whatever is under the place's middle.
                double top = double.MinValue;
                foreach (PlacedPart part in parts.Where(p => p.CanCollide))
                {
                    var (low, high) = Bounds(part);
                    if (low[0] <= center[0] && high[0] >= center[0] && low[2] <= center[2] && high[2] >= center[2])
                        top = Math.Max(top, high[1]);
                }
                AddEntity("info_player_start", Point(center[0], top > double.MinValue ? top + 1 / units : center[1], center[2]), "0 0 0");
            }

            private void AddEntity(string classname, double[] origin, string angles)
            {
                var text = new StringBuilder("entity\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "classname", classname);
                Key(text, "origin", V(origin));
                Key(text, "angles", angles);
                entities.Add(text.Append("}\n").ToString());
            }

            // The sun from Lighting's time of day and latitude (it rises at +X, the east, and at northern latitudes passes
            // to the south, +Z), with its brightness and the outdoor ambient light.
            private void AddSun()
            {
                RobloxInstance lighting = contents.Lighting;
                double clock = 14;
                string time = lighting?.Get<string>("TimeOfDay");
                if (lighting?.Number("ClockTime") is float clockTime)
                    clock = clockTime;
                else if (time != null && TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out TimeSpan parsed))
                    clock = parsed.TotalHours;
                double latitude = (lighting?.Number("GeographicLatitude") ?? 41.733f) * Math.PI / 180;
                double hour = (clock - 12) / 12 * Math.PI;
                double[] sun = { -Math.Sin(hour), Math.Cos(latitude) * Math.Cos(hour), Math.Sin(latitude) * Math.Cos(hour) };
                double brightness = lighting?.Number("Brightness") ?? 2;
                // At night (and at sunrise or sunset) the light stays a little above the horizon, dimmer.
                if (sun[1] < 0.2)
                {
                    brightness *= sun[1] < 0 ? 0.25 : 0.6;
                    sun[1] = 0.2;
                }
                double length = Matrix.Length(sun);
                double[] travel = Direction(sun.Select(v => -v / length).ToArray());
                sunTravel = travel;
                double degrees = 180 / Math.PI;
                double yaw = Math.Atan2(travel[1], travel[0]) * degrees, pitch = Math.Asin(travel[2]) * degrees;
                float[] ambient = lighting?.Get<float[]>("OutdoorAmbient") ?? new float[] { 0.5f, 0.5f, 0.5f };
                double strongest = Math.Max(0.05, ambient.Max());

                var text = new StringBuilder("entity\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "classname", "light_environment");
                Key(text, "origin", "0 0 0");
                Key(text, "angles", "0 " + F(yaw) + " 0");
                // vrad takes light_environment's pitch as negative downwards.
                Key(text, "pitch", F(pitch));
                Key(text, "_light", "255 250 240 " + F(Math.Max(80, Math.Min(500, 110 * brightness))));
                Key(text, "_ambient", (int)(255 * ambient[0] / strongest) + " " + (int)(255 * ambient[1] / strongest) + " " + (int)(255 * ambient[2] / strongest) +
                    " " + F(Math.Max(20, Math.Min(200, 250 * strongest))));
                Key(text, "_lightHDR", "-1 -1 -1 1");
                Key(text, "_ambientHDR", "-1 -1 -1 1");
                Key(text, "SunSpreadAngle", "5");
                entities.Add(text.Append("}\n").ToString());

                // Dynamic shadows (players, physics props) fall the same way.
                text = new StringBuilder("entity\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "classname", "shadow_control");
                Key(text, "origin", "0 0 64");
                Key(text, "angles", F(-pitch) + " " + F(yaw) + " 0");
                Key(text, "color", "128 128 128");
                Key(text, "distance", "75");
                entities.Add(text.Append("}\n").ToString());
            }

            // The place's Sky as the map's skybox, or Roblox's default sky when the place has none (Garry's Mod's own when its
            // images can't be had). Roblox's front (-Z) is Source's +X side ("rt"), its left (-X) Source's +Y ("bk").
            private async Task<string> SkyAsync()
            {
                RobloxInstance sky = contents.Lighting?.Children.FirstOrDefault(c => c.ClassName == "Sky");
                var faces = new[] { ("rt", "Ft"), ("bk", "Lf"), ("lf", "Bk"), ("ft", "Rt"), ("up", "Up"), ("dn", "Dn") };
                var images = new Dictionary<string, byte[]>();
                foreach (var (suffix, face) in faces)
                {
                    byte[] image = sky == null ? RobloxContent.DefaultSky(face.ToLowerInvariant())
                        : await ImageAsync(sky.Get<string>("Skybox" + face)).ConfigureAwait(false);
                    if (image == null)
                    {
                        progress?.Report("The place's sky is incomplete; the map uses Garry's Mod's sky.");
                        return "painted";
                    }
                    images[suffix] = image;
                }
                string name = sky == null ? "roporter_default" : "roporter_" + id;
                string directory = Path.Combine(folder, "materials", "skybox");
                Directory.CreateDirectory(directory);
                foreach (var (suffix, _) in faces)
                {
                    using (Picture image = Picture.Load(images[suffix]))
                        VtfWriter.Write(image, Path.Combine(directory, name + suffix + ".vtf"), VtfWriter.Kind.Color,
                            VtfWriter.FlagClampS | VtfWriter.FlagClampT | VtfWriter.FlagNoLod, compress: true);
                    File.WriteAllText(Path.Combine(directory, name + suffix + ".vmt"), "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"skybox/" + name + suffix +
                        "\"\n\t\"$nofog\" \"1\"\n\t\"$ignorez\" \"1\"\n}\n", Encoding.ASCII);
                }
                return name;
            }

            private Task<byte[]> ImageAsync(string content) =>
                ModelFileBuilder.ImageAsync(content, download, textureKeys, textureBytes, progress, cancellation);

            // A part's Decals and Textures become quads just off its faces, merged into the props like small parts. Roblox
            // projects them onto any shape; here they go on blocks (spawn pads, signs, road markings, a baseplate's grid).
            private async Task AddDecalsAsync(PlacedPart part)
            {
                if (part.Shape != PartShape.Box)
                    return;
                foreach (RobloxInstance decal in ModelFileBuilder.Decals(part))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string material = await DecalMaterialAsync(decal).ConfigureAwait(false);
                    if (material == null)
                        continue;
                    ModelFileBuilder.DecalGeometry(part, decal, 32, out List<float[]> positions, out List<float[]> uvs, out List<int[]> faces, out float[] normal);
                    var piece = new Piece { Decal = true };
                    double[] n = SourceNormal(part.CFrame, normal[0], normal[1], normal[2], new float[] { 1, 1, 1 });
                    for (int i = 0; i < positions.Count; i++)
                    {
                        piece.Points.Add(Source(part.CFrame, positions[i][0], positions[i][1], positions[i][2]));
                        piece.Normals.Add(n);
                        piece.Uvs.Add(uvs[i]);
                    }
                    foreach (int[] face in faces)
                        piece.Triangles.Add((face[0], face[1], face[2], material));
                    piece.Center = new[] { piece.Points.Average(p => p[0]), piece.Points.Average(p => p[1]), piece.Points.Average(p => p[2]) };
                    pieces.Add(piece);
                    decals++;
                }
            }

            // A decal's material: its image tinted by its Color3, see-through where the image is and by its Transparency. Garry's
            // Mod ignores a material's $alpha on static props (a Texture at 0.98 transparency came out solid), so the decal's
            // transparency is multiplied into the texture's own alpha: one texture per image and transparency.
            private async Task<string> DecalMaterialAsync(RobloxInstance decal)
            {
                string content = decal.Get<string>("Texture") ?? decal.Get<string>("ColorMapContent");
                if (string.IsNullOrWhiteSpace(content))
                    return null;
                int alpha = Byte(1 - (decal.Number("Transparency") ?? 0));
                if (alpha == 0)
                    return null;
                string key = content.Trim() + "|" + alpha;
                if (!decalTextures.TryGetValue(key, out var texture))
                {
                    byte[] image = await ImageAsync(content.Trim()).ConfigureAwait(false);
                    texture = (null, false);
                    if (image != null)
                        try
                        {
                            string name = "d" + decalTextures.Count;
                            using (Picture source = Picture.Load(image))
                            {
                                if (alpha < 255)
                                    FadeAlpha(source, alpha / 255.0);
                                texture = (name, VtfWriter.Write(source, Path.Combine(materialFolder, name + ".vtf"), VtfWriter.Kind.Color, compress: true));
                            }
                        }
                        catch (InvalidDataException)
                        {
                            // Not an image that can be read.
                        }
                    if (texture.Name == null)
                        missingDecals++;
                    decalTextures[key] = texture;
                }
                if (texture.Name == null)
                    return null;
                float[] tint = decal.Get<float[]>("Color3") ?? new float[] { 1, 1, 1 };
                string material = texture.Name + "_" + Hex(tint);
                if (materials.Add(material))
                {
                    var vmt = new StringBuilder("\"VertexLitGeneric\"\n{\n");
                    vmt.Append("\t\"$basetexture\" \"").Append(materialDir).Append(texture.Name).Append("\"\n");
                    if (tint.Any(c => c < 0.999f))
                        vmt.Append("\t\"$color2\" \"[").Append(F(tint[0])).Append(' ').Append(F(tint[1])).Append(' ').Append(F(tint[2])).Append("]\"\n");
                    if (texture.Alpha)
                        vmt.Append("\t\"$translucent\" \"1\"\n");
                    vmt.Append(AmbientGlow);
                    File.WriteAllText(Path.Combine(materialFolder, material + ".vmt"), vmt.Append("}\n").ToString(), Encoding.ASCII);
                }
                return material;
            }

            // Multiplies a picture's alpha (opacity 0-1), for see-through textures on props.
            private static void FadeAlpha(Picture picture, double opacity)
            {
                int[] pixels = picture.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    int a = (int)Math.Round(((pixels[i] >> 24) & 255) * opacity);
                    pixels[i] = (pixels[i] & 0xFFFFFF) | (a << 24);
                }
                picture.SetPixels(pixels);
            }

            private static void Key(StringBuilder text, string key, string value) =>
                text.Append("\t\"").Append(key).Append("\" \"").Append(value).Append("\"\n");

            private string Vmf(string sky)
            {
                // The sky shell around everything, and a trigger at its bottom that kills whoever falls off the world.
                var inner = new[] { min.Select((v, axis) => Math.Max(-Limit, v - (axis == 2 ? BottomPadding : SidePadding))).ToArray(),
                    max.Select((v, axis) => Math.Min(Limit, v + (axis == 2 ? TopPadding : SidePadding))).ToArray() };
                double[] lo = inner[0], hi = inner[1];
                const double t = 16;
                var shell = new List<List<Side>>
                {
                    Box(new[] { lo[0] - t, lo[1] - t, hi[2] }, new[] { hi[0] + t, hi[1] + t, hi[2] + t }, "TOOLS/TOOLSSKYBOX"),
                    Box(new[] { lo[0] - t, lo[1] - t, lo[2] - t }, new[] { hi[0] + t, hi[1] + t, lo[2] }, "TOOLS/TOOLSSKYBOX"),
                    Box(new[] { lo[0] - t, lo[1] - t, lo[2] }, new[] { lo[0], hi[1] + t, hi[2] }, "TOOLS/TOOLSSKYBOX"),
                    Box(new[] { hi[0], lo[1] - t, lo[2] }, new[] { hi[0] + t, hi[1] + t, hi[2] }, "TOOLS/TOOLSSKYBOX"),
                    Box(new[] { lo[0], lo[1] - t, lo[2] }, new[] { hi[0], lo[1], hi[2] }, "TOOLS/TOOLSSKYBOX"),
                    Box(new[] { lo[0], hi[1], lo[2] }, new[] { hi[0], hi[1] + t, hi[2] }, "TOOLS/TOOLSSKYBOX")
                };
                brushes.Add(new Brush { Sides = Box(lo, new[] { hi[0], hi[1], lo[2] + 64 }, "TOOLS/TOOLSTRIGGER"), Group = Group.Trigger });

                // Faces at the very bottom of the world face nothing; they are left undrawn (and unlit).
                var parts = brushes.Where(b => b.Group != Group.Trigger && b.Group != Group.World).SelectMany(b => b.Sides).ToList();
                double floor = parts.Select(s => s.MinZ).DefaultIfEmpty(0).Min();
                foreach (Side side in parts)
                    if (side.Normal[2] < -0.99 && side.MaxZ < floor + 0.5 && !side.Material.StartsWith("TOOLS/", StringComparison.Ordinal))
                        side.Material = "TOOLS/TOOLSNODRAW";
                // Lightmap detail: 16 units per luxel, coarser when the map's surfaces would need too many luxels, and for faces
                // so large they would be cut into many pieces.
                double area = brushes.SelectMany(b => b.Sides).Where(s => !s.Material.StartsWith("TOOLS/", StringComparison.Ordinal)).Sum(s => s.Area);
                int luxel = 16;
                while (luxel < 128 && area / (luxel * (double)luxel) > LuxelBudget)
                    luxel *= 2;
                // Terrain has its own share, so its shadows (trees and houses on the ground) stay fine on a big map.
                double ground = brushes.SelectMany(b => b.Sides).Where(s => s.Displacement != null).Sum(s => s.Area);
                terrainLuxel = 16;
                while (terrainLuxel < 128 && ground / (terrainLuxel * (double)terrainLuxel) > LuxelBudget / 2)
                    terrainLuxel *= 2;

                var text = new StringBuilder();
                text.Append("versioninfo\n{\n\t\"editorversion\" \"400\"\n\t\"editorbuild\" \"8864\"\n\t\"mapversion\" \"1\"\n\t\"formatversion\" \"100\"\n\t\"prefab\" \"0\"\n}\n");
                text.Append("world\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "mapversion", "1");
                Key(text, "classname", "worldspawn");
                Key(text, "skyname", sky);
                Key(text, "maxpropscreenwidth", "-1");
                Key(text, "detailvbsp", "detail.vbsp");
                Key(text, "detailmaterial", "detail/detailsprites");
                foreach (List<Side> sides in shell)
                    Solid(text, sides, luxel);
                foreach (Brush brush in brushes.Where(b => b.Group == Group.World))
                    Solid(text, brush.Sides, luxel);
                text.Append("}\n");
                foreach (var group in brushes.Where(b => b.Group != Group.World).GroupBy(b => b.Group))
                {
                    text.Append("entity\n{\n");
                    Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                    switch (group.Key)
                    {
                        case Group.Detail:
                            Key(text, "classname", "func_detail");
                            break;
                        case Group.Illusionary:
                            Key(text, "classname", "func_illusionary");
                            break;
                        case Group.LitHolder:
                        case Group.LitHolderPassable:
                            // Parts holding a light don't block it.
                            Key(text, "classname", "func_brush");
                            Key(text, "Solidity", group.Key == Group.LitHolder ? "2" : "1");
                            Key(text, "vrad_brush_cast_shadows", "0");
                            Key(text, "spawnflags", "2");
                            break;
                        case Group.Trigger:
                            Key(text, "classname", "trigger_hurt");
                            Key(text, "damage", "100000");
                            Key(text, "damagecap", "100000");
                            Key(text, "damagetype", "0");
                            Key(text, "spawnflags", "3");
                            break;
                    }
                    foreach (Brush brush in group)
                        Solid(text, brush.Sides, luxel);
                    text.Append("}\n");
                }
                foreach (var placement in placements.Where(p => p.Key.Compiled))
                {
                    PlacedPart part = placement.Value;
                    StaticProp(text, "models/" + placement.Key.Model, V(Source(part.CFrame, 0, 0, 0)), Angles(part.CFrame), placement.Key.Collides,
                        lightHolders.Contains(part.Instance));
                }
                foreach (Chunk chunk in chunks.Where(c => c.Compiled))
                    StaticProp(text, "models/" + chunk.Model, V(chunk.Origin), "0 0 0", chunk.Collides, false);
                foreach (string entity in entities)
                    text.Append(entity);
                return text.ToString();
            }

            private void StaticProp(StringBuilder text, string model, string origin, string angles, bool solid, bool noShadows)
            {
                text.Append("entity\n{\n");
                Key(text, "id", (nextId++).ToString(CultureInfo.InvariantCulture));
                Key(text, "classname", "prop_static");
                Key(text, "origin", origin);
                Key(text, "angles", angles);
                Key(text, "model", model);
                Key(text, "solid", solid ? "6" : "0");
                Key(text, "disableshadows", noShadows ? "1" : "0");
                Key(text, "skin", "0");
                Key(text, "fademindist", "-1");
                Key(text, "fadescale", "1");
                text.Append("}\n");
            }

            // An axis-aligned box brush (for the shell, the trigger and terrain tiles); its top side comes first.
            private static List<Side> Box(double[] lo, double[] hi, string material)
            {
                double[] P(int x, int y, int z) => new[] { x == 0 ? lo[0] : hi[0], y == 0 ? lo[1] : hi[1], z == 0 ? lo[2] : hi[2] };
                var faces = new List<double[][]>
                {
                    new[] { P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1) },  // top
                    new[] { P(0, 1, 0), P(1, 1, 0), P(1, 0, 0), P(0, 0, 0) },  // bottom
                    new[] { P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0) },  // -X
                    new[] { P(1, 1, 0), P(1, 1, 1), P(1, 0, 1), P(1, 0, 0) },  // +X
                    new[] { P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), P(0, 0, 0) },  // -Y
                    new[] { P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0) }   // +Y
                };
                var sides = faces.Select(face => MakeSide(face.ToList())).ToList();
                foreach (Side side in sides)
                    side.Material = material;
                return sides;
            }

            private void Solid(StringBuilder text, List<Side> sides, int luxel)
            {
                text.Append("\tsolid\n\t{\n\t\t\"id\" \"").Append(nextId++).Append("\"\n");
                foreach (Side side in sides)
                {
                    // Texture axes from the face's main direction (any axes do for solid colours); lightmaps follow them.
                    double[] n = side.Normal;
                    double ax = Math.Abs(n[0]), ay = Math.Abs(n[1]), az = Math.Abs(n[2]);
                    string texel = F(side.TextureScale);
                    string u = (az >= ax && az >= ay ? "[1 0 0 0] " : ax >= ay ? "[0 1 0 0] " : "[1 0 0 0] ") + texel;
                    string v = (az >= ax && az >= ay ? "[0 -1 0 0] " : "[0 0 -1 0] ") + texel;
                    // A displacement's lightmap may be at most 125 luxels across.
                    int scale = side.Displacement != null ? Math.Max(terrainLuxel, (int)Math.Ceiling(side.Extent / 120))
                        : side.Extent > 8192 ? Math.Max(32, luxel) : luxel;
                    text.Append("\t\tside\n\t\t{\n");
                    text.Append("\t\t\t\"id\" \"").Append(nextId++).Append("\"\n");
                    text.Append("\t\t\t\"plane\" \"(").Append(V(side.A)).Append(") (").Append(V(side.B)).Append(") (").Append(V(side.C)).Append(")\"\n");
                    text.Append("\t\t\t\"material\" \"").Append(side.Material).Append("\"\n");
                    text.Append("\t\t\t\"uaxis\" \"").Append(u).Append("\"\n");
                    text.Append("\t\t\t\"vaxis\" \"").Append(v).Append("\"\n");
                    text.Append("\t\t\t\"rotation\" \"0\"\n");
                    text.Append("\t\t\t\"lightmapscale\" \"").Append(scale).Append("\"\n");
                    text.Append("\t\t\t\"smoothing_groups\" \"0\"\n");
                    if (side.Displacement != null)
                        text.Append(side.Displacement);
                    text.Append("\t\t}\n");
                }
                text.Append("\t}\n");
            }
        }

        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(double[] v) => F(v[0]) + " " + F(v[1]) + " " + F(v[2]);
    }
}
