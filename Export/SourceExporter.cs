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

        // The Workshop page text of every addon: Steam BBCode, a linked heading.
        internal const string WorkshopDescription = "[url=https://github.com/Physiceye/RoPorter/releases][h2]Made with RoPorter[/h2][/url]";

        public static string Export(ExportScene scene, IReadOnlyDictionary<string, string> textures, IReadOnlyDictionary<string, MaterialMaps> maps,
            string folder, string name, GarrysModInstall install, IProgress<string> progress, CancellationToken cancellation, string playerName = null,
            string itemLabel = null, string itemType = null, ISet<string> gearGroups = null, SfmInstall sfm = null, string playerCategory = null,
            IReadOnlyDictionary<string, string> accessoryGroups = null, L4d2Install l4d2 = null, L4d2Survivor survivor = null,
            byte[] headshot = null, byte[] bodyShot = null, Tf2Install tf2 = null, Tf2Class tf2Class = null, bool hands = true, bool npcs = false)
        {
            // Source Filmmaker gets the same model without the Garry's Mod parts (its player animations, first-person arms,
            // Lua, spawn menu and addon.json), compiled with SFM's studiomdl. Its models and materials folders go into SFM's
            // usermod folder. Left 4 Dead 2 gets it in place of a survivor, on that survivor's skeleton and animations, packed
            // into an add-on .vpk; Team Fortress 2 in place of a class, the same way, packed for its custom folder.
            WavefrontModel model = scene.Model;
            AvatarRig rig = scene.Rig;
            // An avatar replaces a survivor or class; anything else (a mesh, model or catalog item) becomes a static prop for the
            // game's maps.
            if (l4d2 != null && rig != null && survivor == null)
                throw new InvalidDataException("A Left 4 Dead 2 export replaces a survivor: choose the survivor.");
            if (tf2 != null && rig != null && tf2Class == null)
                throw new InvalidDataException("A Team Fortress 2 export replaces a class: choose the class.");
            ValvePlayer valve = rig != null ? l4d2 != null ? l4d2.Survivor(survivor) : tf2 != null ? tf2.Player(tf2Class) : install.Player : null;
            bool player = rig != null;
            bool replacement = (l4d2 != null || tf2 != null) && player;
            // Held gear goes in its own optional bodygroup instead of the body (and the first-person arms), and every other worn
            // accessory gets its own bodygroup, shown by default, so players can toggle it. Accessories sharing a name (one
            // outfit's pieces) toggle as one. A Source model holds at most 32 body parts; accessories past that stay in the body.
            var accessorySmds = new List<(string Name, string File, HashSet<string> Groups)>();
            if (player && accessoryGroups != null)
                foreach (var item in accessoryGroups.Where(pair => gearGroups == null || !gearGroups.Contains(pair.Key))
                    .GroupBy(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal).Take(30))
                {
                    var groups = new HashSet<string>(item, StringComparer.Ordinal);
                    if (model.Triangles.Any(triangle => groups.Contains(triangle.Group)))
                        accessorySmds.Add((item.Key, "accessory_" + (accessorySmds.Count + 1) + ".smd", groups));
                }
            var separate = new HashSet<string>(accessorySmds.SelectMany(a => a.Groups), StringComparer.Ordinal);
            bool IsBody(ObjTriangle triangle) => (gearGroups == null || !gearGroups.Contains(triangle.Group)) && !separate.Contains(triangle.Group);
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

            List<Bone> bones = rig != null ? BuildSkeleton(scene, rig, valve, units, replacement) : new List<Bone>
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
            bool hasGear = player && gearGroups != null && model.Triangles.Any(triangle => gearGroups.Contains(triangle.Group));
            if (hasGear)
                File.WriteAllText(Path.Combine(src, "gear.smd"), Smd(bones, model, scene, units, boneOf, materialNames, t => gearGroups.Contains(t.Group), cancellation), Encoding.ASCII);
            foreach (var accessory in accessorySmds)
                File.WriteAllText(Path.Combine(src, accessory.File),
                    Smd(bones, model, scene, units, boneOf, materialNames, triangle => accessory.Groups.Contains(triangle.Group), cancellation), Encoding.ASCII);
            if (accessorySmds.Count > 0)
                progress?.Report("Bodygroups you can toggle: " + string.Join(", ", accessorySmds.Select(a => a.Name)) + ".");
            List<string> flexes = player ? WriteFlexes(Path.Combine(src, "face.vta"), bones, model, scene, units, IsBody, cancellation) : null;
            if (flexes != null)
                progress?.Report("Face: " + flexes.Count + " flexes for posing, with blinking" + (flexes.Contains(JawDropFlex) ? " and voice chat mouth movement." : "."));
            else if (player)
                progress?.Report("Face: no flexes, so Face Poser has nothing to move. Only a dynamic head with facial animation can be posed; " +
                    "a classic face is a flat picture on the head.");
            File.WriteAllText(Path.Combine(src, "physics.smd"), PhysicsSmd(bones, model, scene, units, boneOf, rig, valve), Encoding.ASCII);
            if (player && sfm == null)
            {
                // The game's animations are in its model file's own frame, so the avatar's pose is too: only the root bones'
                // transforms depend on the frame (every other bone's is relative to its parent).
                double[] back = Matrix.Transpose(valve.Frame);
                File.WriteAllText(Path.Combine(src, "anims", "proportions.smd"), SkeletonSmd(bones, bones.Select(b =>
                {
                    var local = Local(b, bones);
                    return b.Parent < 0 ? (Matrix.Apply(back, local.Position), Matrix.Multiply(back, local.Rotation)) : local;
                }).ToList()), Encoding.ASCII);
                File.WriteAllText(Path.Combine(src, "anims", "valve_reference.smd"), SkeletonSmd(bones, bones.Select(b =>
                {
                    ValvePlayer.Bone v = valve.Find(b.Name);
                    return v != null ? (v.LocalPosition, v.LocalRotation) : Local(b, bones);
                }).ToList()), Encoding.ASCII);
            }

            string modelName = l4d2 != null && player ? survivor.Model : tf2 != null && player ? tf2Class.Model : sfm != null ? (player ? "roporter/" : "roporter/items/") + id + ".mdl"
                : player ? "player/roporter/" + id + ".mdl" : "roporter/items/" + id + ".mdl";
            // Garry's Mod's playermodels take its player animations; a survivor or class takes the same animation models as the original.
            IEnumerable<string> includes = replacement
                ? valve.IncludeModels.Select(p => Regex.Replace(p.Replace('\\', '/'), "^models/", "", RegexOptions.IgnoreCase))
                : sfm == null ? new[] { "m_anm.mdl" } : null;
            // Team Fortress 2's hit groups (the head's takes headshots), sized to the avatar.
            string hitboxes = tf2 != null && player ? Hitboxes(bones, model, scene, units, boneOf, rig, valve) : null;
            // Items for the games' maps are static props (Hammer's prop_static takes only those); Garry's Mod still spawns them
            // as physics props. Source Filmmaker's stay movable.
            string ModelQc(string mdl) => Qc(mdl, materialDir, bones, rig, valve, player, scene, units, flexes, hasGear, sfm == null,
                accessorySmds.Select(a => (a.Name, a.File)).ToList(), includes, hitboxes, staticProp: !player && sfm == null);
            File.WriteAllText(Path.Combine(src, "model.qc"), ModelQc(modelName), Encoding.ASCII);
            if (sfm != null)
            {
                progress?.Report("Compiling with Source Filmmaker's studiomdl...");
                Compile(sfm.StudioMdl, null, "1840", folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
                return Path.Combine("models", modelName.Replace('/', Path.DirectorySeparatorChar));
            }
            // The addon's (and Workshop item's) title: the username for avatars or the catalog name for items.
            string displayName = player && !string.IsNullOrWhiteSpace(playerName) ? playerName.Trim()
                : !player && !string.IsNullOrWhiteSpace(itemLabel) ? itemLabel.Trim() : id;
            // Layered clothing goes in the arms even when it is its own bodygroup: Roblox deletes the body under it, so sleeves
            // and arm warmers left out would leave holes and floating hands in first person.
            bool InArms(ObjTriangle triangle) => IsBody(triangle) || rig.LayeredGroups.Contains(triangle.Group) &&
                (gearGroups == null || !gearGroups.Contains(triangle.Group));
            if ((tf2 != null || l4d2 != null) && !player)
            {
                // A prop for the game's maps: compiled with the game's own studiomdl, its models and materials folders ready to
                // copy into the game.
                string gameName = tf2 != null ? "Team Fortress 2" : "Left 4 Dead 2";
                progress?.Report("Compiling a static prop with " + gameName + "'s studiomdl...");
                if (tf2 != null)
                    Compile(tf2.StudioMdl, tf2.Content.ToList(), "440", folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
                else
                    Compile(l4d2.StudioMdl, l4d2.Content.ToList(), "550", folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
                progress?.Report("Copy the export's models and materials folders into " + (tf2 != null
                    ? "Team Fortress 2's tf\\custom\\" + id + " folder"
                    : "Left 4 Dead 2's left4dead2 folder") + ", then place models/" + modelName + " in Hammer as a prop_static (or prop_physics).");
                return Path.Combine("models", modelName.Replace('/', Path.DirectorySeparatorChar));
            }
            if (tf2 != null)
            {
                RenameBonesIn(src, Tf2Bones.ToTf2);
                List<string> mounts = tf2.Content.ToList();
                progress?.Report("Compiling " + tf2Class.Name + "'s replacement with Team Fortress 2's studiomdl...");
                Compile(tf2.StudioMdl, mounts, "440", folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
                // The class's first-person arms: its viewmodel, which plays the class's first-person animations itself.
                ValvePlayer viewModel = valve.ViewModel;
                int classArmTriangles = 0;
                string classArms = hands ? ArmsSmd(bones, model, scene, units, boneOf, valve, materialNames, InArms, out classArmTriangles, cancellation, viewModel) : null;
                if (classArms == null)
                    progress?.Report((hands ? "No arm geometry was found" : "First-person hands are turned off") + "; the " + tf2Class.Name + "'s own first-person arms stay.");
                else
                {
                    string ArmBone(string bone) => bone == Prefix + "Spine4" ? "root" : Tf2Bones.ToTf2(bone);
                    File.WriteAllText(Path.Combine(src, "arms.smd"), RenameBones(classArms, ArmBone), Encoding.ASCII);
                    var armsQc = new StringBuilder("// Generated by RoPorter\n$modelname \"" + tf2Class.ArmsModel + "\"\n$model \"arms\" \"arms.smd\"\n" +
                        "$cdmaterials \"" + materialDir + "\"\n");
                    foreach (ValvePlayer.Attachment attachment in viewModel.Attachments)
                        armsQc.Append(AttachmentLine(attachment.Name, ArmBone(attachment.Bone), Matrix.Scale(attachment.Position, scene.Scale), attachment.Rotation));
                    armsQc.Append("$sequence \"idle\" \"arms.smd\" fps 30\n");
                    foreach (string include in viewModel.IncludeModels)
                        armsQc.Append("$includemodel \"").Append(Regex.Replace(include.Replace('\\', '/'), "^models/", "", RegexOptions.IgnoreCase)).Append("\"\n");
                    File.WriteAllText(Path.Combine(src, "arms.qc"), armsQc.ToString(), Encoding.ASCII);
                    progress?.Report("Compiling first-person arms (" + classArmTriangles + " triangles)...");
                    try
                    {
                        Compile(tf2.StudioMdl, mounts, "440", folder, Path.Combine(src, "arms.qc"), tf2Class.ArmsModel, progress, cancellation);
                    }
                    catch (InvalidDataException error)
                    {
                        progress?.Report("The first-person arms could not be compiled (" + error.Message + "); the " + tf2Class.Name + "'s own arms stay.");
                    }
                }
                WriteClassIcons(folder, tf2Class, headshot, bodyShot, progress);
                return PackTeamFortress2(tf2, folder, id, tf2Class, progress, cancellation);
            }
            if (l4d2 != null)
            {
                List<string> mounts = l4d2.Content.ToList();
                progress?.Report("Compiling " + survivor.Name + "'s replacement with Left 4 Dead 2's studiomdl...");
                Compile(l4d2.StudioMdl, mounts, "550", folder, Path.Combine(src, "model.qc"), modelName, progress, cancellation);
                if (survivor.HasLightModel)
                {
                    // The same model under the name The Passing and The Sacrifice use.
                    string light = Path.ChangeExtension(modelName, null) + "_light.mdl";
                    File.WriteAllText(Path.Combine(src, "model_light.qc"), ModelQc(light), Encoding.ASCII);
                    Compile(l4d2.StudioMdl, mounts, "550", folder, Path.Combine(src, "model_light.qc"), light, progress, cancellation);
                }
                int survivorArmTriangles = 0;
                string survivorArms = hands ? ArmsSmd(bones, model, scene, units, boneOf, valve, materialNames, InArms, out survivorArmTriangles, cancellation) : null;
                if (survivorArms == null)
                    progress?.Report((hands ? "No arm geometry was found" : "First-person hands are turned off") + "; " + survivor.Name + "'s own first-person arms stay.");
                else
                {
                    File.WriteAllText(Path.Combine(src, "arms.smd"), survivorArms, Encoding.ASCII);
                    File.WriteAllText(Path.Combine(src, "arms.qc"), "// Generated by RoPorter\n$modelname \"" + survivor.ArmsModel + "\"\n$model \"arms\" \"arms.smd\"\n" +
                        "$cdmaterials \"" + materialDir + "\"\n$sequence \"idle\" \"arms.smd\" fps 30\n", Encoding.ASCII);
                    progress?.Report("Compiling first-person arms (" + survivorArmTriangles + " triangles)...");
                    try
                    {
                        Compile(l4d2.StudioMdl, mounts, "550", folder, Path.Combine(src, "arms.qc"), survivor.ArmsModel, progress, cancellation);
                    }
                    catch (InvalidDataException error)
                    {
                        progress?.Report("The first-person arms could not be compiled (" + error.Message + "); " + survivor.Name + "'s own arms stay.");
                    }
                }
                using (SurvivorRenders renders = RenderSurvivor(l4d2, valve, src, accessorySmds.Select(a => a.File), model, textures, materialNames, folder, progress))
                    WriteSurvivorIcons(folder, survivor, headshot, bodyShot, renders, progress);
                return PackLeft4Dead2(l4d2, folder, id, survivor, displayName, progress, cancellation);
            }
            string title = displayName;
            File.WriteAllText(Path.Combine(folder, "addon.json"), "{\n\t\"title\": " + Newtonsoft.Json.JsonConvert.ToString(title) +
                ",\n\t\"description\": " + Newtonsoft.Json.JsonConvert.ToString(WorkshopDescription) +
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

            string handsModel = "models/weapons/c_arms_citizen.mdl";
            int armTriangles = 0;
            string armsSmd = hands ? ArmsSmd(bones, model, scene, units, boneOf, valve, materialNames, InArms, out armTriangles, cancellation) : null;
            if (armsSmd == null)
                progress?.Report((hands ? "No arm geometry was found" : "First-person hands are turned off") + "; the playermodel uses the citizen first-person arms.");
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
                    handsModel = "models/" + armsName;
                }
                catch (InvalidDataException error)
                {
                    progress?.Report("The first-person arms could not be compiled (" + error.Message + "); the citizen arms are used instead.");
                }
            }

            // NPCs can't use the playermodel's player animations, so they get the same model on Half-Life 2's citizen animations.
            string npcModel = null;
            if (npcs)
            {
                string npcName = "roporter/npc/" + id + ".mdl";
                File.WriteAllText(Path.Combine(src, "npc.qc"), Qc(npcName, materialDir, bones, rig, valve, player, scene, units, flexes, hasGear, true,
                    accessorySmds.Select(a => (a.Name, a.File)).ToList(),
                    new[] { "humans/male_shared.mdl", "humans/male_ss.mdl", "humans/male_gestures.mdl", "humans/male_postures.mdl" }), Encoding.ASCII);
                progress?.Report("Compiling the NPC model...");
                try
                {
                    Compile(install, folder, Path.Combine(src, "npc.qc"), npcName, progress, cancellation);
                    npcModel = "models/" + npcName;
                }
                catch (InvalidDataException error)
                {
                    progress?.Report("The NPC model could not be compiled (" + error.Message + "); no NPCs were added.");
                }
            }

            string lua = Path.Combine(folder, "lua", "autorun");
            Directory.CreateDirectory(lua);
            // In game the playermodel is listed by the same name as the Workshop title.
            string label = LuaString(displayName), path = LuaString("models/" + modelName);
            string npcLua = "";
            if (npcModel != null)
            {
                // Friendly and hostile citizens in the spawn menu's NPC tab. Citizen type 4 ("unique") keeps the avatar's model.
                // A hostile one is a citizen that hates players; the hook finds it by its spawn menu name.
                string NpcEntry(string key, string suffix) => "list.Set( \"NPC\", " + LuaString(key) + ", { Name = " + LuaString(displayName + suffix) +
                    ", Class = \"npc_citizen\", Model = " + LuaString(npcModel) + ", Category = \"RoPorter\", Weapons = { \"weapon_smg1\" }," +
                    " KeyValues = { citizentype = 4 } } )\n";
                npcLua = NpcEntry("roporter_" + id, "") + NpcEntry("roporter_hostile_" + id, " (Hostile)") +
                    "if SERVER then\n" +
                    "\thook.Add( \"PlayerSpawnedNPC\", \"RoPorterHostileNPCs\", function( ply, npc )\n" +
                    "\t\tif isstring( npc.NPCName ) and string.StartWith( npc.NPCName, \"roporter_hostile_\" ) then\n" +
                    "\t\t\tnpc:AddRelationship( \"player D_HT 99\" )\n" +
                    "\t\tend\n" +
                    "\tend )\n" +
                    "end\n";
                progress?.Report("NPCs: " + displayName + " (friendly and hostile) are in the spawn menu's NPC tab under RoPorter.");
            }
            File.WriteAllText(Path.Combine(lua, "roporter_" + id + ".lua"),
                "player_manager.AddValidModel( " + label + ", " + path + " )\n" +
                "list.Set( \"PlayerOptionsModel\", " + label + ", " + path + " )\n" +
                "player_manager.AddValidHands( " + label + ", " + LuaString(handsModel) + ", 0, \"00000000\" )\n" + npcLua, new UTF8Encoding(false));
            // The playermodel is also listed in the spawn menu, where it spawns as a ragdoll: under "Avatars", or its own category
            // ("Bundles").
            string category = string.IsNullOrWhiteSpace(playerCategory) ? "Avatars" : playerCategory.Trim();
            WriteSpawnMenu(folder, (category == "Bundles" ? "bundle_" : "avatar_") + id, displayName, category, "models/" + modelName);
            return Path.Combine("models", modelName.Replace('/', Path.DirectorySeparatorChar));
        }

        // Lists a model in the spawn menu under "RoPorter", in a sub-category: "Avatars", "Bundles", or the Roblox item type
        // ("Full Masks", "Head Accessories"). Every exported addon adds itself to the shared RoPorterCatalog table and installs
        // the same hook, so all of them are grouped together. Items from older exports (RoPorterItems, name only) go under "Other".
        private static void WriteSpawnMenu(string folder, string id, string label, string type, string model)
        {
            // Category icons, shared by every RoPorter addon: the app icon, Accoutrement_icon for item types, Humanoid_icon for
            // avatars, Player_icon for bundles, Model_icon for model files.
            string icons = Path.Combine(folder, "materials", "roporter", "spawnmenu");
            Directory.CreateDirectory(icons);
            foreach (var icon in new[] { ("favicon.png", "roporter.png"), ("Accoutrement_icon.png", "catalog.png"), ("Humanoid_icon.png", "avatars.png"),
                ("Player_icon.png", "bundles.png"), ("Model_icon.png", "models.png") })
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
                "\t\tlocal icon = name == \"Avatars\" and \"roporter/spawnmenu/avatars.png\" or name == \"Bundles\" and \"roporter/spawnmenu/bundles.png\"\n" +
                "\t\t\tor name == \"Models\" and \"roporter/spawnmenu/models.png\" or \"roporter/spawnmenu/catalog.png\"\n" +
                "\t\tspawnmenu.AddPropCategory( \"RoPorterItems/\" .. name, name, contents, icon,\n" +
                "\t\t\t7672768 + tonumber( util.CRC( name ) ) % 1000000, 7672767 )\n" +
                "\tend\n" +
                "end )\n", new UTF8Encoding(false));
        }

        // First-person arm radius as a share of Valve's shoulder-to-wrist length (about 2 units on a 23-unit citizen arm).
        private const double ArmRadius = 0.085;

        // R6 blocks in first person: the corner radius as a share of the arm's length. Thicker than ArmRadius so the block still
        // reads as one, but not by much: at 0.18 the two blocks filled most of the view with the fists up.
        private const double BlockArmRadius = 0.11;
        // An R6 first-person block: how far it reaches past the wrist (units), and its length as a share of the forearm's.
        private const double BlockPastWrist = 2, BlockLength = 1.1;

        private static string LuaString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";


        // First-person arms: Spine4 with the clavicle, arm, hand and finger bones (like Valve's c_arms models) and the triangles
        // those bones mainly carry. Garry's Mod bonemerges the arms onto each weapon's viewmodel by bone name, so every bone
        // takes the viewmodel's position; each arm segment is scaled to Valve's so its pieces meet at the viewmodel's joints.
        private static string ArmsSmd(List<Bone> bones, WavefrontModel model, ExportScene scene, double units, int[] boneOf, ValvePlayer valve,
            Dictionary<string, string> materials, Func<ObjTriangle, bool> include, out int triangles, CancellationToken cancellation, ValvePlayer viewModel = null)
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

            // Each segment is fitted to Valve's own: the upper arm about the shoulder to Valve's upper arm, then the forearm, hand
            // and fingers from the fitted elbow to Valve's forearm. Fitting the arm as a whole put a long Roblox forearm past
            // the viewmodel's wrist, so the hands reached further up the screen than Valve's.
            var pivot = new Dictionary<char, double[]>();
            var elbow = new Dictionary<char, double[]>();
            var fittedElbow = new Dictionary<char, double[]>();
            var upperScale = new Dictionary<char, double>();
            var foreScale = new Dictionary<char, double>();
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Ours(string bone) => bones.First(b => b.Name == Prefix + side + "_" + bone).Position;
                double[] Valve(string bone) => valve.Find(Prefix + side + "_" + bone).WorldPosition;
                double ourUpper = Matrix.Length(Matrix.Subtract(Ours("Forearm"), Ours("UpperArm"))), ourFore = Matrix.Length(Matrix.Subtract(Ours("Hand"), Ours("Forearm")));
                double valveUpper = Matrix.Length(Matrix.Subtract(Valve("Forearm"), Valve("UpperArm"))), valveFore = Matrix.Length(Matrix.Subtract(Valve("Hand"), Valve("Forearm")));
                pivot[side] = Ours("UpperArm");
                elbow[side] = Ours("Forearm");
                // A segment with no length (a joint the rig could not place) takes the whole arm's scale.
                double whole = ourUpper + ourFore > 1e-6 ? (valveUpper + valveFore) / (ourUpper + ourFore) : 1;
                upperScale[side] = ourUpper > 1e-6 ? valveUpper / ourUpper : whole;
                foreScale[side] = ourFore > 1e-6 ? valveFore / ourFore : whole;
                fittedElbow[side] = Matrix.Add(pivot[side], Matrix.Scale(Matrix.Subtract(elbow[side], pivot[side]), upperScale[side]));
            }
            bool IsUpper(string part) => part == "UpperArm" || part == "Clavicle";
            double SegmentScale(char side, string part) => IsUpper(part) ? upperScale[side] : foreScale[side];
            // A point carried by a part of the arm (the upper arm, or the forearm and everything past it).
            double[] Fit(double[] p, char side, string part) => IsUpper(part)
                ? Matrix.Add(pivot[side], Matrix.Scale(Matrix.Subtract(p, pivot[side]), upperScale[side]))
                : Matrix.Add(fittedElbow[side], Matrix.Scale(Matrix.Subtract(p, elbow[side]), foreScale[side]));

            // R6 arms are one block (see Straight).
            var wholeArm = new Dictionary<int, char>();
            var wholeSides = new HashSet<char>();
            int Bone(char side, string name) => bones.FindIndex(b => b.Name == Prefix + side + "_" + name);
            foreach (char side in new[] { 'L', 'R' })
                if (scene.Rig.IsWholeLimb(side == 'L' ? BodyPart.LeftLowerArm : BodyPart.RightLowerArm))
                {
                    wholeSides.Add(side);
                    wholeArm[Bone(side, "Hand")] = side;
                    wholeArm[Bone(side, "UpperArm")] = side;
                    wholeArm[Bone(side, "Forearm")] = side;
                }
            List<KeyValuePair<int, double>> RigLinks(int position) =>
                Links(position, scene.Rig, boneOf).Where(l => l.Key != spine && map.ContainsKey(l.Key)).ToList();
            // The R6 side whose block a vertex belongs to (its main weight is on that side's upper arm or hand, the bones the block
            // blends between), or null.
            char? WholeSide(List<KeyValuePair<int, double>> links)
            {
                KeyValuePair<int, double> main = links.OrderByDescending(l => l.Value).FirstOrDefault();
                return wholeArm.TryGetValue(main.Key, out char found) ? found : (char?)null;
            }
            // The direction an R6 block runs, from the shoulder joint to the wrist (unfitted Source space).
            double[] BlockAxis(char side)
            {
                double[] axis = Matrix.Subtract(bones[Bone(side, "Hand")].Position, pivot[side]);
                return Matrix.Scale(axis, 1 / Math.Max(Matrix.Length(axis), 1e-6));
            }
            List<KeyValuePair<int, double>> ArmLinks(int position) => RigLinks(position);

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
                var links = ArmLinks(position);
                if (links.Sum(l => l.Value) < 0.5)
                    continue;
                string part = PartOf(links.OrderByDescending(l => l.Value).First().Key, out char side);
                if (part == "UpperArm" || part == "Forearm")
                    radii[side].Add(Matrix.Length(Across(ToSource(scene.Point(model.Positions[position]), units), side, part)));
            }
            // Per side, the scale across the arm that brings it to Valve's thickness (no thinner than each segment's own scale).
            var thinTarget = new Dictionary<char, double>();
            double Thin(char side, string part) => Math.Min(SegmentScale(side, part), thinTarget[side]);
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Valve(string bone) => valve.Find(Prefix + side + "_" + bone).WorldPosition;
                double valveLength = Matrix.Length(Matrix.Subtract(Valve("Forearm"), Valve("UpperArm"))) + Matrix.Length(Matrix.Subtract(Valve("Hand"), Valve("Forearm")));
                List<double> sorted = radii[side].OrderBy(r => r).ToList();
                double radius = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))] : 0;
                // An R6 block keeps more of its bulk: measured at its corners, Valve's radius would leave a thin stick.
                double target = (wholeSides.Contains(side) ? BlockArmRadius : ArmRadius) * valveLength;
                thinTarget[side] = radius > 1e-6 ? target / radius : double.MaxValue;
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
                wrist[side] = Fit(Ours("Hand"), side, "Hand");

                double[] valveHand = valve.Find(Prefix + side + "_Hand").WorldPosition;
                double[] valveAxis = Matrix.Subtract(valveHand, valve.Find(Prefix + side + "_Forearm").WorldPosition);
                valveAxis = Matrix.Scale(valveAxis, 1 / Math.Max(1e-6, Matrix.Length(valveAxis)));
                // The last finger joints, plus about an inch for the fingertips past them.
                var fingers = valve.Bones.Where(b => b.Name.StartsWith(Prefix + side + "_Finger", StringComparison.Ordinal)).ToList();
                double valveReach = fingers.Count > 0 ? fingers.Max(b => Matrix.Dot(Matrix.Subtract(b.WorldPosition, valveHand), valveAxis)) + 1 : 7;

                var reaches = new List<double>();
                foreach (int position in model.Triangles.Where(include).SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
                {
                    var links = ArmLinks(position);
                    if (links.Sum(l => l.Value) < 0.5)
                        continue;
                    string part = PartOf(links.OrderByDescending(l => l.Value).First().Key, out char linkSide);
                    if (linkSide == side && part != null && (part == "Hand" || part.StartsWith("Finger", StringComparison.Ordinal)))
                        reaches.Add(Matrix.Dot(Matrix.Subtract(Fit(ToSource(scene.Point(model.Positions[position]), units), side, "Hand"), wrist[side]), forearmAxis[side]));
                }
                // The 95th percentile, so a held item or stray vertex doesn't decide the hand's length.
                List<double> sorted = reaches.OrderBy(r => r).ToList();
                double reach = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))] : 0;
                shorten[side] = reach > valveReach ? valveReach / reach : 1;
            }

            var upperAxis = new Dictionary<char, double[]>();
            var shoulderCap = new Dictionary<char, double>();
            foreach (char side in new[] { 'L', 'R' })
            {
                double[] Valve(string bone) => valve.Find(Prefix + side + "_" + bone).WorldPosition;
                double[] axis = Matrix.Subtract(bones.First(b => b.Name == Prefix + side + "_Forearm").Position, pivot[side]);
                double axisLength = Matrix.Length(axis);
                upperAxis[side] = axisLength > 1e-6 ? Matrix.Scale(axis, 1 / axisLength) : new double[] { 0, 0, -1 };
                shoulderCap[side] = ArmRadius * (Matrix.Length(Matrix.Subtract(Valve("Forearm"), Valve("UpperArm"))) + Matrix.Length(Matrix.Subtract(Valve("Hand"), Valve("Forearm"))));
            }

            // The thinning is blended by the vertex's skin weights, so it changes smoothly across the elbow and wrist instead of
            // switching axis where neighbouring vertices follow different bones (which sheared the mesh at the joints).
            double[] Shape(double[] p, List<KeyValuePair<int, double>> links)
            {
                PartOf(links.OrderByDescending(l => l.Value).First().Key, out char side);
                double total = links.Sum(l => l.Value);
                // Each bone's fit, blended by its weight (so the elbow stays smooth where the two segments' scales meet).
                double[] result = new double[3];
                foreach (var link in links)
                {
                    string part = PartOf(link.Key, out char linkSide) ?? "UpperArm";
                    result = Matrix.Add(result, Matrix.Scale(Fit(p, linkSide, part), link.Value / total));
                }
                foreach (var link in links)
                {
                    string part = PartOf(link.Key, out char linkSide);
                    if (part == null || part == "Clavicle")
                        continue;
                    double[] across = Across(p, linkSide, part == "UpperArm" ? "UpperArm" : "Forearm");
                    result = Matrix.Add(result, Matrix.Scale(across, (Thin(linkSide, part) - SegmentScale(linkSide, part)) * link.Value / total));
                }
                double past = Matrix.Dot(Matrix.Subtract(result, wrist[side]), forearmAxis[side]);
                if (past > 0)
                    result = Matrix.Subtract(result, Matrix.Scale(forearmAxis[side], past * (1 - shorten[side])));
                // Roblox's blocky arms overlap at the elbow, the upper arm block reaching about half a forearm past the joint.
                // With the elbow bent in first person, that overlap stuck out past the elbow as a long block. The upper arm is
                // cut back to about the forearm's thickness past the elbow (the forearm reaches back over the joint, so no gap
                // opens), in proportion to how much the vertex follows the upper arm.
                double upperShare = links.Where(l => IsUpper(PartOf(l.Key, out _) ?? "")).Sum(l => l.Value) / total;
                double beyondElbow = Matrix.Dot(Matrix.Subtract(result, fittedElbow[side]), upperAxis[side]);
                if (upperShare > 0 && beyondElbow > shoulderCap[side])
                    result = Matrix.Subtract(result, Matrix.Scale(upperAxis[side], (beyondElbow - shoulderCap[side]) * upperShare));
                return result;
            }

            // An R6 arm is one rigid block in first person too: laid straight along the forearm, its end just past the wrist and
            // the rest running back off screen, at about Valve's thickness and with no elbow.
            var blockEnds = new Dictionary<char, (double Start, double End)>();
            foreach (char side in wholeSides)
            {
                BodyPart arm = side == 'L' ? BodyPart.LeftLowerArm : BodyPart.RightLowerArm;
                double[] shoulder = pivot[side], axis = BlockAxis(side);
                var along = model.Triangles.Where(t => scene.Rig.Parts.TryGetValue(t.Group, out BodyPart part) && part == arm).SelectMany(t => t.Corners)
                    .Select(c => Matrix.Dot(Matrix.Subtract(ToSource(scene.Point(model.Positions[c.Position]), units), shoulder), axis)).ToList();
                blockEnds[side] = along.Count > 0 ? (along.Min(), along.Max()) : (0, 1);
            }
            double[] Straight(double[] p, char side)
            {
                double[] axis = BlockAxis(side), offset = Matrix.Subtract(p, pivot[side]);
                double along = Matrix.Dot(offset, axis);
                double[] across = Matrix.Subtract(offset, Matrix.Scale(axis, along));
                double[] forearm = Matrix.Subtract(wrist[side], fittedElbow[side]);
                double forearmLength = Math.Max(Matrix.Length(forearm), 1e-6);
                forearm = Matrix.Scale(forearm, 1 / forearmLength);
                across = Matrix.Subtract(across, Matrix.Scale(forearm, Matrix.Dot(across, forearm)));
                var (start, end) = blockEnds[side];
                double[] tip = Matrix.Add(wrist[side], Matrix.Scale(forearm, BlockPastWrist));
                double back = (end - along) / Math.Max(end - start, 1e-6) * BlockLength * forearmLength;
                return Matrix.Add(Matrix.Subtract(tip, Matrix.Scale(forearm, back)), Matrix.Scale(across, Thin(side, "Forearm")));
            }

            var arms = new List<Bone>();
            foreach (int i in keep)
            {
                Bone b = bones[i];
                int parent = b.Parent;
                while (parent >= 0 && !map.ContainsKey(parent))
                    parent = bones[parent].Parent;
                Match match = armBone.Match(b.Name);
                double[] position = match.Success && match.Groups[2].Value != "Clavicle" ? Fit(b.Position, match.Groups[1].Value[0], match.Groups[2].Value) : b.Position;
                arms.Add(new Bone { Name = b.Name, Parent = parent >= 0 ? map[parent] : -1, Position = position, Rotation = b.Rotation });
            }
            if (viewModel != null)
                ConformToViewModel(arms, valve, viewModel, scene.Scale);

            var text = new StringBuilder(Nodes(arms)).Append(Skeleton(arms.Select(b => Local(b, arms)).ToList())).Append("triangles\n");
            foreach (ObjTriangle triangle in model.Triangles.Where(include))
            {
                cancellation.ThrowIfCancellationRequested();
                var rigLinks = triangle.Corners.Select(c => RigLinks(c.Position)).ToList();
                if (rigLinks.Any(links => links.Sum(l => l.Value) < 0.5))
                    continue;
                char? block = rigLinks.Select(WholeSide).Distinct().Count() == 1 ? WholeSide(rigLinks[0]) : null;
                triangles++;
                text.Append(materials[triangle.Material]).Append('\n');
                for (int c = 0; c < 3; c++)
                {
                    ObjCorner corner = triangle.Corners[c];
                    double[] point = ToSource(scene.Point(model.Positions[corner.Position]), units);
                    double[] n = DirectionToSource(ExportScene.Direction(model.Normals[corner.Normal]));
                    double length = Matrix.Length(n);
                    n = length > 0 ? Matrix.Scale(n, 1 / length) : new double[] { 0, 0, 1 };
                    float[] uv = corner.Texture >= 0 ? model.Texcoords[corner.Texture] : new float[2];
                    var links = block.HasValue ? new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(Bone(block.Value, "Forearm"), 1) } : rigLinks[c];
                    double total = links.Sum(l => l.Value);
                    double[] p = block.HasValue ? Straight(point, block.Value) : Shape(point, links);
                    text.Append(map[links[0].Key]).Append(' ').Append(V(p)).Append(' ').Append(V(n)).Append(' ')
                        .Append(F(uv[0])).Append(' ').Append(F(uv[1])).Append(' ').Append(links.Count);
                    foreach (var link in links)
                        text.Append(' ').Append(map[link.Key]).Append(' ').Append(F(link.Value / total));
                    text.Append('\n');
                }
            }
            return triangles > 0 ? text.Append("end\n").ToString() : null;
        }

        // Team Fortress 2's first-person arms are animated themselves (weapons bonemerge onto them), on a skeleton in the class's
        // rest pose but whose arm bones point along other axes. Each arm bone takes the arms model's axes (the turn between the
        // two skeletons' frames, relative to the upper spine where the arms' root is, snapped to the nearest axis swap), and the
        // arms model's other bones (weapon and effect bones, fingers the avatar lacks) follow at their offsets from their parents.
        private static void ConformToViewModel(List<Bone> arms, ValvePlayer valve, ValvePlayer viewModel, double scale)
        {
            double[] Relative(ValvePlayer player, string name)
            {
                ValvePlayer.Bone root = player.Find(Prefix + "Spine4"), bone = player.Find(name);
                return root == null || bone == null ? null : Matrix.Multiply(Matrix.Transpose(root.WorldRotation), bone.WorldRotation);
            }
            foreach (Bone bone in arms)
            {
                double[] ours = Relative(valve, bone.Name), theirs = Relative(viewModel, bone.Name);
                if (ours != null && theirs != null)
                    bone.Rotation = Matrix.Multiply(bone.Rotation, SnapAxes(Matrix.Multiply(Matrix.Transpose(ours), theirs)));
            }
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < arms.Count; i++)
                index[arms[i].Name] = i;
            foreach (ValvePlayer.Bone v in viewModel.Bones)
            {
                if (index.ContainsKey(v.Name) || v.Parent < 0 || !index.TryGetValue(viewModel.Bones[v.Parent].Name, out int parent))
                    continue;
                Bone p = arms[parent];
                index[v.Name] = arms.Count;
                arms.Add(new Bone
                {
                    Name = v.Name,
                    Parent = parent,
                    Position = Matrix.Add(p.Position, Matrix.Apply(p.Rotation, Matrix.Scale(v.LocalPosition, scale))),
                    Rotation = Matrix.Multiply(p.Rotation, v.LocalRotation)
                });
            }
        }

        // The signed axis swap nearest a turn, or the turn itself when it is not close to one.
        private static double[] SnapAxes(double[] turn)
        {
            var result = new double[9];
            var used = new bool[3];
            for (int row = 0; row < 3; row++)
            {
                int best = Enumerable.Range(0, 3).OrderByDescending(column => Math.Abs(turn[3 * row + column])).First();
                if (used[best] || Math.Abs(turn[3 * row + best]) < 0.7)
                    return turn;
                used[best] = true;
                result[3 * row + best] = Math.Sign(turn[3 * row + best]);
            }
            return result;
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
        internal static void BakeDefaultExpression(WavefrontModel model, FaceShapes face)
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

        private static List<Bone> BuildSkeleton(ExportScene scene, AvatarRig rig, ValvePlayer valve, double units, bool helpers = false)
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
            // Left 4 Dead 2's animations hold weapons, clips and items with helper bones (weapon_bone, L_weapon_bone, the
            // bandage attachments...); each keeps its offset from its parent. Parents come first, so chains are placed in order.
            if (helpers)
                foreach (ValvePlayer.Bone v in valve.Bones)
                    if (!positions.ContainsKey(v.Name) && v.Parent >= 0 && positions.TryGetValue(valve.Bones[v.Parent].Name, out double[] at))
                        positions[v.Name] = Matrix.Add(at, Matrix.Apply(valve.Bones[v.Parent].WorldRotation, Matrix.Scale(v.LocalPosition, scene.Scale)));

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
            ExportScene scene, double units, List<string> flexes = null, bool hasGear = false, bool proportions = true,
            IReadOnlyList<(string Name, string File)> accessories = null, IEnumerable<string> includeModels = null, string hitboxes = null,
            bool staticProp = false)
        {
            var qc = new StringBuilder();
            qc.Append("// Generated by RoPorter\n");
            qc.Append("$modelname \"").Append(modelName).Append("\"\n");
            if (staticProp)
                qc.Append("$staticprop\n");
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
            // Each worn accessory: its own part, shown by default, with a blank option that hides it.
            foreach (var (name, file) in accessories ?? new List<(string, string)>())
            {
                // The QC is written as ASCII, so curly quotes become straight ones and anything else non-ASCII is dropped.
                string label = Regex.Replace(name.Replace('‘', '\'').Replace('’', '\'').Replace('“', ' ').Replace('”', ' '),
                    "[\"\\\\\\r\\n]|[^\\u0020-\\u007e]", " ").Trim();
                qc.Append("$bodygroup \"").Append(label.Length > 0 ? label : "Accessory")
                    .Append("\"\n{\n\tstudio \"").Append(file).Append("\"\n\tblank\n}\n");
            }
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
                qc.Append(AttachmentLine(attachment.Name, attachment.Bone, position, attachment.Rotation));
            }
            if (player)
            {
                foreach (ValvePlayer.IkChain chain in valve.IkChains.Where(c => present.Contains(c.Bone)))
                    qc.Append("$ikchain \"").Append(chain.Name).Append("\" \"").Append(chain.Bone).Append("\" knee ").Append(V(chain.Knee)).Append('\n');
                // Proportions trick: the difference between this skeleton and Valve's is applied under every animation.
                // Source Filmmaker poses the model itself and has no game animations to adjust.
                if (proportions)
                {
                    qc.Append("$sequence \"roporter_valve_reference\" \"anims/valve_reference.smd\" fps 1\n");
                    qc.Append("$animation \"a_roporter_proportions\" \"anims/proportions.smd\" fps 1 subtract \"roporter_valve_reference\" 0\n");
                    qc.Append("$sequence \"roporter_proportions\" \"a_roporter_proportions\" predelta autoplay\n");
                }
            }
            // The ragdoll's pose is in the animations' frame: the model's own when it is the game's (Garry's Mod), else the avatar's
            // pose written in the class's frame (Team Fortress 2).
            bool ownFrame = valve == null || valve.Frame.SequenceEqual(Matrix.Identity);
            qc.Append("$sequence \"ragdoll\" \"").Append(ownFrame || !proportions ? "reference.smd" : "anims/proportions.smd")
                .Append("\" activity \"ACT_DIERAGDOLL\" 1 fps 30\n");
            if (player)
                foreach (string include in includeModels ?? Enumerable.Empty<string>())
                    qc.Append("$includemodel \"").Append(include).Append("\"\n");
            if (player && hitboxes != null)
            {
                qc.Append(hitboxes);
                // Team Fortress 2: every bone of the class is kept, though the avatar weights none of most of them (fingers, dog
                // tags, the pack): cosmetics and weapons follow the class's bones by name, and studiomdl drops a bone nothing
                // uses, which left items floating.
                foreach (Bone bone in bones.Where(b => valve.Find(b.Name) != null))
                    qc.Append("$bonemerge \"").Append(bone.Name).Append("\"\n");
            }
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

        private static string AttachmentLine(string name, string bone, double[] position, double[] rotation)
        {
            // Matrix.ToEuler returns (roll, pitch, yaw), handling gimbal lock; QC "rotate" takes pitch yaw roll.
            double[] euler = Matrix.ToEuler(rotation);
            double degrees = 180 / Math.PI;
            return "$attachment \"" + name + "\" \"" + bone + "\" " + V(position) +
                " rotate " + V(new[] { euler[1] * degrees, euler[2] * degrees, euler[0] * degrees }) + "\n";
        }

        // Team Fortress 2's hitboxes ($hbox: hit group, bone, box in the bone's frame): one per class hitbox bone, around the body
        // vertices it (or a bone below it without its own box) mainly carries.
        private static string Hitboxes(List<Bone> bones, WavefrontModel model, ExportScene scene, double units, int[] boneOf, AvatarRig rig, ValvePlayer valve)
        {
            var groups = new Dictionary<int, int>();
            foreach (var (name, group) in valve.Hitboxes)
            {
                int bone = bones.FindIndex(b => b.Name == name);
                if (bone >= 0 && !groups.ContainsKey(bone))
                    groups[bone] = group;
            }
            var body = new HashSet<string>(rig.Parts.Keys, StringComparer.Ordinal);
            var points = new Dictionary<int, List<double[]>>();
            foreach (int position in model.Triangles.Where(t => body.Contains(t.Group)).SelectMany(t => t.Corners).Select(c => c.Position).Distinct())
            {
                int bone = Links(position, rig, boneOf)[0].Key;
                while (bone >= 0 && !groups.ContainsKey(bone))
                    bone = bones[bone].Parent;
                if (bone < 0)
                    continue;
                if (!points.TryGetValue(bone, out List<double[]> list))
                    points[bone] = list = new List<double[]>();
                list.Add(ToSource(scene.Point(model.Positions[position]), units));
            }
            if (points.Count == 0)
                return null;
            var text = new StringBuilder("$hboxset \"default\"\n");
            foreach (var pair in points.OrderBy(p => p.Key))
            {
                Bone bone = bones[pair.Key];
                double[] inverse = Matrix.Transpose(bone.Rotation);
                var local = pair.Value.Select(p => Matrix.Apply(inverse, Matrix.Subtract(p, bone.Position))).ToList();
                double[] min = { local.Min(p => p[0]), local.Min(p => p[1]), local.Min(p => p[2]) };
                double[] max = { local.Max(p => p[0]), local.Max(p => p[1]), local.Max(p => p[2]) };
                for (int axis = 0; axis < 3; axis++)
                    if (max[axis] - min[axis] < 1)
                    {
                        min[axis] -= 0.5;
                        max[axis] += 0.5;
                    }
                text.Append("$hbox ").Append(groups[pair.Key]).Append(" \"").Append(bone.Name).Append("\" ").Append(V(min)).Append(' ').Append(V(max)).Append('\n');
            }
            return text.ToString();
        }

        // Renames every quoted ValveBiped bone in the model sources (the .smd, .vta and .qc files under src).
        private static void RenameBonesIn(string src, Func<string, string> rename)
        {
            foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.(smd|vta|qc)$", RegexOptions.IgnoreCase)))
                File.WriteAllText(file, RenameBones(File.ReadAllText(file, Encoding.ASCII), rename), Encoding.ASCII);
        }

        private static string RenameBones(string text, Func<string, string> rename) =>
            Regex.Replace(text, "\"(ValveBiped\\.[^\"]+)\"", match => "\"" + rename(match.Groups[1].Value) + "\"");

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
                    // A see-through part (its material's opacity, such as a glass hat) stays see-through even when its texture's
                    // own alpha is only on or off.
                    vmt.Append(mode == "MASK" && material.Opacity >= 0.99f ? "\t\"$alphatest\" \"1\"\n" : "\t\"$translucent\" \"1\"\n");
                }
                if (material.Opacity < 0.99f)
                    vmt.Append("\t\"$alpha\" \"").Append(F(material.Opacity)).Append("\"\n");
                // Back faces are drawn only for DoubleSided accessories, as in Roblox: outline effects are inside-out shells
                // whose front faces must stay hidden.
                if (material.DoubleSided)
                    vmt.Append("\t\"$nocull\" \"1\"\n");
                vmt.Append("}\n");
                File.WriteAllText(Path.Combine(directory, name + ".vmt"), vmt.ToString(), Encoding.ASCII);
            }
            return names;
        }

        // Compiles inside the export folder: a temporary gameinfo.txt mounts Garry's Mod (for m_anm.mdl) so studiomdl
        // writes the model into the folder's own models/ directory.
        internal static void Compile(GarrysModInstall install, string folder, string qc, string modelName, IProgress<string> progress, CancellationToken cancellation) =>
            Compile(install.StudioMdl, new[] { Path.Combine(install.Content, "garrysmod.vpk"), install.Content }, "4000", folder, qc, modelName, progress, cancellation);

        // The same with any game's studiomdl; mounts (the game's folders and packs, highest priority first) are optional.
        private static void Compile(string studioMdl, IEnumerable<string> mounts, string appId, string folder, string qc, string modelName,
            IProgress<string> progress, CancellationToken cancellation)
        {
            string gameInfo = Path.Combine(folder, "gameinfo.txt");
            string searchPaths = string.Concat((mounts ?? Enumerable.Empty<string>()).Select(mount => "\t\t\tgame \"" + ToolPath(mount) + "\"\n"));
            File.WriteAllText(gameInfo, "\"GameInfo\"\n{\n\tgame \"RoPorter\"\n\ttype multiplayer_only\n\tFileSystem\n\t{\n\t\tSteamAppId " + appId +
                "\n\t\tSearchPaths\n\t\t{\n\t\t\tgame+mod |gameinfo_path|.\n" + searchPaths + "\t\t}\n\t}\n}\n");
            try
            {
                using IDisposable textures = HideTextures(folder);
                ProcessStartInfo start = ToolStart(studioMdl, "-nop4 -nox360 -game \"" + ToolPath(folder) + "\" \"" + ToolPath(qc) + "\"");
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.WorkingDirectory = Path.GetDirectoryName(qc);
                var output = new StringBuilder();
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    SourceExporter.WaitForTool(process, "studiomdl", TimeSpan.FromMinutes(10), cancellation);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(qc), Path.GetFileNameWithoutExtension(qc) + "_studiomdl.log"), output.ToString());
                    string mdl = Path.Combine(folder, "models", modelName.Replace('/', Path.DirectorySeparatorChar));
                    string log = output.ToString();
                    if (process.ExitCode != 0 || !File.Exists(mdl) || Regex.IsMatch(log, @"^\s*ERROR", RegexOptions.Multiline))
                        throw new InvalidDataException("studiomdl could not compile the model:" + Environment.NewLine + ToolFailure(log, studioMdl));
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

        // The avatar posed as the survivor stands (Body, from the waist up for the lobby) and lies incapacitated (Lying, head
        // to the left), rendered from the exported meshes with the survivor's own animations. A picture that cannot be made is
        // null, and the downloaded avatar pictures stand in for it. (The HUD portrait stays the Roblox headshot.)
        private sealed class SurvivorRenders : IDisposable
        {
            public Picture Body, Lying;

            public void Dispose()
            {
                Body?.Dispose();
                Lying?.Dispose();
            }
        }

        private static readonly string[] StandingSequences = { "Idle_Standing_SMG", "Idle_Standing_Rifle", "Idle_Standing_Pistol", "Idle_Standing_Elites" };
        private static readonly string[] IncapSequences = { "Idle_Incap_Elites", "Idle_Incap_Pistol", "Idle_Incap" };

        private static SurvivorRenders RenderSurvivor(L4d2Install l4d2, ValvePlayer valve, string src, IEnumerable<string> accessories, WavefrontModel model,
            IReadOnlyDictionary<string, string> textures, Dictionary<string, string> materialNames, string folder, IProgress<string> progress)
        {
            var renders = new SurvivorRenders();
            try
            {
                string animations = valve.IncludeModels.Select(p => p.Replace('\\', '/'))
                    .FirstOrDefault(p => Path.GetFileName(p).StartsWith("anim_", StringComparison.OrdinalIgnoreCase));
                if (animations == null)
                    return renders;
                byte[] data = l4d2.ReadFile(animations.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? animations : "models/" + animations);
                List<ValvePlayer.Bone> rest = Mdl.Bones(data).ToList();
                PoseRenderer renderer = PoseRenderer.Read(new[] { Path.Combine(src, "reference.smd") }.Concat(accessories.Select(f => Path.Combine(src, f))));
                Dictionary<string, PoseRenderer.Look> looks = Looks(model, textures, materialNames, folder);
                Dictionary<string, (double[] Position, double[] Rotation)> Frame(string[] sequences)
                {
                    foreach (string sequence in sequences)
                    {
                        List<ValvePlayer.Bone> frame = MdlAnimation.Pose(data, sequence, name => l4d2.ReadFile(name.Replace('\\', '/')));
                        if (frame != null)
                            return renderer.Animate(frame, rest);
                    }
                    return null;
                }
                bool AboveWaist(string bone) => !Regex.IsMatch(bone, "Thigh|Calf|Foot|Toe", RegexOptions.IgnoreCase);
                var standing = Frame(StandingSequences);
                if (standing != null)
                    renders.Body = renderer.Render(standing, looks, 18, 4, AboveWaist, 0.06, 256, 256);
                var incap = Frame(IncapSequences);
                if (incap != null)
                    renders.Lying = renderer.Render(incap, looks, -110, 30, null, 0.03, 240, 64, align: 0);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is IndexOutOfRangeException ||
                error is ArgumentException || error is KeyNotFoundException || error is FormatException)
            {
                progress?.Report("The avatar could not be posed for its pictures (" + error.Message + "); they use its Roblox pictures.");
            }
            return renders;
        }

        // Each exported material's texture (or colour), by its name in the meshes.
        private static Dictionary<string, PoseRenderer.Look> Looks(WavefrontModel model, IReadOnlyDictionary<string, string> textures,
            Dictionary<string, string> materialNames, string folder)
        {
            var looks = new Dictionary<string, PoseRenderer.Look>(StringComparer.Ordinal);
            foreach (var pair in materialNames)
            {
                ObjMaterial material = model.Materials[pair.Key];
                var look = new PoseRenderer.Look { Color = material.Diffuse, Opacity = material.Opacity };
                if (material.Texture != null && textures != null && textures.TryGetValue(material.Name, out string relative))
                {
                    string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                        using (Picture picture = Picture.Load(path))
                        {
                            look.Pixels = picture.GetPixels();
                            look.Width = picture.Width;
                            look.Height = picture.Height;
                        }
                }
                looks[pair.Value] = look;
            }
            return looks;
        }

        // The survivor's pictures in the game's style: the HUD portrait (s_panel_<key>, 128x128) from the headshot, and the
        // lobby picture (s_panel_lobby_<key>, the Left 4 Dead 2 cast) or character select picture (select_<name>, the
        // original cast), 256x256, from the full-body render, and the incapacitated portrait (s_panel_<key>_incap, 256x128)
        // with the render lying down. Without a headshot the HUD portrait is the top of the body picture. Renders of the avatar
        // in the survivor's poses replace the downloaded pictures where there are any.
        private static void WriteSurvivorIcons(string folder, L4d2Survivor survivor, byte[] head, byte[] body, SurvivorRenders renders, IProgress<string> progress)
        {
            if (head == null && body == null && renders?.Body == null)
            {
                progress?.Report("No avatar pictures were downloaded; " + survivor.Name + "'s HUD and lobby pictures stay.");
                return;
            }
            string vgui = Path.Combine(folder, "materials", "vgui");
            Directory.CreateDirectory(vgui);
            bool original = survivor.Original;
            void Save(Picture picture, string texture)
            {
                VtfWriter.Write(picture, Path.Combine(vgui, texture + ".vtf"), VtfWriter.Kind.Color, IconNoMip | VtfWriter.FlagNoLod, true);
                File.WriteAllText(Path.Combine(vgui, texture + ".vmt"), "UnlitGeneric\n{\n\t$translucent 1\n\t$basetexture \"vgui/" + texture +
                    "\"\n\t$vertexcolor 1\n\t$vertexalpha 1\n\t$no_fullbright 1\n\t$ignorez 1\n\t$additive 0\n}\n", Encoding.ASCII);
            }

            using (Picture headPicture = head != null ? Picture.Load(head) : null)
            using (Picture bodyPicture = body != null ? Picture.Load(body) : null)
            {
                // HUD portrait: a close-up, mostly grey and contrasty like the cast's, on a scratched backdrop in a worn frame.
                using (var portrait = new Picture(128, 128))
                using (var face = new Picture(128, 128))
                {
                    if (headPicture != null)
                        DrawCropped(face, headPicture, Opaque(headPicture), 4, 4, 120);
                    else
                    {
                        Picture from = bodyPicture ?? renders.Body;
                        (int x, int y, int width, int height) box = Opaque(from);
                        int side = Math.Max(1, Math.Min(box.width, (int)(box.height * 0.32)));
                        DrawCropped(face, from, (box.x + (box.width - side) / 2, box.y, side, side), 4, 4, 120);
                    }
                    Grade(face, 0.8, 1.3);
                    using (Picture backdrop = Resource("L4D.Small.png"))
                        portrait.Draw(backdrop, 0, 0, 128, 128);
                    portrait.Draw(face, 0, 0, 128, 128);
                    using (Picture frame = Resource("L4D.SmallFrame.png"))
                        portrait.Draw(frame, 0, 0, 128, 128);
                    Save(portrait, survivor.Portrait);
                }

                // Lobby (the Left 4 Dead 2 cast: scratched backdrop and worn frame) or character select (the original cast: dark
                // blue) picture: the avatar from the waist up.
                Picture source = bodyPicture ?? headPicture;
                using (var picture = new Picture(256, 256))
                using (var avatar = new Picture(256, 256))
                {
                    int inset = original ? 0 : 6;
                    if (renders?.Body != null)
                        avatar.Draw(renders.Body, inset, inset, 256 - 2 * inset, 256 - 2 * inset);
                    else if (source != null)
                    {
                        (int x, int y, int width, int height) box = Opaque(source);
                        if (bodyPicture != null)
                        {
                            int side = Math.Max(1, Math.Max(box.width, (int)(box.height * 0.62)));
                            box = (box.x + (box.width - side) / 2, box.y - side / 16, side, side);
                        }
                        DrawCropped(avatar, source, box, inset, inset + (bodyPicture != null ? 8 : 0), 256 - 2 * inset);
                    }
                    Grade(avatar, original ? 0.15 : 0.45, 1.1);
                    if (original)
                        picture.FillVertical(unchecked((int)0xFF3E5468), unchecked((int)0xFF0C1218));
                    else
                        using (Picture backdrop = Resource("L4D.Lobby.png"))
                            picture.Draw(backdrop, 0, 0, 256, 256);
                    picture.Draw(avatar, 0, 0, 256, 256);
                    if (!original)
                        using (Picture frame = Resource("L4D.LobbyFrame.png"))
                            picture.Draw(frame, 0, 0, 256, 256);
                    Save(picture, survivor.LobbyPicture);
                }

                // Incapacitated portrait (256x128): the avatar lying on its back, head to the left, at the left of the red
                // splatter above the black bar.
                if (renders?.Lying != null || bodyPicture != null)
                    using (var incap = new Picture(256, 128))
                    {
                        using (Picture backdrop = Resource("L4D.Incap.png"))
                            incap.Draw(backdrop, 0, 0, 256, 128);
                        if (renders?.Lying != null)
                        {
                            Grade(renders.Lying, 0.5, 1.1);
                            incap.Draw(renders.Lying, 8, 0, 240, 64);
                        }
                        else
                        {
                            (int x, int y, int width, int height) box = Opaque(bodyPicture);
                            using (var upright = new Picture(box.width, box.height))
                            {
                                upright.Draw(bodyPicture, 0, 0, box.width, box.height, box.x, box.y, box.width, box.height);
                                using (Picture lying = RotateLeft(upright))
                                {
                                    Grade(lying, 0.5, 1.1);
                                    double scale = Math.Min(190.0 / lying.Width, 70.0 / lying.Height);
                                    int width = Math.Max(1, (int)Math.Round(lying.Width * scale)), height = Math.Max(1, (int)Math.Round(lying.Height * scale));
                                    incap.Draw(lying, 16, 68 - height, width, height);
                                }
                            }
                        }
                        Save(incap, survivor.Portrait + "_incap");
                    }
            }
            progress?.Report(survivor.Name + "'s HUD, incapacitated and " + (original ? "character select" : "lobby") + " pictures show the avatar.");
        }

        private static Picture Resource(string name)
        {
            using (Stream stream = typeof(SourceExporter).Assembly.GetManifestResourceStream("RoPorter." + name)
                ?? throw new InvalidOperationException("RoPorter is missing its " + name + " resource."))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return Picture.Load(memory.ToArray());
            }
        }

        // The picture turned a quarter turn anticlockwise (its top on the left).
        private static Picture RotateLeft(Picture picture)
        {
            int[] pixels = picture.GetPixels(), turned = new int[pixels.Length];
            int width = picture.Width, height = picture.Height;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    turned[(width - 1 - x) * height + y] = pixels[y * width + x];
            var result = new Picture(height, width);
            result.SetPixels(turned);
            return result;
        }

        private const uint IconNoMip = 0x100;

        // The smallest rectangle around a picture's visible pixels (the whole picture when it is all transparent).
        private static (int x, int y, int width, int height) Opaque(Picture picture)
        {
            int[] pixels = picture.GetPixels();
            int left = picture.Width, top = picture.Height, right = -1, bottom = -1;
            for (int y = 0; y < picture.Height; y++)
                for (int x = 0; x < picture.Width; x++)
                    if (((pixels[y * picture.Width + x] >> 24) & 255) > 16)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
            return right < 0 ? (0, 0, picture.Width, picture.Height) : (left, top, right - left + 1, bottom - top + 1);
        }

        // Draws a square around a region of source into a square of the target; parts of it outside the source are left empty.
        private static void DrawCropped(Picture target, Picture source, (int x, int y, int width, int height) region, int x, int y, int size)
        {
            int side = Math.Max(region.width, region.height);
            int left = region.x + (region.width - side) / 2, top = region.y + (region.height - side) / 2;
            double scale = size / (double)side;
            int fromX = Math.Max(0, left), fromY = Math.Max(0, top);
            int toX = Math.Min(source.Width, left + side), toY = Math.Min(source.Height, top + side);
            if (toX <= fromX || toY <= fromY)
                return;
            int drawX = x + (int)Math.Round((fromX - left) * scale), drawY = y + (int)Math.Round((fromY - top) * scale);
            int drawRight = x + (int)Math.Round((toX - left) * scale), drawBottom = y + (int)Math.Round((toY - top) * scale);
            target.Draw(source, drawX, drawY, drawRight - drawX, drawBottom - drawY, fromX, fromY, toX - fromX, toY - fromY);
        }

        // Moves colours toward grey (grey 0-1) and stretches them away from mid grey (contrast above 1).
        private static void Grade(Picture picture, double grey, double contrast)
        {
            int[] pixels = picture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                int p = pixels[i];
                double r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                int Channel(double c) => (int)Math.Max(0, Math.Min(255, Math.Round(((c + (luma - c) * grey) - 128) * contrast + 128)));
                pixels[i] = (p & unchecked((int)0xFF000000)) | (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
            }
            picture.SetPixels(pixels);
        }

        // A Left 4 Dead 2 add-on: the compiled models and materials with an addoninfo.txt, packed by the game's vpk.exe into
        // <name>.vpk in the export folder (for the game's addons folder). The sources stay beside it.
        private static string PackLeft4Dead2(L4d2Install l4d2, string folder, string id, L4d2Survivor survivor, string title,
            IProgress<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string packName = "roporter_" + id + "_" + survivor.Key.ToLowerInvariant();
            string pack = Path.Combine(folder, packName);
            Directory.CreateDirectory(pack);
            foreach (string part in new[] { "models", "materials" })
                if (Directory.Exists(Path.Combine(folder, part)))
                    Directory.Move(Path.Combine(folder, part), Path.Combine(pack, part));
            // KeyValues text: quotes and line breaks would end a value early.
            string Clean(string text) => Regex.Replace(text, "[\"\\r\\n\\t]", " ").Trim();
            File.WriteAllText(Path.Combine(pack, "addoninfo.txt"), "\"AddonInfo\"\n{\n" +
                "\t\"addonSteamAppID\"\t\"550\"\n" +
                "\t\"addontitle\"\t\"" + Clean(title) + " as " + survivor.Name + "\"\n" +
                "\t\"addonversion\"\t\"1.0\"\n" +
                "\t\"addonauthor\"\t\"RoPorter\"\n" +
                "\t\"addonURL0\"\t\"https://github.com/Physiceye/RoPorter\"\n" +
                "\t\"addonDescription\"\t\"" + Clean(title) + "'s Roblox avatar in place of " + survivor.Name + ". Made with RoPorter.\"\n" +
                "\t\"addonContent_Survivor\"\t\"1\"\n}\n", new UTF8Encoding(false));

            progress?.Report("Packing " + packName + ".vpk with Left 4 Dead 2's vpk tool...");
            string vpk = RunVpk(l4d2.VpkTool, folder, pack, cancellation);
            progress?.Report("Copy " + packName + ".vpk into Left 4 Dead 2's left4dead2" + Path.DirectorySeparatorChar + "addons folder; " +
                survivor.Name + " is replaced in every campaign.");
            return Path.GetFileName(vpk);
        }

        // The class's pictures: the HUD's (hud/class_<key>red and class_<key>blue, 256x256: the avatar from the waist up on a
        // clear background) and the class portraits (vgui/class_portraits/<key>, _blue and their _grey copies: a close-up of the
        // head). The game's own .vmt files point at these textures, so only the .vtf files are replaced.
        private static void WriteClassIcons(string folder, Tf2Class tf2Class, byte[] head, byte[] body, IProgress<string> progress)
        {
            if (head == null && body == null)
            {
                progress?.Report("No avatar pictures were downloaded; the " + tf2Class.Name + "'s HUD pictures stay.");
                return;
            }
            string hud = Path.Combine(folder, "materials", "hud"), portraits = Path.Combine(folder, "materials", "vgui", "class_portraits");
            Directory.CreateDirectory(hud);
            Directory.CreateDirectory(portraits);
            void Save(Picture picture, string path) =>
                VtfWriter.Write(picture, path + ".vtf", VtfWriter.Kind.Color, IconNoMip | VtfWriter.FlagNoLod, true);

            using (Picture headPicture = head != null ? Picture.Load(head) : null)
            using (Picture bodyPicture = body != null ? Picture.Load(body) : null)
            {
                Picture source = bodyPicture ?? headPicture;
                using (var bust = new Picture(256, 256))
                {
                    (int x, int y, int width, int height) box = Opaque(source);
                    if (bodyPicture != null)
                    {
                        int side = Math.Max(1, Math.Max(box.width, (int)(box.height * 0.62)));
                        box = (box.x + (box.width - side) / 2, box.y - side / 16, side, side);
                    }
                    DrawCropped(bust, source, box, 0, bodyPicture != null ? 12 : 0, 256);
                    Save(bust, Path.Combine(hud, "class_" + tf2Class.HudKey + "red"));
                    Save(bust, Path.Combine(hud, "class_" + tf2Class.HudKey + "blue"));
                }
                using (var portrait = new Picture(256, 256))
                {
                    if (headPicture != null)
                        DrawCropped(portrait, headPicture, Opaque(headPicture), 0, 0, 256);
                    else
                    {
                        (int x, int y, int width, int height) box = Opaque(bodyPicture);
                        int side = Math.Max(1, Math.Min(box.width, (int)(box.height * 0.32)));
                        DrawCropped(portrait, bodyPicture, (box.x + (box.width - side) / 2, box.y, side, side), 0, 0, 256);
                    }
                    string name = Path.Combine(portraits, tf2Class.PortraitKey);
                    Save(portrait, name);
                    Save(portrait, name + "_blue");
                    Grade(portrait, 1, 1);
                    Save(portrait, name + "_grey");
                    Save(portrait, name + "_blue_grey");
                }
            }
            progress?.Report("The " + tf2Class.Name + "'s HUD pictures and class portraits show the avatar.");
        }

        // A Team Fortress 2 custom pack: the compiled models and materials packed by the game's vpk.exe into <name>.vpk in the
        // export folder (for the game's tf/custom folder). The sources stay beside it.
        private static string PackTeamFortress2(Tf2Install tf2, string folder, string id, Tf2Class tf2Class, IProgress<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string packName = "roporter_" + id + "_" + tf2Class.Key;
            string pack = Path.Combine(folder, packName);
            Directory.CreateDirectory(pack);
            foreach (string part in new[] { "models", "materials" })
                if (Directory.Exists(Path.Combine(folder, part)))
                    Directory.Move(Path.Combine(folder, part), Path.Combine(pack, part));
            progress?.Report("Packing " + packName + ".vpk with Team Fortress 2's vpk tool...");
            string vpk = RunVpk(tf2.VpkTool, folder, pack, cancellation);
            progress?.Report("Copy " + packName + ".vpk into Team Fortress 2's tf" + Path.DirectorySeparatorChar + "custom folder. The " + tf2Class.Name +
                " is replaced on servers that allow custom models (your own, or community servers with sv_pure 0); Valve's matchmaking servers use the original.");
            return Path.GetFileName(vpk);
        }

        // Packs a folder into <folder>.vpk beside it with a game's vpk.exe, then deletes the folder.
        private static string RunVpk(string vpkTool, string workingFolder, string pack, CancellationToken cancellation)
        {
            string vpk = pack + ".vpk";
            if (File.Exists(vpk))
                File.Delete(vpk);
            ProcessStartInfo start = ToolStart(vpkTool, "\"" + ToolPath(pack) + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.WorkingDirectory = workingFolder;
            var output = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                WaitForTool(process, "vpk", TimeSpan.FromMinutes(5), cancellation);
                if (process.ExitCode != 0 || !File.Exists(vpk))
                    throw new InvalidDataException("vpk could not pack the add-on:" + Environment.NewLine + ToolFailure(output.ToString(), vpkTool));
            }
            Directory.Delete(pack, true);
            return vpk;
        }

        // Garry's Mod's studiomdl (from its October 2026 update) crashes with EXCEPTION_ACCESS_VIOLATION whenever it opens a
        // loose .vtf, ours or Valve's, after writing the model. It only needs the .vmt files (their flags), so the folder's
        // textures are renamed out of its way while it runs and put back when this is disposed.
        internal static IDisposable HideTextures(string folder)
        {
            string materials = Path.Combine(folder, "materials");
            var hidden = new List<string>();
            if (Directory.Exists(materials))
                foreach (string texture in Directory.EnumerateFiles(materials, "*.vtf", SearchOption.AllDirectories).ToList())
                {
                    File.Move(texture, texture + HiddenTexture);
                    hidden.Add(texture);
                }
            return new Restore(() =>
            {
                foreach (string texture in hidden)
                    if (File.Exists(texture + HiddenTexture))
                        File.Move(texture + HiddenTexture, texture, true);
            });
        }

        private const string HiddenTexture = ".hidden";

        private sealed class Restore : IDisposable
        {
            private Action action;
            public Restore(Action action) => this.action = action;
            public void Dispose()
            {
                action?.Invoke();
                action = null;
            }
        }

        // The lines of a Valve tool's log that say why it failed: its errors (studiomdl's "ERROR:" lines, and messages such as
        // "Error opening ... (Check for write enable)" or "Unable to find gameinfo.txt" that have no prefix), or else its last lines.
        internal static string ToolFailure(string log, string exe)
        {
            var lines = log.Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.IndexOf("mount.cfg", StringComparison.OrdinalIgnoreCase) < 0 && l.IndexOf("Aborted Processing", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            var errors = lines.Where(l => !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(l, @"error|leak|MAX_MAP|exceeded|can't|cannot|unable|failed", RegexOptions.IgnoreCase)).Distinct().Take(6).ToList();
            string failure = string.Join(Environment.NewLine, errors.Count > 0 ? errors : lines.Skip(Math.Max(0, lines.Count - 6)));
            // Windows' Controlled folder access keeps unknown programs from writing to Documents and the Desktop.
            if (log.IndexOf("Check for write enable", StringComparison.OrdinalIgnoreCase) >= 0)
                failure += Environment.NewLine + "Windows did not let " + Path.GetFileName(exe) + " write to the output folder. " + BlockedFolderHint(exe);
            return failure.Length > 0 ? failure : "it stopped without a message";
        }

        // What to do when Windows refuses writes to the output folder.
        internal static string BlockedFolderHint(string exe) =>
            "If Controlled folder access is on (Windows Security > Virus & threat protection > Ransomware protection), allow " + exe +
            " through it, or choose an output folder outside Documents and the Desktop.";

        // Valve's tools read their command line and file paths in Windows' ANSI code page, so a path with characters outside
        // it (a Japanese folder name on an English Windows) reaches them garbled and they report it missing. Such a path is
        // passed in its 8.3 short form, which is plain ASCII on drives that keep short names. On Linux and macOS the tools run
        // in Wine, which sees the file system as drive Z:.
        internal static string ToolPath(string path)
        {
            if (!OperatingSystem.IsWindows())
                return "Z:" + Path.GetFullPath(path).Replace('/', '\\');
            Encoding ansi = AnsiEncoding.Value;
            bool Readable(string text) => ansi.GetString(ansi.GetBytes(text)) == text;
            if (Readable(path))
                return path;
            var buffer = new StringBuilder(32767);
            int length = GetShortPathName(path, buffer, buffer.Capacity);
            if (length > 0 && length < buffer.Capacity && Readable(buffer.ToString()))
                return buffer.ToString();
            string unreadable = new string(path.Where(c => !Readable(c.ToString())).Distinct().Take(8).ToArray());
            throw new InvalidDataException("Valve's tools cannot read the path " + path + ": it has characters (" + unreadable +
                ") outside the language Windows uses for non-Unicode programs. Choose an output folder whose path has none of them.");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

        // Windows' ANSI code page (the "language for non-Unicode programs"); .NET's default encoding is UTF-8.
        private static readonly Lazy<Encoding> AnsiEncoding = new Lazy<Encoding>(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetACP());
        });

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetACP();

        // Waits for a started compiler, killing it (and anything it started, such as Wine's processes) when the export is cancelled
        // or it runs past the time limit, so a hung tool never leaves the app busy forever.
        internal static void WaitForTool(Process process, string name, TimeSpan limit, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            while (!process.WaitForExit(200))
            {
                bool timedOut = clock.Elapsed > limit;
                if (!timedOut && !cancellation.IsCancellationRequested)
                    continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                }
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException(name + " stopped responding and was closed after " + (int)limit.TotalMinutes + " minutes.");
            }
            process.WaitForExit();
        }

        // How a Valve tool (a Windows program) is started: directly on Windows, in Wine on Linux and macOS.
        internal static ProcessStartInfo ToolStart(string exe, string arguments)
        {
            if (OperatingSystem.IsWindows())
                return new ProcessStartInfo(ToolPath(exe), arguments);
            string wine = new[] { "wine64", "wine" }.Select(FindProgram).FirstOrDefault(found => found != null)
                ?? throw new InvalidDataException(Path.GetFileName(exe) + " is a Windows program: install Wine to run Garry's Mod's tools on " +
                    (OperatingSystem.IsMacOS() ? "macOS" : "Linux") + ".");
            var start = new ProcessStartInfo(wine, "\"" + ToolPath(exe) + "\" " + arguments);
            // Wine's own messages would otherwise fill the tool's log.
            start.Environment["WINEDEBUG"] = "-all";
            return start;
        }

        // A program on the PATH, or null.
        private static string FindProgram(string name) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(folder => folder.Length > 0)
                .Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }
}
