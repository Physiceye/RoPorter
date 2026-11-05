using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Rbx2Source.Export
{
    // Another game's character skeleton (Tower Unite's mannequin, s&box's Citizen), read from that game's own files, and the
    // avatar fitted onto it: the limbs posed like the game's reference pose, then every bone with the game's orientation at
    // the avatar's joint, so the game's animations move the avatar as they intend (as the Garry's Mod export does with
    // Valve's skeleton). Positions and rotations here are in the export scene's frame (Y up, facing +Z, metres).
    public sealed class TargetSkeleton
    {
        public sealed class Bone
        {
            public string Name;
            public int Parent = -1;
            public double[] Position;
            // World rotation, 3x3 row-major; its columns are the bone's axes.
            public double[] Rotation;
        }

        public List<Bone> Bones { get; } = new List<Bone>();
        // Rig bone (AvatarRig index) -> this skeleton's bone name.
        public Dictionary<int, string> Map { get; } = new Dictionary<int, string>();

        public Bone Find(string name) => Bones.FirstOrDefault(b => b.Name == name);
        public int IndexOf(string name) => Bones.FindIndex(b => b.Name == name);

        // A file's own frame to the scene's: row-major 3x3, then metres per file unit.
        internal static TargetSkeleton FromWorld(IEnumerable<(string Name, int Parent, double[] Position, double[] Rotation)> bones, double[] toScene, double metres)
        {
            var skeleton = new TargetSkeleton();
            foreach (var b in bones)
                skeleton.Bones.Add(new Bone
                {
                    Name = b.Name,
                    Parent = b.Parent,
                    Position = Matrix.Scale(Matrix.Apply(toScene, b.Position), metres),
                    Rotation = Matrix.Multiply(toScene, b.Rotation)
                });
            return skeleton;
        }

        // The bind pose of a binary FBX file's bones.
        public static TargetSkeleton FromFbx(byte[] fbx, double[] toScene, double metres) =>
            FromWorld(FbxReader.ReadSkeleton(fbx).Select(b => (b.Name, b.Parent, new[] { b.World[12], b.World[13], b.World[14] },
                new[] { b.World[0], b.World[4], b.World[8], b.World[1], b.World[5], b.World[9], b.World[2], b.World[6], b.World[10] })), toScene, metres);

        // The joints of a COLLADA file's skeleton (its "transform" matrices, row-major with the translation in the last column).
        public static TargetSkeleton FromCollada(string path, double[] toScene, double metres)
        {
            XDocument document = XDocument.Load(path);
            XNamespace ns = document.Root.Name.Namespace;
            var bones = new List<(string, int, double[], double[])>();
            var world = new List<double[]>();
            void Visit(XElement node, int parent)
            {
                int index = parent;
                if ((string)node.Attribute("type") == "JOINT")
                {
                    double[] m = ((string)node.Element(ns + "matrix")).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    double[] parentWorld = parent >= 0 ? world[parent] : new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                    double[] w = new double[16];
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            for (int k = 0; k < 4; k++)
                                w[r * 4 + c] += parentWorld[r * 4 + k] * m[k * 4 + c];
                    index = world.Count;
                    world.Add(w);
                    bones.Add(((string)node.Attribute("name") ?? (string)node.Attribute("id"), parent, new[] { w[3], w[7], w[11] },
                        new[] { w[0], w[1], w[2], w[4], w[5], w[6], w[8], w[9], w[10] }));
                }
                foreach (XElement child in node.Elements(ns + "node"))
                    Visit(child, index);
            }
            foreach (XElement node in document.Descendants(ns + "visual_scene").Elements(ns + "node"))
                Visit(node, -1);
            if (bones.Count == 0)
                throw new InvalidDataException(Path.GetFileName(path) + " has no skeleton.");
            return FromWorld(bones, toScene, metres);
        }

        // The rig bones that name a side's limb segments, and the fingers in AvatarRig's order.
        private static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        private static readonly string[] Phalanges = { "Proximal", "Intermediate", "Distal" };

        private static int Rig(string name) => Array.IndexOf(AvatarRig.BoneNames, name);
        private static BodyPart Part(string name) => (BodyPart)Enum.Parse(typeof(BodyPart), name);

        // name(side, part) gives this skeleton's bone for a side ("Left" / "Right") and a part ("UpperArm", "Thumb0", ...),
        // or null; spine gives the trunk's.
        public void MapHumanoid(Func<string, string> trunk, Func<string, string, string> limb)
        {
            foreach (string part in new[] { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" })
            {
                string name = trunk(part);
                if (name != null && Find(name) != null)
                    Map[Rig(part)] = name;
            }
            foreach (string side in new[] { "Left", "Right" })
            {
                var parts = new[] { "Shoulder", "UpperArm", "LowerArm", "Hand", "UpperLeg", "LowerLeg", "Foot", "Toes", "Eye" }.ToList();
                for (int f = 0; f < Fingers.Length; f++)
                    for (int p = 0; p < Phalanges.Length; p++)
                        parts.Add(Fingers[f] + Phalanges[p]);
                foreach (string part in parts)
                {
                    string name = limb(side, part);
                    int rig = Rig(side + part);
                    if (name != null && rig >= 0 && Find(name) != null)
                        Map[rig] = name;
                }
            }
        }

        // Rotates the avatar's arms and legs to point where this skeleton's do, so its animations put them where they intend.
        public void Pose(WavefrontModel model, AvatarRig rig)
        {
            double[] Direction(string from, string to)
            {
                Bone a = Find(from), b = Find(to);
                if (a == null || b == null)
                    return null;
                double[] d = Matrix.Subtract(b.Position, a.Position);
                // Scene (facing +Z) to Roblox render space (facing -Z).
                return new[] { -d[0], d[1], -d[2] };
            }
            var segments = new List<KeyValuePair<int[], double[]>>();
            foreach (string side in new[] { "Left", "Right" })
                foreach (var pair in new[] { ("UpperArm", "LowerArm"), ("LowerArm", "Hand"), ("UpperLeg", "LowerLeg"), ("LowerLeg", "Foot"), ("UpperArm", "Hand"), ("UpperLeg", "Foot") })
                {
                    // An R6 limb can't bend, so it runs straight from one end of the hero's limb to the other.
                    bool arm = pair.Item1.EndsWith("Arm", StringComparison.Ordinal), straight = pair.Item2 == (arm ? "Hand" : "Foot") && pair.Item1.StartsWith("Upper", StringComparison.Ordinal);
                    if (rig.IsWholeLimb(Part(side + (arm ? "LowerArm" : "LowerLeg"))) != straight)
                        continue;
                    int a = Rig(side + pair.Item1), b = Rig(side + pair.Item2);
                    if (Map.TryGetValue(a, out string from) && Map.TryGetValue(b, out string to))
                    {
                        double[] direction = Direction(from, to);
                        if (direction != null)
                            segments.Add(new KeyValuePair<int[], double[]>(new[] { a, b }, direction));
                    }
                }
            rig.AlignSegments(model, segments);
        }

        // The scale that puts the avatar's head where this skeleton's is, for a scene at full size.
        public double FitScale(ExportScene fullSize)
        {
            Bone head = Find(Map[AvatarRig.HeadBone]);
            float[] ours = fullSize.Point(fullSize.Rig.Bones[AvatarRig.HeadBone].Position);
            return ours[1] > 1e-6 && head.Position[1] > 1e-6 ? head.Position[1] / ours[1] : 1;
        }

        // The skeleton for the avatar: this one's bones and orientations, at the avatar's joints. Bones with no rig bone (twist
        // and helper bones, IK targets) keep their place relative to the joints around them: along the limb towards the next
        // joint, or offset from the nearest joint above them scaled with the avatar.
        public List<Bone> Fit(ExportScene scene)
        {
            var ours = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var pair in Map)
                ours[pair.Value] = scene.Point(scene.Rig.Bones[pair.Key].Position).Select(v => (double)v).ToArray();
            double theirsHeight = Find(Map[AvatarRig.HeadBone]).Position[1], oursHeight = ours[Map[AvatarRig.HeadBone]][1];
            double scale = theirsHeight > 1e-6 ? oursHeight / theirsHeight : 1;

            int MappedAncestor(int bone)
            {
                for (int p = Bones[bone].Parent; p >= 0; p = Bones[p].Parent)
                    if (ours.ContainsKey(Bones[p].Name))
                        return p;
                return -1;
            }
            var fitted = new List<Bone>();
            for (int i = 0; i < Bones.Count; i++)
            {
                Bone theirs = Bones[i];
                double[] position;
                if (ours.TryGetValue(theirs.Name, out double[] joint))
                    position = joint;
                else
                {
                    int anchor = MappedAncestor(i);
                    if (anchor < 0)
                        position = Matrix.Scale(theirs.Position, scale);
                    else
                    {
                        double[] origin = Bones[anchor].Position, offset = Matrix.Subtract(theirs.Position, origin);
                        // The joint below the anchor that this bone lies towards (a twist bone sits along its limb).
                        var next = Enumerable.Range(0, Bones.Count).Where(j => MappedAncestor(j) == anchor && ours.ContainsKey(Bones[j].Name))
                            .Select(j => (Index: j, Axis: Matrix.Subtract(Bones[j].Position, origin)))
                            .Where(c => Matrix.Length(c.Axis) > 1e-6 && Matrix.Length(offset) > 1e-6 &&
                                Matrix.Dot(c.Axis, offset) / (Matrix.Length(c.Axis) * Matrix.Length(offset)) > 0.9)
                            .OrderByDescending(c => Matrix.Dot(c.Axis, offset) / Matrix.Length(c.Axis)).FirstOrDefault();
                        double[] start = ours[Bones[anchor].Name];
                        if (next.Axis != null)
                        {
                            double length = Matrix.Length(next.Axis), t = Matrix.Dot(offset, next.Axis) / (length * length);
                            double[] end = ours[Bones[next.Index].Name];
                            double[] along = Matrix.Add(start, Matrix.Scale(Matrix.Subtract(end, start), t));
                            double[] across = Matrix.Subtract(offset, Matrix.Scale(next.Axis, t));
                            position = Matrix.Add(along, Matrix.Scale(across, scale));
                        }
                        else
                            position = Matrix.Add(start, Matrix.Scale(offset, scale));
                    }
                }
                fitted.Add(new Bone { Name = theirs.Name, Parent = theirs.Parent, Position = position, Rotation = theirs.Rotation });
            }
            return fitted;
        }

        private double TopOfHead() => Find("head_end")?.Position[1] ?? Find(Map[AvatarRig.HeadBone]).Position[1] * 1.1;

        // The scale that makes the avatar as tall as this skeleton, measured to the top of the head so that a big Roblox head
        // and tall hats don't add to it, for a scene at full size.
        public double FitHeadTop(ExportScene fullSize)
        {
            string group = fullSize.Rig.HeadGroup;
            float ours = fullSize.Model.Triangles.Where(t => t.Group == group).SelectMany(t => t.Corners)
                .Select(corner => fullSize.Point(fullSize.Model.Positions[corner.Position])[1]).DefaultIfEmpty(0).Max();
            double theirs = TopOfHead();
            return ours > 1e-6 && theirs > 1e-6 ? theirs / ours : 1;
        }

        // Leans the avatar's whole body, unbent, as this skeleton's trunk leans in its bind pose.
        public void PoseTrunk(WavefrontModel model, AvatarRig rig)
        {
            double[] lean = Matrix.Subtract(Find(Map[AvatarRig.HeadBone]).Position, Find(Map[AvatarRig.Hips]).Position);
            rig.AlignSegments(model, new[] { new KeyValuePair<int[], double[]>(new[] { AvatarRig.Hips, AvatarRig.HeadBone }, new[] { -lean[0], lean[1], -lean[2] }) });
        }

        // Puts the avatar on this skeleton, for a game whose animations carry the bones' positions. Connected, each piece moves
        // onto its own bone's joints (see ConnectMoves), so the animations hinge the limbs where they hold them; otherwise the
        // avatar moves as one piece over this skeleton's hips with its feet on the ground, and nothing stretches. The skeleton
        // keeps its own joints. The scene returned keeps this one's origin.
        public ExportScene FitMesh(ExportScene scene, bool connect = false)
        {
            WavefrontModel model = scene.Model;
            AvatarRig rig = scene.Rig;
            int[] boneOf = RigToBone();
            Func<double[], double[]>[] moves = connect ? ConnectMoves(scene, boneOf) : null;
            float[] hips = scene.Point(rig.Bones[AvatarRig.Hips].Position);
            double[] target = Find(Map[AvatarRig.Hips]).Position;
            double[] shift = { target[0] - hips[0], 0, target[2] - hips[2] };
            float units = ExportScene.MetresPerStud * scene.Scale;
            double[] Back(double[] p) => new[] { -p[0] / units + scene.Origin[0], p[1] / units + scene.Origin[1], -p[2] / units + scene.Origin[2] };
            // The maps of one limb agree where its pieces meet, but a limb's and the trunk's pull far apart further down the
            // limb, so averaging them smears a vertex between the two (layered sleeves carry weights on both the torso and the
            // arm). A vertex follows the chain holding most of its weight, blending only within it; chains part where their maps
            // agree, at the shoulders and hips, so the seam barely shows.
            int[] chains = connect ? LimbChains() : null;
            for (int i = 0; i < model.Positions.Count; i++)
            {
                double[] point = scene.Point(model.Positions[i]).Select(v => (double)v).ToArray(), moved = new double[3];
                if (moves == null)
                    moved = Matrix.Add(point, shift);
                else
                {
                    var held = new double[5];
                    for (int k = 0; k < 4; k++)
                        if (rig.Weights[i][k] > 0)
                            held[chains[rig.Joints[i][k]]] += rig.Weights[i][k];
                    int best = Array.IndexOf(held, held.Max());
                    double total = held[best];
                    for (int k = 0; k < 4; k++)
                        if (rig.Weights[i][k] > 0 && chains[rig.Joints[i][k]] == best)
                            moved = Matrix.Add(moved, Matrix.Scale(moves[rig.Joints[i][k]](point), rig.Weights[i][k] / total));
                }
                moved = Back(moved);
                model.Positions[i] = new[] { (float)moved[0], (float)moved[1], (float)moved[2] };
            }
            for (int i = 0; i < rig.Bones.Count; i++)
                rig.Bones[i].Position = Back(Bones[boneOf[i]].Position);
            return new ExportScene(model, rig, null, scene.Scale, scene.Origin);
        }

        // Where each rig bone's surface goes when the avatar is connected to this skeleton's joints, so that its parts pivot where
        // they join however far this skeleton's proportions are from Roblox's, while the character still looks like a Roblox
        // avatar. One even scale fits the trunk between this skeleton's hips and shoulders, so the arms attach where the hero's
        // do; the head keeps its Roblox proportion to the torso and may stand above this skeleton's head. Each limb segment runs
        // from one of this skeleton's joints to the next and thickens as it lengthens (within bounds, so legs don't fuse), which
        // keeps each piece blocky; a limb stops short of a joint deep in the body by up to most of its own thickness, which keeps
        // the joint inside the limb. Feet scale evenly to meet the ground.
        private Func<double[], double[]>[] ConnectMoves(ExportScene scene, int[] boneOf)
        {
            AvatarRig rig = scene.Rig;
            double[] Ours(int bone) => scene.Point(rig.Bones[bone].Position).Select(v => (double)v).ToArray();
            double[] Theirs(int bone) => Bones[boneOf[bone]].Position;
            double[] hips = Ours(AvatarRig.Hips), theirHips = Theirs(AvatarRig.Hips);
            double[] Mid(double[] a, double[] b) => Matrix.Scale(Matrix.Add(a, b), 0.5);
            double[] shoulders = Mid(Ours(Rig("LeftUpperArm")), Ours(Rig("RightUpperArm")));
            double[] theirShoulders = Mid(Theirs(Rig("LeftUpperArm")), Theirs(Rig("RightUpperArm")));
            double trunkSpan = Matrix.Length(Matrix.Subtract(shoulders, hips));
            double fit = trunkSpan > 1e-6 ? Matrix.Length(Matrix.Subtract(theirShoulders, theirHips)) / trunkSpan : 1;
            // A piece's thickness follows how much it lengthens, held in bounds: thighs an inch apart would fuse unbounded.
            double Blocky(double stretch) => Math.Min(Math.Max(stretch, 0.5), 1.5);
            Func<double[], double[]> trunk = p => Matrix.Add(theirHips, Matrix.Scale(Matrix.Subtract(p, hips), fit));
            var moves = new Func<double[], double[]>[AvatarRig.BoneNames.Length];

            List<double[]> Surface(BodyPart part)
            {
                var groups = new HashSet<string>(rig.Parts.Where(pair => pair.Value == part).Select(pair => pair.Key), StringComparer.Ordinal);
                return scene.Model.Triangles.Where(t => groups.Contains(t.Group)).SelectMany(t => t.Corners).Select(c => c.Position).Distinct()
                    .Select(i => scene.Point(scene.Model.Positions[i]).Select(v => (double)v).ToArray()).ToList();
            }
            // The widest a part reaches from the line through its joints.
            double Radius(List<double[]> surface, double[] from, double[] axis) => surface
                .Select(p => Matrix.Subtract(p, from)).Select(d => Matrix.Length(Matrix.Subtract(d, Matrix.Scale(axis, Matrix.Dot(d, axis))))).DefaultIfEmpty(0).Max();
            // The lowest point of everything on these bones, shoes included.
            double Lowest(params int[] bones) => Enumerable.Range(0, scene.Model.Positions.Count).Where(i => rig.Weights[i][0] > 0.5f && bones.Contains(rig.Joints[i][0]))
                .Select(i => (double)scene.Point(scene.Model.Positions[i])[1]).DefaultIfEmpty(0).Min();
            // Maps a segment onto another, keeping the piece blocky: its length becomes the new segment's, and across the line
            // and past its ends it scales by the piece's thickness scale.
            Func<double[], double[]> Segment(double[] from, double[] axis, double reach, double[] start, double[] end, double thick)
            {
                double[] span = Matrix.Subtract(end, start);
                double length = Matrix.Length(span);
                double[] along = Matrix.Scale(span, 1 / length);
                return p =>
                {
                    double[] d = Matrix.Subtract(p, from);
                    double t = Matrix.Dot(d, axis);
                    double placed = t <= 0 ? t * thick : t >= reach ? length + (t - reach) * thick : t * length / reach;
                    return Matrix.Add(Matrix.Add(start, Matrix.Scale(along, placed)), Matrix.Scale(Matrix.Subtract(d, Matrix.Scale(axis, t)), thick));
                };
            }

            foreach (string side in new[] { "Left", "Right" })
                foreach (bool arm in new[] { true, false })
                {
                    int upper = Rig(side + (arm ? "UpperArm" : "UpperLeg")), lower = Rig(side + (arm ? "LowerArm" : "LowerLeg")), end = Rig(side + (arm ? "Hand" : "Foot"));
                    BodyPart middle = Part(side + (arm ? "LowerArm" : "LowerLeg"));
                    bool whole = rig.IsWholeLimb(middle);
                    double[] top = Ours(upper), bottom = Ours(end), axis = Matrix.Scale(Matrix.Subtract(bottom, top), 1 / Matrix.Length(Matrix.Subtract(bottom, top)));
                    List<double[]> surface = Surface(whole ? middle : Part(side + (arm ? "UpperArm" : "UpperLeg")));
                    double Stretch(int a, int b, double ours) => ours > 1e-6 ? Matrix.Length(Matrix.Subtract(Theirs(b), Theirs(a))) / ours : 1;
                    double thickUpper = Blocky(Stretch(upper, whole ? end : lower, Matrix.Length(Matrix.Subtract((whole ? bottom : Ours(lower)), top))));
                    double keep = 0.75 * thickUpper * Radius(surface, top, axis);
                    double[] outside = Matrix.Subtract(trunk(top), Theirs(upper));
                    double apart = Matrix.Length(outside);
                    if (apart > keep)
                        outside = Matrix.Scale(outside, keep / apart);
                    double[] Joint(int bone) => Matrix.Add(Theirs(bone), outside);
                    if (whole)
                    {
                        double reach = Matrix.Length(Matrix.Subtract(bottom, top));
                        double[] start = Joint(upper), target = Joint(end);
                        if (!arm && surface.Count > 0)
                        {
                            // A leg block runs on past the ankle, to the ground.
                            reach = surface.Select(p => Matrix.Dot(Matrix.Subtract(p, top), axis)).Max();
                            double[] down = Matrix.Subtract(target, start);
                            double corner = thickUpper * surface.Select(p => Matrix.Subtract(p, top)).Select(d => d[1] - axis[1] * Matrix.Dot(d, axis)).Min();
                            target = Matrix.Add(start, Matrix.Scale(down, (start[1] + corner) / -down[1]));
                        }
                        moves[upper] = moves[lower] = moves[end] = Segment(top, axis, reach, start, target, thickUpper);
                        continue;
                    }
                    double[] knee = Ours(lower);
                    double thickLower = Blocky(Stretch(lower, end, Matrix.Length(Matrix.Subtract(bottom, knee))));
                    double[] upperAxis = Matrix.Scale(Matrix.Subtract(knee, top), 1 / Matrix.Length(Matrix.Subtract(knee, top)));
                    double[] lowerAxis = Matrix.Scale(Matrix.Subtract(bottom, knee), 1 / Matrix.Length(Matrix.Subtract(bottom, knee)));
                    moves[upper] = Segment(top, upperAxis, Matrix.Length(Matrix.Subtract(knee, top)), Joint(upper), Joint(lower), thickUpper);
                    moves[lower] = Segment(knee, lowerAxis, Matrix.Length(Matrix.Subtract(bottom, knee)), Joint(lower), Joint(end), thickLower);
                    double[] wrist = Joint(end);
                    double sole = arm ? 0 : Lowest(lower, end);
                    double size = arm || bottom[1] - sole < 1e-6 ? thickLower : Math.Max(0.4, wrist[1] / (bottom[1] - sole));
                    moves[end] = p => Matrix.Add(wrist, Matrix.Scale(Matrix.Subtract(p, bottom), size));
                }

            int[] parents = AvatarRig.ParentIndices;
            for (int i = 0; i < moves.Length; i++)
                if (moves[i] == null)
                {
                    int bone = i;
                    while (bone >= 0 && moves[bone] == null)
                        bone = parents[bone];
                    moves[i] = bone >= 0 ? moves[bone] : trunk;
                }
            return moves;
        }

        // Rig bone -> which chain its surface moves with when connected: 0 for the trunk and head, 1-4 for each arm and leg
        // (fingers and toes with their limb).
        private static int[] LimbChains()
        {
            var chains = new int[AvatarRig.BoneNames.Length];
            int[] parents = AvatarRig.ParentIndices;
            int[] roots = { Rig("LeftUpperArm"), Rig("RightUpperArm"), Rig("LeftUpperLeg"), Rig("RightUpperLeg") };
            for (int i = 0; i < chains.Length; i++)
                for (int bone = i; bone >= 0 && chains[i] == 0; bone = parents[bone])
                    chains[i] = Array.IndexOf(roots, bone) + 1;
            return chains;
        }

        // Rig bone -> bone index in this skeleton: its mapped bone, or its nearest mapped ancestor's (the eyes and jaw follow
        // the head where the skeleton has none).
        public int[] RigToBone()
        {
            var result = new int[AvatarRig.BoneNames.Length];
            int[] parents = AvatarRig.ParentIndices;
            for (int i = 0; i < result.Length; i++)
            {
                int bone = i, found = -1;
                while (bone >= 0 && found < 0)
                {
                    if (Map.TryGetValue(bone, out string name))
                        found = IndexOf(name);
                    bone = parents[bone];
                }
                result[i] = found >= 0 ? found : IndexOf(Map[AvatarRig.Hips]);
            }
            return result;
        }
    }
}
