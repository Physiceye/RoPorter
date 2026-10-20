using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rbx2Source.Export
{
    // Roblox smooth terrain (Terrain.SmoothGrid): 4-stud voxels, each with a material and how full it is. The grid is version 1
    // data in chunks (32 voxels a side): each chunk's position as an offset from the previous chunk (three big-endian 32-bit
    // integers with their bytes interleaved), then its voxels X fastest, then Z, then Y, as runs. A run is a byte with the
    // material in its low six bits, then the occupancy (when bit 6 is set; full otherwise) and the run's length less one (when
    // bit 7 is set; one voxel otherwise).
    public sealed class RobloxTerrain
    {
        public const float VoxelSize = 4;
        public const int Air = 0, Water = 1;
        public static readonly string[] MaterialNames =
        {
            "Air", "Water", "Grass", "Slate", "Concrete", "Brick", "Sand", "WoodPlanks", "Rock", "Glacier", "Snow", "Sandstone", "Mud", "Basalt",
            "Ground", "CrackedLava", "Asphalt", "Cobblestone", "Ice", "LeafyGrass", "Salt", "Limestone", "Pavement"
        };

        // Roblox's default terrain colours, for places without Terrain.MaterialColors.
        private static readonly byte[] DefaultColors =
        {
            0, 0, 0, 0, 0, 0, 111, 126, 62, 88, 89, 86, 152, 152, 152, 138, 97, 73, 207, 203, 167, 172, 148, 108, 99, 100, 102, 221, 228, 229,
            235, 253, 255, 148, 124, 95, 121, 112, 98, 75, 74, 74, 140, 130, 104, 255, 24, 67, 80, 84, 84, 134, 134, 118, 204, 210, 223,
            106, 134, 64, 255, 255, 254, 255, 243, 192, 143, 144, 135
        };

        public int ChunkSize { get; private set; }
        // Voxels by chunk position (in chunks): materials and occupancies (0-255), X fastest, then Z, then Y.
        public Dictionary<(int X, int Y, int Z), (byte[] Materials, byte[] Occupancy)> Chunks { get; } =
            new Dictionary<(int, int, int), (byte[], byte[])>();
        private byte[] colors = DefaultColors;
        public bool HasWater { get; private set; }

        // A terrain material's colour (0-1 RGB).
        public float[] Color(int material)
        {
            int i = Math.Max(0, Math.Min(material, colors.Length / 3 - 1));
            return new[] { colors[3 * i] / 255f, colors[3 * i + 1] / 255f, colors[3 * i + 2] / 255f };
        }

        public static string MaterialName(int material) => material >= 0 && material < MaterialNames.Length ? MaterialNames[material] : "Material" + material;


        public static RobloxTerrain Read(byte[] grid, byte[] materialColors)
        {
            if (grid == null || grid.Length < 2)
                throw new InvalidDataException("The terrain data is empty.");
            if (grid[0] != 1)
                throw new NotSupportedException("Terrain data version " + grid[0] + " is not supported.");
            int log = grid[1];
            if (log < 1 || log > 8)
                throw new InvalidDataException("The terrain data has an invalid chunk size.");
            var terrain = new RobloxTerrain { ChunkSize = 1 << log };
            if (materialColors != null && materialColors.Length >= DefaultColors.Length)
                terrain.colors = materialColors;
            int size = terrain.ChunkSize, perChunk = size * size * size;
            int position = 2, cx = 0, cy = 0, cz = 0;
            while (position < grid.Length)
            {
                if (position + 12 > grid.Length)
                    throw new InvalidDataException("The terrain data is truncated.");
                int Offset(int k) => (grid[position + k] << 24) | (grid[position + 3 + k] << 16) | (grid[position + 6 + k] << 8) | grid[position + 9 + k];
                cx += Offset(0);
                cy += Offset(1);
                cz += Offset(2);
                position += 12;
                var materials = new byte[perChunk];
                var occupancy = new byte[perChunk];
                int filled = 0;
                while (filled < perChunk)
                {
                    if (position >= grid.Length)
                        throw new InvalidDataException("The terrain data is truncated.");
                    int header = grid[position++];
                    int material = header & 0x3F;
                    int full = material == Air ? 0 : 255;
                    if ((header & 0x40) != 0)
                    {
                        if (position >= grid.Length)
                            throw new InvalidDataException("The terrain data is truncated.");
                        full = grid[position++];
                    }
                    int count = 1;
                    if ((header & 0x80) != 0)
                    {
                        if (position >= grid.Length)
                            throw new InvalidDataException("The terrain data is truncated.");
                        count = grid[position++] + 1;
                    }
                    if (filled + count > perChunk)
                        throw new InvalidDataException("The terrain data has an overlong run.");
                    if (material == Water)
                        terrain.HasWater = true;
                    for (int i = 0; i < count; i++, filled++)
                    {
                        materials[filled] = (byte)material;
                        occupancy[filled] = (byte)full;
                    }
                }
                terrain.Chunks[(cx, cy, cz)] = (materials, occupancy);
            }
            return terrain;
        }

        // The ground's top: for each 4-stud column, the top of its highest solid voxel (lower for one only partly full) and
        // that voxel's material. Water and air are not ground.
        public TerrainSurface Surface()
        {
            int size = ChunkSize;
            var tops = new Dictionary<(int X, int Z), (float Height, byte Material)>();
            foreach (var chunk in Chunks)
            {
                var (materials, occupancy) = chunk.Value;
                for (int y = 0; y < size; y++)
                    for (int z = 0; z < size; z++)
                        for (int x = 0; x < size; x++)
                        {
                            int i = x + size * (z + size * y);
                            byte material = materials[i];
                            if (material == Air || material == Water || occupancy[i] == 0)
                                continue;
                            int vx = chunk.Key.X * size + x, vy = chunk.Key.Y * size + y, vz = chunk.Key.Z * size + z;
                            float top = (vy + occupancy[i] / 255f) * VoxelSize;
                            if (!tops.TryGetValue((vx, vz), out var current) || top > current.Height)
                                tops[(vx, vz)] = (top, material);
                        }
            }
            if (tops.Count == 0)
                return null;
            int minX = tops.Keys.Min(k => k.X), maxX = tops.Keys.Max(k => k.X), minZ = tops.Keys.Min(k => k.Z), maxZ = tops.Keys.Max(k => k.Z);
            var surface = new TerrainSurface(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            foreach (var top in tops)
            {
                int index = (top.Key.X - minX) + surface.Width * (top.Key.Z - minZ);
                surface.Heights[index] = top.Value.Height;
                surface.Materials[index] = top.Value.Material;
            }
            return surface;
        }
    }

    // A heightfield of terrain ground: per 4-stud column (X across, Z down the grid), the ground's height in studs (NaN where
    // there is none) and its material.
    public sealed class TerrainSurface
    {
        public int MinX { get; }
        public int MinZ { get; }
        public int Width { get; }
        public int Depth { get; }
        public float[] Heights { get; }
        public byte[] Materials { get; }

        public TerrainSurface(int minX, int minZ, int width, int depth)
        {
            MinX = minX;
            MinZ = minZ;
            Width = width;
            Depth = depth;
            Heights = Enumerable.Repeat(float.NaN, width * depth).ToArray();
            Materials = new byte[width * depth];
        }

        public bool Has(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth && !float.IsNaN(Heights[x + Width * z]);

        // The height at a corner of the column grid (corner (x, z) is shared by columns x-1..x, z-1..z): the average of the
        // ground columns around it, which smooths the voxel steps as Roblox's terrain does.
        public float CornerHeight(int x, int z, out bool any)
        {
            float sum = 0;
            int count = 0;
            for (int dz = -1; dz <= 0; dz++)
                for (int dx = -1; dx <= 0; dx++)
                    if (Has(x + dx, z + dz))
                    {
                        sum += Heights[x + dx + Width * (z + dz)];
                        count++;
                    }
            any = count > 0;
            return count > 0 ? sum / count : 0;
        }

        // A corner's mix of materials around it.
        public IEnumerable<byte> CornerMaterials(int x, int z)
        {
            for (int dz = -1; dz <= 0; dz++)
                for (int dx = -1; dx <= 0; dx++)
                    if (Has(x + dx, z + dz))
                        yield return Materials[x + dx + Width * (z + dz)];
        }
    }
}
