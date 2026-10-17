using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rbx2Source.Export
{
    // Builds a Garry's Mod addon: the avatar on Valve's ValveBiped skeleton (so GMod's player animations and weapons fit),
    // VTF/VMT materials, and the compiled model. Avatars use Valve's bone orientations with Roblox bone positions, posed like
    // Valve's reference, plus the "proportions trick" so animations keep the Roblox body's proportions. One model serves as
    // both the playermodel and the ragdoll (it carries ragdoll physics). Catalog items become physics props.
    public static class SourceExporter
    {
        private sealed class Bone
        {
            public string Name;
            public int Parent = -1;
            public double[] Position, Rotation;
        }

        private const string Prefix = "ValveBiped.Bip01_";

        private static string ValveName(int rigBone)
        {
            switch (rigBone)
            {
                case AvatarRig.Hips: return Prefix + "Pelvis";
                case AvatarRig.Spine: return Prefix + "Spine";
                case AvatarRig.Chest: return Prefix + "Spine1";
                case AvatarRig.UpperChest: return Prefix + "Spine4";
                case AvatarRig.Neck: return Prefix + "Neck1";
                case AvatarRig.HeadBone: return Prefix + "Head1";
            }
            string name = AvatarRig.BoneNames[rigBone];
            string side = name.StartsWith("Left", StringComparison.Ordinal) ? "L_" : name.StartsWith("Right", StringComparison.Ordinal) ? "R_" : null;
            if (side == null)
                return null;
            string part = name.Substring(side == "L_" ? 4 : 5);
            switch (part)
            {
                case "Shoulder": return Prefix + side + "Clavicle";
                case "UpperArm": return Prefix + side + "UpperArm";
                case "LowerArm": return Prefix + side + "Forearm";
                case "Hand": return Prefix + side + "Hand";
                case "UpperLeg": return Prefix + side + "Thigh";
                case "LowerLeg": return Prefix + side + "Calf";
                case "Foot": return Prefix + side + "Foot";
                case "Toes": return Prefix + side + "Toe0";
            }
            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            string[] phalanges = { "Proximal", "Intermediate", "Distal" };
            for (int f = 0; f < fingers.Length; f++)
                for (int p = 0; p < phalanges.Length; p++)
                    if (part == fingers[f] + phalanges[p])
                        return Prefix + side + "Finger" + f + (p == 0 ? "" : p.ToString(CultureInfo.InvariantCulture));
            return null;
        }

        // Source frame (as in Valve's player skeleton): Z up, the character faces -Y, its left is +X. Units are inches.
        private static double[] ToSource(float[] gltf, double units) => new[] { gltf[0] * units, -gltf[2] * units, gltf[1] * units };
        private static double[] DirectionToSource(float[] gltf) => new double[] { gltf[0], -gltf[2], gltf[1] };
        // Valve direction (Source frame) to Roblox render space.
        private static double[] SourceToRender(double[] s) => new[] { -s[0], s[2], s[1] };

        private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string V(double[] v) => F(v[0]) + " " + F(v[1]) + " " + F(v[2]);

        public static string Export(ExportScene scene, IReadOnlyDictionary<string, string> textures, IReadOnlyDictionary<string, MaterialMaps> maps,
            string folder, string name, GarrysModInstall install, IProgress<string> progress, CancellationToken cancellation, string playerName = null,
            string itemLabel = null, string itemType = null, ISet<string> gearGroups = null)
        {
            WavefrontModel model = scene.Model;
            // Held gear goes in its own optional bodygroup instead of the body (and the first-person arms).
            bool IsBody(ObjTriangle triangle) => gearGroups == null || !gearGroups.Contains(triangle.Group);
            AvatarRig rig = scene.Rig;
            ValvePlayer valve = rig != null ? install.Player : null;
            bool player = rig != null;
            string id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]", "_");

            if (rig != null)
            {
                PoseLikeValve(model, rig, valve);
                BakeDefaultExpression(model, scene.Face);
            }
            // The avatar's eyes go where Valve's are, so GMod's camera and eye attachment line up; props stay real-size. The
            // export scale then shrinks either.
            double units = 1 / 0.0254;
            if (rig != null)
            {
                ValvePlayer.Bone head = valve.Find(Prefix + "Head1");
                ValvePlayer.Attachment eyes = valve.Attachments.FirstOrDefault(a => a.Name == "eyes");
                double valveEyes = head.WorldPosition[2] + (eyes != null ? Matrix.Apply(head.WorldRotation, eyes.Position)[2] : 2);
                float[] left = scene.Point(rig.Bones[AvatarRig.LeftEye].Position), right = scene.Point(rig.Bones[AvatarRig.RightEye].Position);
                double ours = (left[1] + right[1]) / 2;
                if (ours > 0.1 * scene.Scale)
                    units = valveEyes / ours * scene.Scale;
            }

            List<Bone> bones = rig != null ? BuildSkeleton(scene, rig, valve, units) : new List<Bone>
            {
                new Bone { Name = "root", Position = new double[3], Rotation = Matrix.Identity }
            };
            int[] boneOf = rig != null ? RigToSource(bones) : null;

            string src = Path.Combine(folder, "src");
            Directory.CreateDirectory(Path.Combine(src, "anims"));
            string materialDir = "models/roporter/" + id + "/";
            var materialNames = WriteMaterials(model, textures, maps, folder, materialDir, cancellation);

            progress?.Report("Writing Source model (" + model.Triangles.Count + " triangles, " + bones.Count + " bones)...");
            File.WriteAllText(Path.Combine(src, "reference.smd"), Smd(bones, model, scene, units, boneOf, materialNames, IsBody, cancellation), Encoding.ASCII);
            bool hasGear = player && model.Triangles.Any(triangle => !IsBody(triangle));
            if (hasGear)
                File.WriteAllText(Path.Combine(src, "gear.smd"), Smd(bones, model, scene, units, boneOf, materialNames, t => !IsBody(t), cancellation), Encoding.ASCII);
            List<string> flexes = player ? WriteFlexes(Path.Combine(src, "face.vta"), bones, model, scene, units, IsBody, cancellation) : null;
            if (flexes != null)
                progress?.Report("Face: " + flexes.Count + " flexes for posing, with blinking" + (flexes.Contains(JawDropFlex) ? " and voice chat mouth movement." : "."));
            File.WriteAllText(Path.Combine(src, "physics.smd"), PhysicsSmd(bones, model, scene, units, boneOf, rig, valve), Encoding.ASCII);
            if (player)
            {
                File.WriteAllText(Path.Combine(src, "anims", "proportions.smd"), SkeletonSmd(bones, bones.Select(b => Local(b, bones)).ToList()), Encoding.ASCII);
                File.WriteAllText(Path.Combine(src, "anims", "valve_reference.smd"), SkeletonSmd(bones, bones.Select(b =>
                {
                    ValvePlayer.Bone v = valve.Find(b.Name);
                    return v != null ? (v.LocalPosition, v.LocalRotation) : Local(b, bones);
                }).ToList()), Encoding.ASCII);
            }

            string modelName = player ? "player/roporter/" + id + ".mdl" : "roporter/items/" + id + ".mdl";
            File.WriteAllText(Path.Combine(src, "model.qc"), Qc(modelName, materialDir, bones, rig, valve, player, scene, units, flexes, hasGear), Encoding.ASCII);
            // The addon's (and Workshop item's) title and description: "RoPorter - " and the username for avatars or the
            // catalog name for items.
            string displayName = player && !string.IsNullOrWhiteSpace(playerName) ? playerName.Trim()
                : !player && !string.IsNullOrWhiteSpace(itemLabel) ? itemLabel.Trim() : id;
            string title = "RoPorter - " + displayName;
            // Steam BBCode: a linked heading on the Workshop page.
            const string description = "[url=https://github.com/Physiceye/RoPorter/releases][h2]Made with RoPorter[/h2][/url]";
            File.WriteAllText(Path.Combine(folder, "addon.json"), "{\n\t\"title\": " + Newtonsoft.Json.JsonConvert.ToString(title) +
                ",\n\t\"description\": " + Newtonsoft.Json.JsonConvert.ToString(description) +
                ",\n\t\"type\": \"model\",\n\t\"tags\": [\"fun\"],\n\t\"ignore\": [\"src/*\", \"" + Workshop.IconFile + "\", \"" + Workshop.PreviewFile + "\"]\n}\n",
                new UTF8Encoding(false)); // gmad cannot parse addon.json with a byte order mark

            progress?.Report("Compiling with Garry's Mod's studiomdl...");
            Compile(install, folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
            if (!player)
            {
                WriteSpawnMenu(folder, "item_" + id, string.IsNullOrWhiteSpace(itemLabel) ? name.Replace('_', ' ').Trim() : itemLabel.Trim(),
                    string.IsNullOrWhiteSpace(itemType) ? "Other" : itemType.Trim(), "models/" + modelName);
                return Path.Combine("models", modelName.Replace('/', Path.DirectorySeparatorChar));
            }

            string hands = "models/weapons/c_arms_citizen.mdl";
            string armsSmd = ArmsSmd(bones, model, scene, units, boneOf, valve, materialNames, IsBody, out int armTriangles, cancellation);
            if (armsSmd == null)
                progress?.Report("No arm geometry was found; the playermodel uses the citizen first-person arms.");
            else
            {
                string armsName = "weapons/roporter/c_arms_" + id + ".mdl";
                File.WriteAllText(Path.Combine(src, "arms.smd"), armsSmd, Encoding.ASCII);
                File.WriteAllText(Path.Combine(src, "arms.qc"), "// Generated by RoPorter\n$modelname \"" + armsName + "\"\n$model \"arms\" \"arms.smd\"\n" +
                    "$cdmaterials \"" + materialDir + "\"\n$sequence \"idle\" \"arms.smd\" fps 30\n", Encoding.ASCII);
                progress?.Report("Compiling first-person arms (" + armTriangles + " triangles)...");
                try
                {
                    Compile(install, folder, Path.Combine(src, "arms.qc"), armsName, progress, cancellation);
                    hands = "models/" + armsName;
                }
                catch (InvalidDataException error)
                {
                    progress?.Report("The first-person arms could not be compiled (" + error.Message + "); the citizen arms are used instead.");
                }
            }

            string lua = Path.Combine(folder, "lua", "autorun");
            Directory.CreateDirectory(lua);
            // In game the playermodel is listed by its plain name, without the Workshop title's "RoPorter - ".
            string label = LuaString(displayName), path = LuaString("models/" + modelName);
            File.WriteAllText(Path.Combine(lua, "roporter_" + id + ".lua"),
                "player_manager.AddValidModel( " + label + ", " + path + " )\n" +
                "list.Set( \"PlayerOptionsModel\", " + label + ", " + path + " )\n" +
                "player_manager.AddValidHands( " + label + ", " + LuaString(hands) + ", 0, \"00000000\" )\n", new UTF8Encoding(false));
            // The playermodel is also listed in the spawn menu, where it spawns as a ragdoll.
            WriteSpawnMenu(folder, "avatar_" + id, displayName, "Avatars", "models/" + modelName);
            return Path.Combine("models", modelName.Replace('/', Path.DirectorySeparatorChar));
        }

        // Lists a model in the spawn menu under "RoPorter", in a sub-category: "Avatars", or the Roblox item type ("Full Masks",
        // "Head Accessories"). Every exported addon adds itself to the shared RoPorterCatalog table and installs the same
        // hook, so all of them are grouped together. Items from older exports (RoPorterItems, name only) go under "Other".
        private static void WriteSpawnMenu(string folder, string id, string label, string type, string model)
        {
            // Category icons, shared by every RoPorter addon: the app icon, Accoutrement_icon for item types, Humanoid_icon for avatars.
            string icons = Path.Combine(folder, "materials", "roporter", "spawnmenu");
            Directory.CreateDirectory(icons);
            foreach (var icon in new[] { ("favicon.png", "roporter.png"), ("Accoutrement_icon.png", "catalog.png"), ("Humanoid_icon.png", "avatars.png") })
                using (Stream source = typeof(SourceExporter).Assembly.GetManifestResourceStream("RoPorter." + icon.Item1))
                using (FileStream target = File.Create(Path.Combine(icons, icon.Item2)))
                    (source ?? throw new InvalidDataException("The spawn menu icon " + icon.Item1 + " is missing from RoPorter.")).CopyTo(target);

            string lua = Path.Combine(folder, "lua", "autorun", "client");
            Directory.CreateDirectory(lua);
            File.WriteAllText(Path.Combine(lua, "roporter_" + id + ".lua"),
                "-- Generated by RoPorter\n" +
                "RoPorterCatalog = RoPorterCatalog or {}\n" +
                "RoPorterCatalog[ " + LuaString(model) + " ] = { name = " + LuaString(label) + ", type = " + LuaString(type) + " }\n\n" +
                "hook.Add( \"PopulatePropMenu\", \"RoPorterItems\", function()\n" +
                "\tlocal items = {}\n" +
                "\tfor model, name in pairs( RoPorterItems or {} ) do items[ model ] = { name = name, type = \"Other\" } end\n" +
                "\tfor model, item in pairs( RoPorterCatalog ) do items[ model ] = item end\n" +
                "\tlocal types = {}\n" +
                "\tfor model, item in pairs( items ) do\n" +
                "\t\ttypes[ item.type ] = types[ item.type ] or {}\n" +
                "\t\ttable.insert( types[ item.type ], model )\n" +
                "\tend\n" +
                "\tspawnmenu.AddPropCategory( \"RoPorterItems\", \"RoPorter\", {}, \"roporter/spawnmenu/roporter.png\", 7672767, 0 )\n" +
                "\tfor name, models in SortedPairs( types ) do\n" +
                "\t\ttable.sort( models, function( a, b ) return items[ a ].name:lower() < items[ b ].name:lower() end )\n" +
                "\t\tlocal contents = {}\n" +
                "\t\tfor _, model in ipairs( models ) do contents[ #contents + 1 ] = { type = \"model\", model = model } end\n" +
                "\t\tlocal icon = name == \"Avatars\" and \"roporter/spawnmenu/avatars.png\" or \"roporter/spawnmenu/catalog.png\"\n" +
                "\t\tspawnmenu.AddPropCategory( \"RoPorterItems/\" .. name, name, contents, icon,\n" +
                "\t\t\t7672768 + tonumber( util.CRC( name ) ) % 1000000, 7672767 )\n" +
                "\tend\n" +
                "end )\n", new UTF8Encoding(false));
        }

        // First-person arm radius as a share of Valve's shoulder-to-wrist length (about 2 units on a 23-unit citizen arm).
        private const double ArmRadius = 0.085;

        private static string LuaString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

        // First-person arms: Spine4 with the clavicle, arm, hand and finger bones (like Valve's c_arms models) and the triangles
        // those bones mainly carry. Garry's Mod bonemerges the arms onto each weapon's viewmodel by bone name, so every bone
        // takes the viewmodel's position; each arm is scaled to Valve's arm length so its pieces meet at the viewmodel's joints.
        private static string ArmsSmd(List<Bone> bones, WavefrontModel model, ExportScene scene, double units, int[] boneOf, ValvePlayer valve,
            Dictionary<string, string> materials, Func<ObjTriangle, bool> include, out int triangles, CancellationToken cancellation)
        {
            triangles = 0;
            var armBone = new Regex("^" + Regex.Escape(Prefix) + "([LR])_(Clavicle|UpperArm|Forearm|Hand|Finger[0-9]+)$");
            int spine = bones.FindIndex(b => b.Name == Prefix + "Spine4");
            if (spine < 0)
                return null;
            var keep = new List<int> { spine };
            keep.AddRange(Enumerable.Range(0, bones.Count).Where(i => armBone.IsMatch(bones[i].Name)));
            var map = new Dictionary<int, int>();
            for (int k = 0; k < keep.Count; k++)
                map[keep[k]] = k;

            var pivot = new Dictionary<char, double[]>();
            var scale = new Dictionary<char, double>();
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Ours(string bone) => bones.First(b => b.Name == Prefix + side + "_" + bone).Position;
                double[] Valve(string bone) => valve.Find(Prefix + side + "_" + bone).WorldPosition;
                double ours = Matrix.Length(Matrix.Subtract(Ours("Forearm"), Ours("UpperArm"))) + Matrix.Length(Matrix.Subtract(Ours("Hand"), Ours("Forearm")));
                double theirs = Matrix.Length(Matrix.Subtract(Valve("Forearm"), Valve("UpperArm"))) + Matrix.Length(Matrix.Subtract(Valve("Hand"), Valve("Forearm")));
                pivot[side] = Ours("UpperArm");
                scale[side] = ours > 1e-6 ? theirs / ours : 1;
            }
            double[] Fit(double[] p, char side) => Matrix.Add(pivot[side], Matrix.Scale(Matrix.Subtract(p, pivot[side]), scale[side]));

            // Roblox arms are much thicker for their length than Valve's (a blocky arm is half as wide as it is long), so a
            // uniformly fitted arm fills the bottom of the first-person view. Across the arm, each side is instead scaled to
            // about Valve's thickness: the offset from the upper arm or forearm axis shrinks; arms already that thin keep it.
            double[] Across(double[] p, char side, string part)
            {
                double[] Ours(string bone) => bones.First(b => b.Name == Prefix + side + "_" + bone).Position;
                double[] start = part == "UpperArm" ? Ours("UpperArm") : Ours("Forearm");
                double[] axis = Matrix.Subtract(part == "UpperArm" ? Ours("Forearm") : Ours("Hand"), start);
                double length = Matrix.Length(axis);
                if (length < 1e-6)
                    return new double[3];
                axis = Matrix.Scale(axis, 1 / length);
                double[] offset = Matrix.Subtract(p, start);
                return Matrix.Subtract(offset, Matrix.Scale(axis, Matrix.Dot(offset, axis)));
            }
            // The arm bone a vertex mostly follows: its side and name ("UpperArm", "Forearm", "Hand", "Finger1", ...).
            string PartOf(int bone, out char side)
            {
                Match match = armBone.Match(bones[bone].Name);
                side = match.Success ? match.Groups[1].Value[0] : 'L';
                return match.Success ? match.Groups[2].Value : null;
            }
            // The arm's own thickness: the 90th-percentile distance of upper arm and forearm vertices from their axis.
            var radii = new Dictionary<char, List<double>> { ['L'] = new List<double>(), ['R'] = new List<double>() };
            foreach (int position in model.Triangles.Where(include).SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
            {
                var links = Links(position, scene.Rig, boneOf).Where(l => l.Key != spine && map.ContainsKey(l.Key)).ToList();
                if (links.Sum(l => l.Value) < 0.5)
                    continue;
                string part = PartOf(links.OrderByDescending(l => l.Value).First().Key, out char side);
                if (part == "UpperArm" || part == "Forearm")
                    radii[side].Add(Matrix.Length(Across(ToSource(scene.Point(model.Positions[position]), units), side, part)));
            }
            var thin = new Dictionary<char, double>();
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Valve(string bone) => valve.Find(Prefix + side + "_" + bone).WorldPosition;
                double valveLength = Matrix.Length(Matrix.Subtract(Valve("Forearm"), Valve("UpperArm"))) + Matrix.Length(Matrix.Subtract(Valve("Hand"), Valve("Forearm")));
                List<double> sorted = radii[side].OrderBy(r => r).ToList();
                double radius = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))] : 0;
                thin[side] = radius > 1e-6 ? Math.Min(scale[side], ArmRadius * valveLength / radius) : scale[side];
            }
            // Fitted to Valve's length, then thinned across the upper arm or forearm axis (hands and fingers use the forearm's).
            // Roblox hands reach much further past the wrist than Valve's (13-19 units once fitted, against about 7), so the
            // arm looks long and the hand pokes past the weapon. Whatever lies past the wrist is shortened along the forearm
            // to Valve's hand reach (its wrist to fingertips); the squeeze starts at nothing at the wrist, so there is no seam.
            var wrist = new Dictionary<char, double[]>();
            var forearmAxis = new Dictionary<char, double[]>();
            var shorten = new Dictionary<char, double>();
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Ours(string bone) => bones.First(b => b.Name == Prefix + side + "_" + bone).Position;
                double[] axis = Matrix.Subtract(Ours("Hand"), Ours("Forearm"));
                double axisLength = Matrix.Length(axis);
                forearmAxis[side] = axisLength > 1e-6 ? Matrix.Scale(axis, 1 / axisLength) : new double[] { 0, 0, -1 };
                wrist[side] = Fit(Ours("Hand"), side);

                double[] valveHand = valve.Find(Prefix + side + "_Hand").WorldPosition;
                double[] valveAxis = Matrix.Subtract(valveHand, valve.Find(Prefix + side + "_Forearm").WorldPosition);
                valveAxis = Matrix.Scale(valveAxis, 1 / Math.Max(1e-6, Matrix.Length(valveAxis)));
                // The last finger joints, plus about an inch for the fingertips past them.
                var fingers = valve.Bones.Where(b => b.Name.StartsWith(Prefix + side + "_Finger", StringComparison.Ordinal)).ToList();
                double valveReach = fingers.Count > 0 ? fingers.Max(b => Matrix.Dot(Matrix.Subtract(b.WorldPosition, valveHand), valveAxis)) + 1 : 7;

                var reaches = new List<double>();
                foreach (int position in model.Triangles.Where(include).SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
                {
                    var links = Links(position, scene.Rig, boneOf).Where(l => l.Key != spine && map.ContainsKey(l.Key)).ToList();
                    if (links.Sum(l => l.Value) < 0.5)
                        continue;
                    string part = PartOf(links.OrderByDescending(l => l.Value).First().Key, out char linkSide);
                    if (linkSide == side && part != null && (part == "Hand" || part.StartsWith("Finger", StringComparison.Ordinal)))
                        reaches.Add(Matrix.Dot(Matrix.Subtract(Fit(ToSource(scene.Point(model.Positions[position]), units), side), wrist[side]), forearmAxis[side]));
                }
                // The 95th percentile, so a held item or stray vertex doesn't decide the hand's length.
                List<double> sorted = reaches.OrderBy(r => r).ToList();
                double reach = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))] : 0;
                shorten[side] = reach > valveReach ? valveReach / reach : 1;
            }

            // The thinning is blended by the vertex's skin weights, so it changes smoothly across the elbow and wrist instead of
            // switching axis where neighbouring vertices follow different bones (which sheared the mesh at the joints).
            double[] Shape(double[] p, List<KeyValuePair<int, double>> links)
            {
                PartOf(links.OrderByDescending(l => l.Value).First().Key, out char side);
                double[] result = Fit(p, side);
                double total = links.Sum(l => l.Value);
                foreach (var link in links)
                {
                    string part = PartOf(link.Key, out char linkSide);
                    if (part == null || part == "Clavicle")
                        continue;
                    double[] across = Across(p, linkSide, part == "UpperArm" ? "UpperArm" : "Forearm");
                    result = Matrix.Add(result, Matrix.Scale(across, (thin[linkSide] - scale[linkSide]) * link.Value / total));
                }
                double past = Matrix.Dot(Matrix.Subtract(result, wrist[side]), forearmAxis[side]);
                if (past > 0)
                    result = Matrix.Subtract(result, Matrix.Scale(forearmAxis[side], past * (1 - shorten[side])));
                return result;
            }

            var arms = new List<Bone>();
            foreach (int i in keep)
            {
                Bone b = bones[i];
                int parent = b.Parent;
                while (parent >= 0 && !map.ContainsKey(parent))
                    parent = bones[parent].Parent;
                Match match = armBone.Match(b.Name);
                double[] position = match.Success && match.Groups[2].Value != "Clavicle" ? Fit(b.Position, match.Groups[1].Value[0]) : b.Position;
                arms.Add(new Bone { Name = b.Name, Parent = parent >= 0 ? map[parent] : -1, Position = position, Rotation = b.Rotation });
            }

            var text = new StringBuilder(Nodes(arms)).Append(Skeleton(arms.Select(b => Local(b, arms)).ToList())).Append("triangles\n");
            foreach (ObjTriangle triangle in model.Triangles.Where(include))
            {
                cancellation.ThrowIfCancellationRequested();
                var corners = triangle.Corners.Select(c => Links(c.Position, scene.Rig, boneOf).Where(l => l.Key != spine && map.ContainsKey(l.Key)).ToList()).ToList();
                if (corners.Any(links => links.Sum(l => l.Value) < 0.5))
                    continue;
                triangles++;
                text.Append(materials[triangle.Material]).Append('\n');
                for (int c = 0; c < 3; c++)
                {
                    ObjCorner corner = triangle.Corners[c];
                    var links = corners[c];
                    double total = links.Sum(l => l.Value);
                    double[] p = Shape(ToSource(scene.Point(model.Positions[corner.Position]), units), links);
                    double[] n = DirectionToSource(ExportScene.Direction(model.Normals[corner.Normal]));
                    double length = Matrix.Length(n);
                    n = length > 0 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 };
                    float[] uv = corner.Texture >= 0 ? model.Texcoords[corner.Texture] : new float[2];
                    text.Append(map[links[0].Key]).Append(' ').Append(V(p)).Append(' ').Append(V(n)).Append(' ')
                        .Append(F(uv[0])).Append(' ').Append(F(uv[1])).Append(' ').Append(links.Count);
                    foreach (var link in links)
                        text.Append(' ').Append(map[link.Key]).Append(' ').Append(F(link.Value / total));
                    text.Append('\n');
                }
            }
            return triangles > 0 ? text.Append("end\n").ToString() : null;
        }

        // Valve's reference pose has the arms down and slightly forward. Rotating the Roblox limbs to the same directions
        // lets every Valve animation (and weapon hold) put them where it intends.
        private static void PoseLikeValve(WavefrontModel model, AvatarRig rig, ValvePlayer valve)
        {
            double[] Axis(string bone)
            {
                double[] r = valve.Find(Prefix + bone).WorldRotation;
                return SourceToRender(new[] { r[0], r[3], r[6] });
            }
            var segments = new List<KeyValuePair<int[], double[]>>();
            foreach (string side in new[] { "L_", "R_" })
            {
                bool left = side == "L_";
                segments.Add(new KeyValuePair<int[], double[]>(new[] { left ? AvatarRig.LeftUpperArm : AvatarRig.RightUpperArm, left ? AvatarRig.LeftLowerArm : AvatarRig.RightLowerArm }, Axis(side + "UpperArm")));
                segments.Add(new KeyValuePair<int[], double[]>(new[] { left ? AvatarRig.LeftLowerArm : AvatarRig.RightLowerArm, left ? AvatarRig.LeftHand : AvatarRig.RightHand }, Axis(side + "Forearm")));
                segments.Add(new KeyValuePair<int[], double[]>(new[] { left ? AvatarRig.LeftUpperLeg : AvatarRig.RightUpperLeg, left ? AvatarRig.LeftLowerLeg : AvatarRig.RightLowerLeg }, Axis(side + "Thigh")));
                segments.Add(new KeyValuePair<int[], double[]>(new[] { left ? AvatarRig.LeftLowerLeg : AvatarRig.RightLowerLeg, left ? AvatarRig.LeftFoot : AvatarRig.RightFoot }, Axis(side + "Calf")));
            }
            rig.AlignSegments(model, segments);
        }

        // Source models here have no flex animation, so the mood (the "Default" face) is applied to the mesh.
        private static void BakeDefaultExpression(WavefrontModel model, FaceShapes face)
        {
            FaceShape mood = face?.Shapes.FirstOrDefault(s => s.Name == FaceShapeBuilder.MoodShapeName && s.Weight > 0);
            if (mood == null)
                return;
            foreach (var pair in mood.Deltas)
            {
                float[] p = model.Positions[pair.Key];
                model.Positions[pair.Key] = new[] { p[0] + mood.Weight * pair.Value[0], p[1] + mood.Weight * pair.Value[1], p[2] + mood.Weight * pair.Value[2] };
            }
        }

        private static List<Bone> BuildSkeleton(ExportScene scene, AvatarRig rig, ValvePlayer valve, double units)
        {
            var positions = new Dictionary<string, double[]>(StringComparer.Ordinal);
            for (int i = 0; i < rig.Bones.Count; i++)
            {
                string name = ValveName(i);
                if (name != null)
                    positions[name] = ToSource(scene.Point(rig.Bones[i].Position), units);
            }
            double[] Lerp(double[] a, double[] b, double t) => Matrix.Add(a, Matrix.Scale(Matrix.Subtract(b, a), t));
            double Distance(string a, string b) => Matrix.Length(Matrix.Subtract(valve.Find(Prefix + a).WorldPosition, valve.Find(Prefix + b).WorldPosition));
            // Valve's chest bone sits between Spine1 and Spine4.
            positions[Prefix + "Spine2"] = Lerp(positions[Prefix + "Spine1"], positions[Prefix + "Spine4"], 0.28);
            foreach (string side in new[] { "L", "R" })
            {
                string hand = Prefix + side + "_Hand";
                double valveArm = Distance(side + "_UpperArm", side + "_Forearm") + Distance(side + "_Forearm", side + "_Hand");
                double ourArm = Matrix.Length(Matrix.Subtract(positions[Prefix + side + "_Forearm"], positions[Prefix + side + "_UpperArm"])) +
                    Matrix.Length(Matrix.Subtract(positions[hand], positions[Prefix + side + "_Forearm"]));
                ValvePlayer.Bone attachment = valve.Find("ValveBiped.Anim_Attachment_" + side + "H");
                if (attachment != null)
                    positions[attachment.Name] = Matrix.Add(positions[hand],
                        Matrix.Apply(valve.Find(hand).WorldRotation, Matrix.Scale(attachment.LocalPosition, ourArm / Math.Max(valveArm, 1e-6))));
            }
            ValvePlayer.Bone forward = valve.Find("ValveBiped.forward");
            if (forward != null)
                positions[forward.Name] = Matrix.Add(positions[Prefix + "Head1"], Matrix.Apply(valve.Find(Prefix + "Head1").WorldRotation, Matrix.Scale(forward.LocalPosition, scene.Scale)));

            var bones = new List<Bone>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ValvePlayer.Bone v in valve.Bones)
            {
                if (!positions.TryGetValue(v.Name, out double[] position))
                    continue;
                int parent = -1;
                for (int p = v.Parent; p >= 0 && parent < 0; p = valve.Bones[p].Parent)
                    if (index.TryGetValue(valve.Bones[p].Name, out int found))
                        parent = found;
                index[v.Name] = bones.Count;
                bones.Add(new Bone { Name = v.Name, Parent = parent, Position = position, Rotation = v.WorldRotation });
            }
            return bones;
        }

        // Rig bone -> Source bone. The upper torso follows Spine2 (Valve's chest); eyes and jaw follow the head.
        private static int[] RigToSource(List<Bone> bones)
        {
            int Find(string name) => bones.FindIndex(b => b.Name == name);
            var result = new int[AvatarRig.BoneNames.Length];
            for (int i = 0; i < result.Length; i++)
            {
                string name = i == AvatarRig.Chest ? Prefix + "Spine2" : ValveName(i);
                int found = name != null ? Find(name) : -1;
                result[i] = found >= 0 ? found : Find(Prefix + "Head1");
            }
            return result;
        }

        private static (double[] Position, double[] Rotation) Local(Bone bone, List<Bone> bones)
        {
            if (bone.Parent < 0)
                return (bone.Position, bone.Rotation);
            Bone parent = bones[bone.Parent];
            double[] inverse = Matrix.Transpose(parent.Rotation);
            return (Matrix.Apply(inverse, Matrix.Subtract(bone.Position, parent.Position)), Matrix.Multiply(inverse, bone.Rotation));
        }

        private static string Nodes(List<Bone> bones)
        {
            var text = new StringBuilder("version 1\nnodes\n");
            for (int i = 0; i < bones.Count; i++)
                text.Append(i).Append(" \"").Append(bones[i].Name).Append("\" ").Append(bones[i].Parent).Append('\n');
            return text.Append("end\n").ToString();
        }

        private static string Skeleton(List<(double[] Position, double[] Rotation)> locals)
        {
            var text = new StringBuilder("skeleton\ntime 0\n");
            for (int i = 0; i < locals.Count; i++)
                text.Append(i).Append(' ').Append(V(locals[i].Position)).Append(' ').Append(V(Matrix.ToEuler(locals[i].Rotation))).Append('\n');
            return text.Append("end\n").ToString();
        }

        private static string SkeletonSmd(List<Bone> bones, List<(double[] Position, double[] Rotation)> locals) => Nodes(bones) + Skeleton(locals);

        private static string Smd(List<Bone> bones, WavefrontModel model, ExportScene scene, double units, int[] boneOf,
            Dictionary<string, string> materials, Func<ObjTriangle, bool> include, CancellationToken cancellation)
        {
            var text = new StringBuilder(Nodes(bones)).Append(Skeleton(bones.Select(b => Local(b, bones)).ToList())).Append("triangles\n");
            foreach (ObjTriangle triangle in model.Triangles.Where(include))
            {
                cancellation.ThrowIfCancellationRequested();
                text.Append(materials[triangle.Material]).Append('\n');
                foreach (ObjCorner corner in triangle.Corners)
                {
                    var links = Links(corner.Position, scene.Rig, boneOf);
                    double[] p = ToSource(scene.Point(model.Positions[corner.Position]), units);
                    double[] n = DirectionToSource(ExportScene.Direction(model.Normals[corner.Normal]));
                    double length = Matrix.Length(n);
                    n = length > 0 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 };
                    float[] uv = corner.Texture >= 0 ? model.Texcoords[corner.Texture] : new float[2];
                    text.Append(links[0].Key).Append(' ').Append(V(p)).Append(' ').Append(V(n)).Append(' ')
                        .Append(F(uv[0])).Append(' ').Append(F(uv[1])).Append(' ').Append(links.Count);
                    foreach (var link in links)
                        text.Append(' ').Append(link.Key).Append(' ').Append(F(link.Value));
                    text.Append('\n');
                }
            }
            return text.Append("end\n").ToString();
        }

        // Flex controllers Garry's Mod drives itself: "blink" for automatic blinking, "jaw_drop" for the mouth while a player talks.
        private const string BlinkFlex = "blink", JawDropFlex = "jaw_drop";

        // studiomdl's limit on flex controllers.
        private const int MaxFlexes = 96;

        // Source flexes for a dynamic head with FACS facial animation (face.vta): one vertex-animation frame per FACS control,
        // plus "blink" (both eyes closed) and "jaw_drop" (JawDrop). Frame 0 lists every reference vertex, one per triangle
        // corner in reference.smd's order; studiomdl matches them to the model by position. The mood is already baked into
        // the mesh, and the VRChat shapes are left out. Returns the flex names in frame order (frame = index + 1), or null.
        private static List<string> WriteFlexes(string path, List<Bone> bones, WavefrontModel model, ExportScene scene, double units,
            Func<ObjTriangle, bool> include, CancellationToken cancellation)
        {
            FaceShapes face = scene.Face;
            if (face == null)
                return null;
            var shapes = new List<KeyValuePair<string, Dictionary<int, float[]>>>();
            Dictionary<int, float[]> Find(string name) => face.Shapes.FirstOrDefault(s => s.Name == name && s.Deltas.Count > 0)?.Deltas;
            Dictionary<int, float[]> left = Find("LeftEyeClosed"), right = Find("RightEyeClosed"), jaw = Find("JawDrop");
            if (left != null && right != null)
            {
                var both = new Dictionary<int, float[]>(left);
                foreach (var pair in right)
                    both[pair.Key] = both.TryGetValue(pair.Key, out float[] d) ? new[] { d[0] + pair.Value[0], d[1] + pair.Value[1], d[2] + pair.Value[2] } : pair.Value;
                shapes.Add(new KeyValuePair<string, Dictionary<int, float[]>>(BlinkFlex, both));
            }
            if (jaw != null)
                shapes.Add(new KeyValuePair<string, Dictionary<int, float[]>>(JawDropFlex, jaw));
            foreach (FaceShape shape in face.Shapes)
            {
                if (shape.Name == FaceShapeBuilder.MoodShapeName || shape.Name.StartsWith("vrc.", StringComparison.Ordinal) || shape.Deltas.Count == 0)
                    continue;
                string name = Regex.Replace(shape.Name, "[^A-Za-z0-9_]", "_");
                if (shapes.All(s => s.Key != name))
                    shapes.Add(new KeyValuePair<string, Dictionary<int, float[]>>(name, shape.Deltas));
            }
            if (shapes.Count == 0)
                return null;
            if (shapes.Count > MaxFlexes)
                shapes = shapes.Take(MaxFlexes).ToList();

            var text = new StringBuilder(Nodes(bones)).Append("skeleton\n");
            string pose = Skeleton(bones.Select(b => Local(b, bones)).ToList());
            pose = pose.Substring(pose.IndexOf('\n', pose.IndexOf("time 0", StringComparison.Ordinal)) + 1);
            pose = pose.Substring(0, pose.LastIndexOf("end", StringComparison.Ordinal));
            for (int frame = 0; frame <= shapes.Count; frame++)
                text.Append("time ").Append(frame).Append('\n').Append(pose);
            text.Append("end\nvertexanimation\ntime 0\n");

            var corners = new List<(int Position, double[] Point, double[] Normal)>();
            foreach (ObjTriangle triangle in model.Triangles.Where(include))
                foreach (ObjCorner corner in triangle.Corners)
                {
                    double[] n = DirectionToSource(ExportScene.Direction(model.Normals[corner.Normal]));
                    double length = Matrix.Length(n);
                    corners.Add((corner.Position, ToSource(scene.Point(model.Positions[corner.Position]), units),
                        length > 0 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 }));
                }
            for (int i = 0; i < corners.Count; i++)
                text.Append(i).Append(' ').Append(V(corners[i].Point)).Append(' ').Append(V(corners[i].Normal)).Append('\n');
            for (int s = 0; s < shapes.Count; s++)
            {
                cancellation.ThrowIfCancellationRequested();
                text.Append("time ").Append(s + 1).Append('\n');
                var moved = new Dictionary<int, double[]>();
                for (int i = 0; i < corners.Count; i++)
                {
                    int position = corners[i].Position;
                    if (!shapes[s].Value.TryGetValue(position, out float[] delta))
                        continue;
                    if (!moved.TryGetValue(position, out double[] point))
                    {
                        float[] p = model.Positions[position];
                        moved[position] = point = ToSource(scene.Point(new[] { p[0] + delta[0], p[1] + delta[1], p[2] + delta[2] }), units);
                    }
                    if (Matrix.Length(Matrix.Subtract(point, corners[i].Point)) > 1e-4)
                        text.Append(i).Append(' ').Append(V(point)).Append(' ').Append(V(corners[i].Normal)).Append('\n');
                }
            }
            File.WriteAllText(path, text.Append("end\n").ToString(), Encoding.ASCII);
            return shapes.Select(s => s.Key).ToList();
        }

        // Source vertices take at most three bones.
        private static List<KeyValuePair<int, double>> Links(int position, AvatarRig rig, int[] boneOf)
        {
            if (rig == null)
                return new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(0, 1) };
            var weights = new Dictionary<int, double>();
            for (int k = 0; k < 4; k++)
            {
                float w = rig.Weights[position][k];
                if (w <= 0)
                    continue;
                int bone = boneOf[rig.Joints[position][k]];
                weights[bone] = (weights.TryGetValue(bone, out double existing) ? existing : 0) + w;
            }
            var top = weights.OrderByDescending(pair => pair.Value).Take(3).ToList();
            double total = top.Sum(pair => pair.Value);
            return top.Select(pair => new KeyValuePair<int, double>(pair.Key, pair.Value / total)).ToList();
        }

        // One box per physics bone, fitted in the bone's own frame around the body vertices it mainly carries.
        private static string PhysicsSmd(List<Bone> bones, WavefrontModel model, ExportScene scene, double units, int[] boneOf, AvatarRig rig, ValvePlayer valve)
        {
            var groups = new Dictionary<int, List<double[]>>();
            HashSet<string> body = rig != null ? new HashSet<string>(rig.Parts.Keys, StringComparer.Ordinal) : null;
            var physical = rig != null ? new HashSet<string>(new[] { Prefix + "Pelvis" }.Concat(valve.Constraints.Select(c => c.Bone)), StringComparer.Ordinal) : null;
            foreach (int position in model.Triangles.Where(t => body == null || body.Contains(t.Group)).SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
            {
                int bone = Links(position, rig, boneOf)[0].Key;
                // Fingers, toes and the like ride on the nearest physics bone up the chain.
                while (physical != null && bone >= 0 && !physical.Contains(bones[bone].Name))
                    bone = bones[bone].Parent;
                if (bone < 0)
                    continue;
                if (!groups.TryGetValue(bone, out List<double[]> list))
                    groups[bone] = list = new List<double[]>();
                list.Add(ToSource(scene.Point(model.Positions[position]), units));
            }
            // Every ragdoll bone needs a collision piece, or studiomdl rejects the joint limits of the bones below it ("rotation
            // constraint on bone ... which has no parent"). Bundles often leave whole limbs out of the render, so a bone with
            // too little geometry gets a box spanning it to its ragdoll children instead.
            var padded = new HashSet<int>();
            if (physical != null)
                for (int i = 0; i < bones.Count; i++)
                {
                    if (!physical.Contains(bones[i].Name) || (groups.TryGetValue(i, out List<double[]> list) && list.Count >= 4))
                        continue;
                    var points = list ?? new List<double[]>();
                    points.Add(bones[i].Position);
                    for (int j = 0; j < bones.Count; j++)
                    {
                        int parent = bones[j].Parent;
                        while (parent >= 0 && !physical.Contains(bones[parent].Name))
                            parent = bones[parent].Parent;
                        if (parent == i && physical.Contains(bones[j].Name))
                            points.Add(bones[j].Position);
                    }
                    while (points.Count < 4)
                        points.Add(bones[i].Position);
                    groups[i] = points;
                    padded.Add(i);
                }
            var text = new StringBuilder(Nodes(bones)).Append(Skeleton(bones.Select(b => Local(b, bones)).ToList())).Append("triangles\n");
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (var group in groups.Where(g => g.Value.Count >= 4).OrderBy(g => g.Key))
            {
                Bone bone = bones[group.Key];
                double[] inverse = Matrix.Transpose(bone.Rotation);
                var local = group.Value.Select(p => Matrix.Apply(inverse, Matrix.Subtract(p, bone.Position))).ToList();
                double[] min = { local.Min(p => p[0]), local.Min(p => p[1]), local.Min(p => p[2]) };
                double[] max = { local.Max(p => p[0]), local.Max(p => p[1]), local.Max(p => p[2]) };
                // Boxes built from bone positions alone are a line or a point; give them a limb's thickness.
                double least = padded.Contains(group.Key) ? 3 * scene.Scale : 0.5;
                for (int axis = 0; axis < 3; axis++)
                    if (max[axis] - min[axis] < least)
                    {
                        double grow = padded.Contains(group.Key) ? (least - (max[axis] - min[axis])) / 2 : 0.25;
                        min[axis] -= grow;
                        max[axis] += grow;
                    }
                var corners = Enumerable.Range(0, 8).Select(c => Matrix.Add(bone.Position, Matrix.Apply(bone.Rotation,
                    new[] { (c & 1) != 0 ? max[0] : min[0], (c & 2) != 0 ? max[1] : min[1], (c & 4) != 0 ? max[2] : min[2] }))).ToList();
                foreach (int[] face in faces)
                    foreach (int[] triangle in new[] { new[] { face[0], face[1], face[2] }, new[] { face[0], face[2], face[3] } })
                    {
                        text.Append("phys\n");
                        foreach (int c in triangle)
                            text.Append(group.Key).Append(' ').Append(V(corners[c])).Append(" 0 0 1 0 0 1 ").Append(group.Key).Append(" 1\n");
                    }
            }
            return text.Append("end\n").ToString();
        }

        private static string Qc(string modelName, string materialDir, List<Bone> bones, AvatarRig rig, ValvePlayer valve, bool player,
            ExportScene scene, double units, List<string> flexes = null, bool hasGear = false)
        {
            var qc = new StringBuilder();
            qc.Append("// Generated by RoPorter\n");
            qc.Append("$modelname \"").Append(modelName).Append("\"\n");
            if (flexes == null)
                qc.Append("$model \"body\" \"reference.smd\"\n");
            else
            {
                // Each flex has a controller of the same name (listed by the Face Poser tool) driving it directly. studiomdl
                // needs $model's and flexfile's opening braces on the same line.
                qc.Append("$model \"body\" \"reference.smd\" {\n\tflexfile \"face.vta\" {\n\t\tdefaultflex frame 0\n");
                for (int i = 0; i < flexes.Count; i++)
                    qc.Append("\t\tflex \"").Append(flexes[i]).Append("\" frame ").Append(i + 1).Append('\n');
                qc.Append("\t}\n");
                foreach (string flex in flexes)
                    qc.Append("\tflexcontroller ").Append(flex == BlinkFlex ? "eyes" : flex == JawDropFlex ? "phoneme" : "face")
                        .Append(" range 0 1 \"").Append(flex).Append("\"\n");
                foreach (string flex in flexes)
                    qc.Append("\t%").Append(flex).Append(" = ").Append(flex).Append('\n');
                qc.Append("}\n");
            }
            // Held gear: an optional part, hidden by default.
            if (hasGear)
                qc.Append("$bodygroup \"gear\"\n{\n\tblank\n\tstudio \"gear.smd\"\n}\n");
            qc.Append("$cdmaterials \"").Append(materialDir).Append("\"\n");
            qc.Append("$surfaceprop \"").Append(rig != null ? "flesh" : "plastic").Append("\"\n");
            qc.Append("$contents \"solid\"\n");
            if (rig == null)
            {
                qc.Append("$sequence \"idle\" \"reference.smd\" fps 30\n");
                qc.Append("$collisionmodel \"physics.smd\"\n{\n\t$mass 10\n}\n");
                return qc.ToString();
            }

            var present = new HashSet<string>(bones.Select(b => b.Name), StringComparer.Ordinal);
            Bone Get(string name) => bones.First(b => b.Name == name);
            foreach (ValvePlayer.Attachment attachment in valve.Attachments.Where(a => present.Contains(a.Bone)))
            {
                double[] position = Matrix.Scale(attachment.Position, scene.Scale);
                int eye = attachment.Name == "lefteye" ? AvatarRig.LeftEye : attachment.Name == "righteye" ? AvatarRig.RightEye : -1;
                if (attachment.Name == "eyes" || eye >= 0)
                {
                    Bone bone = Get(attachment.Bone);
                    double[] point = eye >= 0 ? ToSource(scene.Point(rig.Bones[eye].Position), units)
                        : Matrix.Scale(Matrix.Add(ToSource(scene.Point(rig.Bones[AvatarRig.LeftEye].Position), units),
                            ToSource(scene.Point(rig.Bones[AvatarRig.RightEye].Position), units)), 0.5);
                    position = Matrix.Apply(Matrix.Transpose(bone.Rotation), Matrix.Subtract(point, bone.Position));
                }
                // Matrix.ToEuler returns (roll, pitch, yaw), handling gimbal lock; QC "rotate" takes pitch yaw roll.
                double[] euler = Matrix.ToEuler(attachment.Rotation);
                double degrees = 180 / Math.PI;
                qc.Append("$attachment \"").Append(attachment.Name).Append("\" \"").Append(attachment.Bone).Append("\" ").Append(V(position))
                    .Append(" rotate ").Append(V(new[] { euler[1] * degrees, euler[2] * degrees, euler[0] * degrees })).Append('\n');
            }
            if (player)
            {
                foreach (ValvePlayer.IkChain chain in valve.IkChains.Where(c => present.Contains(c.Bone)))
                    qc.Append("$ikchain \"").Append(chain.Name).Append("\" \"").Append(chain.Bone).Append("\" knee ").Append(V(chain.Knee)).Append('\n');
                // Proportions trick: the difference between this skeleton and Valve's is applied under every animation.
                qc.Append("$sequence \"roporter_valve_reference\" \"anims/valve_reference.smd\" fps 1\n");
                qc.Append("$animation \"a_roporter_proportions\" \"anims/proportions.smd\" fps 1 subtract \"roporter_valve_reference\" 0\n");
                qc.Append("$sequence \"roporter_proportions\" \"a_roporter_proportions\" predelta autoplay\n");
            }
            qc.Append("$sequence \"ragdoll\" \"reference.smd\" activity \"ACT_DIERAGDOLL\" 1 fps 30\n");
            if (player)
                qc.Append("$includemodel \"m_anm.mdl\"\n");
            qc.Append("$collisionjoints \"physics.smd\"\n{\n");
            qc.Append("\t$mass ").Append(F(valve.TotalMass)).Append('\n');
            qc.Append("\t$inertia ").Append(F(valve.Inertia)).Append('\n');
            qc.Append("\t$damping ").Append(F(valve.Damping)).Append('\n');
            qc.Append("\t$rotdamping ").Append(F(valve.RotationalDamping)).Append('\n');
            qc.Append("\t$rootbone \"").Append(Prefix).Append("Pelvis\"\n");
            foreach (ValvePlayer.Constraint constraint in valve.Constraints.Where(c => present.Contains(c.Bone)))
            {
                string[] axes = { "x", "y", "z" };
                for (int axis = 0; axis < 3; axis++)
                    qc.Append("\t$jointconstrain \"").Append(constraint.Bone).Append("\" ").Append(axes[axis]).Append(" limit ")
                        .Append(F(constraint.Min[axis])).Append(' ').Append(F(constraint.Max[axis])).Append(' ').Append(F(constraint.Friction[axis])).Append('\n');
            }
            return qc.Append("}\n").ToString();
        }

        // VMT + VTF per material: colour, Source-convention normal map, and emission as a self-illumination mask.
        private static Dictionary<string, string> WriteMaterials(WavefrontModel model, IReadOnlyDictionary<string, string> textures,
            IReadOnlyDictionary<string, MaterialMaps> maps, string root, string materialDir, CancellationToken cancellation)
        {
            string directory = Path.Combine(root, "materials", materialDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            string Full(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (ObjMaterial material in model.Triangles.Select(t => model.Materials[t.Material]).Distinct())
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Regex.Replace(material.Name.ToLowerInvariant(), "[^a-z0-9_]", "_");
                for (int n = 2; !used.Add(name); n++)
                    name = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') + n;
                names[material.Name] = name;
                bool alpha;
                if (material.Texture != null && textures != null && textures.TryGetValue(material.Name, out string color))
                    alpha = VtfWriter.Write(Full(color), Path.Combine(directory, name + ".vtf"), VtfWriter.Kind.Color);
                else
                {
                    VtfWriter.WriteSolid(Path.Combine(directory, name + ".vtf"), material.Diffuse, material.Opacity);
                    alpha = material.Opacity < 1;
                }
                var vmt = new StringBuilder("\"VertexLitGeneric\"\n{\n");
                vmt.Append("\t\"$basetexture\" \"").Append(materialDir).Append(name).Append("\"\n");
                MaterialMaps extra = null;
                maps?.TryGetValue(material.Name, out extra);
                if (extra?.Normal != null)
                {
                    VtfWriter.Write(Full(extra.Normal), Path.Combine(directory, name + "_n.vtf"), VtfWriter.Kind.Normal);
                    vmt.Append("\t\"$bumpmap\" \"").Append(materialDir).Append(name).Append("_n\"\n");
                }
                if (extra?.Emissive != null)
                {
                    VtfWriter.Write(Full(extra.Emissive), Path.Combine(directory, name + "_e.vtf"), VtfWriter.Kind.Mask);
                    vmt.Append("\t\"$selfillum\" \"1\"\n\t\"$selfillummask\" \"").Append(materialDir).Append(name).Append("_e\"\n");
                }
                if (alpha)
                {
                    string mode = material.Texture != null && textures != null && textures.TryGetValue(material.Name, out string path)
                        ? GltfWriter.AlphaMode(Full(path)) : "BLEND";
                    vmt.Append(mode == "MASK" ? "\t\"$alphatest\" \"1\"\n" : "\t\"$translucent\" \"1\"\n");
                    // Cards such as hair and fringes are usually meant to be seen from both sides.
                    vmt.Append("\t\"$nocull\" \"1\"\n");
                }
                vmt.Append("}\n");
                File.WriteAllText(Path.Combine(directory, name + ".vmt"), vmt.ToString(), Encoding.ASCII);
            }
            return names;
        }

        // Compiles inside the export folder: a temporary gameinfo.txt mounts Garry's Mod (for m_anm.mdl) so studiomdl
        // writes the model into the folder's own models/ directory.
        private static void Compile(GarrysModInstall install, string folder, string qc, string modelName, IProgress<string> progress, CancellationToken cancellation)
        {
            string gameInfo = Path.Combine(folder, "gameinfo.txt");
            File.WriteAllText(gameInfo, "\"GameInfo\"\n{\n\tgame \"RoPorter\"\n\ttype multiplayer_only\n\tFileSystem\n\t{\n\t\tSteamAppId 4000\n\t\tSearchPaths\n\t\t{\n" +
                "\t\t\tgame+mod |gameinfo_path|.\n" +
                "\t\t\tgame \"" + Path.Combine(install.Content, "garrysmod.vpk") + "\"\n" +
                "\t\t\tgame \"" + install.Content + "\"\n\t\t}\n\t}\n}\n");
            try
            {
                var start = new ProcessStartInfo(install.StudioMdl, "-nop4 -nox360 -game \"" + folder + "\" \"" + qc + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(qc)
                };
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
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(qc), Path.GetFileNameWithoutExtension(qc) + "_studiomdl.log"), output.ToString());
                    string mdl = Path.Combine(folder, "models", modelName.Replace('/', Path.DirectorySeparatorChar));
                    string log = output.ToString();
                    if (process.ExitCode != 0 || !File.Exists(mdl) || Regex.IsMatch(log, @"^\s*ERROR", RegexOptions.Multiline))
                    {
                        var errors = log.Split('\n').Select(l => l.Trim())
                            .Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) && l.IndexOf("Aborted Processing", StringComparison.OrdinalIgnoreCase) < 0)
                            .Distinct().Take(5).ToList();
                        string error = errors.Count > 0 ? string.Join(Environment.NewLine, errors)
                            : log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? "exit code " + process.ExitCode;
                        throw new InvalidDataException("studiomdl could not compile the model:" + Environment.NewLine + error);
                    }
                    foreach (string warning in log.Split('\n').Select(l => l.Trim())
                        .Where(l => l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) && l.IndexOf("mount.cfg", StringComparison.OrdinalIgnoreCase) < 0)
                        .Distinct().Take(5))
                        progress?.Report("studiomdl " + warning);
                }
            }
            finally
            {
                File.Delete(gameInfo);
            }
        }
    }
}
