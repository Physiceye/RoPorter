using System;
using System.Collections.Generic;
using System.Linq;

namespace Rbx2Source.Export
{
    // A worn accessory, as listed by Roblox's avatar API and described by its model file.
    public sealed class AccessoryInfo
    {
        public long Id;
        public string Name;
        public int TypeId;

        // Layered clothing (WrapLayer) deforms with the body; everything else is welded to one body part.
        public bool Layered;
        public string Attachment;
        public BodyPart Part;

        // The item's own mesh. For layered clothing it carries Roblox's skin weights on the R15 skeleton.
        public long? MeshId;
        public RobloxMesh Mesh;
        public bool HasSkinWeights => Mesh?.SkinBones != null && Mesh.Bones.Count > 0;

        private static readonly HashSet<int> RigidTypes = new HashSet<int> { 8, 41, 42, 43, 44, 45, 46, 47, 57, 58 };
        private static readonly HashSet<int> LayeredTypes = new HashSet<int> { 64, 65, 66, 67, 68, 69, 70, 71, 72 };

        public static bool IsAccessoryType(int typeId) => RigidTypes.Contains(typeId) || LayeredTypes.Contains(typeId);
        public static bool IsLayeredType(int typeId) => LayeredTypes.Contains(typeId);

        // R15 parts that hold each accessory attachment. Accessories are welded to the part with the matching attachment.
        private static readonly Dictionary<string, BodyPart> AttachmentParts = new Dictionary<string, BodyPart>(StringComparer.Ordinal)
        {
            ["HatAttachment"] = BodyPart.Head, ["HairAttachment"] = BodyPart.Head,
            ["FaceFrontAttachment"] = BodyPart.Head, ["FaceCenterAttachment"] = BodyPart.Head,
            ["NeckAttachment"] = BodyPart.UpperTorso, ["BodyFrontAttachment"] = BodyPart.UpperTorso,
            ["BodyBackAttachment"] = BodyPart.UpperTorso, ["LeftCollarAttachment"] = BodyPart.UpperTorso,
            ["RightCollarAttachment"] = BodyPart.UpperTorso,
            ["WaistFrontAttachment"] = BodyPart.LowerTorso, ["WaistBackAttachment"] = BodyPart.LowerTorso,
            ["WaistCenterAttachment"] = BodyPart.LowerTorso,
            ["LeftShoulderAttachment"] = BodyPart.LeftUpperArm, ["RightShoulderAttachment"] = BodyPart.RightUpperArm,
            ["LeftGripAttachment"] = BodyPart.LeftHand, ["RightGripAttachment"] = BodyPart.RightHand,
            ["LeftFootAttachment"] = BodyPart.LeftFoot, ["RightFootAttachment"] = BodyPart.RightFoot
        };

        private static readonly Dictionary<int, string> DefaultAttachments = new Dictionary<int, string>
        {
            [8] = "HatAttachment", [41] = "HairAttachment", [42] = "FaceFrontAttachment", [43] = "NeckAttachment",
            [44] = "NeckAttachment", [45] = "BodyFrontAttachment", [46] = "BodyBackAttachment", [47] = "WaistBackAttachment",
            [57] = "FaceFrontAttachment", [58] = "FaceFrontAttachment",
            [64] = "BodyFrontAttachment", [65] = "BodyFrontAttachment", [67] = "BodyFrontAttachment", [68] = "BodyFrontAttachment",
            [66] = "WaistCenterAttachment", [69] = "WaistCenterAttachment", [72] = "WaistCenterAttachment",
            [70] = "LeftFootAttachment", [71] = "RightFootAttachment"
        };

        public static bool TryGetPart(string attachment, out BodyPart part) =>
            AttachmentParts.TryGetValue(attachment ?? "", out part);

