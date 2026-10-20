using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rbx2Source.Export
{
    public enum BodyPart
    {
        Head, UpperTorso, LowerTorso,
        LeftUpperArm, LeftLowerArm, LeftHand,
        RightUpperArm, RightLowerArm, RightHand,
        LeftUpperLeg, LeftLowerLeg, LeftFoot,
        RightUpperLeg, RightLowerLeg, RightFoot
    }

    public sealed class RigBone
    {
        public string Name;
        public int Parent;
        public double[] Position;
    }

    public sealed class PartBounds
    {
        public double[] Min = { double.MaxValue, double.MaxValue, double.MaxValue };
        public double[] Max = { double.MinValue, double.MinValue, double.MinValue };

        public void Add(float[] p)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Min[axis] = Math.Min(Min[axis], p[axis]);
                Max[axis] = Math.Max(Max[axis], p[axis]);
            }
        }

        public double Center(int axis) => (Min[axis] + Max[axis]) / 2;
        public double Size(int axis) => Max[axis] - Min[axis];
        public bool IsEmpty => Min[0] > Max[0];

        public PartBounds MirrorX(double centerX)
        {
            return new PartBounds
            {
                Min = new[] { 2 * centerX - Max[0], Min[1], Min[2] },
                Max = new[] { 2 * centerX - Min[0], Max[1], Max[2] }
            };
        }
    }

    // Builds a Unity/VRChat-style humanoid skeleton for Roblox's rendered (already posed) R15 avatar geometry.
    // Every Unity humanoid bone is present (names match HumanBodyBones). The R15 part bones carry the skin weights, plus
    // UpperChest for long hair down the back (see Drape); the others (shoulders, toes, eyes, jaw, fingers) exist for
    // humanoid mapping.
    public sealed class AvatarRig
    {
        public const int Hips = 0, Spine = 1, Chest = 2, Neck = 3, HeadBone = 4,
            LeftUpperArm = 5, LeftLowerArm = 6, LeftHand = 7,
            RightUpperArm = 8, RightLowerArm = 9, RightHand = 10,
            LeftUpperLeg = 11, LeftLowerLeg = 12, LeftFoot = 13,
            RightUpperLeg = 14, RightLowerLeg = 15, RightFoot = 16,
            UpperChest = 17, LeftShoulder = 18, RightShoulder = 19, LeftToes = 20, RightToes = 21,
            LeftEye = 22, RightEye = 23, Jaw = 24, LeftThumbProximal = 25, RightThumbProximal = 40;

        private static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        private static readonly string[] Phalanges = { "Proximal", "Intermediate", "Distal" };

        // First finger bone for a hand (thumb proximal); fingers follow in Fingers x Phalanges order.
        public static int FingerBone(bool left, int finger, int phalanx) =>
            (left ? LeftThumbProximal : RightThumbProximal) + finger * 3 + phalanx;

        public static readonly string[] BoneNames = new[]
        {
            "Hips", "Spine", "Chest", "Neck", "Head",
            "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
            "RightUpperLeg", "RightLowerLeg", "RightFoot",
            "UpperChest", "LeftShoulder", "RightShoulder", "LeftToes", "RightToes", "LeftEye", "RightEye", "Jaw"
        }.Concat(new[] { "Left", "Right" }.SelectMany(side => Fingers.SelectMany(finger => Phalanges.Select(p => side + finger + p)))).ToArray();

        private static readonly int[] Parents = BuildParents();

        private static int[] BuildParents()
        {
            var parents = new List<int> { -1, Hips, Spine, UpperChest, Neck, LeftShoulder, LeftUpperArm, LeftLowerArm,
                RightShoulder, RightUpperArm, RightLowerArm, Hips, LeftUpperLeg, LeftLowerLeg, Hips, RightUpperLeg, RightLowerLeg,
                Chest, UpperChest, UpperChest, LeftFoot, RightFoot, HeadBone, HeadBone, HeadBone };
            foreach (int hand in new[] { LeftHand, RightHand })
                for (int finger = 0; finger < Fingers.Length; finger++)
                    for (int phalanx = 0; phalanx < Phalanges.Length; phalanx++)
                        parents.Add(phalanx == 0 ? hand : parents.Count - 1);
            return parents.ToArray();
        }

        private static readonly Dictionary<BodyPart, int> PartBone = new Dictionary<BodyPart, int>
        {
            [BodyPart.Head] = HeadBone, [BodyPart.UpperTorso] = Chest, [BodyPart.LowerTorso] = Hips,
            [BodyPart.LeftUpperArm] = LeftUpperArm, [BodyPart.LeftLowerArm] = LeftLowerArm, [BodyPart.LeftHand] = LeftHand,
            [BodyPart.RightUpperArm] = RightUpperArm, [BodyPart.RightLowerArm] = RightLowerArm, [BodyPart.RightHand] = RightHand,
            [BodyPart.LeftUpperLeg] = LeftUpperLeg, [BodyPart.LeftLowerLeg] = LeftLowerLeg, [BodyPart.LeftFoot] = LeftFoot,
            [BodyPart.RightUpperLeg] = RightUpperLeg, [BodyPart.RightLowerLeg] = RightLowerLeg, [BodyPart.RightFoot] = RightFoot
        };

        public List<RigBone> Bones { get; } = new List<RigBone>();
        public Dictionary<string, BodyPart> Parts { get; } = new Dictionary<string, BodyPart>(StringComparer.Ordinal);

        // Render groups identified as worn accessories, and which groups are weighted as layered clothing.
        public Dictionary<string, AccessoryInfo> AccessoryGroups { get; } = new Dictionary<string, AccessoryInfo>(StringComparer.Ordinal);
        public HashSet<string> LayeredGroups { get; } = new HashSet<string>(StringComparer.Ordinal);

        // Render groups of the avatar's gear (asset type 19), held in the right hand and rigid on it. Only real gear is held:
        // every other item stays attached where Roblox wears it.
        public HashSet<string> HeldGroups { get; } = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<BodyPart, PartBounds> PartBoxes { get; } = new Dictionary<BodyPart, PartBounds>();
        private IReadOnlyList<AccessoryInfo> accessories;
        public int[][] Joints { get; private set; }
        public float[][] Weights { get; private set; }
        public string HeadGroup => primaryHead ?? Parts.Where(pair => pair.Value == BodyPart.Head).Select(pair => pair.Key).FirstOrDefault();
        private string primaryHead;

        // Other copies of the head: face makeup layers, which follow the head and its face shapes.
        public IEnumerable<string> HeadLayers => Parts.Where(pair => pair.Value == BodyPart.Head && pair.Key != HeadGroup).Select(pair => pair.Key);

        // With makeup the render has several copies of the head. The head itself has the skin colour; makeup layers are
        // white. Layers are pushed out by 1% so they don't flicker against the head they cover.
        private void ChooseHead(WavefrontModel model, Dictionary<string, HashSet<int>> groupPositions)
        {
            var heads = Parts.Where(pair => pair.Value == BodyPart.Head).Select(pair => pair.Key).ToList();
            if (heads.Count < 2)
                return;
            bool White(string group)
            {
                ObjTriangle triangle = model.Triangles.FirstOrDefault(t => t.Group == group);
                return triangle != null && model.Materials[triangle.Material].Diffuse.All(value => value > 0.99f);
            }
            primaryHead = heads.OrderBy(group => White(group) ? 1 : 0)
                .ThenBy(group => int.TryParse(Regex.Match(group, @"\d+$").Value, out int number) ? number : int.MaxValue).First();
            var box = new PartBounds();
            foreach (int index in groupPositions[primaryHead])
                box.Add(model.Positions[index]);
            foreach (string layer in heads.Where(group => group != primaryHead))
                foreach (int index in groupPositions[layer])
                {
                    float[] p = model.Positions[index];
                    model.Positions[index] = new[]
                    {
                        (float)(box.Center(0) + 1.01 * (p[0] - box.Center(0))), (float)(box.Center(1) + 1.01 * (p[1] - box.Center(1))),
                        (float)(box.Center(2) + 1.01 * (p[2] - box.Center(2)))
                    };
                }
            Notes.Add(primaryHead + " is the head; " + string.Join(", ", heads.Where(group => group != primaryHead)) + " (face makeup) follows it.");
        }
        public List<string> Notes { get; } = new List<string>();

        public static int BoneFor(BodyPart part) => PartBone[part];

        private static BodyPart Mirror(BodyPart part)
        {
            string name = part.ToString();
            if (name.StartsWith("Left", StringComparison.Ordinal))
                return (BodyPart)Enum.Parse(typeof(BodyPart), "Right" + name.Substring(4));
            if (name.StartsWith("Right", StringComparison.Ordinal))
                return (BodyPart)Enum.Parse(typeof(BodyPart), "Left" + name.Substring(5));
            return part;
        }

        public static AvatarRig Build(WavefrontModel model, CancellationToken cancellation = default)
        {
            return Build(model, null, cancellation);
        }

        // Accessories (from the avatar API) let rigid items follow their real attachment bone and mark layered clothing.
        // connectJoints blends the body's weights across its joints (see ConnectJoints).
        public static AvatarRig Build(WavefrontModel model, IReadOnlyList<AccessoryInfo> accessories, CancellationToken cancellation = default,
            bool connectJoints = false)
        {
            var rig = new AvatarRig { accessories = accessories, connectJoints = connectJoints };
            var groupBounds = new Dictionary<string, PartBounds>(StringComparer.Ordinal);
            var groupPositions = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            var positionGroup = new Dictionary<int, string>();
            foreach (ObjTriangle triangle in model.Triangles)
            {
                if (!groupBounds.TryGetValue(triangle.Group, out PartBounds bounds))
                {
                    groupBounds[triangle.Group] = bounds = new PartBounds();
                    groupPositions[triangle.Group] = new HashSet<int>();
                }
                foreach (ObjCorner corner in triangle.Corners)
                {
                    if (groupPositions[triangle.Group].Add(corner.Position))
                        bounds.Add(model.Positions[corner.Position]);
                    if (!positionGroup.ContainsKey(corner.Position))
                        positionGroup[corner.Position] = triangle.Group;
                }
            }

            // The body's rig comes first: its parts, and where layered clothing shows hidden ones. Rigid accessories are
            // attached to it afterwards (see ComputeWeights) and never change it.
            rig.MatchSkins(model, groupPositions);
            rig.Classify(groupBounds);
            // An avatar holding gear is rendered with its right arm raised straight forward. It is lowered (with the gear) and
            // the parts are classified again, since bones are placed for hanging arms.
            if (rig.LowerRaisedArm(model, groupBounds, groupPositions))
            {
                foreach (var pair in groupPositions)
                {
                    var bounds = new PartBounds();
                    foreach (int index in pair.Value)
                        bounds.Add(model.Positions[index]);
                    groupBounds[pair.Key] = bounds;
                }
                rig.Parts.Clear();
                rig.Classify(groupBounds);
            }
            rig.ChooseHead(model, groupPositions);
            cancellation.ThrowIfCancellationRequested();

            var all = new PartBounds();
            foreach (PartBounds bounds in groupBounds.Values)
            {
                all.Add(new[] { (float)bounds.Min[0], (float)bounds.Min[1], (float)bounds.Min[2] });
                all.Add(new[] { (float)bounds.Max[0], (float)bounds.Max[1], (float)bounds.Max[2] });
            }
            var body = new PartBounds();
            foreach (string group in rig.Parts.Keys)
            {
                PartBounds bounds = groupBounds[group];
                body.Add(new[] { (float)bounds.Min[0], (float)bounds.Min[1], (float)bounds.Min[2] });
                body.Add(new[] { (float)bounds.Max[0], (float)bounds.Max[1], (float)bounds.Max[2] });
            }
            if (body.IsEmpty)
            {
                body = all;
                rig.Notes.Add("No Roblox body parts were recognised; the skeleton uses standard R15 proportions.");
            }

            var parts = new Dictionary<BodyPart, PartBounds>();
            foreach (var pair in rig.Parts)
                parts[pair.Value] = groupBounds[pair.Key];
            // Body parts Roblox deleted under layered clothing (feet inside boots, forearms inside sleeves) are where the
            // clothing's own skin weights put them.
            foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
                if (!parts.ContainsKey(part) && rig.skinnedBoxes.TryGetValue(PartBone[part], out PartBounds covered) && !covered.IsEmpty)
                {
                    parts[part] = covered;
                    rig.Notes.Add(part + " is hidden by layered clothing; it is placed from the clothing's skin weights.");
                }
            rig.PlaceBones(parts, body);
            cancellation.ThrowIfCancellationRequested();
            rig.ComputeWeights(model, groupPositions, positionGroup, parts, body, cancellation);
            return rig;
        }

        private void Classify(Dictionary<string, PartBounds> groups)
        {
            // Some renders use real part names; Roblox's avatar renderer currently names body parts "Player#".
            foreach (var pair in groups)
            {
                string name = Regex.Replace(pair.Key, @"\d+$", "");
                if (Enum.TryParse(name, false, out BodyPart part) && !Parts.ContainsValue(part))
                    Parts[pair.Key] = part;
            }
            if (Parts.Count > 0)
                return;

            var body = groups.Where(pair => pair.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (body.Count == 0)
                return;

            double minX = body.Values.Min(b => b.Min[0]), maxX = body.Values.Max(b => b.Max[0]);
            double centerX = (minX + maxX) / 2, width = Math.Max(maxX - minX, 1e-6);
            // Head and torso parts are centred on the body, not just across its centre line (big legs can reach across it).
            var central = body.Where(pair => pair.Value.Min[0] < centerX - 0.02 * width && pair.Value.Max[0] > centerX + 0.02 * width &&
                    Math.Abs(pair.Value.Center(0) - centerX) < 0.1 * width)
                .OrderByDescending(pair => pair.Value.Max[1]).Select(pair => pair.Key).ToList();
            var sides = body.Keys.Except(central).ToList();

            if (central.Count > 0)
            {
                // Face makeup is rendered as another copy of the head; every copy is part of the head.
                PartBounds top = body[central[0]];
                double tolerance = 0.05 * Math.Max(top.Size(0), Math.Max(top.Size(1), top.Size(2)));
                bool SameBox(PartBounds box) => Enumerable.Range(0, 3).All(axis =>
                    Math.Abs(box.Min[axis] - top.Min[axis]) <= tolerance && Math.Abs(box.Max[axis] - top.Max[axis]) <= tolerance);
                var heads = central.Where(group => SameBox(body[group])).ToList();
                foreach (string head in heads)
                    Parts[head] = BodyPart.Head;
                var torso = central.Except(heads).OrderByDescending(group => body[group].Center(1)).ToList();
                if (torso.Count >= 2)
                {
                    Parts[torso[0]] = BodyPart.UpperTorso;
                    Parts[torso[1]] = BodyPart.LowerTorso;
                }
                else if (torso.Count == 1)
                {
                    Parts[torso[0]] = BodyPart.UpperTorso;
                }
            }

            // Arms sit further from the centre line than legs; split side groups at the largest horizontal gap.
            var distances = sides.Select(group => Math.Abs(body[group].Center(0) - centerX)).OrderBy(d => d).ToList();
            double threshold = double.NaN, largestGap = 0;
            for (int i = 1; i < distances.Count; i++)
                if (distances[i] - distances[i - 1] > largestGap)
                {
                    largestGap = distances[i] - distances[i - 1];
                    threshold = (distances[i] + distances[i - 1]) / 2;
                }
            if (largestGap < 0.1 * width)
            {
                string upperTorso = Parts.Where(pair => pair.Value == BodyPart.UpperTorso).Select(pair => pair.Key).FirstOrDefault();
                threshold = upperTorso != null ? body[upperTorso].Size(0) / 2 * 0.95 : width * 0.33;
            }

            // A part entirely above the middle of the lower torso is an arm, however close to the centre line it hangs (posed
            // renders and short-armed bodies put upper arms as close in as the legs).
            string lowerTorso = Parts.Where(pair => pair.Value == BodyPart.LowerTorso).Select(pair => pair.Key).FirstOrDefault();
            double hipLine = lowerTorso != null ? body[lowerTorso].Center(1) : double.PositiveInfinity;
            // Legs stand on the floor: each side's leg is the chain of touching parts climbing from the part on the floor to
            // the one that meets the hips. Whatever else is on that side is its arm, however close it hangs to the legs
            // (posed renders, short arms resting on big legs). Sides with nothing on the floor fall back to the distances.
            double floor = body.Values.Min(b => b.Min[1]), reach = 0.03 * Math.Max(body.Values.Max(b => b.Max[1]) - floor, 1e-6);
            bool Touch(PartBounds a, PartBounds b) => Enumerable.Range(0, 3).All(axis => a.Min[axis] - reach <= b.Max[axis] && b.Min[axis] - reach <= a.Max[axis]);
            double Overlap(PartBounds a, PartBounds b)
            {
                double x = Math.Max(0, Math.Min(a.Max[0], b.Max[0]) - Math.Max(a.Min[0], b.Min[0]));
                double z = Math.Max(0, Math.Min(a.Max[2], b.Max[2]) - Math.Max(a.Min[2], b.Min[2]));
                return x * z / Math.Max(Math.Min(a.Size(0) * a.Size(2), b.Size(0) * b.Size(2)), 1e-9);
            }
            string upperTorsoGroup = Parts.Where(pair => pair.Value == BodyPart.UpperTorso).Select(pair => pair.Key).FirstOrDefault();
            PartBounds hips = lowerTorso != null ? body[lowerTorso] : upperTorsoGroup != null ? body[upperTorsoGroup] : null;
            var legChains = new Dictionary<int, List<string>>();
            foreach (int sign in new[] { -1, 1 })
            {
                var mine = sides.Where(group => Math.Sign(body[group].Center(0) - centerX) == sign).ToList();
                string start = mine.Where(group => body[group].Min[1] <= floor + reach).OrderBy(group => body[group].Min[1]).FirstOrDefault();
                if (start == null || hips == null)
                    continue;
                var chain = new List<string> { start };
                while (chain.Count < 3)
                {
                    PartBounds last = body[chain[chain.Count - 1]];
                    // Once the chain meets the hips' box it only goes on into a part straight above the last one that meets the
                    // hips too: a thigh tucked up into a long torso, whose shin already reaches the hips (cartoon bodies). A
                    // hand hanging beside the leg overlaps it far less.
                    bool atHips = Touch(last, hips);
                    string next = mine.Where(group => !chain.Contains(group) && Touch(body[group], last) && body[group].Center(1) > last.Center(1) &&
                            (!atHips || (Overlap(body[group], last) >= 0.5 && Touch(body[group], hips))))
                        .OrderByDescending(group => Overlap(body[group], last)).FirstOrDefault();
                    if (next == null)
                        break;
                    chain.Add(next);
                }
                // A chain that never reaches the hips is not a leg (an arm hanging to the floor); use the distances instead.
                if (Touch(body[chain[chain.Count - 1]], hips))
                    legChains[sign] = chain;
            }

            var limbs = new Dictionary<string, List<string>>();
            foreach (string group in sides)
            {
                double dx = body[group].Center(0) - centerX;
                bool arm = legChains.TryGetValue(Math.Sign(dx), out List<string> leg) ? !leg.Contains(group)
                    : Math.Abs(dx) > threshold || body[group].Min[1] > hipLine;
                string key = (dx > 0 ? "Right" : "Left") + (arm ? "Arm" : "Leg");
                if (!limbs.ContainsKey(key))
                    limbs[key] = new List<string>();
                limbs[key].Add(group);
            }

            var labelled = new Dictionary<string, BodyPart>();
            foreach (var limb in limbs)
            {
                var ordered = limb.Value.OrderByDescending(group => body[group].Center(1)).ToList();
                if (ordered.Count == 3)
                {
                    BodyPart[] labels = LimbParts(limb.Key);
                    for (int i = 0; i < 3; i++)
                        labelled[ordered[i]] = labels[i];
                }
            }
            foreach (var limb in limbs)
            {
                if (limb.Value.Count == 3)
                    continue;
                BodyPart[] labels = LimbParts(limb.Key);
                string opposite = limb.Key.StartsWith("Left", StringComparison.Ordinal) ? "Right" + limb.Key.Substring(4) : "Left" + limb.Key.Substring(5);
                var used = new HashSet<BodyPart>();
                if (limbs.TryGetValue(opposite, out List<string> mirrorGroups) && mirrorGroups.All(labelled.ContainsKey) && mirrorGroups.Count == 3)
                {
                    // Hidden-surface removal can delete covered limbs; match the remaining ones to the other side.
                    foreach (string group in limb.Value.OrderByDescending(g => body[g].Center(1)))
                    {
                        var best = mirrorGroups.Where(g => !used.Contains(Mirror(labelled[g])))
                            .OrderBy(g => Math.Abs(body[g].Center(1) - body[group].Center(1))).FirstOrDefault();
                        if (best == null)
                            continue;
                        BodyPart part = Mirror(labelled[best]);
                        used.Add(part);
                        labelled[group] = part;
                    }
                }
                else
                {
                    var ordered = limb.Value.OrderByDescending(group => body[group].Center(1)).ToList();
                    if (ordered.Count > 3)
                        ordered = ordered.Take(3).ToList();
                    double top = ordered.Max(g => body[g].Max[1]), bottom = ordered.Min(g => body[g].Min[1]);
                    // Layered clothing over the limb's hidden parts shows where the limb starts and ends: boots over the shins
                    // and feet, sleeves over the upper arm and forearm (a hand left alone at the end of a sleeve is a hand).
                    foreach (BodyPart hidden in labels)
                        if (skinnedBoxes.TryGetValue(PartBone[hidden], out PartBounds covered) && !covered.IsEmpty)
                        {
                            top = Math.Max(top, covered.Max[1]);
                            bottom = Math.Min(bottom, covered.Min[1]);
                        }
                    double[] expected = { 0.2, 0.55, 0.88 };
                    int next = 0;
                    foreach (string group in ordered)
                    {
                        double t = (top - body[group].Center(1)) / Math.Max(top - bottom, 1e-6);
                        int best = next;
                        for (int i = next; i <= 3 - (ordered.Count - ordered.IndexOf(group)); i++)
                            if (Math.Abs(expected[i] - t) < Math.Abs(expected[best] - t))
                                best = i;
                        labelled[group] = labels[best];
                        next = best + 1;
                    }
                }
            }
            foreach (var pair in labelled)
                if (!Parts.ContainsValue(pair.Value))
                    Parts[pair.Key] = pair.Value;
        }

        private static BodyPart[] LimbParts(string limb)
        {
            switch (limb)
            {
                case "LeftArm": return new[] { BodyPart.LeftUpperArm, BodyPart.LeftLowerArm, BodyPart.LeftHand };
                case "RightArm": return new[] { BodyPart.RightUpperArm, BodyPart.RightLowerArm, BodyPart.RightHand };
                case "LeftLeg": return new[] { BodyPart.LeftUpperLeg, BodyPart.LeftLowerLeg, BodyPart.LeftFoot };
                default: return new[] { BodyPart.RightUpperLeg, BodyPart.RightLowerLeg, BodyPart.RightFoot };
            }
        }

        // The point of a part's box closest to a point.
        private static double[] Nearest(PartBounds part, double[] point) => new[]
        {
            Math.Max(part.Min[0], Math.Min(part.Max[0], point[0])),
            Math.Max(part.Min[1], Math.Min(part.Max[1], point[1])),
            Math.Max(part.Min[2], Math.Min(part.Max[2], point[2]))
        };

        private static double EndOffset(PartBounds part) => Math.Min(0.5 * part.Size(0), 0.25 * part.Size(1));

        // Pivot between a parent part and an adjacent child part (below it for limbs, above it for the head).
        private static double[] Joint(PartBounds parent, PartBounds child)
        {
            if (parent != null && child != null)
            {
                double low = Math.Max(parent.Min[1], child.Min[1]), high = Math.Min(parent.Max[1], child.Max[1]);
                bool above = child.Center(1) > parent.Center(1);
                double y;
                if (high > low)
                    y = (low + high) / 2;
                else if (above)
                    y = child.Min[1] - parent.Max[1] <= 0.25 * child.Size(1) ? (parent.Max[1] + child.Min[1]) / 2 : child.Min[1] + EndOffset(child);
                else if (parent.Min[1] - child.Max[1] <= 0.25 * child.Size(1))
                    y = (parent.Min[1] + child.Max[1]) / 2;
                else
                    y = child.Max[1] - EndOffset(child);
                return new[] { (parent.Center(0) + child.Center(0)) / 2, y, (parent.Center(2) + child.Center(2)) / 2 };
            }
            if (child != null)
                return new[] { child.Center(0), child.Max[1] - EndOffset(child), child.Center(2) };
            if (parent != null)
                return new[] { parent.Center(0), parent.Min[1] + EndOffset(parent), parent.Center(2) };
            return null;
        }

        private void PlaceBones(Dictionary<BodyPart, PartBounds> actual, PartBounds body)
        {
            double centerX = body.Center(0), centerZ = body.Center(2), height = body.Size(1), width = body.Size(0), floor = body.Min[1];
            PartBounds Get(BodyPart part)
            {
                if (actual.TryGetValue(part, out PartBounds bounds))
                    return bounds;
                if (actual.TryGetValue(Mirror(part), out bounds) && Mirror(part) != part)
                    return bounds.MirrorX(centerX);
                return null;
            }

            // Fallback ratios measured from a standard R15 render (height fraction, half-width fraction).
            double[] Template(double heightFraction, double side, double widthFraction) =>
                new[] { centerX + side * widthFraction * width, floor + heightFraction * height, centerZ };

            var position = new double[BoneNames.Length][];
            PartBounds head = Get(BodyPart.Head), upper = Get(BodyPart.UpperTorso), lower = Get(BodyPart.LowerTorso);

            position[Hips] = lower != null ? new[] { centerX, lower.Center(1), lower.Center(2) }
                : upper != null ? new[] { centerX, upper.Min[1], upper.Center(2) } : Template(0.40, 0, 0);
            double[] neck = Joint(upper, head);
            double seam = neck != null ? neck[1] : Template(0.68, 0, 0)[1];
            double torsoZ = (upper ?? lower ?? body).Center(2);
            // Evenly spaced spine bones: Unity's humanoid auto-mapping skips spine bones that sit almost on top of
            // each other, which left Spine mapped to Chest and no Neck.
            double hipsY = position[Hips][1], length = Math.Max(seam - hipsY, 0.05 * height);
            position[Spine] = new[] { centerX, hipsY + 0.2 * length, torsoZ };
            position[Chest] = new[] { centerX, hipsY + 0.45 * length, torsoZ };
            position[UpperChest] = new[] { centerX, hipsY + 0.7 * length, torsoZ };
            position[Neck] = new[] { centerX, hipsY + 0.94 * length, torsoZ };
            position[HeadBone] = head != null ? new[] { centerX, seam + 0.2 * head.Size(1), head.Center(2) }
                : new[] { centerX, seam + 0.04 * height, torsoZ };

            foreach (string side in new[] { "Left", "Right" })
            {
                double sign = side == "Right" ? 1 : -1;
                int offset = side == "Right" ? 3 : 0;
                PartBounds upperArm = Get(Part(side + "UpperArm")), lowerArm = Get(Part(side + "LowerArm")), hand = Get(Part(side + "Hand"));
                position[LeftUpperArm + offset] = upperArm != null
                    ? new[] { upperArm.Center(0), upperArm.Max[1] - 0.5 * Math.Min(upperArm.Size(0), 0.5 * upperArm.Size(1)), upperArm.Center(2) }
                    : Template(0.61, sign, 0.34);
                position[LeftLowerArm + offset] = Joint(upperArm, lowerArm) ?? Template(0.52, sign, 0.38);
                position[LeftHand + offset] = Joint(lowerArm, hand) ?? Template(0.42, sign, 0.40);
                // A forearm hidden under a sleeve is missing from the render: the wrist is where the hand comes closest to
                // the elbow (a posed hand can point sideways, so its top middle is not the wrist).
                if (lowerArm == null && upperArm != null && hand != null)
                {
                    position[LeftLowerArm + offset] = new[] { upperArm.Center(0), upperArm.Min[1], upperArm.Center(2) };
                    position[LeftHand + offset] = Nearest(hand, position[LeftLowerArm + offset]);
                }
                double[] shoulderJoint = position[LeftUpperArm + offset];
                position[side == "Left" ? LeftShoulder : RightShoulder] = new[]
                {
                    centerX + 0.45 * (shoulderJoint[0] - centerX), Math.Min(shoulderJoint[1], position[Neck][1] - 0.02 * height), torsoZ
                };

                PartBounds upperLeg = Get(Part(side + "UpperLeg")), lowerLeg = Get(Part(side + "LowerLeg")), foot = Get(Part(side + "Foot"));
                double[] hip = upperLeg != null && lower != null ? Joint(lower, upperLeg) : null;
                position[LeftUpperLeg + offset] = upperLeg != null
                    ? new[] { upperLeg.Center(0), hip != null ? hip[1] : upperLeg.Max[1] - 0.1 * upperLeg.Size(1), upperLeg.Center(2) }
                    : lowerLeg != null ? new[] { lowerLeg.Center(0), (lower ?? body).Min[1] + (lower != null ? 0.2 * lower.Size(1) : 0.37 * height), lowerLeg.Center(2) }
                    : Template(0.37, sign, 0.12);
                position[LeftLowerLeg + offset] = Joint(upperLeg, lowerLeg) ?? Template(0.14, sign, 0.13);
                position[LeftFoot + offset] = Joint(lowerLeg, foot) ?? Template(0.07, sign, 0.13);
                if (lowerLeg == null && upperLeg != null && foot != null)
                {
                    position[LeftLowerLeg + offset] = new[] { upperLeg.Center(0), upperLeg.Min[1], upperLeg.Center(2) };
                    position[LeftFoot + offset] = Nearest(foot, position[LeftLowerLeg + offset]);
                }

                // R6 limbs are one block (found as the middle part). Their joints go along the block, and its vertices are
                // split between the limb's three bones at them (see WholeLimbBone), so the limb bends at the elbow or knee.
                if (upperArm == null && hand == null && lowerArm != null)
                {
                    double top = lowerArm.Max[1], bottom = lowerArm.Min[1];
                    double shoulder = top - 0.5 * Math.Min(lowerArm.Size(0), 0.5 * lowerArm.Size(1));
                    double wrist = bottom + 0.15 * (top - bottom);
                    position[LeftUpperArm + offset] = new[] { lowerArm.Center(0), shoulder, lowerArm.Center(2) };
                    position[LeftLowerArm + offset] = new[] { lowerArm.Center(0), (shoulder + wrist) / 2, lowerArm.Center(2) };
                    position[LeftHand + offset] = new[] { lowerArm.Center(0), wrist, lowerArm.Center(2) };
                    wholeLimbs.Add(Part(side + "LowerArm"));
                }
                if (upperLeg == null && foot == null && lowerLeg != null)
                {
                    double top = lowerLeg.Max[1], bottom = lowerLeg.Min[1], legLength = top - bottom;
                    position[LeftUpperLeg + offset] = new[] { lowerLeg.Center(0), top - 0.1 * legLength, lowerLeg.Center(2) };
                    position[LeftLowerLeg + offset] = new[] { lowerLeg.Center(0), top - 0.5 * legLength, lowerLeg.Center(2) };
                    position[LeftFoot + offset] = new[] { lowerLeg.Center(0), bottom + 0.1 * legLength, lowerLeg.Center(2) };
                    wholeLimbs.Add(Part(side + "LowerLeg"));
                }
            }

            // Keep limb chains descending; a bad estimate must not invert the hierarchy.
            foreach (int[] chain in new[] { new[] { 5, 6, 7 }, new[] { 8, 9, 10 }, new[] { 11, 12, 13 }, new[] { 14, 15, 16 } })
                for (int i = 1; i < chain.Length; i++)
                    if (position[chain[i]][1] > position[chain[i - 1]][1] - 0.01 * height)
                        position[chain[i]][1] = position[chain[i - 1]][1] - 0.05 * height;

            foreach (string side in new[] { "Left", "Right" })
            {
                bool left = side == "Left";
                PartBounds foot = Get(Part(side + "Foot"));
                double[] ankle = position[left ? LeftFoot : RightFoot];
                // Toes sit at the front of the foot; Roblox characters face -Z.
                position[left ? LeftToes : RightToes] = foot != null
                    ? new[] { foot.Center(0), foot.Min[1] + 0.3 * foot.Size(1), foot.Min[2] + 0.2 * foot.Size(2) }
                    : new[] { ankle[0], ankle[1] - 0.03 * height, ankle[2] - 0.05 * height };
                PlaceFingers(position, left, Get(Part(side + "Hand")), height);
            }
            PlaceFace(head, position[HeadBone], height);
            for (int bone = LeftEye; bone <= Jaw; bone++)
                position[bone] = facePoints[bone];

            for (int i = 0; i < BoneNames.Length; i++)
                Bones.Add(new RigBone { Name = BoneNames[i], Parent = Parents[i], Position = position[i] });
        }

        private readonly Dictionary<int, double[]> facePoints = new Dictionary<int, double[]>();

        // Eyes and jaw estimated from the head's box: eyes a little above its middle near the front, jaw low inside.
        private void PlaceFace(PartBounds head, double[] headBone, double height)
        {
            if (head == null)
            {
                head = new PartBounds();
                head.Add(new[] { (float)(headBone[0] - 0.06 * height), (float)(headBone[1] - 0.02 * height), (float)(headBone[2] - 0.06 * height) });
                head.Add(new[] { (float)(headBone[0] + 0.06 * height), (float)(headBone[1] + 0.12 * height), (float)(headBone[2] + 0.06 * height) });
            }
            double cx = head.Center(0), w = head.Size(0), h = head.Size(1), d = head.Size(2);
            facePoints[LeftEye] = new[] { cx - 0.2 * w, head.Min[1] + 0.58 * h, head.Min[2] + 0.15 * d };
            facePoints[RightEye] = new[] { cx + 0.2 * w, head.Min[1] + 0.58 * h, head.Min[2] + 0.15 * d };
            facePoints[Jaw] = new[] { cx, head.Min[1] + 0.3 * h, head.Center(2) - 0.1 * d };
        }

        // Finger chains inside the hand, pointing from the wrist to the hand's far end (the fingertips).
        private void PlaceFingers(double[][] position, bool left, PartBounds hand, double height)
        {
            int handBone = left ? LeftHand : RightHand;
            double[] wrist = position[handBone], elbow = position[left ? LeftLowerArm : RightLowerArm];
            // Fingers run from the wrist through the hand (a posed hand need not continue the forearm's line).
            double[] along = Normalize(Subtract(wrist, elbow));
            if (hand != null)
            {
                double[] center = { hand.Center(0), hand.Center(1), hand.Center(2) };
                double[] through = Subtract(center, wrist);
                if (Math.Sqrt(Dot(through, through)) > 0.2 * Math.Max(hand.Size(0), Math.Max(hand.Size(1), hand.Size(2))))
                    along = Normalize(through) ?? along;
            }
            if (along == null)
                along = new double[] { 0, -1, 0 };
            // Thumbs point forward (-Z), towards the front of the character.
            double[] forward = Normalize(Subtract(new double[] { 0, 0, -1 }, Scale(along, -along[2])));
            if (forward == null)
                forward = new double[] { 0, 0, -1 };
            double reach = 0.06 * height, breadth = 0.025 * height;
            if (hand != null)
            {
                reach = 0;
                breadth = 0;
                foreach (double x in new[] { hand.Min[0], hand.Max[0] })
                    foreach (double y in new[] { hand.Min[1], hand.Max[1] })
                        foreach (double z in new[] { hand.Min[2], hand.Max[2] })
                        {
                            double[] offset = { x - wrist[0], y - wrist[1], z - wrist[2] };
                            reach = Math.Max(reach, Dot(offset, along));
                            breadth = Math.Max(breadth, Math.Abs(Dot(offset, forward)));
                        }
                reach = Math.Max(reach, 0.02 * height);
                breadth = Math.Max(breadth, 0.01 * height);
            }
            double[] At(double down, double ahead) => new[]
            {
                wrist[0] + along[0] * down * reach + forward[0] * ahead * breadth,
                wrist[1] + along[1] * down * reach + forward[1] * ahead * breadth,
                wrist[2] + along[2] * down * reach + forward[2] * ahead * breadth
            };
            double[][] thumb = { At(0.25, 0.75), At(0.42, 0.9), At(0.58, 0.95) };
            double[] lanes = { 0.6, 0.2, -0.2, -0.6 };
            for (int phalanx = 0; phalanx < 3; phalanx++)
                position[FingerBone(left, 0, phalanx)] = thumb[phalanx];
            for (int finger = 1; finger < Fingers.Length; finger++)
                for (int phalanx = 0; phalanx < 3; phalanx++)
                    position[FingerBone(left, finger, phalanx)] = At(0.5 + 0.17 * phalanx, lanes[finger - 1]);
        }

        private static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Normalize(double[] a)
        {
            double length = Math.Sqrt(Dot(a, a));
            return length > 1e-9 ? Scale(a, 1 / length) : null;
        }

        // Places the eye and jaw bones on the dynamic head's own facial bones (render space), when known.
        public void SetFaceBone(int bone, double[] renderPosition)
        {
            if (bone != LeftEye && bone != RightEye && bone != Jaw)
                throw new ArgumentOutOfRangeException(nameof(bone));
            Bones[bone].Position = renderPosition;
        }

        private static BodyPart Part(string name) => (BodyPart)Enum.Parse(typeof(BodyPart), name);

        // Samples points across a group's triangles; blocky Roblox parts have large faces with few vertices.
        private static List<double[]> SampleSurface(WavefrontModel model, string group, double step)
        {
            var points = new List<double[]>();
            foreach (ObjTriangle triangle in model.Triangles)
            {
                if (triangle.Group != group)
                    continue;
                float[] a = model.Positions[triangle.Corners[0].Position];
                float[] b = model.Positions[triangle.Corners[1].Position];
                float[] c = model.Positions[triangle.Corners[2].Position];
                double longest = Math.Max(Distance(a, b), Math.Max(Distance(b, c), Distance(c, a)));
                int divisions = Math.Max(1, Math.Min(32, (int)Math.Ceiling(longest / step)));
                for (int i = 0; i <= divisions; i++)
                    for (int j = 0; i + j <= divisions; j++)
                    {
                        double u = (double)i / divisions, v = (double)j / divisions, w = 1 - u - v;
                        points.Add(new[]
                        {
                            a[0] * w + b[0] * u + c[0] * v, a[1] * w + b[1] * u + c[1] * v, a[2] * w + b[2] * u + c[2] * v
                        });
                    }
            }
            return points;
        }

        private static double Distance(float[] a, float[] b)
        {
            double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }

        // Body parts that meet at a joint, parent first. The neck is left out: the dynamic head is replaced by its own mesh
        // after weighting.
        private bool connectJoints;

        private static readonly BodyPart[][] JointedParts =
        {
            new[] { BodyPart.LowerTorso, BodyPart.UpperTorso },
            new[] { BodyPart.UpperTorso, BodyPart.LeftUpperArm }, new[] { BodyPart.LeftUpperArm, BodyPart.LeftLowerArm }, new[] { BodyPart.LeftLowerArm, BodyPart.LeftHand },
            new[] { BodyPart.UpperTorso, BodyPart.RightUpperArm }, new[] { BodyPart.RightUpperArm, BodyPart.RightLowerArm }, new[] { BodyPart.RightLowerArm, BodyPart.RightHand },
            new[] { BodyPart.LowerTorso, BodyPart.LeftUpperLeg }, new[] { BodyPart.LeftUpperLeg, BodyPart.LeftLowerLeg }, new[] { BodyPart.LeftLowerLeg, BodyPart.LeftFoot },
            new[] { BodyPart.LowerTorso, BodyPart.RightUpperLeg }, new[] { BodyPart.RightUpperLeg, BodyPart.RightLowerLeg }, new[] { BodyPart.RightLowerLeg, BodyPart.RightFoot }
        };

        // "Connect joints": UGC bodies are often modelled as one surface cut into parts, and Roblox's render leaves out the
        // faces where blocky parts cover each other's ends, so rigid parts open gaps and holes where a joint bends. Near each
        // joint a vertex shares its weight with the other part's bone: half and half where the parts meet, fading to rigid a
        // short way off, so the surface stretches across the joint.
        // - Where the parts touch or overlap, a vertex close to the other part's vertices blends (only around the joint, so
        //   an arm hanging against the torso side stays separate).
        // - Where the parts meet along open edges (edges of a single triangle: the seam where the surface was cut), every
        //   vertex blends by its distance from that seam. That weight depends only on where a vertex is, so both parts' open
        //   edges move together and stay closed even where their vertices don't line up (blocky parts, the waist's sides).
        // R6 blocks stay rigid.
        private void ConnectJoints(WavefrontModel model, Dictionary<string, HashSet<int>> groupPositions, Dictionary<BodyPart, PartBounds> actual, double height)
        {
            double seam = 0.04 * height;
            var groupsOf = Parts.GroupBy(pair => pair.Value).ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToList());
            var triangles = model.Triangles.ToLookup(t => t.Group, StringComparer.Ordinal);
            var blends = new Dictionary<int, Dictionary<int, double>>();
            // Vertices lying on a seam, with the other part's bone and their distance from the seam.
            var onSeam = new Dictionary<int, KeyValuePair<int, double>>();
            int connected = 0;
            foreach (BodyPart[] pair in JointedParts)
            {
                BodyPart parent = pair[0], child = pair[1];
                if (!groupsOf.TryGetValue(parent, out List<string> parentGroups) || !groupsOf.TryGetValue(child, out List<string> childGroups) ||
                    wholeLimbs.Contains(parent) || wholeLimbs.Contains(child) || !actual.TryGetValue(child, out PartBounds childBox))
                    continue;
                int parentBone = PartBone[parent], childBone = PartBone[child];
                double[] joint = child == BodyPart.UpperTorso && actual.TryGetValue(parent, out PartBounds lowerBox)
                    ? Joint(lowerBox, childBox) : Bones[childBone].Position;
                if (joint == null)
                    continue;
                // The joint's reach: the child part's cross-section. Touching vertices only blend across the torso's depth at the
                // waist (a torso hanging over the hips at its sides stays separate); a seam blends all the way round.
                double half = 0.5 * Math.Max(childBox.Size(0), childBox.Size(2));
                double touchRadius = (child == BodyPart.UpperTorso ? 0.5 * childBox.Size(2) : half) + seam, seamRadius = half + seam;
                float[] center = { (float)joint[0], (float)joint[1], (float)joint[2] };
                List<int> Near(List<string> groups, double radius) => groups.SelectMany(g => groupPositions[g]).Distinct()
                    .Where(i => Distance(model.Positions[i], center) <= radius + seam).ToList();
                bool any = false;
                void Share(int vertex, double distance, int otherBone)
                {
                    if (distance >= seam)
                        return;
                    double t = distance / seam, weight = 0.5 * (1 - t * t * (3 - 2 * t));
                    if (!blends.TryGetValue(vertex, out var shares))
                        blends[vertex] = shares = new Dictionary<int, double>();
                    shares[otherBone] = Math.Max(shares.TryGetValue(otherBone, out double existing) ? existing : 0, weight);
                    any = true;
                }
                List<int> parents = Near(parentGroups, touchRadius), children = Near(childGroups, touchRadius);
                void Touching(List<int> from, List<int> to, int otherBone)
                {
                    foreach (int vertex in from)
                    {
                        float[] p = model.Positions[vertex];
                        double nearest = double.MaxValue;
                        foreach (int other in to)
                            nearest = Math.Min(nearest, Distance(p, model.Positions[other]));
                        Share(vertex, nearest, otherBone);
                    }
                }
                Touching(parents, children, childBone);
                Touching(children, parents, parentBone);
                var edges = SeamEdges(model, triangles, parentGroups, childGroups, center, seamRadius + seam, 0.5 * seam)
                    .Concat(SeamEdges(model, triangles, childGroups, parentGroups, center, seamRadius + seam, 0.5 * seam)).ToList();
                void Seam(List<int> from, int otherBone)
                {
                    foreach (int vertex in from)
                    {
                        float[] p = model.Positions[vertex];
                        double nearest = double.MaxValue;
                        foreach (float[][] edge in edges)
                            nearest = Math.Min(nearest, SegmentDistance(p, edge[0], edge[1]));
                        Share(vertex, nearest, otherBone);
                        // A vertex on the seam is held at exactly half and half between the two parts, whatever other joints
                        // nearby add, so both sides of the seam move alike.
                        if (nearest <= 0.05 * seam && (!onSeam.TryGetValue(vertex, out var held) || nearest < held.Value))
                            onSeam[vertex] = new KeyValuePair<int, double>(otherBone, nearest);
                    }
                }
                if (edges.Count > 0)
                {
                    Seam(Near(parentGroups, seamRadius), childBone);
                    Seam(Near(childGroups, seamRadius), parentBone);
                }
                if (any)
                    connected++;
            }
            foreach (var entry in blends)
            {
                int own = Joints[entry.Key][0];
                var shares = onSeam.TryGetValue(entry.Key, out var seamBone) && seamBone.Key != own
                    ? new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(seamBone.Key, 0.5) }
                    : entry.Value.Where(s => s.Key != own).OrderByDescending(s => s.Value).Take(3).ToList();
                double others = shares.Sum(s => s.Value), scale = others > 0.75 ? 0.75 / others : 1;
                var joints = new int[4];
                var weights = new float[4];
                joints[0] = own;
                weights[0] = (float)(1 - others * scale);
                for (int k = 0; k < shares.Count; k++)
                {
                    joints[k + 1] = shares[k].Key;
                    weights[k + 1] = (float)(shares[k].Value * scale);
                }
                Joints[entry.Key] = joints;
                Weights[entry.Key] = weights;
            }
            Notes.Add(connected > 0 ? "Connected " + connected + " body joints (" + blends.Count + " vertices blended)." : "No connected body joints were found.");
        }

        // A part's open edges within reach of a joint that lie on the other part (both ends within the tolerance of its
        // surface): where the two parts were cut apart. Corners at the same position count as one vertex, since a render can
        // split them for normals or UVs.
        private static List<float[][]> SeamEdges(WavefrontModel model, ILookup<string, ObjTriangle> triangles, List<string> groups,
            List<string> others, float[] center, double reach, double tolerance)
        {
            var surface = others.SelectMany(g => triangles[g])
                .Where(t => t.Corners.Any(c => Distance(model.Positions[c.Position], center) <= reach + tolerance)).ToList();
            bool OnSurface(float[] p) => surface.Any(t => TriangleDistance(p, model.Positions[t.Corners[0].Position],
                model.Positions[t.Corners[1].Position], model.Positions[t.Corners[2].Position]) <= tolerance);
            var result = new List<float[][]>();
            foreach (string group in groups)
            {
                var ids = new Dictionary<string, int>(StringComparer.Ordinal);
                var points = new List<float[]>();
                int Id(int index)
                {
                    float[] p = model.Positions[index];
                    string key = Math.Round(p[0], 4).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p[1], 4).ToString(CultureInfo.InvariantCulture) +
                        "," + Math.Round(p[2], 4).ToString(CultureInfo.InvariantCulture);
                    if (!ids.TryGetValue(key, out int id))
                    {
                        ids[key] = id = points.Count;
                        points.Add(p);
                    }
                    return id;
                }
                var uses = new Dictionary<long, int>();
                foreach (ObjTriangle triangle in triangles[group])
                    for (int i = 0; i < 3; i++)
                    {
                        int a = Id(triangle.Corners[i].Position), b = Id(triangle.Corners[(i + 1) % 3].Position);
                        if (a == b)
                            continue;
                        long key = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                        uses[key] = uses.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                foreach (var edge in uses.Where(e => e.Value == 1))
                {
                    float[] a = points[(int)(edge.Key >> 32)], b = points[(int)(edge.Key & 0xffffffff)];
                    if ((Distance(a, center) <= reach || Distance(b, center) <= reach) && OnSurface(a) && OnSurface(b))
                        result.Add(new[] { a, b });
                }
            }
            return result;
        }

        private static double SegmentDistance(float[] p, float[] a, float[] b)
        {
            double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, ap = { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
            double length = Dot(ab, ab), t = length > 1e-12 ? Math.Max(0, Math.Min(1, Dot(ap, ab) / length)) : 0;
            double[] d = { ap[0] - t * ab[0], ap[1] - t * ab[1], ap[2] - t * ab[2] };
            return Math.Sqrt(Dot(d, d));
        }

        // Distance from a point to a triangle (the closest point found by its Voronoi region, as in Ericson's Real-Time
        // Collision Detection).
        private static double TriangleDistance(float[] point, float[] a, float[] b, float[] c)
        {
            double[] p = { point[0], point[1], point[2] }, A = { a[0], a[1], a[2] }, B = { b[0], b[1], b[2] }, C = { c[0], c[1], c[2] };
            double[] ab = Subtract(B, A), ac = Subtract(C, A), ap = Subtract(p, A);
            double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
            double[] closest;
            if (d1 <= 0 && d2 <= 0)
                closest = A;
            else
            {
                double[] bp = Subtract(p, B);
                double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
                double[] cp = Subtract(p, C);
                double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
                double vc = d1 * d4 - d3 * d2, vb = d5 * d2 - d1 * d6, va = d3 * d6 - d5 * d4;
                if (d3 >= 0 && d4 <= d3)
                    closest = B;
                else if (vc <= 0 && d1 >= 0 && d3 <= 0)
                    closest = Add(A, Scale(ab, d1 / (d1 - d3)));
                else if (d6 >= 0 && d5 <= d6)
                    closest = C;
                else if (vb <= 0 && d2 >= 0 && d6 <= 0)
                    closest = Add(A, Scale(ac, d2 / (d2 - d6)));
                else if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                    closest = Add(B, Scale(Subtract(C, B), (d4 - d3) / (d4 - d3 + d5 - d6)));
                else
                {
                    double denominator = va + vb + vc;
                    if (Math.Abs(denominator) < 1e-18)
                        closest = A;
                    else
                        closest = Add(A, Add(Scale(ab, vb / denominator), Scale(ac, vc / denominator)));
                }
            }
            double[] d = Subtract(p, closest);
            return Math.Sqrt(Dot(d, d));
        }

        private void ComputeWeights(WavefrontModel model, Dictionary<string, HashSet<int>> groupPositions,
            Dictionary<int, string> positionGroup, Dictionary<BodyPart, PartBounds> actual, PartBounds body, CancellationToken cancellation)
        {
            Joints = new int[model.Positions.Count][];
            Weights = new float[model.Positions.Count][];
            double height = Math.Max(body.Size(1), 1e-6);
            var cloud = new List<double[]>();
            var cloudBones = new List<int>();

            var samples = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
            foreach (var pair in Parts)
            {
                int bone = PartBone[pair.Value];
                foreach (int index in groupPositions[pair.Key])
                {
                    Joints[index] = new[] { WholeLimbBone(pair.Value, model.Positions[index][1]), 0, 0, 0 };
                    Weights[index] = new[] { 1f, 0, 0, 0 };                }
                samples[pair.Key] = SampleSurface(model, pair.Key, 0.03 * height);
                foreach (double[] p in samples[pair.Key])
                {
                    cloud.Add(p);
                    cloudBones.Add(WholeLimbBone(pair.Value, p[1]));
                }
            }
            if (connectJoints)
                ConnectJoints(model, groupPositions, actual, height);

            // Body parts hidden by layered clothing still need influence volumes for the clothing that covers them.
            double centerX = body.Center(0);
            foreach (var pair in actual)
                PartBoxes[pair.Key] = pair.Value;
            foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
            {
                if (actual.ContainsKey(part))
                    continue;
                int bone = PartBone[part];
                string mirrorGroup = Parts.Where(pair => pair.Value == Mirror(part) && Mirror(part) != part).Select(pair => pair.Key).FirstOrDefault();
                if (mirrorGroup != null)
                {
                    PartBoxes[part] = actual[Mirror(part)].MirrorX(centerX);
                    foreach (double[] p in samples[mirrorGroup])
                    {
                        cloud.Add(new[] { 2 * centerX - p[0], p[1], p[2] });
                        cloudBones.Add(bone);
                    }
                    Notes.Add(part + " was hidden in the render; weights were mirrored from the other side.");
                    continue;
                }
                double[] start = Bones[bone].Position;
                double[] end = SegmentEnd(bone, height);
                double radius = (part == BodyPart.UpperTorso || part == BodyPart.LowerTorso ? 0.12 : 0.05) * height;
                var estimate = new PartBounds();
                for (int step = 0; step <= 10; step++)
                {
                    double t = step / 10.0;
                    double[] c = { start[0] + (end[0] - start[0]) * t, start[1] + (end[1] - start[1]) * t, start[2] + (end[2] - start[2]) * t };
                    for (int k = 0; k < 8; k++)
                    {
                        double angle = Math.PI * 2 * k / 8;
                        var point = new[] { c[0] + Math.Cos(angle) * radius, c[1], c[2] + Math.Sin(angle) * radius };
                        cloud.Add(point);
                        cloudBones.Add(bone);
                        estimate.Add(new[] { (float)point[0], (float)point[1], (float)point[2] });
                    }
                }
                PartBoxes[part] = estimate;
                if (Parts.Count > 0)
                    Notes.Add(part + " was not found in the render; its weights use an estimated volume.");
            }

            var search = new SpatialIndex(cloud, Math.Max(0.05 * height, 1e-3));
            double hugDistance = 0.05 * height;
            BuildVolumes(actual, centerX, height);
            var groups = new List<AccessoryGroup>();
            foreach (var pair in groupPositions)
            {
                if (Parts.ContainsKey(pair.Key))
                    continue;
                cancellation.ThrowIfCancellationRequested();
                var group = new AccessoryGroup { Name = pair.Key };
                int hugging = 0;
                var votes = new int[BoneNames.Length];
                foreach (int index in pair.Value)
                {
                    if (Joints[index] != null)
                        continue;
                    float[] q = model.Positions[index];
                    group.Bounds.Add(q);
                    var found = search.Nearest(q[0], q[1], q[2], 8);
                    group.Nearest[index] = found;
                    if (found.Count == 0)
                        continue;
                    if (found[0].Value < hugDistance)
                        hugging++;
                    votes[cloudBones[found[0].Key]]++;
                }
                if (group.Nearest.Count == 0)
                    continue;
                group.Hug = (double)hugging / group.Nearest.Count;
                group.Majority = Array.IndexOf(votes, votes.Max());
                groups.Add(group);
            }

            MatchSkinnedClothing(groups);
            MatchAccessories(model, groups.Where(group => group.Accessory == null).ToList(), height,
                accessories?.Where(accessory => groups.All(group => group.Accessory != accessory)).ToList());
            foreach (AccessoryGroup group in groups)
            {
                bool layered;
                int bone;
                if (group.Accessory != null)
                {
                    layered = group.Accessory.Layered;
                    bone = AccessoryBone(group.Accessory.Part);
                    AccessoryGroups[group.Name] = group.Accessory;
                }
                else
                {
                    // Without accessory metadata, layered clothing is recognised by how closely it wraps the body. An item
                    // wrapped closely around the head (a fur hood, a mask, hair) is still a head accessory, rigid on the head:
                    // blending it into the chest made it stretch and clip whenever the head turned. With metadata, every
                    // layered item was matched by its mesh or position, so a leftover group is a rigid accessory (or a part of
                    // one): Roblox welds those to a single body part, here the one it is mostly around.
                    layered = accessories == null && group.Hug >= 0.6 && group.Majority != HeadBone;
                    bone = group.Majority;
                }
                // Only gear is held: the group matched to the avatar's gear, and any unidentified piece that came down with the
                // raised arm holding it (a tool can have several parts). It is rigid on the right hand, where Roblox holds tools.
                if (group.Accessory != null ? group.Accessory.IsGear : heldBy.Contains(group.Name))
                {
                    HeldGroups.Add(group.Name);
                    layered = false;
                    bone = RightHand;
                    // Every vertex of the item's triangles follows the hand, including any its vertex list missed.
                    foreach (int index in model.Triangles.Where(t => t.Group == group.Name).SelectMany(t => t.Corners).Select(c => c.Position))
                        if (Joints[index] == null && !group.Nearest.ContainsKey(index))
                            group.Nearest[index] = new List<KeyValuePair<int, double>>();
                    Notes.Add(group.Name + " is gear, held in the right hand.");
                }
                if (layered)
                    LayeredGroups.Add(group.Name);
                HashSet<int> allowed = Within(bone, 3);

                if (group.Skin != null)
                {
                    // Roblox's own weights; the few vertices without a match follow the body like auto-weighted clothing.
                    foreach (var entry in group.Nearest)
                    {
                        Dictionary<int, double> influence = group.Skin.TryGetValue(entry.Key, out int vertex)
                            ? SkinTransfer.Influence(group.Accessory.Mesh, vertex) : null;
                        SetWeights(entry.Key, influence != null && influence.Count > 0 ? influence
                            : VolumeInfluence(model.Positions[entry.Key], height, allowed));
                    }
                    continue;
                }
                bool drape = !layered && bone == HeadBone && !HeldGroups.Contains(group.Name) && PartBoxes.ContainsKey(BodyPart.Head);
                foreach (var entry in group.Nearest)
                {
                    if (drape)
                    {
                        Drape(entry.Key, model.Positions[entry.Key][1], height);
                        continue;
                    }
                    if (!layered || entry.Value.Count == 0)
                    {
                        Joints[entry.Key] = new[] { bone, 0, 0, 0 };
                        Weights[entry.Key] = new[] { 1f, 0, 0, 0 };
                        continue;
                    }
                    SetWeights(entry.Key, VolumeInfluence(model.Positions[entry.Key], height, allowed));
                }
                if (layered)
                    SmoothWeights(model, group.Name, 4);
            }

            for (int i = 0; i < Joints.Length; i++)
                if (Joints[i] == null)
                {
                    Joints[i] = new[] { Hips, 0, 0, 0 };
                    Weights[i] = new[] { 1f, 0, 0, 0 };
                }
        }

        // Head accessories are rigid on the head down to the bottom of the head. Below it (long hair down the back) they blend
        // into the upper chest, following it fully 15% of the avatar's height lower. Roblox keeps them rigid, but its heads
        // barely move; Valve's animations turn the head a lot, which swung long hair out from the back and through the body.
        private void Drape(int vertex, float y, double height)
        {
            double below = PartBoxes[BodyPart.Head].Min[1] - y, t = Math.Max(0, Math.Min(1, below / (0.15 * height)));
            float chest = (float)(t * t * (3 - 2 * t));
            Joints[vertex] = chest > 0 ? new[] { HeadBone, UpperChest, 0, 0 } : new[] { HeadBone, 0, 0, 0 };
            Weights[vertex] = new[] { 1 - chest, chest, 0, 0 };
        }

        // R6 limbs: one block covering the whole limb, found as its middle part (LowerArm or LowerLeg).
        private readonly HashSet<BodyPart> wholeLimbs = new HashSet<BodyPart>();
        public bool IsWholeLimb(BodyPart part) => wholeLimbs.Contains(part);

        // The bone an accessory welded to a body part follows. An R6 limb is one block, rigid on its end bone, so anything
        // attached anywhere on it (gloves at the shoulder or grip attachment, say) follows that same bone.
        private int AccessoryBone(BodyPart part)
        {
            foreach (string side in new[] { "Left", "Right" })
            {
                if (wholeLimbs.Contains(Part(side + "LowerArm")) &&
                    (part == Part(side + "UpperArm") || part == Part(side + "LowerArm") || part == Part(side + "Hand")))
                    return PartBone[Part(side + "Hand")];
                if (wholeLimbs.Contains(Part(side + "LowerLeg")) &&
                    (part == Part(side + "UpperLeg") || part == Part(side + "LowerLeg") || part == Part(side + "Foot")))
                    return PartBone[Part(side + "Foot")];
            }
            return PartBone[part];
        }

        // The bone a body part's point follows: its part's bone, except that a whole-limb (R6) block is rigid on the limb's end
        // bone: arms on the hands, legs on the feet.
        private int WholeLimbBone(BodyPart part, double y)
        {
            int bone = PartBone[part];
            return wholeLimbs.Contains(part) ? bone + 1 : bone;
        }

        // Items that came down with the raised right arm: the gear, and anything worn on that arm.
        private readonly HashSet<string> heldBy = new HashSet<string>(StringComparer.Ordinal);

        // Roblox renders an avatar holding its gear with the right arm pointing straight forward (-Z). Only an avatar that has
        // gear is checked, and only when its right arm reaches forward further than it is tall and as far as an arm does (a
        // lone hand left by sleeves can be deeper than tall). The arm is rotated down 90 degrees about its shoulder with the
        // items at its end: the gear, and anything worn on that arm. Layered clothing and accessories worn elsewhere (known by
        // their own meshes) stay where they are. Returns whether the arm was lowered.
        private bool LowerRaisedArm(WavefrontModel model, Dictionary<string, PartBounds> groupBounds, Dictionary<string, HashSet<int>> groupPositions)
        {
            if (accessories == null || !accessories.Any(accessory => accessory.IsGear))
                return false;
            var armParts = new[] { BodyPart.RightUpperArm, BodyPart.RightLowerArm, BodyPart.RightHand };
            var arm = Parts.Where(pair => armParts.Contains(pair.Value)).Select(pair => pair.Key).ToList();
            if (arm.Count == 0)
                return false;
            PartBounds Box(IEnumerable<string> groups)
            {
                var result = new PartBounds();
                foreach (string group in groups)
                {
                    result.Add(new[] { (float)groupBounds[group].Min[0], (float)groupBounds[group].Min[1], (float)groupBounds[group].Min[2] });
                    result.Add(new[] { (float)groupBounds[group].Max[0], (float)groupBounds[group].Max[1], (float)groupBounds[group].Max[2] });
                }
                return result;
            }
            PartBounds box = Box(arm);
            double thickness = box.Size(1), reach = box.Size(2), height = Box(Parts.Keys).Size(1);
            if (reach < 1.5 * thickness || reach < 0.2 * height)
                return false;
            // The shoulder is half the arm's thickness in from its back end; the hand is at its front end.
            double[] pivot = { box.Center(0), box.Center(1), box.Max[2] - 0.5 * thickness };
            double[] end = { box.Center(0), box.Center(1), box.Min[2] };
            var triangles = model.Triangles.ToLookup(t => t.Group, StringComparer.Ordinal);
            bool WornElsewhere(string group) => skinMatches.ContainsKey(group) || accessories.Any(accessory => !accessory.IsGear &&
                accessory.Mesh != null && !armParts.Contains(accessory.Part) && SkinTransfer.Match(model, triangles[group].ToList(), accessory.Mesh) != null);
            var held = groupBounds.Where(pair => !Parts.ContainsKey(pair.Key) && DistanceToBox(end, pair.Value) < 0.25 * reach && !WornElsewhere(pair.Key))
                .Select(pair => pair.Key).ToList();
            var groups = new HashSet<string>(arm.Concat(held), StringComparer.Ordinal);

            // Forward (-Z) becomes down (-Y): (dy, dz) -> (dz, -dy), for positions about the shoulder and for normals.
            foreach (int index in groups.SelectMany(group => groupPositions[group]).Distinct())
            {
                float[] p = model.Positions[index];
                double dy = p[1] - pivot[1], dz = p[2] - pivot[2];
                model.Positions[index] = new[] { p[0], (float)(pivot[1] + dz), (float)(pivot[2] - dy) };
            }
            foreach (int index in model.Triangles.Where(t => groups.Contains(t.Group)).SelectMany(t => t.Corners)
                .Select(c => c.Normal).Where(n => n >= 0).Distinct())
            {
                float[] n = model.Normals[index];
                model.Normals[index] = new[] { n[0], n[2], -n[1] };
            }
            heldBy.UnionWith(held);
            Notes.Add("Right arm was raised to hold " + (held.Count > 0 ? string.Join(", ", held) : "the gear") + " in the render; it was lowered.");
            return true;
        }

        private sealed class AccessoryGroup
        {
            public string Name;
            public readonly PartBounds Bounds = new PartBounds();
            public readonly Dictionary<int, List<KeyValuePair<int, double>>> Nearest = new Dictionary<int, List<KeyValuePair<int, double>>>();
            public double Hug;
            public int Majority;
            public AccessoryInfo Accessory;
            // Rendered position -> vertex of the accessory's own mesh, when Roblox's skin weights can be used.
            public Dictionary<int, int> Skin;
        }

        // Layered clothing matched to its rendered group (see MatchSkins), and per rig bone the box of rendered clothing
        // vertices that mostly follow it.
        private readonly Dictionary<string, KeyValuePair<AccessoryInfo, Dictionary<int, int>>> skinMatches =
            new Dictionary<string, KeyValuePair<AccessoryInfo, Dictionary<int, int>>>(StringComparer.Ordinal);
        private readonly Dictionary<int, PartBounds> skinnedBoxes = new Dictionary<int, PartBounds>();

        // Layered clothing whose own mesh matches a rendered group (same triangles and UVs) keeps Roblox's weights. This
        // runs before the body parts are classified, since the weights also show where hidden body parts are.
        private void MatchSkins(WavefrontModel model, Dictionary<string, HashSet<int>> groupPositions)
        {
            if (accessories == null)
                return;
            var triangles = model.Triangles.ToLookup(t => t.Group, StringComparer.Ordinal);
            var candidates = groupPositions.Keys.Where(g => !g.StartsWith("Player", StringComparison.OrdinalIgnoreCase)).ToList();
            var bodyPositions = groupPositions.Where(pair => pair.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase)).SelectMany(pair => pair.Value).ToList();
            double centerX = bodyPositions.Count > 0 ? (bodyPositions.Min(i => model.Positions[i][0]) + bodyPositions.Max(i => model.Positions[i][0])) / 2 : 0;

            // Share of side-weighted vertices that sit on that side (character right is +X in the render). Left and right
            // shoes are mirror twins with the same triangles and UVs, so both match either rendered shoe equally; only
            // their weights tell which one is which.
            double SideAgreement(Dictionary<int, int> map, RobloxMesh mesh)
            {
                int agree = 0, total = 0;
                foreach (var pair in map)
                {
                    var influence = SkinTransfer.Influence(mesh, pair.Value);
                    if (influence.Count == 0)
                        continue;
                    string bone = BoneNames[influence.OrderByDescending(entry => entry.Value).First().Key];
                    int side = bone.StartsWith("Left", StringComparison.Ordinal) ? -1 : bone.StartsWith("Right", StringComparison.Ordinal) ? 1 : 0;
                    if (side == 0)
                        continue;
                    total++;
                    if (Math.Sign(model.Positions[pair.Key][0] - centerX) == side)
                        agree++;
                }
                return total == 0 ? 1 : (double)agree / total;
            }

            foreach (AccessoryInfo accessory in accessories.Where(a => a.Layered && a.HasSkinWeights))
            {
                string best = null;
                Dictionary<int, int> bestMap = null;
                double bestScore = 0;
                foreach (string group in candidates.Where(g => !skinMatches.ContainsKey(g)))
                {
                    Dictionary<int, int> map = SkinTransfer.Match(model, triangles[group].ToList(), accessory.Mesh);
                    if (map == null)
                        continue;
                    double score = (double)map.Count / groupPositions[group].Count * (0.5 + 0.5 * SideAgreement(map, accessory.Mesh));
                    if (bestMap == null || score > bestScore)
                    {
                        best = group;
                        bestMap = map;
                        bestScore = score;
                    }
                }
                if (best == null)
                {
                    Notes.Add(accessory.Name + ": its mesh did not match the render; it is auto-weighted.");
                    continue;
                }
                skinMatches[best] = new KeyValuePair<AccessoryInfo, Dictionary<int, int>>(accessory, bestMap);
                foreach (var pair in bestMap)
                    foreach (var influence in SkinTransfer.Influence(accessory.Mesh, pair.Value))
                        if (influence.Value >= 0.6)
                        {
                            if (!skinnedBoxes.TryGetValue(influence.Key, out PartBounds box))
                                skinnedBoxes[influence.Key] = box = new PartBounds();
                            box.Add(model.Positions[pair.Key]);
                        }
            }
        }

        private void MatchSkinnedClothing(List<AccessoryGroup> groups)
        {
            foreach (AccessoryGroup group in groups)
                if (skinMatches.TryGetValue(group.Name, out var match))
                {
                    group.Accessory = match.Key;
                    group.Skin = match.Value;
                    Notes.Add(group.Name + " = " + match.Key.Name + " (layered, Roblox skin weights)");
                }
        }

        // Full body-part volumes for skinning layered clothing. Roblox deletes body geometry hidden under layered
        // clothing, so each part's box is completed from the other side and stretched over its bone segment.
        private readonly Dictionary<int, PartBounds> volumes = new Dictionary<int, PartBounds>();

        private static PartBounds Union(PartBounds a, PartBounds b)
        {
            var result = new PartBounds();
            foreach (PartBounds box in new[] { a, b })
                if (box != null && !box.IsEmpty)
                {
                    result.Add(new[] { (float)box.Min[0], (float)box.Min[1], (float)box.Min[2] });
                    result.Add(new[] { (float)box.Max[0], (float)box.Max[1], (float)box.Max[2] });
                }
            return result;
        }

        private void BuildVolumes(Dictionary<BodyPart, PartBounds> actual, double centerX, double height)
        {
            foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
            {
                PartBounds box = PartBoxes[part];
                if (Mirror(part) != part && actual.TryGetValue(Mirror(part), out PartBounds other))
                    box = Union(box, other.MirrorX(centerX));
                int bone = PartBone[part];
                double[] start = Bones[bone].Position, end = SegmentEnd(bone, height);
                // Torso volumes keep their own height; limbs and the head must reach between their joints.
                if (part != BodyPart.UpperTorso && part != BodyPart.LowerTorso)
                {
                    var segment = new PartBounds();
                    segment.Add(new[] { (float)box.Center(0), (float)Math.Min(start[1], end[1]), (float)box.Center(2) });
                    segment.Add(new[] { (float)box.Center(0), (float)Math.Max(start[1], end[1]), (float)box.Center(2) });
                    box = Union(box, segment);
                }
                volumes[bone] = box;
            }
        }

        // Inside a volume, depth is relative to the part's size so overlapping parts at a joint blend evenly; outside,
        // the absolute distance decides which part clothing belongs to.
        private static double VolumeMetric(float[] p, PartBounds box, double softness)
        {
            double outside = 0, depth = double.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                double below = box.Min[axis] - p[axis], above = p[axis] - box.Max[axis];
                double d = Math.Max(below, above);
                if (d > 0)
                    outside += d * d;
                else
                    depth = Math.Min(depth, -d / Math.Max(box.Size(axis) / 2, 1e-6));
            }
            return outside > 0 ? Math.Sqrt(outside) : -2 * softness * depth;
        }

        // R15 parts that physically connect. Clothing only blends between connected parts, never across limbs.
        private static readonly Dictionary<int, int[]> Connected = new Dictionary<int, int[]>
        {
            [HeadBone] = new[] { Chest },
            [Chest] = new[] { HeadBone, Hips, LeftUpperArm, RightUpperArm },
            [Hips] = new[] { Chest, LeftUpperLeg, RightUpperLeg },
            [LeftUpperArm] = new[] { Chest, LeftLowerArm }, [LeftLowerArm] = new[] { LeftUpperArm, LeftHand }, [LeftHand] = new[] { LeftLowerArm },
            [RightUpperArm] = new[] { Chest, RightLowerArm }, [RightLowerArm] = new[] { RightUpperArm, RightHand }, [RightHand] = new[] { RightLowerArm },
            [LeftUpperLeg] = new[] { Hips, LeftLowerLeg }, [LeftLowerLeg] = new[] { LeftUpperLeg, LeftFoot }, [LeftFoot] = new[] { LeftLowerLeg },
            [RightUpperLeg] = new[] { Hips, RightLowerLeg }, [RightLowerLeg] = new[] { RightUpperLeg, RightFoot }, [RightFoot] = new[] { RightLowerLeg }
        };

        private static Dictionary<int, double> KeepConnected(Dictionary<int, double> influence)
        {
            if (influence.Count == 0)
                return influence;
            int main = influence.OrderByDescending(pair => pair.Value).First().Key;
            var allowed = new HashSet<int>(Connected.TryGetValue(main, out int[] near) ? near : Array.Empty<int>()) { main };
            return influence.Where(pair => allowed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        // Clothing follows the body part it is in or nearest to, blending where connected parts meet at joints.
        // Only bones within a few joints of the item's own attachment are considered (a left shoe never uses the right foot).
        private Dictionary<int, double> VolumeInfluence(float[] p, double height, HashSet<int> allowed)
        {
            double softness = 0.02 * height;
            var metrics = volumes.Where(pair => allowed.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => VolumeMetric(p, pair.Value, softness));
            double nearest = metrics.Values.Min();
            return KeepConnected(metrics.Where(pair => pair.Value <= nearest + 4 * softness)
                .ToDictionary(pair => pair.Key, pair => Math.Exp(-(pair.Value - nearest) / softness)));
        }

        private static HashSet<int> Within(int bone, int steps)
        {
            var reached = new HashSet<int> { bone };
            var frontier = new List<int> { bone };
            for (int step = 0; step < steps; step++)
                frontier = frontier.SelectMany(b => Connected.TryGetValue(b, out int[] next) ? next : Array.Empty<int>())
                    .Where(reached.Add).ToList();
            return reached;
        }

        private static double DistanceToBox(double[] p, PartBounds box)
        {
            double sum = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double d = Math.Max(0, Math.Max(box.Min[axis] - p[axis], p[axis] - box.Max[axis]));
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        // Pairs rendered accessory groups with the avatar's accessories. Each accessory's attachment point must lie
        // on or near its group, and layered clothing must wrap the body. Groups left unmatched use geometry alone.
        // Positions alone can mislead: long hair reaching the hips sits mostly at chest height, and an aura around the head
        // at head height. An accessory whose own mesh matches a rendered group (same triangles and UVs) is that group; one
        // whose mesh matches a different group is penalised, so the two are not swapped.
        private const double MeshMismatch = 0.8;

        private void MatchAccessories(WavefrontModel model, List<AccessoryGroup> groups, double height, List<AccessoryInfo> accessories)
        {
            if (accessories == null || accessories.Count == 0 || groups.Count == 0)
                return;
            const double Unmatched = 1.0;
            var triangles = model.Triangles.ToLookup(t => t.Group, StringComparer.Ordinal);
            var meshMatch = new bool[groups.Count, accessories.Count];
            var cost = new double[groups.Count, accessories.Count + groups.Count];
            for (int g = 0; g < groups.Count; g++)
            {
                List<ObjTriangle> groupTriangles = triangles[groups[g].Name].ToList();
                for (int a = 0; a < accessories.Count; a++)
                {
                    AccessoryInfo accessory = accessories[a];
                    double[] point = AccessoryInfo.AttachmentPoint(accessory.Attachment, PartBoxes[accessory.Part]);
                    double distance = DistanceToBox(point, groups[g].Bounds) / height;
                    cost[g, a] = 3 * distance + (accessory.Layered ? 1 - groups[g].Hug : 0.5 * groups[g].Hug);
                    meshMatch[g, a] = accessory.Mesh != null && SkinTransfer.Match(model, groupTriangles, accessory.Mesh) != null;
                }
                for (int d = 0; d < groups.Count; d++)
                    cost[g, accessories.Count + d] = Unmatched;
            }
            // The penalty only applies where a mesh identified something: an accessory whose mesh matched a group, or a
            // group another accessory's mesh matched. An accessory whose mesh matches nothing (older meshes, parts Roblox
            // renders differently) is matched by position as before.
            for (int a = 0; a < accessories.Count; a++)
            {
                bool identified = Enumerable.Range(0, groups.Count).Any(g => meshMatch[g, a]);
                for (int g = 0; g < groups.Count; g++)
                    if (!meshMatch[g, a] && (identified || Enumerable.Range(0, accessories.Count).Any(other => meshMatch[g, other])))
                        cost[g, a] += MeshMismatch;
            }
            int[] assignment = AccessoryInfo.Assign(cost);
            for (int g = 0; g < groups.Count; g++)
            {
                if (assignment[g] < accessories.Count)
                {
                    AccessoryInfo match = accessories[assignment[g]];
                    groups[g].Accessory = match;
                    // Accessories with the same attachment rig identically, so the render cannot tell them apart (unless
                    // the accessory's own mesh identified it).
                    var alike = accessories.Where(a => a.Layered == match.Layered && a.Attachment == match.Attachment).ToList();
                    string label = alike.Count == 1 || meshMatch[g, assignment[g]] ? match.Name : "one of " + string.Join(" / ", alike.Select(a => a.Name));
                    Notes.Add(groups[g].Name + " = " + label + " (" +
                        (match.Layered ? "layered, auto-weighted" : "rigid on " + BoneNames[AccessoryBone(match.Part)]) + ")");
                }
                else
                {
                    Notes.Add(groups[g].Name + " matched no worn accessory; weighted from its shape.");
                }
            }
        }

        // Averages weights across each vertex's mesh neighbours, so clothing bends smoothly over joints instead of
        // tearing where the nearest body part changes. Vertices at the same position are treated as one.
        private void SmoothWeights(WavefrontModel model, string group, int iterations)
        {
            var canonical = new Dictionary<int, int>();
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            var members = new List<List<int>>();
            var triangles = model.Triangles.Where(t => t.Group == group).ToList();
            foreach (int index in triangles.SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
            {
                float[] p = model.Positions[index];
                string key = Math.Round(p[0], 4) + "," + Math.Round(p[1], 4) + "," + Math.Round(p[2], 4);
                if (!keys.TryGetValue(key, out int id))
                {
                    keys[key] = id = members.Count;
                    members.Add(new List<int>());
                }
                canonical[index] = id;
                members[id].Add(index);
            }
            var neighbours = members.Select(_ => new HashSet<int>()).ToList();
            foreach (ObjTriangle triangle in triangles)
                for (int i = 0; i < 3; i++)
                {
                    int a = canonical[triangle.Corners[i].Position], b = canonical[triangle.Corners[(i + 1) % 3].Position];
                    if (a == b)
                        continue;
                    neighbours[a].Add(b);
                    neighbours[b].Add(a);
                }

            int bones = BoneNames.Length;
            var current = new double[members.Count][];
            for (int id = 0; id < members.Count; id++)
            {
                current[id] = new double[bones];
                foreach (int index in members[id])
                    for (int k = 0; k < 4; k++)
                        current[id][Joints[index][k]] += Weights[index][k] / members[id].Count;
            }
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var next = new double[members.Count][];
                for (int id = 0; id < members.Count; id++)
                {
                    next[id] = new double[bones];
                    if (neighbours[id].Count == 0)
                    {
                        Array.Copy(current[id], next[id], bones);
                        continue;
                    }
                    foreach (int other in neighbours[id])
                        for (int b = 0; b < bones; b++)
                            next[id][b] += current[other][b];
                    for (int b = 0; b < bones; b++)
                        next[id][b] = 0.5 * current[id][b] + 0.5 * next[id][b] / neighbours[id].Count;
                }
                current = next;
            }
            for (int id = 0; id < members.Count; id++)
            {
                var influence = new Dictionary<int, double>();
                for (int b = 0; b < bones; b++)
                    if (current[id][b] > 0)
                        influence[b] = current[id][b];
                foreach (int index in members[id])
                    SetWeights(index, KeepConnected(influence));
            }
        }

        private void SetWeights(int index, Dictionary<int, double> influence)
        {
            var top = influence.OrderByDescending(pair => pair.Value).Take(4).ToList();
            double total = top.Sum(pair => pair.Value);
            top = top.Where(pair => pair.Value / total >= 0.02).ToList();
            total = top.Sum(pair => pair.Value);
            var joints = new int[4];
            var weights = new float[4];
            for (int i = 0; i < top.Count; i++)
            {
                joints[i] = top[i].Key;
                weights[i] = (float)(top[i].Value / total);
            }
            weights[0] = 1 - weights.Skip(1).Sum();
            Joints[index] = joints;
            Weights[index] = weights;
        }

        private double[] SegmentEnd(int bone, double height)
        {
            int child = Array.IndexOf(Parents, bone);
            double[] p = Bones[bone].Position;
            switch (bone)
            {
                case Hips: return Bones[Spine].Position;
                case HeadBone: return new[] { p[0], p[1] + 0.15 * height, p[2] };
                case LeftHand: case RightHand: return new[] { p[0], p[1] - 0.07 * height, p[2] };
                case LeftFoot: case RightFoot: return new[] { p[0], p[1] - 0.05 * height, p[2] - 0.07 * height };
            }
            return child >= 0 ? Bones[child].Position : p;
        }

        public string Describe()
        {
            return string.Join(", ", Parts.OrderBy(pair => (int)pair.Value)
                .Select(pair => pair.Value.ToString() + "=" + pair.Key)) + (Notes.Count > 0 ? ". " + string.Join(" ", Notes) : "");
        }

        // Weights vertices appended after the rig was built (e.g. a replacement head) fully to one bone.
        public void AppendRigid(int positionCount, int bone)
        {
            int previous = Joints.Length;
            if (positionCount <= previous)
                return;
            var joints = Joints;
            var weights = Weights;
            Array.Resize(ref joints, positionCount);
            Array.Resize(ref weights, positionCount);
            for (int i = previous; i < positionCount; i++)
            {
                joints[i] = new[] { bone, 0, 0, 0 };
                weights[i] = new[] { 1f, 0, 0, 0 };
            }
            Joints = joints;
            Weights = weights;
        }

        public const float AnchorWeight = 0.01f;

        // Unity's humanoid auto-mapper only accepts transforms that skin something (or have several child bones), so
        // bones that deform nothing (spine, neck, shoulders, toes, eyes, jaw, fingers) were skipped. Each one gets a
        // negligible weight on the vertex nearest to it among those fully weighted to its closest weighted ancestor.
        public void AnchorHelperBones(WavefrontModel model)
        {
            if (Joints == null || Joints.Length < model.Positions.Count)
                throw new InvalidOperationException("Every vertex must be weighted before anchoring helper bones.");
            var used = new HashSet<int>(model.Triangles.SelectMany(t => t.Corners).Select(c => c.Position));
            var bodyGroups = new HashSet<string>(Parts.Keys, StringComparer.Ordinal);
            var bodyPositions = new HashSet<int>(model.Triangles.Where(t => bodyGroups.Contains(t.Group))
                .SelectMany(t => t.Corners).Select(c => c.Position));
            var weighted = new HashSet<int>();
            var rigid = new Dictionary<int, List<int>>();
            foreach (int i in used)
            {
                for (int k = 0; k < 4; k++)
                    if (Weights[i][k] > 0)
                        weighted.Add(Joints[i][k]);
                if (Weights[i][0] >= 1)
                {
                    if (!rigid.TryGetValue(Joints[i][0], out List<int> list))
                        rigid[Joints[i][0]] = list = new List<int>();
                    list.Add(i);
                }
            }

            var taken = new HashSet<int>();
            for (int bone = 0; bone < Bones.Count; bone++)
            {
                if (weighted.Contains(bone))
                    continue;
                int ancestor = Bones[bone].Parent;
                while (ancestor >= 0 && !rigid.ContainsKey(ancestor))
                    ancestor = Bones[ancestor].Parent;
                if (ancestor < 0)
                    continue;
                double[] target = Bones[bone].Position;
                int best = -1;
                bool bestBody = false;
                double bestDistance = double.MaxValue;
                foreach (int i in rigid[ancestor])
                {
                    if (taken.Contains(i))
                        continue;
                    bool body = bodyPositions.Contains(i);
                    float[] p = model.Positions[i];
                    double x = p[0] - target[0], y = p[1] - target[1], z = p[2] - target[2], distance = x * x + y * y + z * z;
                    if (best < 0 || (body && !bestBody) || (body == bestBody && distance < bestDistance))
                    {
                        best = i;
                        bestBody = body;
                        bestDistance = distance;
                    }
                }
                if (best < 0)
                    continue;
                // Every copy of that vertex at the same position (renders split vertices along UV and normal seams) takes the
                // same anchor: anchoring one copy alone opened a hairline crack there whenever the helper bone moved (fingers
                // curling round a first-person weapon, VRChat hand gestures).
                float[] at = model.Positions[best];
                foreach (int i in rigid[ancestor])
                    if (i == best || (!taken.Contains(i) && model.Positions[i][0] == at[0] && model.Positions[i][1] == at[1] && model.Positions[i][2] == at[2]))
                    {
                        taken.Add(i);
                        Joints[i] = new[] { ancestor, bone, 0, 0 };
                        Weights[i] = new[] { 1 - AnchorWeight, AnchorWeight, 0, 0 };
                    }
            }
        }

        private IEnumerable<int> Descendants(int bone)
        {
            yield return bone;
            for (int child = 0; child < Bones.Count; child++)
                if (Bones[child].Parent == bone)
                    foreach (int descendant in Descendants(child))
                        yield return descendant;
        }

        // Re-poses the avatar so its arms point straight out to the sides and makes that the bind pose, the T-pose
        // humanoid tools (Unity, VRChat, Blender retargeting) measure animation against. Each arm rotates about its
        // shoulder joint and each forearm about its elbow; skinned vertices (including layered sleeves) follow their weights.
        public void ApplyTPose(WavefrontModel model)
        {
            var segments = new List<KeyValuePair<int[], double[]>>();
            foreach (bool left in new[] { true, false })
            {
                int upper = left ? LeftUpperArm : RightUpperArm, lower = left ? LeftLowerArm : RightLowerArm, hand = left ? LeftHand : RightHand;
                // Character left is -X in Roblox render space.
                double[] target = { left ? -1 : 1, 0, 0 };
                // Upper arm and forearm are straightened separately: Unity's humanoid expects each within 5 degrees
                // of horizontal and treats any remaining tilt as part of every animation.
                segments.Add(new KeyValuePair<int[], double[]>(new[] { upper, lower }, target));
                segments.Add(new KeyValuePair<int[], double[]>(new[] { lower, hand }, target));
            }
            AlignSegments(model, segments);
        }

        // Rotates each segment (a bone and the child it points at) about the bone so it points along the given direction
        // (render space), carrying its descendants. Segments are applied in order, so list parents before children.
        public void AlignSegments(WavefrontModel model, IEnumerable<KeyValuePair<int[], double[]>> segments)
        {
            if (Joints == null || Joints.Length < model.Positions.Count)
                throw new InvalidOperationException("Every vertex must be weighted before posing.");
            // Rigid transform per moved bone: 3x3 row-major rotation followed by a translation (p' = M p + t).
            var transforms = new Dictionary<int, double[]>();
            foreach (var segment in segments)
            {
                double[] target = Normalize(segment.Value);
                double[] pivot = Bones[segment.Key[0]].Position;
                double[] direction = Normalize(Subtract(Bones[segment.Key[1]].Position, pivot));
                if (direction == null || target == null)
                    continue;
                double[] rotation = RotationBetween(direction, target);
                foreach (int bone in Descendants(segment.Key[0]).ToList())
                {
                    Bones[bone].Position = Add(pivot, Rotate(rotation, Subtract(Bones[bone].Position, pivot)));
                    double[] previous = transforms.TryGetValue(bone, out double[] existing) ? existing
                        : new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
                    double[] m = Multiply(rotation, previous);
                    double[] t = Add(pivot, Rotate(rotation, Subtract(new[] { previous[9], previous[10], previous[11] }, pivot)));
                    transforms[bone] = m.Take(9).Concat(t).ToArray();
                }
            }
            if (transforms.Count == 0)
                return;

            var moved = new double[model.Positions.Count][];
            var blends = new double[model.Positions.Count][];
            for (int i = 0; i < model.Positions.Count; i++)
            {
                float[] p = model.Positions[i];
                double[] point = { p[0], p[1], p[2] }, result = (double[])point.Clone();
                var blend = new double[9];
                double rotated = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = Weights[i][k];
                    if (w <= 0 || !transforms.TryGetValue(Joints[i][k], out double[] transform))
                        continue;
                    double[] target = Add(Rotate(transform, point), new[] { transform[9], transform[10], transform[11] });
                    for (int axis = 0; axis < 3; axis++)
                        result[axis] += w * (target[axis] - point[axis]);
                    for (int m = 0; m < 9; m++)
                        blend[m] += w * transform[m];
                    rotated += w;
                }
                if (rotated <= 0)
                    continue;
                for (int m = 0; m < 9; m++)
                    blend[m] += (1 - rotated) * (m % 4 == 0 ? 1 : 0);
                moved[i] = result;
                blends[i] = blend;
            }

            // Normals can be shared between positions with different weights, so posed normals get new entries.
            var posedNormals = new Dictionary<long, int>();
            foreach (ObjCorner corner in model.Triangles.SelectMany(t => t.Corners).Distinct())
            {
                if (blends[corner.Position] == null)
                    continue;
                long key = ((long)corner.Normal << 32) | (uint)corner.Position;
                if (!posedNormals.TryGetValue(key, out int index))
                {
                    float[] n = model.Normals[corner.Normal];
                    double[] normal = Normalize(Rotate(blends[corner.Position], new double[] { n[0], n[1], n[2] })) ?? new double[] { n[0], n[1], n[2] };
                    index = model.Normals.Count;
                    model.Normals.Add(new[] { (float)normal[0], (float)normal[1], (float)normal[2] });
                    posedNormals[key] = index;
                }
                corner.Normal = index;
            }
            for (int i = 0; i < moved.Length; i++)
                if (moved[i] != null)
                    model.Positions[i] = new[] { (float)moved[i][0], (float)moved[i][1], (float)moved[i][2] };
        }

        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

        private static double[] Rotate(double[] m, double[] v) => new[]
        {
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]
        };

        private static double[] Multiply(double[] a, double[] b)
        {
            var result = new double[9];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    result[row * 3 + column] = a[row * 3] * b[column] + a[row * 3 + 1] * b[3 + column] + a[row * 3 + 2] * b[6 + column];
            return result;
        }

        // Row-major rotation turning unit vector a onto unit vector b (Rodrigues' formula).
        private static double[] RotationBetween(double[] a, double[] b)
        {
            double[] axis = { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
            double sin = Math.Sqrt(Dot(axis, axis)), cos = Dot(a, b);
            if (sin < 1e-9)
            {
                if (cos > 0)
                    return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                // Opposite directions: half turn about any perpendicular axis.
                axis = Math.Abs(a[0]) < 0.9 ? new[] { 0, a[2], -a[1] } : new[] { -a[2], 0, a[0] };
                axis = Normalize(axis);
                sin = 0;
                cos = -1;
            }
            else
            {
                axis = Scale(axis, 1 / sin);
            }
            double x = axis[0], y = axis[1], z = axis[2], t = 1 - cos;
            return new[]
            {
                t * x * x + cos, t * x * y - sin * z, t * x * z + sin * y,
                t * x * y + sin * z, t * y * y + cos, t * y * z - sin * x,
                t * x * z - sin * y, t * y * z + sin * x, t * z * z + cos
            };
        }
    }
}
