using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace Rbx2Source.Export
{
    // Writes a skinned COLLADA (.dae) model in another game's frame and skeleton, laid out like the files Blender exports
    // (which Tower Unite's character importer is made for): one mesh, its materials with their textures beside the file, and
    // the skeleton as joint nodes under an "Armature" node.
    public static class ColladaWriter
    {
        public sealed class Options
        {
            // The scene's frame to the file's, row-major 3x3, and the file's units per metre.
            public double[] FromScene = Matrix.Identity;
            public double UnitsPerMetre = 1;
            public string UnitName = "meter";
            public double MetresPerUnit = 1;
            public string UpAxis = "Y_UP";
        }

        private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Id(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

        // bones: the skeleton in scene space (parents before children); boneOf: rig bone -> bone index. textures: material name ->
        // image file name beside the .dae.
        public static void Write(ExportScene scene, List<TargetSkeleton.Bone> bones, int[] boneOf, IReadOnlyDictionary<string, string> textures,
            string path, Options options, CancellationToken cancellation = default)
        {
            WavefrontModel model = scene.Model;
            if (model.Triangles.Count == 0)
                throw new InvalidDataException("The model contains no triangles.");
            double[] Point(double[] p) => Matrix.Scale(Matrix.Apply(options.FromScene, p), options.UnitsPerMetre);
            double[] Rotation(double[] r) => Matrix.Multiply(options.FromScene, r);

            // World matrices in the file's frame (row-major 4x4).
            double[][] world = bones.Select(b =>
            {
                double[] r = Rotation(b.Rotation), t = Point(b.Position);
                return new[] { r[0], r[1], r[2], t[0], r[3], r[4], r[5], t[1], r[6], r[7], r[8], t[2], 0, 0, 0, 1 };
            }).ToArray();
            double[] Inverse(double[] m)
            {
                double[] r = { m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10] };
                double[] t = Matrix.Scale(Matrix.Apply(r, new[] { m[3], m[7], m[11] }), -1);
                return new[] { r[0], r[1], r[2], t[0], r[3], r[4], r[5], t[1], r[6], r[7], r[8], t[2], 0, 0, 0, 1 };
            }
            double[] Times(double[] a, double[] b)
            {
                var result = new double[16];
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        for (int k = 0; k < 4; k++)
                            result[r * 4 + c] += a[r * 4 + k] * b[k * 4 + c];
                return result;
            }

            var points = model.Triangles.SelectMany(t => t.Corners).Select(c => c.Position).Distinct().ToList();
            var local = new Dictionary<int, int>();
            for (int i = 0; i < points.Count; i++)
                local[points[i]] = i;
            var normals = new List<double[]>();
            var normalIndex = new Dictionary<int, int>();
            var uvs = new List<float[]>();
            var uvIndex = new Dictionary<int, int>();
            int Normal(int n)
            {
                if (!normalIndex.TryGetValue(n, out int index))
                {
                    double[] v = Matrix.Apply(options.FromScene, ExportScene.Direction(model.Normals[n]).Select(x => (double)x).ToArray());
                    double length = Matrix.Length(v);
                    normals.Add(length > 0 ? Matrix.Scale(v, 1 / length) : new double[] { 0, 0, 1 });
                    normalIndex[n] = index = normals.Count - 1;
                }
                return index;
            }
            int Uv(int t)
            {
                if (!uvIndex.TryGetValue(t, out int index))
                {
                    uvs.Add(t >= 0 ? model.Texcoords[t] : new float[2]);
                    uvIndex[t] = index = uvs.Count - 1;
                }
                return index;
            }

            var text = new StringBuilder();
            text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n");
            text.Append("  <asset>\n    <contributor><authoring_tool>RoPorter</authoring_tool></contributor>\n");
            text.Append("    <created>1970-01-01T00:00:00</created>\n    <modified>1970-01-01T00:00:00</modified>\n");
            text.Append("    <unit name=\"").Append(options.UnitName).Append("\" meter=\"").Append(F(options.MetresPerUnit)).Append("\"/>\n");
            text.Append("    <up_axis>").Append(options.UpAxis).Append("</up_axis>\n  </asset>\n");

            var materials = model.Triangles.Select(t => t.Material).Distinct().ToList();
            text.Append("  <library_images>\n");
            foreach (string material in materials.Where(m => textures.ContainsKey(m)))
                text.Append("    <image id=\"").Append(Id(material)).Append("-image\" name=\"").Append(Id(material)).Append("-image\"><init_from>")
                    .Append(SecurityElement.Escape(textures[material])).Append("</init_from></image>\n");
            text.Append("  </library_images>\n  <library_effects>\n");
            foreach (string material in materials)
            {
                string id = Id(material);
                ObjMaterial m = model.Materials[material];
                text.Append("    <effect id=\"").Append(id).Append("-effect\">\n      <profile_COMMON>\n");
                if (textures.ContainsKey(material))
                    text.Append("        <newparam sid=\"").Append(id).Append("-surface\"><surface type=\"2D\"><init_from>").Append(id)
                        .Append("-image</init_from></surface></newparam>\n        <newparam sid=\"").Append(id)
                        .Append("-sampler\"><sampler2D><source>").Append(id).Append("-surface</source></sampler2D></newparam>\n");
                text.Append("        <technique sid=\"common\">\n          <phong>\n            <diffuse>");
                if (textures.ContainsKey(material))
                    text.Append("<texture texture=\"").Append(id).Append("-sampler\" texcoord=\"UVMap\"/>");
                else
                    text.Append("<color sid=\"diffuse\">").Append(F(m.Diffuse[0])).Append(' ').Append(F(m.Diffuse[1])).Append(' ').Append(F(m.Diffuse[2])).Append(" 1</color>");
                text.Append("</diffuse>\n          </phong>\n        </technique>\n      </profile_COMMON>\n    </effect>\n");
            }
            text.Append("  </library_effects>\n  <library_materials>\n");
            foreach (string material in materials)
                text.Append("    <material id=\"").Append(Id(material)).Append("-material\" name=\"").Append(Id(material))
                    .Append("\"><instance_effect url=\"#").Append(Id(material)).Append("-effect\"/></material>\n");
            text.Append("  </library_materials>\n");

            // The mesh. Corners index positions, normals and UVs separately.
            var triangles = new StringBuilder();
            foreach (string material in materials)
            {
                cancellation.ThrowIfCancellationRequested();
                var list = model.Triangles.Where(t => t.Material == material).ToList();
                triangles.Append("        <triangles material=\"").Append(Id(material)).Append("-material\" count=\"").Append(list.Count).Append("\">\n");
                triangles.Append("          <input semantic=\"VERTEX\" source=\"#mesh-vertices\" offset=\"0\"/>\n");
                triangles.Append("          <input semantic=\"NORMAL\" source=\"#mesh-normals\" offset=\"1\"/>\n");
                triangles.Append("          <input semantic=\"TEXCOORD\" source=\"#mesh-map\" offset=\"2\" set=\"0\"/>\n          <p>");
                foreach (ObjTriangle triangle in list)
                    foreach (ObjCorner corner in triangle.Corners)
                        triangles.Append(local[corner.Position]).Append(' ').Append(Normal(corner.Normal)).Append(' ').Append(Uv(corner.Texture)).Append(' ');
                triangles.Length--;
                triangles.Append("</p>\n        </triangles>\n");
            }
            void Source(string id, IEnumerable<double> values, int stride, string[] parameters)
            {
                var array = values.ToList();
                text.Append("        <source id=\"").Append(id).Append("\">\n          <float_array id=\"").Append(id).Append("-array\" count=\"")
                    .Append(array.Count).Append("\">").Append(string.Join(" ", array.Select(F))).Append("</float_array>\n")
                    .Append("          <technique_common><accessor source=\"#").Append(id).Append("-array\" count=\"").Append(array.Count / stride)
                    .Append("\" stride=\"").Append(stride).Append("\">");
                foreach (string parameter in parameters)
                    text.Append("<param name=\"").Append(parameter).Append("\" type=\"float\"/>");
                text.Append("</accessor></technique_common>\n        </source>\n");
            }
            text.Append("  <library_geometries>\n    <geometry id=\"mesh\" name=\"Body\">\n      <mesh>\n");
            Source("mesh-positions", points.SelectMany(p => Point(scene.Point(model.Positions[p]).Select(v => (double)v).ToArray())), 3, new[] { "X", "Y", "Z" });
            string triangleText = triangles.ToString();
            Source("mesh-normals", normals.SelectMany(n => n), 3, new[] { "X", "Y", "Z" });
            Source("mesh-map", uvs.SelectMany(uv => new double[] { uv[0], uv[1] }), 2, new[] { "S", "T" });
            text.Append("        <vertices id=\"mesh-vertices\"><input semantic=\"POSITION\" source=\"#mesh-positions\"/></vertices>\n");
            text.Append(triangleText).Append("      </mesh>\n    </geometry>\n  </library_geometries>\n");

            // Skin: each position's strongest influences (up to four), normalised.
            AvatarRig rig = scene.Rig;
            var influences = points.Select(p =>
            {
                var weights = new Dictionary<int, double>();
                for (int k = 0; k < 4; k++)
                {
                    float w = rig.Weights[p][k];
                    if (w <= 0)
                        continue;
                    int bone = boneOf[rig.Joints[p][k]];
                    weights[bone] = (weights.TryGetValue(bone, out double existing) ? existing : 0) + w;
                }
                var top = weights.OrderByDescending(pair => pair.Value).Take(4).ToList();
                double total = top.Sum(pair => pair.Value);
                return total > 0 ? top.Select(pair => new KeyValuePair<int, double>(pair.Key, pair.Value / total)).ToList()
                    : new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(boneOf[AvatarRig.Hips], 1) };
            }).ToList();
            var weightValues = new List<double>();
            var weightIndex = new Dictionary<double, int>();
            int Weight(double w)
            {
                double key = Math.Round(w, 6);
                if (!weightIndex.TryGetValue(key, out int index))
                {
                    weightValues.Add(key);
                    weightIndex[key] = index = weightValues.Count - 1;
                }
                return index;
            }
            string[] sids = bones.Select(b => Id(b.Name)).ToArray();
            text.Append("  <library_controllers>\n    <controller id=\"Armature_mesh-skin\" name=\"Armature\">\n      <skin source=\"#mesh\">\n");
            text.Append("        <bind_shape_matrix>1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1</bind_shape_matrix>\n");
            text.Append("        <source id=\"Armature_mesh-skin-joints\">\n          <Name_array id=\"Armature_mesh-skin-joints-array\" count=\"").Append(sids.Length)
                .Append("\">").Append(string.Join(" ", sids)).Append("</Name_array>\n          <technique_common><accessor source=\"#Armature_mesh-skin-joints-array\" count=\"")
                .Append(sids.Length).Append("\" stride=\"1\"><param name=\"JOINT\" type=\"name\"/></accessor></technique_common>\n        </source>\n");
            var vcount = new StringBuilder();
            var v = new StringBuilder();
            foreach (var list in influences)
            {
                vcount.Append(list.Count).Append(' ');
                foreach (var pair in list)
                    v.Append(pair.Key).Append(' ').Append(Weight(pair.Value)).Append(' ');
            }
            text.Append("        <source id=\"Armature_mesh-skin-bind_poses\">\n          <float_array id=\"Armature_mesh-skin-bind_poses-array\" count=\"")
                .Append(16 * bones.Count).Append("\">").Append(string.Join(" ", world.SelectMany(Inverse).Select(F)))
                .Append("</float_array>\n          <technique_common><accessor source=\"#Armature_mesh-skin-bind_poses-array\" count=\"").Append(bones.Count)
                .Append("\" stride=\"16\"><param name=\"TRANSFORM\" type=\"float4x4\"/></accessor></technique_common>\n        </source>\n");
            text.Append("        <source id=\"Armature_mesh-skin-weights\">\n          <float_array id=\"Armature_mesh-skin-weights-array\" count=\"")
                .Append(weightValues.Count).Append("\">").Append(string.Join(" ", weightValues.Select(F)))
                .Append("</float_array>\n          <technique_common><accessor source=\"#Armature_mesh-skin-weights-array\" count=\"").Append(weightValues.Count)
                .Append("\" stride=\"1\"><param name=\"WEIGHT\" type=\"float\"/></accessor></technique_common>\n        </source>\n");
            text.Append("        <joints>\n          <input semantic=\"JOINT\" source=\"#Armature_mesh-skin-joints\"/>\n")
                .Append("          <input semantic=\"INV_BIND_MATRIX\" source=\"#Armature_mesh-skin-bind_poses\"/>\n        </joints>\n");
            text.Append("        <vertex_weights count=\"").Append(influences.Count).Append("\">\n")
                .Append("          <input semantic=\"JOINT\" source=\"#Armature_mesh-skin-joints\" offset=\"0\"/>\n")
                .Append("          <input semantic=\"WEIGHT\" source=\"#Armature_mesh-skin-weights\" offset=\"1\"/>\n")
                .Append("          <vcount>").Append(vcount.ToString().TrimEnd()).Append("</vcount>\n          <v>").Append(v.ToString().TrimEnd())
                .Append("</v>\n        </vertex_weights>\n      </skin>\n    </controller>\n  </library_controllers>\n");

            // Scene: the armature's joints (local matrices), then the skinned mesh bound to them.
            text.Append("  <library_visual_scenes>\n    <visual_scene id=\"Scene\" name=\"Scene\">\n");
            text.Append("      <node id=\"Armature\" name=\"Armature\" type=\"NODE\">\n        <matrix sid=\"transform\">1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1</matrix>\n");
            void Joint(int index, int depth)
            {
                string indent = new string(' ', 8 + 2 * depth);
                double[] matrix = bones[index].Parent >= 0 ? Times(Inverse(world[bones[index].Parent]), world[index]) : world[index];
                text.Append(indent).Append("<node id=\"Armature_").Append(sids[index]).Append("\" name=\"").Append(sids[index]).Append("\" sid=\"").Append(sids[index])
                    .Append("\" type=\"JOINT\">\n").Append(indent).Append("  <matrix sid=\"transform\">").Append(string.Join(" ", matrix.Select(F))).Append("</matrix>\n");
                for (int child = 0; child < bones.Count; child++)
                    if (bones[child].Parent == index)
                        Joint(child, depth + 1);
                text.Append(indent).Append("</node>\n");
            }
            int[] roots = Enumerable.Range(0, bones.Count).Where(i => bones[i].Parent < 0).ToArray();
            foreach (int root in roots)
                Joint(root, 0);
            text.Append("      </node>\n      <node id=\"Body\" name=\"Body\" type=\"NODE\">\n        <instance_controller url=\"#Armature_mesh-skin\">\n");
            foreach (int root in roots)
                text.Append("          <skeleton>#Armature_").Append(sids[root]).Append("</skeleton>\n");
            text.Append("          <bind_material><technique_common>\n");
            foreach (string material in materials)
                text.Append("            <instance_material symbol=\"").Append(Id(material)).Append("-material\" target=\"#").Append(Id(material))
                    .Append("-material\"><bind_vertex_input semantic=\"UVMap\" input_semantic=\"TEXCOORD\" input_set=\"0\"/></instance_material>\n");
            text.Append("          </technique_common></bind_material>\n        </instance_controller>\n      </node>\n    </visual_scene>\n  </library_visual_scenes>\n");
            text.Append("  <scene>\n    <instance_visual_scene url=\"#Scene\"/>\n  </scene>\n</COLLADA>\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        // Distinct vertices as a game engine counts them: each different position, normal and UV combination.
        public static int EngineVertexCount(WavefrontModel model) =>
            model.Triangles.SelectMany(t => t.Corners).Select(c => ((long)c.Position << 40) ^ ((long)c.Normal << 20) ^ (uint)c.Texture).Distinct().Count();
    }
}
