using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rbx2Source.Export
{
    public sealed class MeshBone
    {
        public string Name;
        public int Parent;

        // Roblox CFrame in mesh space: rotation rows r00..r22 followed by x, y, z.
        public double[] Rotation = new double[9];
        public double[] Position = new double[3];
    }

    public sealed class FacsData
    {
        public List<string> BoneNames { get; } = new List<string>();
        public List<string> ControlNames { get; } = new List<string>();
        public List<int[]> Correctives { get; } = new List<int[]>();

        // [channel px,py,pz,rx,ry,rz][bone][column]; columns are controls followed by correctives.
        public float[][][] Channels { get; } = new float[6][][];

        public int ColumnCount => ControlNames.Count + Correctives.Count;
    }

    public sealed class RobloxMesh
    {
        public string Version;
        public List<float[]> Positions { get; } = new List<float[]>();
        public List<float[]> Normals { get; } = new List<float[]>();

        // UVs converted to the OBJ convention (origin at the bottom-left).
        public List<float[]> Texcoords { get; } = new List<float[]>();
        public List<int[]> Faces { get; } = new List<int[]>();
        public List<MeshBone> Bones { get; } = new List<MeshBone>();
        public int[][] SkinBones;
        public float[][] SkinWeights;
        public FacsData Facs;

        private sealed class Subset
        {
            public uint VertsBegin, VertsLength;
            public int[] BoneIndices;
        }

        public static RobloxMesh Parse(byte[] data)
        {
            if (data == null || data.Length < 13)
                throw new InvalidDataException("The head mesh is empty or truncated.");
            int newline = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 32));
            string header = Encoding.ASCII.GetString(data, 0, newline < 0 ? 12 : newline).Trim();
            if (!header.StartsWith("version ", StringComparison.Ordinal))
                throw new InvalidDataException("The head asset is not a Roblox mesh.");
            var mesh = new RobloxMesh { Version = header.Substring(8) };
            int major = mesh.Version.Length > 0 ? mesh.Version[0] - '0' : 0;
            if (major < 2)
            {
                mesh.ReadText(Encoding.ASCII.GetString(data, newline + 1, data.Length - newline - 1));
                return mesh;
            }

            using (var reader = new BinaryReader(new MemoryStream(data, newline + 1, data.Length - newline - 1)))
            {
                try
                {
                    if (major >= 6)
                        mesh.ReadChunks(reader);
                    else
                        mesh.ReadFixed(reader, major);
                }
                catch (EndOfStreamException error)
                {
                    throw new InvalidDataException("The head mesh is truncated.", error);
                }
            }
            if (mesh.Positions.Count == 0 || mesh.Faces.Count == 0)
                throw new InvalidDataException("The head mesh contains no geometry.");
            return mesh;
        }

        private void ReadFixed(BinaryReader reader, int major)
        {
            long start = reader.BaseStream.Position;
            int headerSize = reader.ReadUInt16();
            int vertexSize = 40, numLods = 0, numBones = 0, numSubsets = 0;
            uint numVerts, numFaces, nameTableSize = 0, facsSize = 0;
            if (major == 2)
            {
                vertexSize = reader.ReadByte();
                reader.ReadByte();
                numVerts = reader.ReadUInt32();
                numFaces = reader.ReadUInt32();
            }
            else if (major == 3)
            {
                vertexSize = reader.ReadByte();
                reader.ReadByte();
                reader.ReadUInt16();
                numLods = reader.ReadUInt16();
                numVerts = reader.ReadUInt32();
                numFaces = reader.ReadUInt32();
            }
            else
            {
                reader.ReadUInt16();
                numVerts = reader.ReadUInt32();
                numFaces = reader.ReadUInt32();
                numLods = reader.ReadUInt16();
                numBones = reader.ReadUInt16();
                nameTableSize = reader.ReadUInt32();
                numSubsets = reader.ReadUInt16();
                reader.ReadByte();
                reader.ReadByte();
                if (major >= 5)
                {
                    uint format = reader.ReadUInt32();
                    facsSize = reader.ReadUInt32();
                    if (format == 0)
                        facsSize = 0;
                }
            }
            reader.BaseStream.Position = start + headerSize;
            CheckCount(numVerts, reader, vertexSize);
            ReadVertices(reader, numVerts, vertexSize);

            byte[][] skinning = null;
            if (numBones > 0)
            {
                skinning = new byte[numVerts][];
                for (int i = 0; i < numVerts; i++)
                    skinning[i] = reader.ReadBytes(8);
            }
            var faces = ReadFaces(reader, numFaces);
            var lods = new uint[numLods];
            for (int i = 0; i < numLods; i++)
                lods[i] = reader.ReadUInt32();
            AddLod0(faces, lods);

            if (major >= 4)
            {
                var nameIndices = new uint[numBones];
                for (int i = 0; i < numBones; i++)
                    nameIndices[i] = ReadBone(reader);
                ApplyNames(reader.ReadBytes((int)nameTableSize), nameIndices);
                var subsets = new List<Subset>();
                for (int i = 0; i < numSubsets; i++)
                    subsets.Add(ReadSubset(reader));
                if (skinning != null)
                    ApplySkinning(skinning, subsets);
                if (facsSize > 0)
                    Facs = ReadFacs(new BinaryReader(new MemoryStream(reader.ReadBytes((int)facsSize))));
            }
        }

        private void ReadChunks(BinaryReader reader)
        {
            byte[][] skinning = null;
            List<Subset> subsets = null;
            List<int[]> faces = null;
            uint[] lods = Array.Empty<uint>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                string type = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                uint version = reader.ReadUInt32();
                uint size = reader.ReadUInt32();
                if (size > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException("The head mesh chunk " + type + " is truncated.");
                var chunk = new BinaryReader(new MemoryStream(reader.ReadBytes((int)size)));
                switch (type)
                {
                    case "COREMESH":
                        if (version == 2)
                        {
                            faces = ReadDraco(chunk.ReadBytes((int)chunk.ReadUInt32()));
                            break;
                        }
                        if (version != 1)
                            throw new NotSupportedException("Unsupported COREMESH version " + version + " (mesh " + Version + ").");
                        uint numVerts = chunk.ReadUInt32();
                        CheckCount(numVerts, chunk, 40);
                        ReadVertices(chunk, numVerts, 40);
                        faces = ReadFaces(chunk, chunk.ReadUInt32());
                        break;
                    case "LODS":
                        chunk.ReadUInt16();
                        chunk.ReadByte();
                        uint count = chunk.ReadUInt32();
                        CheckCount(count, chunk, 4);
                        lods = new uint[count];
                        for (int i = 0; i < count; i++)
                            lods[i] = chunk.ReadUInt32();
                        break;
                    case "SKINNING":
                        uint numSkinnings = chunk.ReadUInt32();
                        CheckCount(numSkinnings, chunk, 8);
                        skinning = new byte[numSkinnings][];
                        for (int i = 0; i < numSkinnings; i++)
                            skinning[i] = chunk.ReadBytes(8);
                        uint numBones = chunk.ReadUInt32();
                        CheckCount(numBones, chunk, 60);
                        var nameIndices = new uint[numBones];
                        for (int i = 0; i < numBones; i++)
                            nameIndices[i] = ReadBone(chunk);
                        uint nameSize = chunk.ReadUInt32();
                        ApplyNames(chunk.ReadBytes((int)nameSize), nameIndices);
                        uint numSubsets = chunk.ReadUInt32();
                        CheckCount(numSubsets, chunk, 72);
                        subsets = new List<Subset>();
                        for (int i = 0; i < numSubsets; i++)
                            subsets.Add(ReadSubset(chunk));
                        break;
                    case "FACS":
                        uint facsSize = chunk.ReadUInt32();
                        Facs = ReadFacs(new BinaryReader(new MemoryStream(chunk.ReadBytes((int)facsSize))));
                        break;
                }
            }
            if (faces == null)
                throw new InvalidDataException("The head mesh has no COREMESH chunk.");
            AddLod0(faces, lods);
            if (skinning != null && subsets != null)
            {
                if (skinning.Length != Positions.Count)
                    throw new InvalidDataException("The head mesh skinning does not match its vertices.");
                ApplySkinning(skinning, subsets);
            }
        }

        private static void CheckCount(uint count, BinaryReader reader, int stride)
        {
            if ((long)count * stride > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("The head mesh declares more data than it contains.");
        }

        // Mesh v7 stores its geometry as a Draco bitstream. Its points keep Roblox's vertex order, which the
        // SKINNING chunk indexes; the attributes are position, normal, UV, tangent and colour.
        private List<int[]> ReadDraco(byte[] data)
        {
            Openize.Drako.DracoMesh draco;
            try
            {
                draco = Openize.Drako.Draco.Decode(data) as Openize.Drako.DracoMesh;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                throw new InvalidDataException("The Draco mesh data could not be decoded: " + error.Message, error);
            }
            if (draco == null || draco.NumPoints <= 0)
                throw new InvalidDataException("The Draco mesh data contains no triangles.");
            var attributes = Enumerable.Range(0, draco.NumAttributes).Select(draco.Attribute).Where(a => a != null).ToList();
            Openize.Drako.PointAttribute position = attributes.FirstOrDefault(a => a.AttributeType == Openize.Drako.AttributeType.Position);
            Openize.Drako.PointAttribute uv = attributes.FirstOrDefault(a => a.AttributeType == Openize.Drako.AttributeType.TexCoord);
            Openize.Drako.PointAttribute normal = attributes.FirstOrDefault(a => a.AttributeType == Openize.Drako.AttributeType.Normal)
                ?? attributes.FirstOrDefault(a => a.AttributeType == Openize.Drako.AttributeType.Generic && a.ComponentsCount == 3 &&
                    a.DataType == Openize.Drako.DataType.FLOAT32);
            if (position == null || position.ComponentsCount != 3)
                throw new InvalidDataException("The Draco mesh data has no positions.");
            var value = new float[4];
            for (int point = 0; point < draco.NumPoints; point++)
            {
                position.GetValue(position.MappedIndex(point), value);
                var p = new[] { value[0], value[1], value[2] };
                var n = new float[] { 0, 1, 0 };
                if (normal != null)
                {
                    normal.GetValue(normal.MappedIndex(point), value);
                    n = new[] { value[0], value[1], value[2] };
                }
                var t = new float[] { 0, 1 };
                if (uv != null)
                {
                    uv.GetValue(uv.MappedIndex(point), value);
                    t = new[] { value[0], 1 - value[1] };
                }
                if (p.Concat(n).Concat(t).Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    throw new InvalidDataException("The head mesh contains invalid vertex data.");
                Positions.Add(p);
                Normals.Add(n);
                Texcoords.Add(t);
            }
            var faces = new List<int[]>(draco.NumFaces);
            var face = new int[3];
            for (int i = 0; i < draco.NumFaces; i++)
            {
                draco.ReadFace(i, face);
                if (face.Any(index => index < 0 || index >= Positions.Count))
                    throw new InvalidDataException("The head mesh has an out-of-range face index.");
                faces.Add((int[])face.Clone());
            }
            return faces;
        }

        // Version 1 text meshes (old catalog items): a face count, then [position][normal][uv] for each corner of each
        // face. They have no skinning or facial data. Version 1.00 positions are stored at twice their size.
        private void ReadText(string text)
        {
            int newline = text.IndexOf('\n');
            if (newline < 0 || !int.TryParse(text.Substring(0, newline).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int faceCount) || faceCount <= 0)
                throw new InvalidDataException("The text mesh has no face count.");
            float scale = Version == "1.00" ? 0.5f : 1;
            var vectors = Regex.Matches(text.Substring(newline + 1), @"\[([^\]]*)\]");
            if (vectors.Count < faceCount * 9)
                throw new InvalidDataException("The text mesh is truncated.");
            float[] Vector(int index)
            {
                string[] parts = vectors[index].Groups[1].Value.Split(',');
                if (parts.Length < 3 || !parts.Take(3).All(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    throw new InvalidDataException("The text mesh has an invalid vector: " + vectors[index].Value);
                float[] value = parts.Take(3).Select(part => float.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (value.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    throw new InvalidDataException("The text mesh contains invalid vertex data.");
                return value;
            }
            for (int corner = 0; corner < faceCount * 3; corner++)
            {
                float[] p = Vector(corner * 3), n = Vector(corner * 3 + 1), uv = Vector(corner * 3 + 2);
                Positions.Add(new[] { p[0] * scale, p[1] * scale, p[2] * scale });
                Normals.Add(n);
                Texcoords.Add(new[] { uv[0], 1 - uv[1] });
            }
            for (int face = 0; face < faceCount; face++)
                Faces.Add(new[] { face * 3, face * 3 + 1, face * 3 + 2 });
        }

        private void ReadVertices(BinaryReader reader, uint count, int stride)
        {
            if (stride < 36)
                throw new InvalidDataException("Unsupported mesh vertex size: " + stride);
            for (int i = 0; i < count; i++)
            {
                byte[] vertex = reader.ReadBytes(stride);
                if (vertex.Length < stride)
                    throw new EndOfStreamException();
                var p = new[] { BitConverter.ToSingle(vertex, 0), BitConverter.ToSingle(vertex, 4), BitConverter.ToSingle(vertex, 8) };
                var n = new[] { BitConverter.ToSingle(vertex, 12), BitConverter.ToSingle(vertex, 16), BitConverter.ToSingle(vertex, 20) };
                var uv = new[] { BitConverter.ToSingle(vertex, 24), 1 - BitConverter.ToSingle(vertex, 28) };
                if (p.Concat(n).Concat(uv).Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                    throw new InvalidDataException("The head mesh contains invalid vertex data.");
                Positions.Add(p);
                Normals.Add(n);
                Texcoords.Add(uv);
            }
        }

        private List<int[]> ReadFaces(BinaryReader reader, uint count)
        {
            CheckCount(count, reader, 12);
            var faces = new List<int[]>((int)count);
            for (int i = 0; i < count; i++)
            {
                var face = new[] { (int)reader.ReadUInt32(), (int)reader.ReadUInt32(), (int)reader.ReadUInt32() };
                if (face.Any(index => index < 0 || index >= Positions.Count))
                    throw new InvalidDataException("The head mesh has an out-of-range face index.");
                faces.Add(face);
            }
            return faces;
        }

        private void AddLod0(List<int[]> faces, uint[] lods)
        {
            int end = lods.Length >= 2 && lods[1] > lods[0] && lods[1] <= faces.Count ? (int)lods[1] : faces.Count;
            int begin = lods.Length >= 2 && lods[0] < end ? (int)lods[0] : 0;
            Faces.AddRange(faces.Skip(begin).Take(end - begin));
        }

        private uint ReadBone(BinaryReader reader)
        {
            uint nameIndex = reader.ReadUInt32();
            ushort parent = reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadSingle();
            var bone = new MeshBone { Parent = parent == 0xFFFF ? -1 : parent };
            for (int i = 0; i < 9; i++)
                bone.Rotation[i] = reader.ReadSingle();
            for (int i = 0; i < 3; i++)
                bone.Position[i] = reader.ReadSingle();
            Bones.Add(bone);
            return nameIndex;
        }

        private void ApplyNames(byte[] table, uint[] nameIndices)
        {
            for (int i = 0; i < nameIndices.Length; i++)
            {
                int start = (int)nameIndices[i];
                if (start >= table.Length)
                    throw new InvalidDataException("The head mesh has an invalid bone name.");
                int end = Array.IndexOf(table, (byte)0, start);
                Bones[i].Name = Encoding.UTF8.GetString(table, start, (end < 0 ? table.Length : end) - start);
                if (Bones[i].Parent >= nameIndices.Length)
                    throw new InvalidDataException("The head mesh has an invalid bone parent.");
            }
        }

        private static Subset ReadSubset(BinaryReader reader)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            var subset = new Subset { VertsBegin = reader.ReadUInt32(), VertsLength = reader.ReadUInt32() };
            uint used = reader.ReadUInt32();
            subset.BoneIndices = new int[26];
            for (int i = 0; i < 26; i++)
            {
                ushort index = reader.ReadUInt16();
                subset.BoneIndices[i] = i < used && index != 0xFFFF ? index : -1;
            }
            return subset;
        }

        private void ApplySkinning(byte[][] skinning, List<Subset> subsets)
        {
            SkinBones = new int[Positions.Count][];
            SkinWeights = new float[Positions.Count][];
            foreach (Subset subset in subsets)
            {
                long end = Math.Min((long)subset.VertsBegin + subset.VertsLength, Positions.Count);
                for (long v = subset.VertsBegin; v < end; v++)
                {
                    var bones = new List<int>();
                    var weights = new List<float>();
                    for (int j = 0; j < 4; j++)
                    {
                        byte weight = skinning[v][4 + j];
                        int local = skinning[v][j];
                        if (weight == 0)
                            continue;
                        if (local >= 26 || subset.BoneIndices[local] < 0 || subset.BoneIndices[local] >= Bones.Count)
                            throw new InvalidDataException("The head mesh has an invalid skinning bone index.");
                        bones.Add(subset.BoneIndices[local]);
                        weights.Add(weight);
                    }
                    float total = weights.Sum();
                    SkinBones[v] = bones.ToArray();
                    SkinWeights[v] = weights.Select(weight => weight / total).ToArray();
                }
            }
        }

        private static string[] Names(byte[] blob)
        {
            return Encoding.UTF8.GetString(blob).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // Meshes store FaceControls property names as abbreviations (c_JD = JawDrop, l_EC = LeftEyeClosed, ...).
        private static readonly Dictionary<string, string> ControlAbbreviations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["c_COR"] = "Corrugator", ["c_CR"] = "ChinRaiser", ["c_CRUL"] = "ChinRaiserUpperLip", ["c_ELD"] = "EyesLookDown",
            ["c_ELL"] = "EyesLookLeft", ["c_ELR"] = "EyesLookRight", ["c_ELU"] = "EyesLookUp", ["c_FN"] = "Funneler",
            ["c_FP"] = "FlatPucker", ["c_JD"] = "JawDrop", ["c_JL"] = "JawLeft", ["c_JR"] = "JawRight",
            ["c_LLS"] = "LowerLipSuck", ["c_LP"] = "LipPresser", ["c_LPT"] = "LipsTogether", ["c_ML"] = "MouthLeft",
            ["c_MR"] = "MouthRight", ["c_PK"] = "Pucker", ["c_TD"] = "TongueDown", ["c_TO"] = "TongueOut",
            ["c_TU"] = "TongueUp", ["c_ULS"] = "UpperLipSuck",
            ["l_BL"] = "LeftBrowLowerer", ["l_CHP"] = "LeftCheekPuff", ["l_CHR"] = "LeftCheekRaiser", ["l_DM"] = "LeftDimpler",
            ["l_EC"] = "LeftEyeClosed", ["l_EULR"] = "LeftEyeUpperLidRaiser", ["l_IBR"] = "LeftInnerBrowRaiser",
            ["l_LCD"] = "LeftLipCornerDown", ["l_LCP"] = "LeftLipCornerPuller", ["l_LLD"] = "LeftLowerLipDepressor",
            ["l_LS"] = "LeftLipStretcher", ["l_NW"] = "LeftNoseWrinkler", ["l_OBR"] = "LeftOuterBrowRaiser",
            ["l_ULR"] = "LeftUpperLipRaiser",
            ["r_BL"] = "RightBrowLowerer", ["r_CHP"] = "RightCheekPuff", ["r_CHR"] = "RightCheekRaiser", ["r_DM"] = "RightDimpler",
            ["r_EC"] = "RightEyeClosed", ["r_EULR"] = "RightEyeUpperLidRaiser", ["r_IBR"] = "RightInnerBrowRaiser",
            ["r_LCD"] = "RightLipCornerDown", ["r_LCP"] = "RightLipCornerPuller", ["r_LLD"] = "RightLowerLipDepressor",
            ["r_LS"] = "RightLipStretcher", ["r_NW"] = "RightNoseWrinkler", ["r_OBR"] = "RightOuterBrowRaiser",
            ["r_ULR"] = "RightUpperLipRaiser"
        };

        public static string ControlName(string name) => ControlAbbreviations.TryGetValue(name, out string full) ? full : name;

        private static FacsData ReadFacs(BinaryReader reader)
        {
            var facs = new FacsData();
            uint boneSize = reader.ReadUInt32(), controlSize = reader.ReadUInt32();
            reader.ReadUInt64();
            uint twoSize = reader.ReadUInt32(), threeSize = reader.ReadUInt32();
            facs.BoneNames.AddRange(Names(reader.ReadBytes((int)boneSize)));
            facs.ControlNames.AddRange(Names(reader.ReadBytes((int)controlSize)).Select(ControlName));

            var matrices = new float[6][];
            int rows = 0, cols = 0;
            for (int channel = 0; channel < 6; channel++)
            {
                ushort version = reader.ReadUInt16();
                int r = (int)reader.ReadUInt32(), c = (int)reader.ReadUInt32();
                if ((long)r * c > 1 << 24)
                    throw new InvalidDataException("The FACS data is too large.");
                var values = new float[r * c];
                if (version == 1)
                {
                    for (int i = 0; i < values.Length; i++)
                        values[i] = reader.ReadSingle();
                }
                else if (version == 2)
                {
                    float min = reader.ReadSingle(), max = reader.ReadSingle();
                    float precision = (max - min) / 65535f;
                    for (int i = 0; i < values.Length; i++)
                        values[i] = min + reader.ReadUInt16() * precision;
                }
                else
                {
                    throw new InvalidDataException("Unknown FACS matrix version " + version + ".");
                }
                if (channel > 0 && (r != rows || c != cols))
                    throw new InvalidDataException("The FACS matrices have inconsistent sizes.");
                rows = r;
                cols = c;
                matrices[channel] = values;
            }

            for (int i = 0; i < twoSize / 4; i++)
                facs.Correctives.Add(new int[] { reader.ReadUInt16(), reader.ReadUInt16() });
            for (int i = 0; i < threeSize / 6; i++)
                facs.Correctives.Add(new int[] { reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16() });
            if (facs.Correctives.SelectMany(c => c).Any(index => index >= facs.ControlNames.Count))
                throw new InvalidDataException("A FACS corrective refers to an unknown control.");

            // The specification documents rows as bones; accept transposed data defensively.
            bool transposed;
            if (rows == facs.BoneNames.Count && cols == facs.ColumnCount)
                transposed = false;
            else if (cols == facs.BoneNames.Count && rows == facs.ColumnCount)
                transposed = true;
            else
                throw new InvalidDataException("The FACS matrix size does not match its bones and controls.");

            for (int channel = 0; channel < 6; channel++)
            {
                facs.Channels[channel] = new float[facs.BoneNames.Count][];
                for (int bone = 0; bone < facs.BoneNames.Count; bone++)
                {
                    facs.Channels[channel][bone] = new float[facs.ColumnCount];
                    for (int column = 0; column < facs.ColumnCount; column++)
                        facs.Channels[channel][bone][column] = transposed
                            ? matrices[channel][column * cols + bone]
                            : matrices[channel][bone * cols + column];
                }
            }
            return facs;
        }
    }
}
