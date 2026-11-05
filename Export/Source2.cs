using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rbx2Source.Export
{
    // An s&box install, for the Citizen skeleton (addons/citizen), which s&box player models are rigged to.
    public sealed class SboxInstall
    {
        public string Folder { get; }
        public string Citizen => Path.Combine(Folder, "addons", "citizen", "Assets", "models", "citizen", "citizen.fbx");

        private SboxInstall(string folder)
        {
            Folder = folder;
        }

        public static SboxInstall Find()
        {
            foreach (string library in GarrysModInstall.SteamLibraries())
            {
                var install = new SboxInstall(Path.Combine(library, "steamapps", "common", "sbox"));
                if (File.Exists(install.Citizen))
                    return install;
            }
            return null;
        }
    }

    // Builds Source 2 content for several games at once: the model as FBX (Y up, centimetres, like Valve's and Facepunch's
    // sources), its textures as PNG, and for each game a ModelDoc model (.vmdl) and materials (.vmat) for that game's shaders.
    // The folder is a content root: copied into a game's addon, ModelDoc opens and compiles that game's .vmdl. Avatars use
    // RoPorter's humanoid skeleton, and for s&box also the Citizen's (read from s&box's files) so its animations play them.
    public static class Source2Exporter
    {
        private sealed class Game
        {
            public string Key, Title, Shader, Header, Folder;
        }

        private static readonly Game[] Games =
        {
            new Game { Key = "sbox", Title = "s&box", Shader = "shaders/complex.shader",
                Header = "format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}", Folder = "your project's Assets folder" },
            new Game { Key = "hla", Title = "Half-Life: Alyx", Shader = "vr_complex.vfx",
                Header = "format:modeldoc28:version{fb63b6ca-f435-4aa0-a2c7-c66ddc651dca}", Folder = "Half-Life Alyx/content/hlvr_addons/<your addon>" },
            new Game { Key = "cs2", Title = "Counter-Strike 2", Shader = "csgo_complex.vfx",
                Header = "format:modeldoc36:version{972dada4-b828-45a4-bb93-7795cf0585da}", Folder = "Counter-Strike Global Offensive/content/csgo_addons/<your addon>" },
        };

        // FBX in centimetres; ModelDoc reads the numbers as inches, so every model scales them by this.
        private const double InchesPerCentimetre = 0.3937;

        private static string F(double v) => v.ToString("0.0####", CultureInfo.InvariantCulture);

        public static string Export(ExportScene scene, Dictionary<string, string> textures, Dictionary<string, MaterialMaps> maps, string folder,
            string name, IProgress<string> progress, CancellationToken cancellation)
        {
            WavefrontModel model = scene.Model;
            string id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]", "_");
            string modelDir = "models/roporter/" + id, materialDir = "materials/roporter/" + id;
            Directory.CreateDirectory(Path.Combine(folder, modelDir));
            Directory.CreateDirectory(Path.Combine(folder, materialDir));
            GameMaterials.Rename(model, textures, maps);
            List<string> materials = model.Triangles.Select(t => t.Material).Distinct().ToList();
            var slots = materials.ToDictionary(m => m, m => WriteTextures(model.Materials[m], textures, maps, folder, materialDir));
            foreach (Game game in Games)
            {
                Directory.CreateDirectory(Path.Combine(folder, materialDir, game.Key));
                foreach (string material in materials)
                    File.WriteAllText(Path.Combine(folder, materialDir, game.Key, material + ".vmat"), Vmat(game, slots[material]), new UTF8Encoding(false));
            }

            var fbx = new FbxWriter.Options { UnitsPerMetre = 100, UnitScaleFactor = 1, Shapes = false, EmbedTextures = false };
            // The materials are the .vmat files; the FBX only names them, so it points at the colour textures.
            string meshFolder = Path.Combine(folder, modelDir);
            var colour = slots.Where(pair => pair.Value.Color != null).ToDictionary(pair => pair.Key,
                pair => Path.GetRelativePath(meshFolder, Path.Combine(folder, pair.Value.Color)).Replace('\\', '/'));
            bool avatar = scene.Rig != null;
            if (avatar)
                SourceExporter.BakeDefaultExpression(model, scene.Face);
            string mesh = modelDir + "/" + id + ".fbx";
            progress?.Report("Writing Source 2 model (" + model.Triangles.Count + " triangles" + (avatar ? ", " + scene.Rig.Bones.Count + " bones" : "") + ")...");
            FbxWriter.Write(new ExportScene(model, scene.Rig, null, scene.Scale), colour, null, Path.Combine(folder, mesh), cancellation, fbx);

            // s&box: the avatar on the Citizen skeleton, with the Citizen's animations.
            string citizenMesh = null;
            SboxInstall sbox = avatar ? SboxInstall.Find() : null;
            if (avatar && sbox != null)
            {
                TargetSkeleton citizen = Citizen(sbox);
                citizen.Pose(model, scene.Rig);
                double fit = citizen.FitScale(new ExportScene(model, scene.Rig, null, 1));
                var fitted = new ExportScene(model, scene.Rig, null, (float)(fit * scene.Scale));
                citizenMesh = modelDir + "/" + id + "_citizen.fbx";
                fbx.Bones = citizen.Fit(fitted);
                fbx.BoneOf = citizen.RigToBone();
                FbxWriter.Write(fitted, colour, null, Path.Combine(folder, citizenMesh), cancellation, fbx);
                progress?.Report("s&box: rigged to the Citizen skeleton (" + fbx.Bones.Count + " bones), using its animations.");
            }
            else if (avatar)
                progress?.Report("s&box was not found, so the s&box model uses RoPorter's skeleton without animations. Install s&box and export " +
                    "again to rig it to the Citizen.");

            foreach (Game game in Games)
            {
                bool citizenModel = game.Key == "sbox" && citizenMesh != null;
                File.WriteAllText(Path.Combine(folder, modelDir, id + "_" + game.Key + ".vmdl"),
                    Vmdl(game, id, citizenModel ? citizenMesh : mesh, materials, materialDir, avatar, citizenModel), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(folder, "README.txt"), Readme(id, modelDir, avatar), new UTF8Encoding(false));
            progress?.Report("Source 2 content for " + string.Join(", ", Games.Select(g => g.Title)) + ": open " + modelDir + "/" + id + "_<game>.vmdl " +
                "in that game's ModelDoc (see README.txt).");
            return Path.Combine(modelDir.Replace('/', Path.DirectorySeparatorChar), id + ".fbx");
        }

        private static TargetSkeleton Citizen(SboxInstall sbox)
        {
            // Citizen's source is already in the scene's frame (Y up, facing +Z), in centimetres.
            TargetSkeleton skeleton = TargetSkeleton.FromFbx(File.ReadAllBytes(sbox.Citizen), Matrix.Identity, 0.01);
            string[] fingers = { "thumb", "index", "middle", "ring" };
            string[] rigFingers = { "Thumb", "Index", "Middle", "Ring" };
            string[] phalanges = { "Proximal", "Intermediate", "Distal" };
            skeleton.MapHumanoid(part =>
            {
                switch (part)
                {
                    case "Hips": return "pelvis";
                    case "Spine": return "spine_0";
                    case "Chest": return "spine_1";
                    case "UpperChest": return "spine_2";
                    case "Neck": return "neck_0";
                    case "Head": return "head";
                }
                return null;
            }, (side, part) =>
            {
                string s = side == "Left" ? "_L" : "_R";
                switch (part)
                {
                    case "Shoulder": return "clavicle" + s;
                    case "UpperArm": return "arm_upper" + s;
                    case "LowerArm": return "arm_lower" + s;
                    case "Hand": return "hand" + s;
                    case "UpperLeg": return "leg_upper" + s;
                    case "LowerLeg": return "leg_lower" + s;
                    case "Foot": return "ankle" + s;
                    case "Toes": return "ball" + s;
                    case "Eye": return "eye" + s;
                }
                for (int f = 0; f < fingers.Length; f++)
                    for (int p = 0; p < phalanges.Length; p++)
                        if (part == rigFingers[f] + phalanges[p])
                            return "finger_" + fingers[f] + "_" + p + s;
                return null;
            });
            return skeleton;
        }

        // One material's textures as PNGs, relative to the content root.
        private sealed class Slot
        {
            public string Color, Normal, Roughness, Metalness, SelfIllum;
            public bool AlphaTest, Translucent, DoubleSided;
            public float Opacity = 1;
        }

        private static Slot WriteTextures(ObjMaterial material, Dictionary<string, string> textures, Dictionary<string, MaterialMaps> maps, string folder, string materialDir)
        {
            var slot = new Slot { Opacity = material.Opacity, DoubleSided = material.DoubleSided };
            string Target(string suffix) => materialDir + "/" + material.Name + suffix + ".png";
            if (textures.TryGetValue(material.Name, out string texture))
            {
                slot.Color = GameMaterials.Copy(folder, texture, Target("_color"));
                string alpha = GltfWriter.AlphaMode(Path.Combine(folder, slot.Color));
                slot.AlphaTest = alpha == "MASK";
                slot.Translucent = alpha == "BLEND";
            }
            else
            {
                // A plain colour: a small swatch, so every game's shader has a texture to read.
                slot.Color = Target("_color");
                using (var swatch = new Picture(4, 4))
                {
                    int Channel(float v) => Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
                    swatch.Clear(unchecked((int)0xFF000000) | Channel(material.Diffuse[0]) << 16 | Channel(material.Diffuse[1]) << 8 | Channel(material.Diffuse[2]));
                    swatch.Save(Path.Combine(folder, slot.Color), PictureFormat.Png);
                }
            }
            slot.Translucent |= material.Opacity < 0.99f;
            if (maps.TryGetValue(material.Name, out MaterialMaps extra))
            {
                if (extra.Normal != null)
                    slot.Normal = GameMaterials.Copy(folder, extra.Normal, Target("_normal"));
                if (extra.MetallicRoughness != null)
                {
                    // glTF packing (G roughness, B metalness) split into the two greyscale maps Source 2 shaders take.
                    using (Picture packed = Picture.Load(Path.Combine(folder, extra.MetallicRoughness.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        int[] pixels = packed.GetPixels();
                        foreach (var (shift, suffix) in new[] { (8, "_rough"), (0, "_metal") })
                            using (var map = new Picture(packed.Width, packed.Height))
                            {
                                map.SetPixels(pixels.Select(p =>
                                {
                                    int v = (p >> shift) & 0xFF;
                                    return unchecked((int)0xFF000000) | v << 16 | v << 8 | v;
                                }).ToArray());
                                map.Save(Path.Combine(folder, Target(suffix)), PictureFormat.Png);
                            }
                    }
                    slot.Roughness = Target("_rough");
                    slot.Metalness = Target("_metal");
                }
                if (extra.Emissive != null)
                    slot.SelfIllum = GameMaterials.Copy(folder, extra.Emissive, Target("_selfillum"));
            }
            return slot;
        }

        private static string Vmat(Game game, Slot slot)
        {
            var text = new StringBuilder("// Made by RoPorter for " + game.Title + "\n\nLayer0\n{\n\tshader \"" + game.Shader + "\"\n\n");
            void Line(string key, string value) => text.Append('\t').Append(key).Append(" \"").Append(value).Append("\"\n");
            if (slot.AlphaTest)
                Line("F_ALPHA_TEST", "1");
            if (slot.Translucent)
                Line("F_TRANSLUCENT", "1");
            if (slot.DoubleSided)
                Line("F_RENDER_BACKFACES", "1");
            if (slot.Metalness != null)
                Line("F_METALNESS_TEXTURE", "1");
            if (slot.SelfIllum != null)
                Line("F_SELF_ILLUM", "1");
            Line("TextureColor", slot.Color);
            if (slot.Translucent || slot.AlphaTest)
                Line("TextureTranslucency", slot.Color);
            if (slot.Translucent && slot.Opacity < 0.99f)
                Line("g_flOpacityScale", F(slot.Opacity));
            Line("TextureNormal", slot.Normal ?? "materials/default/default_normal.tga");
            if (slot.Roughness != null)
                Line("TextureRoughness", slot.Roughness);
            else
                Line("g_flRoughnessScaleFactor", "1.000");
            if (slot.Metalness != null)
                Line("TextureMetalness", slot.Metalness);
            else
                Line("g_flMetalness", "0.000");
            if (slot.SelfIllum != null)
                Line("TextureSelfIllumMask", slot.SelfIllum);
            return text.Append("}\n").ToString();
        }

        private static string Vmdl(Game game, string id, string mesh, List<string> materials, string materialDir, bool avatar, bool citizen)
        {
            var text = new StringBuilder();
            text.Append("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} ").Append(game.Header).Append(" -->\n{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n");
            void Prefab(string list, string file, string extra = "") =>
                text.Append("\t\t\t{\n\t\t\t\t_class = \"").Append(list).Append("\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"Prefab\"\n\t\t\t\t\t\ttarget_file = \"")
                    .Append(file).Append("\"\n\t\t\t\t\t},\n\t\t\t\t]\n").Append(extra).Append("\t\t\t},\n");

            if (citizen)
                Prefab("AnimConstraintList", "models/citizen/prefabs/citizen_animconstraintlist.vmdl_prefab");
            text.Append("\t\t\t{\n\t\t\t\t_class = \"ModelModifierList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"ModelModifier_ScaleAndMirror\"\n")
                .Append("\t\t\t\t\t\tnote = \"The FBX is in centimetres; this converts it to inches.\"\n\t\t\t\t\t\tscale = ").Append(F(InchesPerCentimetre))
                .Append("\n\t\t\t\t\t\tmirror_x = false\n\t\t\t\t\t\tmirror_y = false\n\t\t\t\t\t\tmirror_z = false\n\t\t\t\t\t\tflip_bone_forward = false\n")
                .Append("\t\t\t\t\t\tswap_left_and_right_bones = false\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n");

            text.Append("\t\t\t{\n\t\t\t\t_class = \"MaterialGroupList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"\n\t\t\t\t\t\tremaps =\n\t\t\t\t\t\t[\n");
            foreach (string material in materials)
                text.Append("\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\tfrom = \"").Append(material).Append(".vmat\"\n\t\t\t\t\t\t\t\tto = \"").Append(materialDir).Append('/')
                    .Append(game.Key).Append('/').Append(material).Append(".vmat\"\n\t\t\t\t\t\t\t},\n");
            text.Append("\t\t\t\t\t\t]\n\t\t\t\t\t\tuse_global_default = false\n\t\t\t\t\t\tglobal_default_material = \"\"\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n");

            text.Append("\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"")
                .Append(id).Append("\"\n\t\t\t\t\t\tfilename = \"").Append(mesh).Append("\"\n\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n")
                .Append("\t\t\t\t\t\timport_scale = 1.0\n\t\t\t\t\t\talign_origin_x_type = \"None\"\n\t\t\t\t\t\talign_origin_y_type = \"None\"\n\t\t\t\t\t\talign_origin_z_type = \"None\"\n")
                .Append("\t\t\t\t\t\tparent_bone = \"\"\n\t\t\t\t\t\timport_filter =\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\texclude_by_default = false\n\t\t\t\t\t\t\texception_list = [  ]\n\t\t\t\t\t\t}\n")
                .Append("\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n");

            string anim = "";
            if (citizen)
            {
                text.Append("\t\t\t{\n\t\t\t\t_class = \"AnimationList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n");
                foreach (string list in new[] { "citizen_animationlist", "citizen_animationlist_unicycle", "citizen_animationlist_debug", "citizen_animationlist_visemes", "citizen_animationlist_menu" })
                    text.Append("\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"Prefab\"\n\t\t\t\t\t\ttarget_file = \"models/citizen/prefabs/").Append(list).Append(".vmdl_prefab\"\n\t\t\t\t\t},\n");
                text.Append("\t\t\t\t]\n\t\t\t\tdefault_root_bone_name = \"pelvis\"\n\t\t\t},\n");
                Prefab("BoneMarkupList", "models/citizen/prefabs/citizen_bonemarkuplist.vmdl_prefab", "\t\t\t\tbone_cull_type = \"None\"\n");
                foreach (string list in new[] { "AttachmentList", "PhysicsJointList", "PhysicsShapeList", "HitboxSetList", "IKData", "PoseParamList" })
                    Prefab(list, "models/citizen/prefabs/citizen_" + list.ToLowerInvariant() + ".vmdl_prefab");
                Prefab("WeightListList", "models/citizen/prefabs/citizen_weightlistlist.vmdl_prefab");
                anim = "models/citizen/citizen.vanmgrph";
            }
            else if (avatar)
            {
                // A bind pose keeps the skeleton (and is what ModelDoc expects of a skinned model without animations).
                text.Append("\t\t\t{\n\t\t\t\t_class = \"AnimationList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"AnimBindPose\"\n")
                    .Append("\t\t\t\t\t\tname = \"bindPose\"\n\t\t\t\t\t\tactivity_name = \"\"\n\t\t\t\t\t\tactivity_weight = 1\n\t\t\t\t\t\tweight_list_name = \"\"\n")
                    .Append("\t\t\t\t\t\tfade_in_time = 0.2\n\t\t\t\t\t\tfade_out_time = 0.2\n\t\t\t\t\t\tlooping = false\n\t\t\t\t\t\tdelta = false\n\t\t\t\t\t\tworldSpace = false\n")
                    .Append("\t\t\t\t\t\thidden = false\n\t\t\t\t\t\tanim_markup_ordered = false\n\t\t\t\t\t\tdisable_compression = false\n\t\t\t\t\t\tdisable_interpolation = false\n")
                    .Append("\t\t\t\t\t\tenable_scale = false\n\t\t\t\t\t\tframe_count = 1\n\t\t\t\t\t\tframe_rate = 30\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t\tdefault_root_bone_name = \"\"\n\t\t\t},\n")
                    .Append("\t\t\t{\n\t\t\t\t_class = \"BoneMarkupList\"\n\t\t\t\tbone_cull_type = \"None\"\n\t\t\t},\n");
            }
            else
            {
                // A prop collides as one convex hull around its render mesh.
                text.Append("\t\t\t{\n\t\t\t\t_class = \"PhysicsShapeList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"PhysicsHullFromRender\"\n")
                    .Append("\t\t\t\t\t\tparent_bone = \"\"\n\t\t\t\t\t\tsurface_prop = \"default\"\n\t\t\t\t\t\tcollision_tags = \"solid\"\n\t\t\t\t\t\tfaceMergeAngle = 10.0\n")
                    .Append("\t\t\t\t\t\tmaxHullVertices = 0\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n");
            }
            text.Append("\t\t]\n\t\tmodel_archetype = \"\"\n\t\tprimary_associated_entity = \"\"\n\t\tanim_graph_name = \"").Append(anim).Append("\"\n\t}\n}\n");
            return text.ToString();
        }

        private static string Readme(string id, string modelDir, bool avatar)
        {
            var text = new StringBuilder("Source 2 content made by RoPorter\r\n=================================\r\n\r\n");
            text.Append("This folder holds the model (" + modelDir + "/" + id + ".fbx), its textures (materials/roporter/" + id + "), and a model and\r\n");
            text.Append("materials for each game. Copy the \"models\" and \"materials\" folders into the game's content folder, then open that\r\n");
            text.Append("game's .vmdl in ModelDoc (or its asset browser) to compile it.\r\n\r\n");
            foreach (Game game in Games)
                text.Append(game.Title).Append(":\r\n  Copy into ").Append(game.Folder).Append("\r\n  Open ").Append(modelDir).Append('/').Append(id).Append('_').Append(game.Key).Append(".vmdl\r\n\r\n");
            if (avatar)
                text.Append("The s&box model is rigged to the Citizen skeleton and uses the Citizen's animation graph, so it works as a player model.\r\n" +
                    "The other games' models keep RoPorter's humanoid skeleton (in its bind pose) for you to animate or rig as those games need.\r\n");
            return text.ToString();
        }
    }
}