        public static AccessoryInfo FromType(long id, string name, int typeId)
        {
            string attachment = DefaultAttachments.TryGetValue(typeId, out string value) ? value : "HatAttachment";
            return new AccessoryInfo
            {
                Id = id, Name = name, TypeId = typeId, Layered = IsLayeredType(typeId),
                Attachment = attachment, Part = AttachmentParts[attachment]
            };
        }

        // Refines the type-based defaults with the accessory's own model (attachment name and WrapLayer).
        public void Apply(RobloxModelFile.AccessoryModel model)
        {
            if (model == null)
                return;
            Layered = model.HasWrapLayer || IsLayeredType(TypeId);
            MeshId = model.MeshId;
            string attachment = model.Attachments.FirstOrDefault(name => AttachmentParts.ContainsKey(name));
            if (attachment != null)
            {
                Attachment = attachment;
                Part = AttachmentParts[attachment];
            }
        }

        // Where the attachment sits on its part's bounding box (render space: front is -Z, character left is -X).
        public static double[] AttachmentPoint(string attachment, PartBounds box)
        {
            double cx = box.Center(0), cy = box.Center(1), cz = box.Center(2);
            switch (attachment)
            {
                case "HatAttachment": case "HairAttachment": case "NeckAttachment":
                case "LeftShoulderAttachment": case "RightShoulderAttachment":
                    return new[] { cx, box.Max[1], cz };
                case "FaceFrontAttachment": case "BodyFrontAttachment": case "WaistFrontAttachment":
                    return new[] { cx, cy, box.Min[2] };
                case "BodyBackAttachment": case "WaistBackAttachment":
                    return new[] { cx, cy, box.Max[2] };
                case "LeftCollarAttachment":
                    return new[] { cx - 0.35 * box.Size(0), box.Max[1], cz };
                case "RightCollarAttachment":
                    return new[] { cx + 0.35 * box.Size(0), box.Max[1], cz };
                case "LeftGripAttachment": case "RightGripAttachment":
                case "LeftFootAttachment": case "RightFootAttachment":
                    return new[] { cx, box.Min[1], cz };
                default:
                    return new[] { cx, cy, cz };
            }
        }

        // Minimum-cost assignment of rows to distinct columns (rows <= columns). Returns the column for each row.
        public static int[] Assign(double[,] cost)
        {
            int n = cost.GetLength(0), m = cost.GetLength(1);
            if (n > m)
                throw new ArgumentException("Assignment needs at least as many columns as rows.");
            var u = new double[n + 1];
            var v = new double[m + 1];
            var p = new int[m + 1];
            var way = new int[m + 1];
            for (int i = 1; i <= n; i++)
            {
                p[0] = i;
                int j0 = 0;
                var minv = Enumerable.Repeat(double.PositiveInfinity, m + 1).ToArray();
                var used = new bool[m + 1];
                do
                {
                    used[j0] = true;
                    int i0 = p[j0], j1 = 0;
                    double delta = double.PositiveInfinity;
                    for (int j = 1; j <= m; j++)
                    {
                        if (used[j])
                            continue;
                        double current = cost[i0 - 1, j - 1] - u[i0] - v[j];
                        if (current < minv[j])
                        {
                            minv[j] = current;
                            way[j] = j0;
                        }
                        if (minv[j] < delta)
                        {
                            delta = minv[j];
                            j1 = j;
                        }
                    }
                    for (int j = 0; j <= m; j++)
                    {
                        if (used[j])
                        {
                            u[p[j]] += delta;
                            v[j] -= delta;
                        }
                        else
                        {
                            minv[j] -= delta;
                        }
                    }
                    j0 = j1;
                } while (p[j0] != 0);
                do
                {
                    int j1 = way[j0];
                    p[j0] = p[j1];
                    j0 = j1;
                } while (j0 != 0);
            }
            var result = new int[n];
            for (int j = 1; j <= m; j++)
                if (p[j] != 0)
                    result[p[j] - 1] = j - 1;
            return result;
        }
    }
}
