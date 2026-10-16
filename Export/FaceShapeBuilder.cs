using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Rbx2Source.Export
{
    // Converts Roblox FACS bone poses into vertex shape keys and transfers them onto the rendered avatar head.
    public static class FaceShapeBuilder
    {
        // VRChat visemes (Oculus Lipsync's 15 mouth shapes, after its reference images) and blinks, as Roblox FACS
        // control weights. A control ending in "?" is optional: the shape is still made without it, but every other
        // control must exist. LipsTogether only closes the lips while the jaw is dropped (alone it turns them inside
        // out), so it is always paired with an equal JawDrop.
        private static readonly KeyValuePair<string, Dictionary<string, float>>[] Composites =
        {
            Composite("vrc.v_sil"),
            // p, b, m: lips pressed shut.
            Composite("vrc.v_pp", "JawDrop", 0.12f, "LipsTogether", 0.12f, "LipPresser?", 0.6f, "UpperLipSuck?", 0.15f, "LowerLipSuck?", 0.15f,
                "Pucker?", 0.15f),
            // f, v: lower lip tucked under the upper teeth.
            Composite("vrc.v_ff", "JawDrop", 0.1f, "LowerLipSuck", 0.7f, "LeftUpperLipRaiser?", 0.4f, "RightUpperLipRaiser?", 0.4f),
            // th: tongue between slightly parted teeth.
            Composite("vrc.v_th", "JawDrop", 0.22f, "TongueOut?", 0.9f, "LeftUpperLipRaiser?", 0.15f, "RightUpperLipRaiser?", 0.15f),
            // t, d: teeth nearly closed, lips parted, tongue up.
            Composite("vrc.v_dd", "JawDrop", 0.18f, "LeftUpperLipRaiser", 0.3f, "RightUpperLipRaiser", 0.3f,
                "LeftLowerLipDepressor", 0.3f, "RightLowerLipDepressor", 0.3f, "TongueUp?", 0.5f),
            // k, g: jaw a little open, lips relaxed and wide.
            Composite("vrc.v_kk", "JawDrop", 0.32f, "LeftLipStretcher", 0.35f, "RightLipStretcher", 0.35f,
                "LeftUpperLipRaiser?", 0.1f, "RightUpperLipRaiser?", 0.1f),
            // ch, j, sh: lips pushed forward and flared, teeth together.
            Composite("vrc.v_ch", "JawDrop", 0.1f, "Funneler", 0.6f, "Pucker?", 0.2f, "LeftUpperLipRaiser", 0.35f, "RightUpperLipRaiser", 0.35f,
                "LeftLowerLipDepressor", 0.35f, "RightLowerLipDepressor", 0.35f),
            // s, z: teeth together, lips spread.
            Composite("vrc.v_ss", "JawDrop", 0.05f, "LeftLipStretcher", 0.55f, "RightLipStretcher", 0.55f,
                "LeftUpperLipRaiser", 0.25f, "RightUpperLipRaiser", 0.25f, "LeftLowerLipDepressor", 0.25f, "RightLowerLipDepressor", 0.25f),
            // n, l: jaw slightly open, tongue up behind the teeth.
            Composite("vrc.v_nn", "JawDrop", 0.2f, "LeftLipStretcher", 0.2f, "RightLipStretcher", 0.2f, "TongueUp?", 0.7f,
                "LeftUpperLipRaiser?", 0.1f, "RightUpperLipRaiser?", 0.1f),
            // r: lips rounded and slightly parted.
            Composite("vrc.v_rr", "JawDrop", 0.12f, "Funneler", 0.45f, "Pucker", 0.45f),
            // a (car): jaw wide open.
            Composite("vrc.v_aa", "JawDrop", 0.75f, "LeftUpperLipRaiser?", 0.1f, "RightUpperLipRaiser?", 0.1f,
                "LeftLowerLipDepressor?", 0.2f, "RightLowerLipDepressor?", 0.2f),
            // e (bed): mid-open, wide.
            Composite("vrc.v_e", "JawDrop", 0.4f, "LeftLipStretcher", 0.45f, "RightLipStretcher", 0.45f,
                "LeftUpperLipRaiser?", 0.2f, "RightUpperLipRaiser?", 0.2f),
            // i (tip): slightly open, wide with raised corners.
            Composite("vrc.v_ih", "JawDrop", 0.22f, "LeftLipStretcher", 0.4f, "RightLipStretcher", 0.4f,
                "LeftLipCornerPuller", 0.35f, "RightLipCornerPuller", 0.35f, "LeftUpperLipRaiser?", 0.15f, "RightUpperLipRaiser?", 0.15f),
            // o (toe): rounded and open.
            Composite("vrc.v_oh", "JawDrop", 0.35f, "Funneler", 0.8f, "Pucker", 0.45f),
            // u (book): small, tight rounding.
            Composite("vrc.v_ou", "JawDrop", 0.08f, "Pucker", 1f, "Funneler", 0.45f, "FlatPucker?", 0.5f),
            Composite("vrc.blink", "LeftEyeClosed", 1f, "RightEyeClosed", 1f),
            Composite("vrc.blink_l", "LeftEyeClosed", 1f),
            Composite("vrc.blink_r", "RightEyeClosed", 1f),
            // VRChat's eyelid "Looking Up" / "Looking Down" blendshapes, blended in as the eyes turn. The rig's eye
            // bones don't deform the eyes, so these carry Roblox's full look pose (eyes and lids together).
            Composite("vrc.lookingup", "EyesLookUp", 1f),
            Composite("vrc.lookingdown", "EyesLookDown", 1f)
        };

        private static KeyValuePair<string, Dictionary<string, float>> Composite(string name, params object[] weights)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < weights.Length; i += 2)
                map[(string)weights[i]] = (float)weights[i + 1];
            return new KeyValuePair<string, Dictionary<string, float>>(name, map);
        }

        // Affine transform: row-major 3x3 rotation followed by translation.
        private struct Frame
        {
            public double[] R;
            public double[] T;

            public static Frame Identity => new Frame { R = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = new double[3] };

            public static Frame operator *(Frame a, Frame b)
            {
                var r = new double[9];
                var t = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                        r[i * 3 + j] = a.R[i * 3] * b.R[j] + a.R[i * 3 + 1] * b.R[3 + j] + a.R[i * 3 + 2] * b.R[6 + j];
                    t[i] = a.R[i * 3] * b.T[0] + a.R[i * 3 + 1] * b.T[1] + a.R[i * 3 + 2] * b.T[2] + a.T[i];
                }
                return new Frame { R = r, T = t };
            }

            public Frame Inverse()
            {
                double[] m = R;
                double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
                if (Math.Abs(det) < 1e-12)
                    throw new InvalidDataException("The head mesh has a degenerate bone transform.");
                var inv = new[]
                {
                    (m[4] * m[8] - m[5] * m[7]) / det, (m[2] * m[7] - m[1] * m[8]) / det, (m[1] * m[5] - m[2] * m[4]) / det,
                    (m[5] * m[6] - m[3] * m[8]) / det, (m[0] * m[8] - m[2] * m[6]) / det, (m[2] * m[3] - m[0] * m[5]) / det,
                    (m[3] * m[7] - m[4] * m[6]) / det, (m[1] * m[6] - m[0] * m[7]) / det, (m[0] * m[4] - m[1] * m[3]) / det
                };
                var t = new double[3];
                for (int i = 0; i < 3; i++)
                    t[i] = -(inv[i * 3] * T[0] + inv[i * 3 + 1] * T[1] + inv[i * 3 + 2] * T[2]);
                return new Frame { R = inv, T = t };
            }

            public double[] Apply(float[] p)
            {
                return new[]
                {
                    R[0] * p[0] + R[1] * p[1] + R[2] * p[2] + T[0],
                    R[3] * p[0] + R[4] * p[1] + R[5] * p[2] + T[1],
                    R[6] * p[0] + R[7] * p[1] + R[8] * p[2] + T[2]
                };
            }

            // Matches CFrame.fromEulerAnglesXYZ: Rx * Ry * Rz.
            public static Frame FromEulerXyz(double x, double y, double z, double[] translation)
            {
                double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
                var rx = new Frame { R = new[] { 1, 0, 0, 0, cx, -sx, 0, sx, cx }, T = new double[3] };
                var ry = new Frame { R = new[] { cy, 0, sy, 0, 1, 0, -sy, 0, cy }, T = new double[3] };
                var rz = new Frame { R = new[] { cz, -sz, 0, sz, cz, 0, 0, 0, 1 }, T = new double[3] };
                Frame result = rx * ry * rz;
                result.T = translation;
                return result;
            }
        }

        // Unified Expressions (VRCFaceTracking's shape standard) from Roblox FaceControls. Roblox and Unified
        // Expressions both name sides from the character's point of view. Side 0 = whole face, -1 = character's
        // left only, +1 = right only (used to split Roblox's two-eye controls into per-eye shapes).
        // https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/unified-blendshapes
        private static readonly (string Name, string Control, int Side)[] UnifiedExpressions =
        {
            ("EyeLookUpLeft", "EyesLookUp", -1), ("EyeLookUpRight", "EyesLookUp", 1),
            ("EyeLookDownLeft", "EyesLookDown", -1), ("EyeLookDownRight", "EyesLookDown", 1),
            ("EyeLookOutLeft", "EyesLookLeft", -1), ("EyeLookInRight", "EyesLookLeft", 1),
            ("EyeLookInLeft", "EyesLookRight", -1), ("EyeLookOutRight", "EyesLookRight", 1),
            ("EyeClosedLeft", "LeftEyeClosed", 0), ("EyeClosedRight", "RightEyeClosed", 0),
            ("EyeWideLeft", "LeftEyeUpperLidRaiser", 0), ("EyeWideRight", "RightEyeUpperLidRaiser", 0),
            ("BrowPinchLeft", "Corrugator", -1), ("BrowPinchRight", "Corrugator", 1),
            ("BrowLowererLeft", "LeftBrowLowerer", 0), ("BrowLowererRight", "RightBrowLowerer", 0),
            ("BrowInnerUpLeft", "LeftInnerBrowRaiser", 0), ("BrowInnerUpRight", "RightInnerBrowRaiser", 0),
            ("BrowOuterUpLeft", "LeftOuterBrowRaiser", 0), ("BrowOuterUpRight", "RightOuterBrowRaiser", 0),
            ("NoseSneerLeft", "LeftNoseWrinkler", 0), ("NoseSneerRight", "RightNoseWrinkler", 0),
            ("CheekSquintLeft", "LeftCheekRaiser", 0), ("CheekSquintRight", "RightCheekRaiser", 0),
            ("CheekPuffLeft", "LeftCheekPuff", 0), ("CheekPuffRight", "RightCheekPuff", 0),
            ("JawOpen", "JawDrop", 0), ("JawLeft", "JawLeft", 0), ("JawRight", "JawRight", 0),
            ("MouthClosed", "LipsTogether", 0),
            ("LipSuckUpper", "UpperLipSuck", 0), ("LipSuckLower", "LowerLipSuck", 0),
            ("LipFunnel", "Funneler", 0), ("LipPucker", "Pucker", 0),
            ("MouthUpperUpLeft", "LeftUpperLipRaiser", 0), ("MouthUpperUpRight", "RightUpperLipRaiser", 0),
            ("MouthLowerDownLeft", "LeftLowerLipDepressor", 0), ("MouthLowerDownRight", "RightLowerLipDepressor", 0),
            ("MouthSmileLeft", "LeftLipCornerPuller", 0), ("MouthSmileRight", "RightLipCornerPuller", 0),
            ("MouthFrownLeft", "LeftLipCornerDown", 0), ("MouthFrownRight", "RightLipCornerDown", 0),
            ("MouthStretchLeft", "LeftLipStretcher", 0), ("MouthStretchRight", "RightLipStretcher", 0),
            ("MouthDimpleLeft", "LeftDimpler", 0), ("MouthDimpleRight", "RightDimpler", 0),
            ("MouthRaiserUpper", "ChinRaiserUpperLip", 0), ("MouthRaiserLower", "ChinRaiser", 0),
            ("MouthPress", "LipPresser", 0), ("MouthLeft", "MouthLeft", 0), ("MouthRight", "MouthRight", 0),
            ("TongueOut", "TongueOut", 0), ("TongueUp", "TongueUp", 0), ("TongueDown", "TongueDown", 0)
        };

        public static IEnumerable<string> UnifiedExpressionNames => UnifiedExpressions.Select(entry => entry.Name);

        private sealed class ShapeRequest
        {
            public string Name;
            public Dictionary<string, float> Weights;
            public int Side;
        }

        // Which side of the face each vertex is on: from its main bone's name (L_eye, LeftCheek...) when it has one,
        // otherwise from its position. Roblox meshes face -Z, so the character's left is -X.
        private static int[] VertexSides(RobloxMesh mesh)
        {
            int NameSide(string name)
            {
                name = name ?? "";
                if (name.StartsWith("L_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Left", StringComparison.Ordinal))
                    return -1;
                if (name.StartsWith("R_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Right", StringComparison.Ordinal))
                    return 1;
                return 0;
            }
            MeshBone centreBone = mesh.Bones.FirstOrDefault(bone => bone.Name == "DynamicHead");
            var used = mesh.Faces.SelectMany(face => face).Distinct().ToList();
            double centre = centreBone != null ? centreBone.Position[0]
                : (used.Min(i => mesh.Positions[i][0]) + used.Max(i => mesh.Positions[i][0])) / 2;
            var sides = new int[mesh.Positions.Count];
            for (int v = 0; v < sides.Length; v++)
            {
                int[] bones = mesh.SkinBones[v];
                int side = 0;
                if (bones != null && bones.Length > 0)
                    side = NameSide(mesh.Bones[bones[Array.IndexOf(mesh.SkinWeights[v], mesh.SkinWeights[v].Max())]].Name);
                sides[v] = side != 0 ? side : mesh.Positions[v][0] < centre ? -1 : 1;
            }
            return sides;
        }

        // Returns mesh-space vertex deltas for each named FACS control and each VRChat composite.
        public static List<KeyValuePair<string, float[][]>> EvaluateShapes(RobloxMesh mesh, CancellationToken cancellation = default)
        {
            return EvaluateShapes(mesh, false, cancellation);
        }

        // With faceTracking, Unified Expressions shapes for VRCFaceTracking are added as well.
        public static List<KeyValuePair<string, float[][]>> EvaluateShapes(RobloxMesh mesh, bool faceTracking,
            CancellationToken cancellation = default)
        {
            return EvaluateShapes(mesh, faceTracking, true, cancellation);
        }

        // Without vrchat, the VRChat composites (vrc.* visemes, blinks, and eyelid look shapes) are left out.
        public static List<KeyValuePair<string, float[][]>> EvaluateShapes(RobloxMesh mesh, bool faceTracking, bool vrchat,
            CancellationToken cancellation = default)
        {
            return EvaluateShapes(mesh, faceTracking, vrchat, null, cancellation);
        }

        // With a mood (FACS control values from the avatar's Mood animation), its expression is added first as "Default".
        public static List<KeyValuePair<string, float[][]>> EvaluateShapes(RobloxMesh mesh, bool faceTracking, bool vrchat,
            IReadOnlyDictionary<string, float> mood, CancellationToken cancellation = default)
        {
            if (mesh.Facs == null)
                throw new InvalidDataException("The head mesh has no FACS data.");
            if (mesh.SkinBones == null || mesh.Bones.Count == 0)
                throw new InvalidDataException("The head mesh has FACS data but no skeleton.");
            FacsData facs = mesh.Facs;
            var faceBone = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < facs.BoneNames.Count; i++)
                faceBone[facs.BoneNames[i]] = i;
            if (!mesh.Bones.Any(bone => faceBone.ContainsKey(bone.Name ?? "")))
                throw new InvalidDataException("None of the FACS bones exist in the head mesh skeleton.");

            var rest = mesh.Bones.Select(bone => new Frame { R = (double[])bone.Rotation.Clone(), T = (double[])bone.Position.Clone() }).ToArray();
            var restInverse = rest.Select(frame => frame.Inverse()).ToArray();
            var local = new Frame[rest.Length];
            for (int i = 0; i < rest.Length; i++)
                local[i] = mesh.Bones[i].Parent >= 0 ? restInverse[mesh.Bones[i].Parent] * rest[i] : rest[i];

            var results = new List<KeyValuePair<string, float[][]>>();
            var requests = facs.ControlNames.Select(name => new ShapeRequest
            {
                Name = name, Weights = new Dictionary<string, float>(StringComparer.Ordinal) { [name] = 1f }
            }).ToList();
            var controls = new HashSet<string>(facs.ControlNames, StringComparer.Ordinal);
            foreach (var composite in vrchat ? Composites : Enumerable.Empty<KeyValuePair<string, Dictionary<string, float>>>())
            {
                if (!composite.Value.Keys.Where(key => !key.EndsWith("?", StringComparison.Ordinal)).All(controls.Contains))
                    continue;
                var weights = composite.Value.Select(pair => new KeyValuePair<string, float>(pair.Key.TrimEnd('?'), pair.Value))
                    .Where(pair => controls.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                requests.Add(new ShapeRequest { Name = composite.Key, Weights = weights });
            }
            if (!requests.Skip(facs.ControlNames.Count).Any(request => request.Weights.Count > 0))
                requests.RemoveAll(request => request.Name == "vrc.v_sil");
            if (faceTracking)
            {
                // Several Unified Expressions names (JawLeft, TongueOut, ...) equal the Roblox control names already added.
                var names = new HashSet<string>(requests.Select(request => request.Name), StringComparer.Ordinal);
                foreach (var entry in UnifiedExpressions)
                    if (controls.Contains(entry.Control) && names.Add(entry.Name))
                        requests.Add(new ShapeRequest
                        {
                            Name = entry.Name, Side = entry.Side,
                            Weights = new Dictionary<string, float>(StringComparer.Ordinal) { [entry.Control] = 1f }
                        });
            }
            if (mood != null)
            {
                var weights = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var pair in mood)
                {
                    string control = RobloxMesh.ControlName(pair.Key);
                    if (controls.Contains(control) && pair.Value != 0)
                        weights[control] = pair.Value;
                }
                if (weights.Count > 0)
                    requests.Insert(0, new ShapeRequest { Name = MoodShapeName, Weights = weights });
            }
            int[] sides = requests.Any(request => request.Side != 0) ? VertexSides(mesh) : null;

            foreach (var request in requests)
            {
                cancellation.ThrowIfCancellationRequested();
                var columnWeights = new double[facs.ColumnCount];
                for (int c = 0; c < facs.ControlNames.Count; c++)
                    columnWeights[c] = request.Weights.TryGetValue(facs.ControlNames[c], out float weight) ? weight : 0;
                for (int k = 0; k < facs.Correctives.Count; k++)
                    columnWeights[facs.ControlNames.Count + k] = facs.Correctives[k].Aggregate(1.0, (product, index) => product * columnWeights[index]);

                var posed = new Frame?[rest.Length];
                Frame Pose(int bone)
                {
                    if (posed[bone].HasValue)
                        return posed[bone].Value;
                    Frame transform = Frame.Identity;
                    if (faceBone.TryGetValue(mesh.Bones[bone].Name ?? "", out int row))
                    {
                        var sums = new double[6];
                        for (int channel = 0; channel < 6; channel++)
                            for (int column = 0; column < columnWeights.Length; column++)
                                if (columnWeights[column] != 0)
                                    sums[channel] += facs.Channels[channel][row][column] * columnWeights[column];
                        const double degrees = Math.PI / 180;
                        transform = Frame.FromEulerXyz(sums[3] * degrees, sums[4] * degrees, sums[5] * degrees,
                            new[] { sums[0], sums[1], sums[2] });
                    }
                    int parent = mesh.Bones[bone].Parent;
                    Frame result = (parent >= 0 ? Pose(parent) * local[bone] : rest[bone]) * transform;
                    posed[bone] = result;
                    return result;
                }
                var skin = new Frame[rest.Length];
                for (int i = 0; i < rest.Length; i++)
                    skin[i] = Pose(i) * restInverse[i];

                var deltas = new float[mesh.Positions.Count][];
                for (int v = 0; v < mesh.Positions.Count; v++)
                {
                    float[] p = mesh.Positions[v];
                    int[] bones = mesh.SkinBones[v];
                    var moved = new double[3];
                    if (bones == null || bones.Length == 0 || (request.Side != 0 && sides[v] != request.Side))
                    {
                        deltas[v] = new float[3];
                        continue;
                    }
                    for (int j = 0; j < bones.Length; j++)
                    {
                        double[] q = skin[bones[j]].Apply(p);
                        for (int axis = 0; axis < 3; axis++)
                            moved[axis] += q[axis] * mesh.SkinWeights[v][j];
                    }
                    deltas[v] = new[] { (float)(moved[0] - p[0]), (float)(moved[1] - p[1]), (float)(moved[2] - p[2]) };
                }
                results.Add(new KeyValuePair<string, float[][]>(request.Name, deltas));
            }
            return results;
        }

        // Quantised FACS matrices cannot store exact zeros; ignore movement below about 0.05 mm.
        private const float MinimumDelta = 2e-4f;

        public sealed class HeadFit
        {
            public double[] Scale, Offset;
            public double Flip, Error, HeadHeight;

            public float[] Point(float[] p) => new[]
            {
                (float)(Flip * p[0] * Scale[0] + Offset[0]), (float)(p[1] * Scale[1] + Offset[1]), (float)(Flip * p[2] * Scale[2] + Offset[2])
            };

            public float[] Delta(float[] d) => new[] { (float)(Flip * d[0] * Scale[0]), (float)(d[1] * Scale[1]), (float)(Flip * d[2] * Scale[2]) };

            public float[] Normal(float[] n)
            {
                var v = new[] { Flip * n[0] / Scale[0], n[1] / Scale[1], Flip * n[2] / Scale[2] };
                double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                return length > 1e-12 ? v.Select(x => (float)(x / length)).ToArray() : new float[] { 0, 1, 0 };
            }
        }

        private static List<int> UsedVertices(RobloxMesh mesh) => mesh.Faces.SelectMany(face => face).Distinct().ToList();

        // Fits the head mesh onto the rendered head with a per-axis scale and translation. Inlier trimming keeps
        // the fit stable when Roblox renders the head with a mood expression already applied.
        public static HeadFit Fit(RobloxMesh mesh, WavefrontModel model, string headGroup, CancellationToken cancellation = default)
        {
            var targets = model.Triangles.Where(triangle => triangle.Group == headGroup)
                .SelectMany(triangle => triangle.Corners).Select(corner => corner.Position).Distinct().ToList();
            var sources = UsedVertices(mesh);
            if (targets.Count == 0 || sources.Count == 0)
                throw new InvalidDataException("The rendered head could not be matched to the head mesh.");

            double[] Bounds(IEnumerable<double[]> points)
            {
                var b = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
                foreach (double[] p in points)
                    for (int axis = 0; axis < 3; axis++)
                    {
                        b[axis] = Math.Min(b[axis], p[axis]);
                        b[axis + 3] = Math.Max(b[axis + 3], p[axis]);
                    }
                return b;
            }
            var target = targets.Select(index => model.Positions[index].Select(value => (double)value).ToArray()).ToList();
            double[] targetBounds = Bounds(target);
            double headHeight = Math.Max(targetBounds[4] - targetBounds[1], 1e-6);
            var sample = target.Where((point, index) => index % Math.Max(1, target.Count / 3000) == 0).ToList();

            HeadFit best = null;
            foreach (double flip in new[] { 1.0, -1.0 })
            {
                // Rendered heads face -Z like mesh space; also try a half turn for meshes authored the other way round.
                var source = sources.Select(index => new double[] { flip * mesh.Positions[index][0], mesh.Positions[index][1], flip * mesh.Positions[index][2] }).ToList();
                double[] sourceBounds = Bounds(source);
                var scale = new double[3];
                var offset = new double[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    double size = sourceBounds[axis + 3] - sourceBounds[axis];
                    scale[axis] = size > 1e-9 ? (targetBounds[axis + 3] - targetBounds[axis]) / size : 1;
                    offset[axis] = (targetBounds[axis] + targetBounds[axis + 3]) / 2 - scale[axis] * (sourceBounds[axis] + sourceBounds[axis + 3]) / 2;
                }
                double error = double.MaxValue;
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var placed = source.Select(p => new[] { p[0] * scale[0] + offset[0], p[1] * scale[1] + offset[1], p[2] * scale[2] + offset[2] }).ToList();
                    var index = new SpatialIndex(placed, headHeight / 40);
                    var pairs = sample.Select(p => new { Target = p, Match = index.Nearest(p[0], p[1], p[2], 1, 24) })
                        .Where(pair => pair.Match.Count > 0).ToList();
                    if (pairs.Count == 0)
                        break;
                    var distances = pairs.Select(pair => pair.Match[0].Value).OrderBy(d => d).ToList();
                    error = distances[distances.Count / 2];
                    double cutoff = Math.Max(3 * error, headHeight * 0.005);
                    var inliers = pairs.Where(pair => pair.Match[0].Value <= cutoff).ToList();
                    if (inliers.Count < 8)
                        break;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double sx = 0, sy = 0, sxx = 0, sxy = 0;
                        foreach (var pair in inliers)
                        {
                            double x = source[pair.Match[0].Key][axis], y = pair.Target[axis];
                            sx += x; sy += y; sxx += x * x; sxy += x * y;
                        }
                        double n = inliers.Count, denominator = n * sxx - sx * sx;
                        if (Math.Abs(denominator) > 1e-12)
                        {
                            double s = (n * sxy - sx * sy) / denominator;
                            if (s > 0)
                            {
                                scale[axis] = s;
                                offset[axis] = (sy - s * sx) / n;
                            }
                        }
                    }
                }
                if (best == null || error < best.Error)
                    best = new HeadFit { Scale = scale, Offset = offset, Flip = flip, Error = error, HeadHeight = headHeight };
            }
            if (best == null || best.Error > 0.03 * headHeight)
                throw new InvalidDataException("The rendered head does not match the dynamic head mesh closely enough to add face shapes.");
            return best;
        }

        // Eye and jaw bone positions from the head mesh's facial skeleton, fitted onto the rendered head.
        private static void AddFaceBones(RobloxMesh mesh, HeadFit fit, FaceShapes result)
        {
            double[] Locate(string exact, string prefix)
            {
                var bones = mesh.Bones.Where(bone => string.Equals(bone.Name, exact, StringComparison.OrdinalIgnoreCase)).ToList();
                if (bones.Count == 0 && prefix != null)
                    bones = mesh.Bones.Where(bone => (bone.Name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (bones.Count == 0)
                    return null;
                var mean = new float[3];
                foreach (MeshBone bone in bones)
                    for (int axis = 0; axis < 3; axis++)
                        mean[axis] += (float)(bone.Position[axis] / bones.Count);
                float[] point = fit.Point(mean);
                return new double[] { point[0], point[1], point[2] };
            }
            var found = new Dictionary<int, double[]>
            {
                [AvatarRig.LeftEye] = Locate("LeftEyeRoot", "L_eye"),
                [AvatarRig.RightEye] = Locate("RightEyeRoot", "R_eye"),
                [AvatarRig.Jaw] = Locate("jaw", null)
            };
            foreach (var pair in found)
                if (pair.Value != null)
                    result.FaceBones[pair.Key] = pair.Value;
        }
        // Replaces the rendered head with the head mesh in its neutral pose so shape keys apply exactly.
        public static FaceShapes ReplaceHead(RobloxMesh mesh, IReadOnlyList<KeyValuePair<string, float[][]>> shapes, HeadFit fit,
            WavefrontModel model, AvatarRig rig, string headGroup, string materialName, string textureKey)
        {
            ObjMaterial rendered = model.Triangles.Where(t => t.Group == headGroup).Select(t => model.Materials[t.Material]).FirstOrDefault();
            model.Materials[materialName] = new ObjMaterial
            {
                Name = materialName, Texture = textureKey,
                Diffuse = rendered != null ? (float[])rendered.Diffuse.Clone() : new float[] { 1, 1, 1 }
            };
            model.Triangles.RemoveAll(t => t.Group == headGroup);

            var remap = new Dictionary<int, int>();
            var normals = new Dictionary<int, int>();
            var texcoords = new Dictionary<int, int>();
            foreach (int vertex in UsedVertices(mesh))
            {
                remap[vertex] = model.Positions.Count;
                normals[vertex] = model.Normals.Count;
                texcoords[vertex] = model.Texcoords.Count;
                model.Positions.Add(fit.Point(mesh.Positions[vertex]));
                model.Normals.Add(fit.Normal(mesh.Normals[vertex]));
                model.Texcoords.Add((float[])mesh.Texcoords[vertex].Clone());
            }

            // Keep the mesh's winding consistent with its stored normals after the transform.
            int agree = 0;
            foreach (int[] face in mesh.Faces)
            {
                float[] a = model.Positions[remap[face[0]]], b = model.Positions[remap[face[1]]], c = model.Positions[remap[face[2]]];
                double nx = (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]);
                double ny = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
                double nz = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
                float[] n = model.Normals[normals[face[0]]];
                agree += nx * n[0] + ny * n[1] + nz * n[2] >= 0 ? 1 : -1;
            }
            foreach (int[] face in mesh.Faces)
            {
                int[] order = agree >= 0 ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };
                model.Triangles.Add(new ObjTriangle
                {
                    Group = headGroup, Material = materialName,
                    Corners = order.Select(k => new ObjCorner { Position = remap[face[k]], Normal = normals[face[k]], Texture = texcoords[face[k]] }).ToArray()
                });
            }
            rig.AppendRigid(model.Positions.Count, AvatarRig.HeadBone);

            var result = new FaceShapes { HeadGroup = headGroup };
            AddFaceBones(mesh, fit, result);
            foreach (var shape in shapes)
            {
                var face = new FaceShape { Name = shape.Key, Weight = shape.Key == MoodShapeName ? 1 : 0 };
                foreach (var pair in remap)
                {
                    float[] moved = fit.Delta(shape.Value[pair.Key]);
                    if (Math.Abs(moved[0]) + Math.Abs(moved[1]) + Math.Abs(moved[2]) > MinimumDelta)
                        face.Deltas[pair.Value] = moved;
                }
                if (face.Deltas.Count > 0 || shape.Key == "vrc.v_sil")
                    result.Shapes.Add(face);
            }
            return result;
        }

        public const string MoodShapeName = "Default";

        // Fallback when the head texture is unavailable: copy deltas onto the rendered head by nearest vertex.
        public static FaceShapes Transfer(RobloxMesh mesh, IReadOnlyList<KeyValuePair<string, float[][]>> shapes,
            WavefrontModel model, string headGroup, List<string> notes, CancellationToken cancellation = default)
        {
            HeadFit fit = Fit(mesh, model, headGroup, cancellation);
            var targets = model.Triangles.Where(triangle => triangle.Group == headGroup)
                .SelectMany(triangle => triangle.Corners).Select(corner => corner.Position).Distinct().ToList();
            var sources = UsedVertices(mesh);
            var placed = sources.Select(index => fit.Point(mesh.Positions[index]).Select(v => (double)v).ToArray()).ToList();
            var finalIndex = new SpatialIndex(placed, fit.HeadHeight / 40);
            double tolerance = Math.Max(4 * fit.Error, 0.02 * fit.HeadHeight);
            var matches = new Dictionary<int, int>();
            foreach (int target in targets)
            {
                float[] p = model.Positions[target];
                var match = finalIndex.Nearest(p[0], p[1], p[2], 1, 24);
                if (match.Count > 0 && match[0].Value <= tolerance)
                    matches[target] = sources[match[0].Key];
            }
            if (matches.Count < 0.6 * targets.Count)
                throw new InvalidDataException("Only " + (100 * matches.Count / targets.Count) +
                    "% of the rendered head matched the dynamic head mesh; face shapes were not added.");
            if (matches.Count < targets.Count)
                notes.Add((targets.Count - matches.Count) + " rendered head vertices had no matching head-mesh vertex and will not move.");

            var result = new FaceShapes { HeadGroup = headGroup };
            AddFaceBones(mesh, fit, result);
            foreach (var shape in shapes)
            {
                var face = new FaceShape { Name = shape.Key, Weight = shape.Key == MoodShapeName ? 1 : 0 };
                foreach (var match in matches)
                {
                    float[] moved = fit.Delta(shape.Value[match.Value]);
                    if (Math.Abs(moved[0]) + Math.Abs(moved[1]) + Math.Abs(moved[2]) > MinimumDelta)
                        face.Deltas[match.Key] = moved;
                }
                if (face.Deltas.Count > 0 || shape.Key == "vrc.v_sil")
                    result.Shapes.Add(face);
            }
            return result;
        }
    }
}