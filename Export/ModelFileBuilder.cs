using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Rbx2Source.Export
{
    public enum PartShape { Box, Sphere, CylinderX, CylinderY, Wedge, CornerWedge, Mesh, Union }

    // One part of a model or place file: where it is, its shape and how it looks.
    public sealed class PlacedPart
    {
        public RobloxInstance Instance;
        // X Y Z, then the rotation R00..R22 row by row.
        public float[] CFrame;
        public float[] Size;
        public PartShape Shape;
        // A primitive is its unit shape (-0.5..0.5 on every axis) times Scale plus Offset. A mesh is its own vertices times
        // Scale plus Offset, or, with FitToSize (MeshParts), its bounds centred on the part and stretched to Size.
        public float[] Scale, Offset;
        public bool FitToSize;
        public long? MeshId, TextureId;
        public float[] Color;
        public float Transparency;
        // The part's colour shows through the texture's transparent parts (MeshPart and SurfaceAppearance textures).
        public bool Overlay;
        public bool DoubleSided, CanCollide;
        // Roblox's Material enum (Plastic, Wood, Neon...).
        public int Material;
        // A union's mesh: the asset holding it (or its mesh data saved in the file), the size it was made at, and whether it
        // shows its own colour (otherwise the colours of the parts it was made from).
        public long? UnionAsset;
        public byte[] UnionMesh;
        public float[] InitialSize;
        public bool UsePartColor = true;

        public const int NeonMaterial = 288;
        public bool Visible => Transparency < 0.999f;
        public string Name => Instance.Name;
    }

    // What a model or place file holds to export.
    public sealed class ModelContents
    {
        public List<RobloxInstance> Roots;
        // A place's Workspace and Lighting; null for a model file.
        public RobloxInstance Workspace, Lighting;
        public List<PlacedPart> Parts { get; } = new List<PlacedPart>();
        public int MissingMeshes;
        // The place's terrain and the top of its ground, or why it could not be read.
        public RobloxTerrain Terrain;
        public TerrainSurface Ground;
        public string TerrainProblem;
        public bool IsPlace => Workspace != null;
    }

    // Builds a static model from a Roblox model or place file (.rbxm / .rbxmx / .rbxl / .rbxlx): every visible part at its
    // CFrame and size, in Roblox world space (studs). MeshParts and file SpecialMeshes download their mesh and texture through
    // the API; other parts become their basic shape (block, sphere, cylinder, wedge) in the part's colour. A place exports
    // what is in its Workspace.
    public static class ModelFileBuilder
    {
        public const string TextureKeyPrefix = "__model_texture_";
        private const string Group = "Model";
        private static readonly float[] DefaultColor = { 163 / 255f, 162 / 255f, 165 / 255f };

        // SpecialMesh.MeshType values.
        private const int MeshHead = 0, MeshTorso = 1, MeshWedge = 2, MeshSphere = 3, MeshCylinder = 4, MeshFile = 5, MeshBrick = 6;
        // Part.Shape values.
        private const int ShapeBall = 0, ShapeCylinder = 2, ShapeWedge = 3, ShapeCornerWedge = 4;

        public static ModelContents Read(byte[] file)
        {
            var contents = new ModelContents { Roots = RobloxModelFile.ReadInstances(file) };
            contents.Workspace = contents.Roots.FirstOrDefault(root => root.ClassName == "Workspace");
            contents.Lighting = contents.Workspace != null ? contents.Roots.FirstOrDefault(root => root.ClassName == "Lighting") : null;
            RobloxInstance terrain = contents.Workspace?.Children.FirstOrDefault(child => child.ClassName == "Terrain");
            if (terrain != null && terrain.Binary.TryGetValue("SmoothGrid", out byte[] grid) && grid.Length > 2)
                try
                {
                    contents.Terrain = RobloxTerrain.Read(grid, terrain.Binary.TryGetValue("MaterialColors", out byte[] colors) ? colors : null);
                    contents.Ground = contents.Terrain.Surface();
                }
                catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
                {
                    contents.TerrainProblem = error.Message;
                }
            foreach (RobloxInstance instance in Walk(contents.Workspace?.Children ?? contents.Roots))
                if (instance.Get<float[]>("CFrame")?.Length == 12 && instance.Get<float[]>("size")?.Length == 3)
                {
                    PlacedPart part = Resolve(instance);
                    if (part != null)
                        contents.Parts.Add(part);
                    else
                        contents.MissingMeshes++;
                }
            return contents;
        }

        // Every instance below these, leaving out the camera (and what it holds) and Terrain, which is voxels rather than a part.
        private static IEnumerable<RobloxInstance> Walk(IEnumerable<RobloxInstance> instances)
        {
            foreach (RobloxInstance instance in instances)
            {
                if (instance.ClassName == "Camera" || instance.ClassName == "Terrain")
                    continue;
                yield return instance;
                foreach (RobloxInstance child in Walk(instance.Children))
                    yield return child;
            }
        }

        // The part's shape and look, or null for a mesh part without a mesh.
        private static PlacedPart Resolve(RobloxInstance instance)
        {
            float[] size = instance.Get<float[]>("size");
            var part = new PlacedPart
            {
                Instance = instance,
                CFrame = instance.Get<float[]>("CFrame"),
                Size = size,
                Shape = PartShape.Box,
                Scale = size,
                Offset = new float[3],
                Color = instance.Get<float[]>("Color3uint8") ?? instance.Get<float[]>("Color") ?? DefaultColor,
                Transparency = instance.Number("Transparency") ?? 0,
                DoubleSided = instance.Flag("DoubleSided"),
                // Parts collide unless CanCollide is saved as false.
                CanCollide = !(instance.Properties.TryGetValue("CanCollide", out object collide) && collide is bool solid && !solid),
                Material = instance.Enum("Material") ?? 256
            };
            // A union's mesh is in its asset (or saved with it), made at its InitialSize.
            if (instance.ClassName.EndsWith("Operation", StringComparison.Ordinal))
            {
                part.Shape = PartShape.Union;
                part.UnionAsset = RobloxModelFile.ParseId(instance.Get<string>("AssetId"));
                part.UnionMesh = instance.Binary.TryGetValue("MeshData", out byte[] mesh) && mesh.Length > 0 ? mesh : null;
                part.InitialSize = instance.Get<float[]>("InitialSize") ?? size;
                part.UsePartColor = !(instance.Properties.TryGetValue("UsePartColor", out object own) && own is bool shown && !shown);
                return part.UnionAsset.HasValue || part.UnionMesh != null ? part : null;
            }
            RobloxInstance special = instance.Children.FirstOrDefault(c => c.ClassName == "SpecialMesh" || c.ClassName == "FileMesh" ||
                c.ClassName == "BlockMesh" || c.ClassName == "CylinderMesh");
            RobloxInstance surface = instance.Children.FirstOrDefault(c => c.ClassName == "SurfaceAppearance");
            if (instance.ClassName == "MeshPart")
            {
                part.Shape = PartShape.Mesh;
                part.MeshId = RobloxModelFile.ParseId(instance.Get<string>("MeshId") ?? instance.Get<string>("MeshContent"));
                part.TextureId = RobloxModelFile.ParseId(instance.Get<string>("TextureID") ?? instance.Get<string>("TextureContent"));
                // A MeshPart's mesh is stretched to its size, and its colour shows through the texture's transparent parts.
                part.FitToSize = part.Overlay = true;
                if (!part.MeshId.HasValue)
                    return null;
            }
            else if (special != null)
            {
                float[] meshScale = special.Get<float[]>("Scale") ?? new float[] { 1, 1, 1 };
                part.Offset = special.Get<float[]>("Offset") ?? part.Offset;
                int type = special.ClassName == "FileMesh" ? MeshFile : special.ClassName == "BlockMesh" ? MeshBrick
                    : special.ClassName == "CylinderMesh" ? MeshCylinder : special.Enum("MeshType") ?? MeshHead;
                if (type == MeshFile)
                {
                    // A file mesh keeps its own size, times the SpecialMesh's scale.
                    part.Shape = PartShape.Mesh;
                    part.MeshId = RobloxModelFile.ParseId(special.Get<string>("MeshId"));
                    part.TextureId = RobloxModelFile.ParseId(special.Get<string>("TextureId"));
                    part.Scale = meshScale;
                    if (!part.MeshId.HasValue)
                        return null;
                }
                else
                {
                    part.Scale = new[] { size[0] * meshScale[0], size[1] * meshScale[1], size[2] * meshScale[2] };
                    part.Shape = type == MeshSphere ? PartShape.Sphere : type == MeshCylinder || type == MeshHead ? PartShape.CylinderY
                        : type == MeshWedge ? PartShape.Wedge : PartShape.Box;
                    if (type == MeshSphere || type == MeshHead)
                        part.Scale = Uniform(part.Scale, type == MeshHead);
                }
            }
            else
            {
                int? shape = instance.Enum("shape");
                if (instance.ClassName == "WedgePart" || shape == ShapeWedge)
                    part.Shape = PartShape.Wedge;
                else if (instance.ClassName == "CornerWedgePart" || shape == ShapeCornerWedge)
                    part.Shape = PartShape.CornerWedge;
                else if (shape == ShapeBall)
                {
                    part.Shape = PartShape.Sphere;
                    part.Scale = Uniform(size, false);
                }
                else if (shape == ShapeCylinder)
                {
                    part.Shape = PartShape.CylinderX;
                    float diameter = Math.Min(size[1], size[2]);
                    part.Scale = new[] { size[0], diameter, diameter };
                }
            }
            if (surface != null)
            {
                part.TextureId = RobloxModelFile.ParseId(surface.Get<string>("ColorMap") ?? surface.Get<string>("ColorMapContent")) ?? part.TextureId;
                part.Overlay = true;
            }
            return part;
        }

        public static Task<WavefrontModel> BuildAsync(byte[] file, Func<long, CancellationToken, Task<byte[]>> download,
            Dictionary<string, byte[]> localTextures, IProgress<string> progress, CancellationToken cancellation) =>
            BuildAsync(Read(file), download, localTextures, progress, cancellation);

        public static async Task<WavefrontModel> BuildAsync(ModelContents contents, Func<long, CancellationToken, Task<byte[]>> download,
            Dictionary<string, byte[]> localTextures, IProgress<string> progress, CancellationToken cancellation)
        {
            if (contents.Parts.Count == 0)
                throw new InvalidDataException(contents.IsPlace ? "The place's Workspace has no parts to export." : "The model file has no parts to export.");
            progress?.Report((contents.IsPlace ? "Place: " : "Model file: ") + contents.Parts.Count + " parts.");
            var model = new WavefrontModel();
            var meshes = new MeshCache(download, progress);
            var unions = new UnionCache(download, progress);
            var textures = new Dictionary<long, string>();
            int hidden = 0, missing = contents.MissingMeshes;
            foreach (PlacedPart part in contents.Parts)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!part.Visible)
                {
                    hidden++;
                    continue;
                }
                if (part.Shape == PartShape.Union)
                {
                    CsgMesh union = await unions.GetAsync(part, cancellation).ConfigureAwait(false);
                    if (union == null)
                    {
                        missing++;
                        continue;
                    }
                    float[] stretch = UnionScale(part);
                    // A union shows its own colour, or the colours of the parts it was made from; textured by its material.
                    bool textured = Textured(part);
                    List<float[]> unionUvs = textured ? BoxUvs(union.Positions, union.Normals, stretch, new float[3]) : new List<float[]>();
                    foreach (var colour in union.Faces.GroupBy(face => part.UsePartColor ? part.Color : union.Colors[face[0]], ColorComparer.Instance))
                        Place(model, part.CFrame, stretch, new float[3], union.Positions, union.Normals, unionUvs, colour.ToList(), textured
                            ? MaterialTexture(model, part.Material, colour.Key, 1 - part.Transparency, part.DoubleSided, localTextures)
                            : Material(model, null, colour.Key, 1 - part.Transparency, false, part.DoubleSided));
                    continue;
                }
                List<float[]> positions, normals, texcoords;
                List<int[]> faces;
                float[] scale = part.Scale, offset = part.Offset;
                if (part.Shape == PartShape.Mesh)
                {
                    RobloxMesh mesh = await meshes.GetAsync(part.MeshId.Value, cancellation).ConfigureAwait(false);
                    if (mesh == null)
                    {
                        missing++;
                        continue;
                    }
                    positions = mesh.Positions;
                    normals = mesh.Normals;
                    texcoords = mesh.Texcoords;
                    faces = mesh.Faces;
                    if (part.FitToSize)
                        FitToSize(mesh, part.Size, out scale, out offset);
                }
                else
                    BuildPrimitive(part.Shape, out positions, out normals, out texcoords, out faces);

                string texture = null;
                if (part.TextureId.HasValue)
                    texture = await TextureAsync(part.TextureId.Value, download, textures, localTextures, progress, cancellation).ConfigureAwait(false);
                string material;
                if (texture == null && Textured(part))
                {
                    // The part's Roblox material (wood, brick...): its texture in the part's colour, tiled across the part.
                    texcoords = BoxUvs(positions, normals, scale, offset);
                    material = MaterialTexture(model, part.Material, part.Color, 1 - part.Transparency, part.DoubleSided, localTextures);
                }
                else
                    material = Material(model, texture, texture != null && !part.Overlay ? new float[] { 1, 1, 1 } : part.Color, 1 - part.Transparency,
                        texture != null && part.Overlay, part.DoubleSided);
                Place(model, part.CFrame, scale, offset, positions, normals, texcoords, faces, material);
            }
            // Decals and Textures, also on invisible parts (a sign can be just its decal).
            int decals = 0;
            var decalImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlacedPart part in contents.Parts.Where(p => p.Shape == PartShape.Box))
                foreach (RobloxInstance decal in Decals(part))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string material = await DecalMaterialAsync(model, decal, decalImages, download, textures, localTextures, progress, cancellation).ConfigureAwait(false);
                    if (material == null)
                        continue;
                    DecalGeometry(part, decal, float.MaxValue, out List<float[]> positions, out List<float[]> uvs, out List<int[]> faces, out float[] normal);
                    Place(model, part.CFrame, new float[] { 1, 1, 1 }, new float[3], positions, positions.Select(p => normal).ToList(), uvs, faces, material);
                    decals++;
                }
            if (decals > 0)
                progress?.Report(decals + " decal" + (decals == 1 ? "" : "s") + " and textures.");
            if (contents.Ground != null)
                AddGround(model, contents.Terrain, contents.Ground, localTextures, cancellation);
            ReportLeftOut(contents, hidden, missing, progress);
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("The " + (contents.IsPlace ? "place" : "model file") + " has no visible parts that could be exported.");
            progress?.Report("Model: " + model.Triangles.Count + " triangles, " + model.Materials.Count + " materials.");
            return model;
        }

        // A Roblox material on a part: its texture multiplied by the part's colour (baked, so every format shows it the same).
        internal static string MaterialTexture(WavefrontModel model, int material, float[] color, float opacity, bool doubleSided, Dictionary<string, byte[]> localTextures)
        {
            int Byte(float value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
            string roblox = RobloxContent.MaterialName(material);
            string hex = Byte(color[0]).ToString("X2") + Byte(color[1]).ToString("X2") + Byte(color[2]).ToString("X2");
            string key = "__material_" + roblox + "_" + hex + "__";
            if (!localTextures.ContainsKey(key))
                localTextures[key] = Tinted(RobloxContent.MaterialTexture(roblox), color, 256, false);
            string name = roblox + "_" + hex + (opacity < 1 ? "_A" + Byte(opacity).ToString("X2") : "") + (doubleSided ? "_2" : "");
            if (!model.Materials.ContainsKey(name))
                model.Materials[name] = new ObjMaterial { Name = name, Diffuse = (float[])color.Clone(), Opacity = opacity, Texture = key, DoubleSided = doubleSided };
            return name;
        }

        // A decal's material: its image (tinted by its Color3), see-through where the image is and by its Transparency.
        private static async Task<string> DecalMaterialAsync(WavefrontModel model, RobloxInstance decal, Dictionary<string, string> images,
            Func<long, CancellationToken, Task<byte[]>> download, Dictionary<long, string> textures, Dictionary<string, byte[]> localTextures,
            IProgress<string> progress, CancellationToken cancellation)
        {
            string content = (decal.Get<string>("Texture") ?? decal.Get<string>("ColorMapContent"))?.Trim();
            if (string.IsNullOrEmpty(content))
                return null;
            float[] tint = decal.Get<float[]>("Color3") ?? new float[] { 1, 1, 1 };
            float opacity = 1 - (decal.Number("Transparency") ?? 0);
            int Byte(float value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
            string hex = Byte(tint[0]).ToString("X2") + Byte(tint[1]).ToString("X2") + Byte(tint[2]).ToString("X2");
            if (!images.TryGetValue(content + "|" + hex, out string key))
            {
                byte[] image = await ImageAsync(content, download, textures, localTextures, progress, cancellation).ConfigureAwait(false);
                key = null;
                if (image != null)
                    try
                    {
                        key = "__decal_" + images.Count + "__";
                        localTextures[key] = tint.All(c => c > 0.999f) ? image : Tinted(image, tint, 0, true);
                    }
                    catch (ArgumentException)
                    {
                        key = null;
                    }
                images[content + "|" + hex] = key;
            }
            if (key == null)
                return null;
            string name = "Decal" + key.Trim('_').Substring("decal_".Length) + (opacity < 1 ? "_A" + Byte(opacity).ToString("X2") : "");
            // The image's own see-through parts are kept as they are (not taken for a missing alpha channel).
            if (!model.Materials.ContainsKey(name))
                model.Materials[name] = new ObjMaterial { Name = name, Opacity = opacity, Texture = key, AlphaModeGiven = true };
            return name;
        }

        // A union's mesh is made at its InitialSize; the union stretches it to its size.
        internal static float[] UnionScale(PlacedPart part) =>
            Enumerable.Range(0, 3).Select(axis => part.InitialSize[axis] > 1e-4f ? part.Size[axis] / part.InitialSize[axis] : 1).ToArray();

        internal sealed class ColorComparer : IEqualityComparer<float[]>
        {
            public static readonly ColorComparer Instance = new ColorComparer();
            private static int Key(float[] c) => ((int)Math.Round(c[0] * 255) << 16) | ((int)Math.Round(c[1] * 255) << 8) | (int)Math.Round(c[2] * 255);
            public bool Equals(float[] a, float[] b) => Key(a) == Key(b);
            public int GetHashCode(float[] c) => Key(c);
        }

        // The terrain's ground as a mesh in its materials' colours: a corner per voxel column edge, at the height of the ground
        // around it.
        private static void AddGround(WavefrontModel model, RobloxTerrain terrain, TerrainSurface ground, Dictionary<string, byte[]> localTextures,
            CancellationToken cancellation)
        {
            var corners = new Dictionary<(int, int), ObjCorner>();
            ObjCorner Corner(int x, int z)
            {
                if (corners.TryGetValue((x, z), out ObjCorner corner))
                    return corner;
                float height = ground.CornerHeight(x, z, out _);
                // The surface's slope from the neighbouring corners gives a smooth normal.
                float Neighbour(int nx, int nz) { float h = ground.CornerHeight(nx, nz, out bool any); return any ? h : height; }
                float dx = (Neighbour(x + 1, z) - Neighbour(x - 1, z)) / (2 * RobloxTerrain.VoxelSize);
                float dz = (Neighbour(x, z + 1) - Neighbour(x, z - 1)) / (2 * RobloxTerrain.VoxelSize);
                float length = (float)Math.Sqrt(dx * dx + 1 + dz * dz);
                float worldX = (ground.MinX + x) * RobloxTerrain.VoxelSize, worldZ = (ground.MinZ + z) * RobloxTerrain.VoxelSize;
                model.Positions.Add(new[] { worldX, height, worldZ });
                model.Normals.Add(new[] { -dx / length, 1 / length, -dz / length });
                // Terrain textures repeat across the world every couple of voxels.
                model.Texcoords.Add(new[] { worldX / RobloxContent.TerrainStudsPerTile, -worldZ / RobloxContent.TerrainStudsPerTile });
                corner = new ObjCorner { Position = model.Positions.Count - 1, Normal = model.Normals.Count - 1, Texture = model.Texcoords.Count - 1 };
                corners[(x, z)] = corner;
                return corner;
            }
            for (int z = 0; z < ground.Depth; z++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < ground.Width; x++)
                {
                    if (!ground.Has(x, z))
                        continue;
                    byte material = ground.Materials[x + ground.Width * z];
                    string name = "Terrain_" + RobloxTerrain.MaterialName(material);
                    if (!model.Materials.ContainsKey(name))
                    {
                        // Roblox's texture for the material multiplied by the place's colour for it (or just the colour).
                        byte[] texture = RobloxContent.MaterialTexture(RobloxTerrain.MaterialName(material));
                        string key = null;
                        if (texture != null)
                            localTextures[key = "__terrain_" + RobloxTerrain.MaterialName(material) + "__"] = Tinted(texture, terrain.Color(material), 512, false);
                        model.Materials[name] = new ObjMaterial { Name = name, Diffuse = terrain.Color(material), Texture = key };
                    }
                    ObjCorner a = Corner(x, z), b = Corner(x + 1, z), c = Corner(x + 1, z + 1), d = Corner(x, z + 1);
                    // Counter-clockwise seen from above.
                    foreach (ObjCorner[] triangle in new[] { new[] { a, c, b }, new[] { a, d, c } })
                        model.Triangles.Add(new ObjTriangle
                        {
                            Material = name,
                            Group = "Terrain",
                            Corners = triangle.Select(k => new ObjCorner { Position = k.Position, Normal = k.Normal, Texture = k.Texture }).ToArray()
                        });
                }
            }
        }

        // Log lines for what an export leaves out.
        internal static void ReportLeftOut(ModelContents contents, int hidden, int missing, IProgress<string> progress)
        {
            if (hidden > 0)
                progress?.Report(hidden + " invisible part" + (hidden == 1 ? " was" : "s were") + " left out.");
            if (missing > 0)
                progress?.Report(missing + " part" + (missing == 1 ? "" : "s") + " left out: " + (missing == 1 ? "its" : "their") + " mesh could not be downloaded or read.");
            if (contents.TerrainProblem != null)
                progress?.Report("The place's terrain was left out: " + contents.TerrainProblem);
            else if (contents.Terrain?.HasWater == true)
                progress?.Report("The terrain's water is not exported.");
        }

        // Downloads each union's mesh once (or reads it from the file); one that can't be read is reported once and then skipped.
        internal sealed class UnionCache
        {
            private readonly Func<long, CancellationToken, Task<byte[]>> download;
            private readonly IProgress<string> progress;
            private readonly Dictionary<long, CsgMesh> meshes = new Dictionary<long, CsgMesh>();

            public UnionCache(Func<long, CancellationToken, Task<byte[]>> download, IProgress<string> progress)
            {
                this.download = download;
                this.progress = progress;
            }

            public async Task<CsgMesh> GetAsync(PlacedPart part, CancellationToken cancellation)
            {
                try
                {
                    if (part.UnionMesh != null)
                        return CsgMesh.Parse(part.UnionMesh);
                }
                catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
                {
                    if (!part.UnionAsset.HasValue)
                    {
                        progress?.Report("Union " + part.Name + " skipped (" + error.Message + ").");
                        return null;
                    }
                }
                long id = part.UnionAsset.Value;
                if (meshes.TryGetValue(id, out CsgMesh mesh))
                    return mesh;
                try
                {
                    progress?.Report("Downloading union " + id + "...");
                    mesh = CsgMesh.FromAsset(await download(id, cancellation).ConfigureAwait(false));
                }
                catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is NotSupportedException ||
                    error is AssetAccessException)
                {
                    progress?.Report("Union " + id + " skipped (" + error.Message + ").");
                }
                meshes[id] = mesh;
                return mesh;
            }
        }

        // Downloads each mesh once; a mesh that can't be downloaded or read is reported once and then skipped.
        internal sealed class MeshCache
        {
            private readonly Func<long, CancellationToken, Task<byte[]>> download;
            private readonly IProgress<string> progress;
            private readonly Dictionary<long, RobloxMesh> meshes = new Dictionary<long, RobloxMesh>();

            public MeshCache(Func<long, CancellationToken, Task<byte[]>> download, IProgress<string> progress)
            {
                this.download = download;
                this.progress = progress;
            }

            public async Task<RobloxMesh> GetAsync(long id, CancellationToken cancellation)
            {
                if (meshes.TryGetValue(id, out RobloxMesh mesh))
                    return mesh;
                try
                {
                    progress?.Report("Downloading mesh " + id + "...");
                    mesh = RobloxMesh.Parse(await download(id, cancellation).ConfigureAwait(false));
                }
                catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is NotSupportedException ||
                    error is AssetAccessException)
                {
                    progress?.Report("Mesh " + id + " skipped (" + error.Message + ").");
                }
                meshes[id] = mesh;
                return mesh;
            }
        }

        // A MeshPart's scale and offset: its mesh's bounds are centred on the part and stretched to its size.
        internal static void FitToSize(RobloxMesh mesh, float[] size, out float[] scale, out float[] offset)
        {
            var used = mesh.Faces.SelectMany(f => f).Distinct().Select(i => mesh.Positions[i]).ToList();
            if (used.Count == 0)
            {
                scale = new float[] { 1, 1, 1 };
                offset = new float[3];
                return;
            }
            float[] min = Enumerable.Range(0, 3).Select(a => used.Min(p => p[a])).ToArray();
            float[] max = Enumerable.Range(0, 3).Select(a => used.Max(p => p[a])).ToArray();
            float[] fit = Enumerable.Range(0, 3).Select(a => max[a] - min[a] > 1e-4f ? size[a] / (max[a] - min[a]) : 1).ToArray();
            scale = fit;
            offset = Enumerable.Range(0, 3).Select(a => -(min[a] + max[a]) / 2 * fit[a]).ToArray();
        }

        // A sphere or a Head mesh is as wide as the part's smallest side (a Head keeps its height).
        private static float[] Uniform(float[] size, bool keepHeight)
        {
            float width = keepHeight ? Math.Min(size[0], size[2]) : size.Min();
            return new[] { width, keepHeight ? size[1] : width, width };
        }

        // Downloads a texture once; a Decal ID is followed to its image. A texture that isn't a PNG or JPEG image is skipped.
        internal static async Task<string> TextureAsync(long id, Func<long, CancellationToken, Task<byte[]>> download, Dictionary<long, string> textures,
            Dictionary<string, byte[]> localTextures, IProgress<string> progress, CancellationToken cancellation)
        {
            if (textures.TryGetValue(id, out string key))
                return key;
            try
            {
                progress?.Report("Downloading texture " + id + "...");
                byte[] image = await download(id, cancellation).ConfigureAwait(false);
                if (!IsImage(image) && image.Length >= 8)
                {
                    long? decal = RobloxModelFile.ReadInstances(image).SelectMany(root => new[] { root }.Concat(root.Descendants()))
                        .Select(instance => RobloxModelFile.ParseId(instance.Get<string>("Texture"))).FirstOrDefault(found => found.HasValue);
                    if (decal.HasValue && decal.Value != id)
                        image = await download(decal.Value, cancellation).ConfigureAwait(false);
                }
                if (!IsImage(image))
                    throw new InvalidDataException("it is not a PNG or JPEG image");
                key = TextureKeyPrefix + id.ToString(CultureInfo.InvariantCulture) + "__";
                localTextures[key] = image;
            }
            catch (Exception error) when (error is HttpRequestException || error is InvalidDataException || error is NotSupportedException ||
                error is AssetAccessException)
            {
                progress?.Report("Texture " + id + " skipped (" + error.Message + ").");
                key = null;
            }
            textures[id] = key;
            return key;
        }

        // An image a property refers to: an image or decal asset (through the API), or an rbxasset:// file Roblox ships with its
        // client (RoPorter's copy, or the asset Roblox serves for that path). Null when it can't be had.
        internal static async Task<byte[]> ImageAsync(string content, Func<long, CancellationToken, Task<byte[]>> download, Dictionary<long, string> textures,
            Dictionary<string, byte[]> localTextures, IProgress<string> progress, CancellationToken cancellation)
        {
            bool shipped = RobloxContent.IsRbxAsset(content);
            if (shipped && RobloxContent.Asset(content) is byte[] copy)
                return copy;
            long? asset = shipped ? RobloxContent.LegacyAsset(content) : RobloxModelFile.ParseId(content);
            if (!asset.HasValue)
                return null;
            string key = await TextureAsync(asset.Value, download, textures, localTextures, progress, cancellation).ConfigureAwait(false);
            return key != null ? localTextures[key] : null;
        }

        internal static bool IsImage(byte[] data) =>
            data != null && (data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ||
                data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255);

        // One material per texture, colour and transparency.
        internal static string Material(WavefrontModel model, string texture, float[] color, float opacity, bool overlay, bool doubleSided)
        {
            int Byte(float value) => (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
            string name = (texture != null ? "Texture" + texture.Substring(TextureKeyPrefix.Length).TrimEnd('_') + (overlay ? "_" : "") : "Color_") +
                (texture == null || overlay ? Byte(color[0]).ToString("X2") + Byte(color[1]).ToString("X2") + Byte(color[2]).ToString("X2") : "") +
                (opacity < 1 ? "_A" + Byte(opacity).ToString("X2") : "") + (doubleSided ? "_2" : "");
            if (!model.Materials.ContainsKey(name))
                model.Materials[name] = new ObjMaterial
                {
                    Name = name, Diffuse = (float[])color.Clone(), Opacity = opacity, Texture = texture, Overlay = overlay, DoubleSided = doubleSided
                };
            return name;
        }

        // Adds a mesh scaled (and offset) in the part's space, then moved to the part's CFrame.
        internal static void Place(WavefrontModel model, float[] cframe, float[] scale, float[] offset, List<float[]> positions, List<float[]> normals,
            List<float[]> texcoords, List<int[]> faces, string material)
        {
            float[] Rotate(float x, float y, float z) => new[]
            {
                cframe[3] * x + cframe[4] * y + cframe[5] * z,
                cframe[6] * x + cframe[7] * y + cframe[8] * z,
                cframe[9] * x + cframe[10] * y + cframe[11] * z
            };
            int positionBase = model.Positions.Count, normalBase = model.Normals.Count, texcoordBase = model.Texcoords.Count;
            foreach (float[] p in positions)
            {
                float[] world = Rotate(p[0] * scale[0] + offset[0], p[1] * scale[1] + offset[1], p[2] * scale[2] + offset[2]);
                model.Positions.Add(new[] { world[0] + cframe[0], world[1] + cframe[1], world[2] + cframe[2] });
            }
            // Normals follow the inverse of the stretch, so a flattened sphere still shades like one.
            foreach (float[] n in normals)
            {
                float[] world = Rotate(n[0] / NonZero(scale[0]), n[1] / NonZero(scale[1]), n[2] / NonZero(scale[2]));
                float length = (float)Math.Sqrt(world[0] * world[0] + world[1] * world[1] + world[2] * world[2]);
                model.Normals.Add(length > 1e-8f ? new[] { world[0] / length, world[1] / length, world[2] / length } : new float[] { 0, 1, 0 });
            }
            model.Texcoords.AddRange(texcoords.Select(t => (float[])t.Clone()));
            // A mirrored scale turns the triangles inside out, so their winding is reversed.
            bool mirrored = scale[0] * scale[1] * scale[2] < 0;
            bool hasNormals = normals.Count == positions.Count, hasTexcoords = texcoords.Count == positions.Count;
            foreach (int[] face in faces)
            {
                // Enumerable.Reverse by name: C# 14 binds array.Reverse() to the in-place span Reverse, which returns nothing.
                IEnumerable<int> corners = mirrored ? Enumerable.Reverse(face) : face;
                model.Triangles.Add(new ObjTriangle
                {
                    Material = material,
                    Group = Group,
                    Corners = corners.Select(index => new ObjCorner
                    {
                        Position = positionBase + index,
                        Normal = hasNormals ? normalBase + index : -1,
                        Texture = hasTexcoords ? texcoordBase + index : -1
                    }).ToArray()
                });
            }
        }

        internal static float NonZero(float value) => Math.Abs(value) > 1e-6f ? value : 1e-6f;

        // Texture coordinates for a material texture: each vertex projected along the axis its face mostly faces, in the
        // part's space, a tile every few studs.
        internal static List<float[]> BoxUvs(List<float[]> positions, List<float[]> normals, float[] scale, float[] offset)
        {
            float tile = RobloxContent.PartStudsPerTile;
            bool smooth = normals.Count == positions.Count;
            var uvs = new List<float[]>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
            {
                float[] v = positions[i], n = smooth ? normals[i] : new float[] { 0, 1, 0 };
                double nx = Math.Abs(n[0] / NonZero(scale[0])), ny = Math.Abs(n[1] / NonZero(scale[1])), nz = Math.Abs(n[2] / NonZero(scale[2]));
                float x = (v[0] * scale[0] + offset[0]) / tile, y = (v[1] * scale[1] + offset[1]) / tile, z = (v[2] * scale[2] + offset[2]) / tile;
                uvs.Add(nx >= ny && nx >= nz ? new[] { z, y } : ny >= nz ? new[] { x, z } : new[] { x, y });
            }
            return uvs;
        }

        // Parts showing their Roblox material's texture (wood, brick...): not Neon, and not meshes, which show their own.
        internal static bool Textured(PlacedPart part) =>
            part.Shape != PartShape.Mesh && part.Material != PlacedPart.NeonMaterial && RobloxContent.HasMaterialTexture(part.Material);

        // An image multiplied by a colour (Roblox's materials take their part's colour that way), as JPEG, or PNG when it keeps
        // see-through parts.
        internal static byte[] Tinted(byte[] image, float[] tint, int size, bool keepAlpha)
        {
            using (var stream = new MemoryStream(image))
            using (var source = new System.Drawing.Bitmap(stream))
            using (var output = new System.Drawing.Bitmap(size > 0 ? size : source.Width, size > 0 ? size : source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(output))
                using (var recolour = new System.Drawing.Imaging.ImageAttributes())
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    recolour.SetWrapMode(System.Drawing.Drawing2D.WrapMode.Tile);
                    recolour.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new[] { tint[0], 0, 0, 0, 0 }, new[] { 0, tint[1], 0, 0, 0 }, new[] { 0, 0, tint[2], 0, 0 }, new[] { 0, 0, 0, 1f, 0 }, new[] { 0, 0, 0, 0, 1f }
                    }));
                    graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, output.Width, output.Height), 0, 0, source.Width, source.Height,
                        System.Drawing.GraphicsUnit.Pixel, recolour);
                }
                using (var result = new MemoryStream())
                {
                    output.Save(result, keepAlpha ? System.Drawing.Imaging.ImageFormat.Png : System.Drawing.Imaging.ImageFormat.Jpeg);
                    return result.ToArray();
                }
            }
        }

        // A part's Decals and Textures (an image on one of its faces).
        internal static IEnumerable<RobloxInstance> Decals(PlacedPart part) =>
            part.Instance.Children.Where(c => (c.ClassName == "Decal" || c.ClassName == "Texture") && (c.Number("Transparency") ?? 0) < 0.999f);

        // NormalId: Right, Top, Back, Left, Bottom, Front. Each face's outward direction, and the image's right and downward
        // directions on it as seen from outside (a face's down × right is its normal).
        private static readonly float[][][] FaceAxes =
        {
            new[] { new float[] { 1, 0, 0 }, new float[] { 0, 0, -1 }, new float[] { 0, -1, 0 } },
            new[] { new float[] { 0, 1, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 0, 1 } },
            new[] { new float[] { 0, 0, 1 }, new float[] { 1, 0, 0 }, new float[] { 0, -1, 0 } },
            new[] { new float[] { -1, 0, 0 }, new float[] { 0, 0, 1 }, new float[] { 0, -1, 0 } },
            new[] { new float[] { 0, -1, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 0, -1 } },
            new[] { new float[] { 0, 0, -1 }, new float[] { -1, 0, 0 }, new float[] { 0, -1, 0 } }
        };

        // A Decal's or Texture's quad on its part's face, in the part's space (studs), lifted a hair off the face: a Decal's
        // image stretched over the face, a Texture's tiled every StudsPerTile (from its offset). Big faces are split into
        // cells up to maxCell studs, so per-vertex lighting has points to work with.
        internal static void DecalGeometry(PlacedPart part, RobloxInstance decal, float maxCell, out List<float[]> positions, out List<float[]> uvs,
            out List<int[]> faces, out float[] normal)
        {
            int face = decal.Enum("Face") ?? 5;
            float[][] axes = FaceAxes[Math.Max(0, Math.Min(5, face))];
            float[] n = axes[0], right = axes[1], down = axes[2];
            float Extent(float[] axis) => Math.Abs(axis[0]) * part.Size[0] + Math.Abs(axis[1]) * part.Size[1] + Math.Abs(axis[2]) * part.Size[2];
            float width = Extent(right), height = Extent(down), depth = Extent(n);
            const float lift = 0.05f;
            bool tiled = decal.ClassName == "Texture";
            float perU = tiled ? Math.Max(0.01f, decal.Number("StudsPerTileU") ?? 2) : width, perV = tiled ? Math.Max(0.01f, decal.Number("StudsPerTileV") ?? 2) : height;
            float offsetU = tiled ? decal.Number("OffsetStudsU") ?? 0 : 0, offsetV = tiled ? decal.Number("OffsetStudsV") ?? 0 : 0;
            int columns = Math.Max(1, Math.Min(64, (int)Math.Ceiling(width / maxCell))), rows = Math.Max(1, Math.Min(64, (int)Math.Ceiling(height / maxCell)));
            positions = new List<float[]>();
            uvs = new List<float[]>();
            faces = new List<int[]>();
            normal = n;
            for (int row = 0; row <= rows; row++)
                for (int column = 0; column <= columns; column++)
                {
                    // Across the face from its top left (as seen from outside), in studs.
                    float s = width * column / columns, t = height * row / rows;
                    positions.Add(Enumerable.Range(0, 3).Select(axis => n[axis] * (depth / 2 + lift) + right[axis] * (s - width / 2) + down[axis] * (t - height / 2)).ToArray());
                    // Texture coordinates from the bottom left, as models have them.
                    uvs.Add(new[] { (s + offsetU) / perU, 1 - (t + offsetV) / perV });
                }
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    int topLeft = row * (columns + 1) + column, topRight = topLeft + 1, bottomLeft = topLeft + columns + 1, bottomRight = bottomLeft + 1;
                    // Counter-clockwise seen from outside: top left, bottom left, bottom right, top right.
                    faces.Add(new[] { topLeft, bottomLeft, bottomRight });
                    faces.Add(new[] { topLeft, bottomRight, topRight });
                }
        }

        // A primitive's flat faces as convex polygons in the unit shape (-0.5..0.5 on every axis), each counter-clockwise seen
        // from outside. Spheres and cylinders are faceted with the given number of segments around; curved[i] marks their
        // rounded faces.
        internal static List<float[][]> Polygons(PartShape shape, int segments, out List<bool> curved)
        {
            var polygons = new List<float[][]>();
            var round = curved = new List<bool>();
            void Add(bool isCurved, params float[][] corners)
            {
                // Corners that meet (at a sphere's poles) are merged.
                var distinct = new List<float[]>();
                foreach (float[] corner in corners)
                    if (distinct.Count == 0 || !Same(distinct[distinct.Count - 1], corner))
                        distinct.Add(corner);
                if (distinct.Count > 1 && Same(distinct[0], distinct[distinct.Count - 1]))
                    distinct.RemoveAt(distinct.Count - 1);
                if (distinct.Count >= 3)
                {
                    polygons.Add(distinct.ToArray());
                    round.Add(isCurved);
                }
            }
            float[] V(float x, float y, float z) => new[] { x, y, z };
            const float h = 0.5f;
            switch (shape)
            {
                case PartShape.Wedge:
                    // Full height at the back (+Z), sloping down to the bottom front edge.
                    Add(false, V(-h, -h, -h), V(h, -h, -h), V(h, -h, h), V(-h, -h, h));  // bottom
                    Add(false, V(-h, -h, h), V(h, -h, h), V(h, h, h), V(-h, h, h));      // back
                    Add(false, V(h, -h, -h), V(-h, -h, -h), V(-h, h, h), V(h, h, h));    // slope
                    Add(false, V(h, -h, -h), V(h, h, h), V(h, -h, h));                   // +X side
                    Add(false, V(-h, -h, -h), V(-h, -h, h), V(-h, h, h));                // -X side
                    break;
                case PartShape.CornerWedge:
                    // A pyramid on the bottom face with its peak above the +X back corner.
                    float[] peak = V(h, h, h);
                    Add(false, V(-h, -h, -h), V(h, -h, -h), V(h, -h, h), V(-h, -h, h));  // bottom
                    Add(false, V(h, -h, h), V(h, -h, -h), peak);                         // +X
                    Add(false, V(-h, -h, h), V(h, -h, h), peak);                         // back
                    Add(false, V(h, -h, -h), V(-h, -h, -h), peak);                       // front slope
                    Add(false, V(-h, -h, -h), V(-h, -h, h), peak);                       // -X slope
                    break;
                case PartShape.Sphere:
                    // Rings from the top pole down, each split into segments around Y.
                    int rings = Math.Max(2, segments / 2);
                    float[] Point(int ring, int segment)
                    {
                        double theta = Math.PI * ring / rings, phi = 2 * Math.PI * segment / segments;
                        return V((float)(Math.Sin(theta) * Math.Cos(phi)) * h, (float)Math.Cos(theta) * h, (float)(Math.Sin(theta) * Math.Sin(phi)) * h);
                    }
                    for (int ring = 0; ring < rings; ring++)
                        for (int segment = 0; segment < segments; segment++)
                            Add(true, Point(ring, segment), Point(ring, segment + 1), Point(ring + 1, segment + 1), Point(ring + 1, segment));
                    break;
                case PartShape.CylinderX:
                case PartShape.CylinderY:
                    // Built along Y, then turned onto X for a cylinder Part (whose length is its X size).
                    var top = new List<float[]>();
                    var bottom = new List<float[]>();
                    for (int side = 0; side < segments; side++)
                    {
                        double a0 = 2 * Math.PI * side / segments, a1 = 2 * Math.PI * (side + 1) / segments;
                        float x0 = (float)Math.Cos(a0) * h, z0 = (float)Math.Sin(a0) * h, x1 = (float)Math.Cos(a1) * h, z1 = (float)Math.Sin(a1) * h;
                        Add(true, V(x0, -h, z0), V(x0, h, z0), V(x1, h, z1), V(x1, -h, z1));
                        top.Insert(0, V(x0, h, z0));
                        bottom.Add(V(x0, -h, z0));
                    }
                    Add(false, top.ToArray());
                    Add(false, bottom.ToArray());
                    if (shape == PartShape.CylinderX)
                        // Y becomes X: (x, y, z) -> (y, -x, z), a quarter turn about Z.
                        foreach (float[][] polygon in polygons)
                            for (int i = 0; i < polygon.Length; i++)
                                polygon[i] = V(polygon[i][1], -polygon[i][0], polygon[i][2]);
                    break;
                default:
                    Add(false, V(h, -h, h), V(h, -h, -h), V(h, h, -h), V(h, h, h));      // +X
                    Add(false, V(-h, -h, -h), V(-h, -h, h), V(-h, h, h), V(-h, h, -h));  // -X
                    Add(false, V(-h, h, h), V(h, h, h), V(h, h, -h), V(-h, h, -h));      // +Y
                    Add(false, V(-h, -h, -h), V(h, -h, -h), V(h, -h, h), V(-h, -h, h));  // -Y
                    Add(false, V(-h, -h, h), V(h, -h, h), V(h, h, h), V(-h, h, h));      // +Z
                    Add(false, V(h, -h, -h), V(-h, -h, -h), V(-h, h, -h), V(h, h, -h));  // -Z
                    break;
            }
            return polygons;
        }

        private static bool Same(float[] a, float[] b) => Math.Abs(a[0] - b[0]) < 1e-6f && Math.Abs(a[1] - b[1]) < 1e-6f && Math.Abs(a[2] - b[2]) < 1e-6f;

        // Unit shapes spanning -0.5..0.5 on every axis, with counter-clockwise triangles seen from outside: flat faces with
        // their own vertices, spheres and cylinders with smooth normals and the given number of segments around.
        internal static void BuildPrimitive(PartShape shape, out List<float[]> positions, out List<float[]> normals, out List<float[]> texcoords, out List<int[]> faces,
            int segments = 24)
        {
            var p = positions = new List<float[]>();
            var n = normals = new List<float[]>();
            var t = texcoords = new List<float[]>();
            var f = faces = new List<int[]>();
            // A flat polygon (counter-clockwise from outside) with its own vertices.
            void Flat(float[][] corners)
            {
                float[] a = corners[0], b = corners[1], c = corners[2];
                float[] u = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, v = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
                float[] normal = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
                float length = (float)Math.Sqrt(normal.Sum(x => x * x));
                int start = p.Count;
                for (int i = 0; i < corners.Length; i++)
                {
                    p.Add(corners[i]);
                    n.Add(normal.Select(x => x / length).ToArray());
                    t.Add(corners.Length == 4 ? new float[] { i == 1 || i == 2 ? 1 : 0, i >= 2 ? 1 : 0 } : new float[] { i == 1 ? 1 : 0, i == 2 ? 1 : 0 });
                }
                for (int i = 1; i + 1 < corners.Length; i++)
                    f.Add(new[] { start, start + i, start + i + 1 });
            }
            float[] V(float x, float y, float z) => new[] { x, y, z };
            const float h = 0.5f;
            switch (shape)
            {
                case PartShape.Sphere:
                    int rings = Math.Max(4, segments * 2 / 3);
                    for (int ring = 0; ring <= rings; ring++)
                    {
                        double theta = Math.PI * ring / rings;
                        for (int segment = 0; segment <= segments; segment++)
                        {
                            double phi = 2 * Math.PI * segment / segments;
                            float[] direction = { (float)(Math.Sin(theta) * Math.Cos(phi)), (float)Math.Cos(theta), (float)(Math.Sin(theta) * Math.Sin(phi)) };
                            p.Add(direction.Select(x => x * h).ToArray());
                            n.Add(direction);
                            t.Add(new[] { (float)segment / segments, (float)ring / rings });
                        }
                    }
                    for (int ring = 0; ring < rings; ring++)
                        for (int segment = 0; segment < segments; segment++)
                        {
                            int a = ring * (segments + 1) + segment, b = a + segments + 1;
                            f.Add(new[] { a, a + 1, b });
                            f.Add(new[] { a + 1, b + 1, b });
                        }
                    break;
                case PartShape.CylinderX:
                case PartShape.CylinderY:
                    // Built along Y, then turned onto X for a cylinder Part (whose length is its X size).
                    int sides = segments;
                    int start = p.Count;
                    for (int side = 0; side <= sides; side++)
                    {
                        double angle = 2 * Math.PI * side / sides;
                        float x = (float)Math.Cos(angle), z = (float)Math.Sin(angle);
                        p.Add(V(x * h, -h, z * h));
                        p.Add(V(x * h, h, z * h));
                        n.Add(V(x, 0, z));
                        n.Add(V(x, 0, z));
                        t.Add(new[] { (float)side / sides, 0f });
                        t.Add(new[] { (float)side / sides, 1f });
                    }
                    for (int side = 0; side < sides; side++)
                    {
                        int a = start + 2 * side;
                        f.Add(new[] { a, a + 1, a + 2 });
                        f.Add(new[] { a + 1, a + 3, a + 2 });
                    }
                    foreach (float[][] cap in Polygons(PartShape.CylinderY, sides, out List<bool> curved).Where((polygon, i) => !curved[i]))
                        Flat(cap);
                    if (shape == PartShape.CylinderX)
                    {
                        // Y becomes X: (x, y, z) -> (y, -x, z), a quarter turn about Z.
                        for (int i = 0; i < p.Count; i++)
                        {
                            p[i] = V(p[i][1], -p[i][0], p[i][2]);
                            n[i] = V(n[i][1], -n[i][0], n[i][2]);
                        }
                    }
                    break;
                default:
                    foreach (float[][] polygon in Polygons(shape, 0, out _))
                        Flat(polygon);
                    break;
            }
        }
    }
}
