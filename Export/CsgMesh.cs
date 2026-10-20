using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Rbx2Source.Export
{
    // A union's mesh (PartOperation / PartOperationAsset MeshData): "CSGMDL", stored scrambled by a repeating 31-byte XOR key.
    // Version 2 (and 4, which adds levels of detail after it) holds a header (magic, version, two 16-byte digests), 84-byte
    // vertices (position, normal, RGBA colour, texture coordinates, tangent) and 32-bit indices. Positions are in studs, centred,
    // at the union's InitialSize.
    public sealed class CsgMesh
    {
        private static readonly byte[] Key =
        {
            0x56, 0x2E, 0x6E, 0x58, 0x31, 0x20, 0x30, 0x04, 0x34, 0x69, 0x0C, 0x77, 0x0C, 0x01, 0x5E, 0x00,
            0x1A, 0x60, 0x37, 0x69, 0x1D, 0x52, 0x2B, 0x07, 0x4F, 0x24, 0x59, 0x65, 0x53, 0x04, 0x7A
        };
        private const int HeaderSize = 42, MinimumStride = 28;

        public List<float[]> Positions { get; } = new List<float[]>();
        public List<float[]> Normals { get; } = new List<float[]>();
        // The colour of the part each vertex came from (shown when the union's UsePartColor is off).
        public List<float[]> Colors { get; } = new List<float[]>();
        public List<int[]> Faces { get; } = new List<int[]>();

        public static CsgMesh Parse(byte[] data)
        {
            if (data == null || data.Length < HeaderSize + 12)
                throw new InvalidDataException("The union's mesh data is empty or truncated.");
            byte[] plain;
            if (Encoding.ASCII.GetString(data, 0, 6) == "CSGMDL")
                plain = data;
            else
            {
                plain = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                    plain[i] = (byte)(data[i] ^ Key[i % Key.Length]);
                if (Encoding.ASCII.GetString(plain, 0, 6) != "CSGMDL")
                    throw new InvalidDataException("The union's mesh data is not in a format RoPorter knows.");
            }
            int version = BitConverter.ToInt32(plain, 6);
            if (version != 2 && version != 4)
                throw new NotSupportedException("Union mesh version " + version + " is not supported.");

            int offset = HeaderSize;
            int count = BitConverter.ToInt32(plain, offset), stride = BitConverter.ToInt32(plain, offset + 4);
            offset += 8;
            if (count <= 0 || stride < MinimumStride || (long)count * stride > plain.Length - offset - 4)
                throw new InvalidDataException("The union's mesh data has an invalid vertex list.");
            var mesh = new CsgMesh();
            for (int i = 0; i < count; i++, offset += stride)
            {
                float[] p = { BitConverter.ToSingle(plain, offset), BitConverter.ToSingle(plain, offset + 4), BitConverter.ToSingle(plain, offset + 8) };
                float[] n = { BitConverter.ToSingle(plain, offset + 12), BitConverter.ToSingle(plain, offset + 16), BitConverter.ToSingle(plain, offset + 20) };
                if (p.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    p = new float[3];
                if (n.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    n = new float[] { 0, 1, 0 };
                mesh.Positions.Add(p);
                mesh.Normals.Add(n);
                mesh.Colors.Add(new[] { plain[offset + 24] / 255f, plain[offset + 25] / 255f, plain[offset + 26] / 255f });
            }
            int indexCount = BitConverter.ToInt32(plain, offset);
            offset += 4;
            if (indexCount < 3 || (long)indexCount * 4 > plain.Length - offset)
                throw new InvalidDataException("The union's mesh data has an invalid index list.");
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = BitConverter.ToInt32(plain, offset + 4 * i);
            offset += 4 * indexCount;
            // Version 4 lists where each level of detail starts in the indices; the first is the full mesh.
            int begin = 0, end = indexCount - indexCount % 3;
            if (version == 4 && offset + 4 <= plain.Length)
            {
                int lods = BitConverter.ToInt32(plain, offset);
                if (lods >= 2 && offset + 4 + 4L * lods <= plain.Length)
                {
                    int first = BitConverter.ToInt32(plain, offset + 4), second = BitConverter.ToInt32(plain, offset + 8);
                    if (first >= 0 && second > first && second <= indexCount && (second - first) % 3 == 0)
                    {
                        begin = first;
                        end = second;
                    }
                }
            }
            for (int i = begin; i + 2 < end; i += 3)
            {
                int[] face = { indices[i], indices[i + 1], indices[i + 2] };
                if (face.All(index => index >= 0 && index < count))
                    mesh.Faces.Add(face);
            }
            if (mesh.Faces.Count == 0)
                throw new InvalidDataException("The union's mesh has no triangles.");
            return mesh;
        }

        // The mesh of a union asset (a PartOperationAsset model) or of a union saved with its mesh inline.
        public static CsgMesh FromAsset(byte[] asset) =>
            Parse(RobloxModelFile.ReadInstances(asset).SelectMany(root => new[] { root }.Concat(root.Descendants()))
                .Select(instance => instance.Binary.TryGetValue("MeshData", out byte[] data) ? data : null)
                .FirstOrDefault(data => data != null && data.Length > 0) ?? throw new InvalidDataException("The union asset has no mesh data."));
    }
}
