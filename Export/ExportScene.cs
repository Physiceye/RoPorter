using System;
using System.Collections.Generic;
using System.Linq;

namespace Rbx2Source.Export
{
    public sealed class FaceShape
    {
        public string Name;

        // Per OBJ position index, the morph delta in Roblox render space (studs).
        public Dictionary<int, float[]> Deltas = new Dictionary<int, float[]>();

        // Initial shape key value written to the model (0 = off, 1 = fully applied).
        public float Weight;
    }

    public sealed class FaceShapes
    {
        public string HeadGroup;
        // Face makeup layers: other copies of the head whose vertices carry its shapes too.
        public HashSet<string> Layers { get; } = new HashSet<string>(StringComparer.Ordinal);
        public List<FaceShape> Shapes { get; } = new List<FaceShape>();

        // The head and its makeup layers make up the face mesh, the one with the shape keys.
        public bool IsFace(string group) => group == HeadGroup || Layers.Contains(group);

        // Eye and jaw positions from the head mesh's facial bones, in Roblox render space.
        public Dictionary<int, double[]> FaceBones { get; } = new Dictionary<int, double[]>();
    }

    // Converts Roblox render space (Y up, avatar facing -Z, studs) into glTF space (Y up, facing +Z, metres), optionally scaled
    // down (Scale 1 is full size).
    public sealed class ExportScene
    {
        public const float MetresPerStud = 0.28f;

        public WavefrontModel Model { get; }
        public AvatarRig Rig { get; }
        public FaceShapes Face { get; }
        public float[] Origin { get; }
        public float Scale { get; }
        private float Units => MetresPerStud * Scale;

        public ExportScene(WavefrontModel model, AvatarRig rig = null, FaceShapes face = null, float scale = 1)
        {
            if (!(scale > 0) || float.IsInfinity(scale))
                throw new ArgumentOutOfRangeException(nameof(scale), "The export scale must be positive.");
            Scale = scale;
            Model = model ?? throw new ArgumentNullException(nameof(model));
            Rig = rig;
            Face = face != null && face.Shapes.Count > 0 ? face : null;

            var used = new HashSet<int>(model.Triangles.SelectMany(triangle => triangle.Corners).Select(corner => corner.Position));
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            foreach (int index in used)
            {
                float[] p = model.Positions[index];
                minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]);
                minZ = Math.Min(minZ, p[2]); maxZ = Math.Max(maxZ, p[2]);
            }
            if (rig != null)
                Origin = new[] { (float)rig.Bones[0].Position[0], minY, (float)rig.Bones[0].Position[2] };
            else
                Origin = new[] { (minX + maxX) / 2, minY, (minZ + maxZ) / 2 };
        }

        public float[] Point(float[] p)
        {
            return new[]
            {
                -(p[0] - Origin[0]) * Units,
                (p[1] - Origin[1]) * Units,
                -(p[2] - Origin[2]) * Units
            };
        }

        public float[] Point(double[] p)
        {
            return Point(new[] { (float)p[0], (float)p[1], (float)p[2] });
        }

        public static float[] Direction(float[] n)
        {
            return new[] { -n[0], n[1], -n[2] };
        }

        // Shape key delta (studs) in this scene's units.
        public float[] Delta(float[] d)
        {
            return new[] { -d[0] * Units, d[1] * Units, -d[2] * Units };
        }

        // Full-size delta, as Delta with Scale 1.
        public static float[] Offset(float[] d)
        {
            return new[] { -d[0] * MetresPerStud, d[1] * MetresPerStud, -d[2] * MetresPerStud };
        }
    }
}
