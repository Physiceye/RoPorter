using System;
using System.Collections.Generic;
using System.Linq;

namespace Rbx2Source.Export
{
    // Carries a layered-clothing item's own skin weights (authored on the R15 skeleton) over to its rendered geometry.
    // Roblox's wrap deformer moves the item's vertices onto the body but keeps its triangles and UVs, so rendered
    // vertices are matched to the item's mesh by triangle order or, failing that, by UV.
    public static class SkinTransfer
    {
        private const double UvTolerance = 2e-3;
        public const double MinimumCoverage = 0.9;

        // Maps each rendered position of the group to a vertex of the mesh, or returns null when they do not match.
        public static Dictionary<int, int> Match(WavefrontModel model, IList<ObjTriangle> triangles, RobloxMesh mesh)
        {
            if (mesh == null || triangles.Count == 0 || triangles.Any(t => t.Corners.Any(c => c.Texture < 0)))
                return null;
            int positions = triangles.SelectMany(t => t.Corners).Select(c => c.Position).Distinct().Count();
            Dictionary<int, int> map = MatchInOrder(model, triangles, mesh, mesh.Texcoords);
            if (map != null && map.Count >= MinimumCoverage * positions)
                return map;
            map = MatchByUv(model, triangles, mesh, mesh.Texcoords);
            return map.Count >= MinimumCoverage * positions ? map : null;
        }

        private static bool SameUv(float[] a, float[] b) =>
            Math.Abs(a[0] - b[0]) <= UvTolerance && Math.Abs(a[1] - b[1]) <= UvTolerance;

        private static Dictionary<int, int> MatchInOrder(WavefrontModel model, IList<ObjTriangle> triangles, RobloxMesh mesh, IList<float[]> uvs)
        {
            if (triangles.Count != mesh.Faces.Count)
                return null;
            var map = new Dictionary<int, int>();
            int matched = 0;
            for (int i = 0; i < triangles.Count; i++)
            {
                ObjCorner[] corners = triangles[i].Corners;
                int[] face = mesh.Faces[i];
                int[] order = null;
                foreach (int[] candidate in new[] { new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 0, 2, 1 }, new[] { 2, 1, 0 }, new[] { 1, 0, 2 } })
                    if (Enumerable.Range(0, 3).All(k => SameUv(model.Texcoords[corners[k].Texture], uvs[face[candidate[k]]])))
                    {
                        order = candidate;
                        break;
                    }
                if (order == null)
                    continue;
                matched++;
                for (int k = 0; k < 3; k++)
                    if (!map.ContainsKey(corners[k].Position))
                        map[corners[k].Position] = face[order[k]];
            }
            return matched >= MinimumCoverage * triangles.Count ? map : null;
        }

        private static Dictionary<int, int> MatchByUv(WavefrontModel model, IList<ObjTriangle> triangles, RobloxMesh mesh, IList<float[]> uvs)
        {
            var used = mesh.Faces.SelectMany(face => face).Distinct().ToList();
            var cells = new Dictionary<long, List<int>>();
            const double Cell = 0.01;
            long Key(double u, double v) => ((long)Math.Floor(u / Cell) << 32) ^ (uint)(int)Math.Floor(v / Cell);
            foreach (int vertex in used)
            {
                float[] t = uvs[vertex];
                long key = Key(t[0], t[1]);
                if (!cells.TryGetValue(key, out List<int> list))
                    cells[key] = list = new List<int>();
                list.Add(vertex);
            }
            // Left and right halves (a pair of socks or shoes) often share mirrored UV islands, and their front faces point the
            // same way, so the normal alone can pick the wrong side, giving a vertex the other leg's weights. The side of
            // the item's centre the vertex is on (character right is +X in the mesh and in the render) settles it.
            double meshMin = used.Min(v => mesh.Positions[v][0]), meshMax = used.Max(v => mesh.Positions[v][0]);
            var rendered = triangles.SelectMany(t => t.Corners).Select(c => c.Position).Distinct().ToList();
            double renderMin = rendered.Min(p => model.Positions[p][0]), renderMax = rendered.Max(p => model.Positions[p][0]);
            double meshCenter = (meshMin + meshMax) / 2, renderCenter = (renderMin + renderMax) / 2;
            double meshMargin = 0.05 * (meshMax - meshMin), renderMargin = 0.05 * (renderMax - renderMin);
            int Side(double x, double center, double margin) => x > center + margin ? 1 : x < center - margin ? -1 : 0;

            var map = new Dictionary<int, int>();
            foreach (ObjCorner corner in triangles.SelectMany(t => t.Corners))
            {
                if (map.ContainsKey(corner.Position))
                    continue;
                float[] uv = model.Texcoords[corner.Texture];
                float[] normal = corner.Normal >= 0 ? model.Normals[corner.Normal] : null;
                int side = Side(model.Positions[corner.Position][0], renderCenter, renderMargin);
                int best = -1;
                double bestScore = double.MaxValue;
                int cu = (int)Math.Floor(uv[0] / Cell), cv = (int)Math.Floor(uv[1] / Cell);
                for (int du = -1; du <= 1; du++)
                    for (int dv = -1; dv <= 1; dv++)
                    {
                        if (!cells.TryGetValue(((long)(cu + du) << 32) ^ (uint)(cv + dv), out List<int> list))
                            continue;
                        foreach (int vertex in list)
                        {
                            float[] t = uvs[vertex];
                            if (!SameUv(uv, t))
                                continue;
                            // Mirrored UV islands share coordinates; the closer normal picks the right side.
                            float[] n = mesh.Normals[vertex];
                            int meshSide = Side(mesh.Positions[vertex][0], meshCenter, meshMargin);
                            double score = Math.Abs(uv[0] - t[0]) + Math.Abs(uv[1] - t[1]) +
                                (normal != null ? 0.01 * (1 - (normal[0] * n[0] + normal[1] * n[1] + normal[2] * n[2])) : 0) +
                                (side != 0 && meshSide != 0 && side != meshSide ? 1 : 0);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = vertex;
                            }
                        }
                    }
                if (best >= 0)
                    map[corner.Position] = best;
            }
            return map;
        }

        // Rig bone for a mesh bone: R15 part names map to their humanoid bone; other bones use their nearest R15 ancestor.
        public static int RigBone(RobloxMesh mesh, int meshBone)
        {
            for (int bone = meshBone, guard = 0; bone >= 0 && bone < mesh.Bones.Count && guard < 256; bone = mesh.Bones[bone].Parent, guard++)
                if (Enum.TryParse(mesh.Bones[bone].Name, false, out BodyPart part) && Enum.IsDefined(typeof(BodyPart), part))
                    return AvatarRig.BoneFor(part);
            return AvatarRig.Hips;
        }

        public static Dictionary<int, double> Influence(RobloxMesh mesh, int vertex)
        {
            var influence = new Dictionary<int, double>();
            int[] bones = mesh.SkinBones[vertex];
            float[] weights = mesh.SkinWeights[vertex];
            if (bones == null)
                return influence;
            for (int k = 0; k < bones.Length; k++)
            {
                if (weights[k] <= 0)
                    continue;
                int bone = RigBone(mesh, bones[k]);
                influence[bone] = (influence.TryGetValue(bone, out double existing) ? existing : 0) + weights[k];
            }
            return influence;
        }
    }
}
