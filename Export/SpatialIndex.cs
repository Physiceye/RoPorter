using System;
using System.Collections.Generic;
using System.Linq;

namespace Rbx2Source.Export
{
    // Uniform-grid nearest-neighbour search for avatar-sized point sets.
    internal sealed class SpatialIndex
    {
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        private readonly IReadOnlyList<double[]> points;
        private readonly double size;

        public SpatialIndex(IReadOnlyList<double[]> points, double size)
        {
            this.points = points;
            this.size = size;
            for (int i = 0; i < points.Count; i++)
            {
                long key = Key(Cell(points[i][0]), Cell(points[i][1]), Cell(points[i][2]));
                if (!cells.TryGetValue(key, out List<int> list))
                    cells[key] = list = new List<int>();
                list.Add(i);
            }
        }

        private int Cell(double value) => (int)Math.Floor(value / size);

        private static long Key(int x, int y, int z) =>
            ((long)(x & 0x1fffff) << 42) | ((long)(y & 0x1fffff) << 21) | (long)(z & 0x1fffff);

        public List<KeyValuePair<int, double>> Nearest(double x, double y, double z, int count, int maxRings = 64)
        {
            int cx = Cell(x), cy = Cell(y), cz = Cell(z);
            var best = new List<KeyValuePair<int, double>>();
            if (points.Count == 0)
                return best;
            for (int ring = 0; ring < maxRings; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                    for (int dy = -ring; dy <= ring; dy++)
                        for (int dz = -ring; dz <= ring; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring)
                                continue;
                            if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out List<int> list))
                                continue;
                            foreach (int index in list)
                            {
                                double[] p = points[index];
                                double ex = p[0] - x, ey = p[1] - y, ez = p[2] - z;
                                best.Add(new KeyValuePair<int, double>(index, Math.Sqrt(ex * ex + ey * ey + ez * ez)));
                            }
                        }
                if (best.Count >= count)
                {
                    best.Sort((a, b) => a.Value.CompareTo(b.Value));
                    // Points beyond the searched shell could still be closer than the current k-th match.
                    if (best[count - 1].Value <= ring * size)
                        return best.Take(count).ToList();
                }
            }
            best.Sort((a, b) => a.Value.CompareTo(b.Value));
            return best.Take(count).ToList();
        }
    }
}
